#!/usr/bin/env python3
"""
Crash-isolated benchmark harness for AuraAudio FLAC decoder & forensic authenticity engine.

Every audio file executes in its own child process (AuraAudio.BenchmarkWorker.exe) with a
hard timeout so fatal access violations or hangs cannot terminate or stall the runner.

Usage:
  python tools/benchmark/run_benchmark.py --corpus <path> --out <results.json>
  (or set environment variable AURA_BENCHMARK_CORPUS)
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKER_CSPROJ = REPO_ROOT / "tools" / "benchmark" / "AuraAudio.BenchmarkWorker" / "AuraAudio.BenchmarkWorker.csproj"
WORKER_EXE = (
    REPO_ROOT
    / "tools"
    / "benchmark"
    / "AuraAudio.BenchmarkWorker"
    / "bin"
    / "Release"
    / "net9.0-windows10.0.19041.0"
    / "AuraAudio.BenchmarkWorker.exe"
)

FALSE_FAKE_VERDICTS = {
    "SuspectedUpscaledLossy",
    "UpscaledFromCd",
    "UpscaledHiRes",
    "Dithered16BitUpscale",
    "ZeroPadded24Bit",
    "StandardLossy",
}
GENUINE_VERDICTS = {"VerifiedLossless", "AcousticNaturalRollOff"}


def build_worker() -> None:
    cmd = ["dotnet", "build", str(WORKER_CSPROJ), "-c", "Release", "--nologo", "-v", "minimal"]
    proc = subprocess.run(cmd, cwd=str(REPO_ROOT), capture_output=True, text=True)
    if proc.returncode != 0:
        sys.stderr.write(proc.stdout + "\n" + proc.stderr + "\n")
        raise RuntimeError(f"Failed to build BenchmarkWorker (exit {proc.returncode})")
    if not WORKER_EXE.is_file():
        raise FileNotFoundError(f"Expected worker executable not found: {WORKER_EXE}")


def run_worker_single(entry: dict[str, Any], timeout_sec: float) -> dict[str, Any]:
    path = entry["absPath"]
    started = time.perf_counter()
    try:
        proc = subprocess.run(
            [str(WORKER_EXE), path],
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_sec,
        )
        elapsed_ms = int((time.perf_counter() - started) * 1000)
        if proc.returncode == 0 and proc.stdout.strip():
            last_line = proc.stdout.strip().splitlines()[-1]
            data = json.loads(last_line)
            return {**entry, **data}

        stderr_lines = [ln.strip() for ln in (proc.stderr or proc.stdout or "").splitlines() if ln.strip()]
        return {
            **entry,
            "status": "FATAL_CRASH",
            "bytes": os.path.getsize(path) if os.path.exists(path) else 0,
            "totalMs": elapsed_ms,
            "exitCode": hex(proc.returncode & 0xFFFFFFFF),
            "errorType": "FatalProcessCrash",
            "errorMessage": " | ".join(stderr_lines[:3])[:400],
        }
    except subprocess.TimeoutExpired:
        elapsed_ms = int((time.perf_counter() - started) * 1000)
        return {
            **entry,
            "status": "TIMEOUT",
            "bytes": os.path.getsize(path) if os.path.exists(path) else 0,
            "totalMs": elapsed_ms,
            "errorType": "TimeoutExpired",
            "errorMessage": f"Child process exceeded {timeout_sec}s timeout",
        }


def collect_corpus_entries(
    corpus_root: Path,
    subset: str,
    split_data: dict[str, Any] | None,
    library_list: Path | None,
) -> list[dict[str, Any]]:
    entries: list[dict[str, Any]] = []

    if subset == "library":
        if not library_list or not library_list.is_file():
            raise ValueError("--subset library requires --library-list <file>")
        for line in library_list.read_text(encoding="utf-8-sig").splitlines():
            p = line.strip()
            if not p or p.startswith("#"):
                continue
            entries.append(
                {
                    "suite": "library",
                    "group": "library",
                    "subcat": "library",
                    "rel": os.path.basename(p),
                    "file": os.path.basename(p),
                    "absPath": p,
                }
            )
        return entries

    # 1. IETF FLAC testbench (86 files)
    if subset in ("all", "external"):
        ietf_root = corpus_root / "ietf-flac-test-files"
        for sub in ("subset", "uncommon", "faulty"):
            sub_dir = ietf_root / sub
            if sub_dir.is_dir():
                for p in sorted(sub_dir.glob("*.flac"), key=lambda x: x.name.lower()):
                    entries.append(
                        {
                            "suite": "external_ietf",
                            "group": f"ietf/{sub}",
                            "subcat": f"ietf_{sub}",
                            "rel": f"ietf-flac-test-files/{sub}/{p.name}",
                            "file": p.name,
                            "absPath": str(p),
                        }
                    )

        # 2. 17 GitHub audio repos (177 files)
        gh_root = corpus_root / "github_repos"
        if gh_root.is_dir():
            for p in sorted(gh_root.rglob("*.flac"), key=lambda x: str(x).lower()):
                rel_gh = p.relative_to(gh_root)
                repo = rel_gh.parts[0] if len(rel_gh.parts) > 1 else "(root)"
                entries.append(
                    {
                        "suite": "external_github",
                        "group": repo,
                        "subcat": "github_repos",
                        "rel": "github_repos/" + rel_gh.as_posix(),
                        "file": p.name,
                        "absPath": str(p),
                    }
                )

    # 3. SoniqTools CC0 (8 files)
    if subset in ("all", "soniqtools"):
        sq_root = corpus_root / "soniqtools_cc0"
        if sq_root.is_dir():
            for p in sorted(sq_root.iterdir(), key=lambda x: x.name.lower()):
                if p.is_file():
                    expected = (
                        "FAKE_LOSSY"
                        if "mp3" in p.name.lower()
                        else ("FAKE_CD_UPSAMPLE" if "upsampled" in p.name.lower() else "GENUINE")
                    )
                    entries.append(
                        {
                            "suite": "soniqtools",
                            "group": "soniqtools_cc0",
                            "subcat": "soniqtools_cc0",
                            "expected": expected,
                            "rel": f"soniqtools_cc0/{p.name}",
                            "file": p.name,
                            "absPath": str(p),
                        }
                    )

    # 4. Ground-truth FFmpeg matrix (179 files)
    if subset in ("all", "ground_truth", "dev", "holdout"):
        gt_root = corpus_root / "ground_truth_bench"
        manifest_path = gt_root / "manifest.json"
        if manifest_path.is_file():
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            dev_masters = set(split_data.get("dev_masters", [])) if split_data else set()
            holdout_masters = set(split_data.get("holdout_masters", [])) if split_data else set()

            for item in manifest:
                src_base = item["source"].removesuffix("_cd")
                split_tag = (
                    "dev"
                    if src_base in dev_masters
                    else ("holdout" if src_base in holdout_masters else "unassigned")
                )
                if subset == "dev" and split_tag != "dev":
                    continue
                if subset == "holdout" and split_tag != "holdout":
                    continue
                p = gt_root / item["subcat"] / item["file"]
                entries.append(
                    {
                        "suite": "ground_truth",
                        "group": item["subcat"],
                        "subcat": item["subcat"],
                        "expected": item["expected"],
                        "source": item["source"],
                        "sourceMaster": src_base,
                        "split": split_tag,
                        "detail": item.get("detail", ""),
                        "rel": f"ground_truth_bench/{item['subcat']}/{item['file']}",
                        "file": item["file"],
                        "absPath": str(p),
                    }
                )

    return entries


def summarize_external(rows: list[dict[str, Any]]) -> dict[str, dict[str, int]]:
    buckets = ["ietf_subset", "ietf_uncommon", "ietf_faulty", "github_repos"]
    out: dict[str, dict[str, int]] = {}
    for b in buckets:
        sub = [r for r in rows if r.get("subcat") == b]
        if not sub:
            continue
        md5_checked = [r for r in sub if r.get("md5Match") is not None]
        md5_ok = sum(1 for r in md5_checked if r.get("md5Match") is True)
        md5_fail = sum(1 for r in md5_checked if r.get("md5Match") is False)
        out[b] = {
            "total": len(sub),
            "ok": sum(1 for r in sub if r["status"] == "OK"),
            "managed": sum(1 for r in sub if r["status"] == "MANAGED_EXCEPTION"),
            "crash": sum(1 for r in sub if r["status"] == "FATAL_CRASH"),
            "timeout": sum(1 for r in sub if r["status"] == "TIMEOUT"),
            "md5_checked": len(md5_checked),
            "md5_ok": md5_ok,
            "md5_fail": md5_fail,
        }
    return out


def classify_gt_row(row: dict[str, Any]) -> str:
    """Return 'correct', 'mislabel_hybrid', 'abstain', 'false_fake', or 'escape'."""
    sub = row["subcat"]
    v = row.get("verdict") if row["status"] == "OK" else row["status"]

    if sub in ("01_genuine", "01_genuine_low_sr"):
        if v in GENUINE_VERDICTS:
            return "correct"
        if v == "HybridMaster":
            return "mislabel_hybrid"
        if v == "Inconclusive":
            return "abstain"
        if v in FALSE_FAKE_VERDICTS:
            return "false_fake"
        return "escape"

    if sub in ("02_fake_mp3", "03_fake_aac", "04_fake_opus", "05_fake_vorbis"):
        if v == "SuspectedUpscaledLossy":
            return "correct"
        if v == "UpscaledFromCd":
            return "caught_other_fake"
        if v == "Inconclusive":
            return "abstain"
        return "escape"

    if sub == "06_fake_cd_upsample_soxr":
        if v == "UpscaledFromCd":
            return "correct"
        if v == "Inconclusive":
            return "abstain"
        return "escape"

    if sub == "07_fake_cd_upsample_linear":
        if v == "UpscaledFromCd":
            return "correct"
        if v == "Inconclusive":
            return "abstain"
        return "escape"

    if sub == "08_fake_zeropad_24b":
        if v in ("ZeroPadded24Bit", "UpscaledFromCd"):
            return "correct"
        if v == "Inconclusive":
            return "abstain"
        return "escape"

    if sub == "09_fake_dither_24b":
        if v in ("Dithered16BitUpscale", "ZeroPadded24Bit", "UpscaledFromCd"):
            return "correct"
        if v == "Inconclusive":
            return "abstain"
        return "escape"

    if sub == "10_fake_hires_upsample":
        if v in ("UpscaledFromCd", "UpscaledHiRes"):
            return "correct"
        if v == "Inconclusive":
            return "abstain"
        return "escape"

    return "abstain"


def format_report(
    results: list[dict[str, Any]],
    baseline: list[dict[str, Any]] | None = None,
) -> str:
    lines: list[str] = []
    ext_rows = [r for r in results if r["suite"] in ("external_ietf", "external_github")]
    gt_rows = [r for r in results if r["suite"] == "ground_truth"]
    sq_rows = [r for r in results if r["suite"] == "soniqtools"]
    lib_rows = [r for r in results if r["suite"] == "library"]

    base_ext = [r for r in baseline if r["suite"] in ("external_ietf", "external_github")] if baseline else None
    base_gt = [r for r in baseline if r["suite"] == "ground_truth"] if baseline else None

    if ext_rows:
        cur_sum = summarize_external(ext_rows)
        old_sum = summarize_external(base_ext) if base_ext else None
        labels = {
            "ietf_subset": "IETF `subset`",
            "ietf_uncommon": "IETF `uncommon`",
            "ietf_faulty": "IETF `faulty`",
            "github_repos": "17 GitHub Repos",
        }
        lines.append("### A. Decoder Stability Across External GitHub `.flac` Files")
        lines.append("")
        if old_sum:
            lines.append("| Corpus | Total | Decoded OK (Before -> After) | Managed Exception (Before -> After) | Fatal Crash (Before -> After) | Timeout | PCM MD5 Match (Checked) |")
            lines.append("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |")
        else:
            lines.append("| Corpus | Total | Decoded OK | Managed Exception | Fatal Process Crash (`0xC0000005`) | Timeout / Hang | PCM MD5 Match (Checked) |")
            lines.append("| :--- | :---: | :---: | :---: | :---: | :---: | :---: |")

        tot = Counter()
        old_tot = Counter()
        for k in ("ietf_subset", "ietf_uncommon", "ietf_faulty", "github_repos"):
            if k not in cur_sum:
                continue
            c = cur_sum[k]
            for f in c:
                tot[f] += c[f]
            md5_str = f"{c['md5_ok']}/{c['md5_checked']}" + (f" ({c['md5_fail']} mismatch)" if c["md5_fail"] else "")
            if old_sum and k in old_sum:
                o = old_sum[k]
                for f in o:
                    old_tot[f] += o[f]
                lines.append(
                    f"| {labels[k]} | {c['total']} | {o['ok']} -> {c['ok']} | {o['managed']} -> {c['managed']} | {o['crash']} -> {c['crash']} | {c['timeout']} | {md5_str} |"
                )
            else:
                lines.append(
                    f"| {labels[k]} | {c['total']} | {c['ok']} | {c['managed']} | **{c['crash']}** | {c['timeout']} | {md5_str} |"
                )
        md5_tot_str = f"{tot['md5_ok']}/{tot['md5_checked']}" + (f" ({tot['md5_fail']} mismatch)" if tot["md5_fail"] else "")
        if old_sum:
            lines.append(
                f"| **Total External FLACs** | **{tot['total']}** | **{old_tot['ok']} -> {tot['ok']}** | **{old_tot['managed']} -> {tot['managed']}** | **{old_tot['crash']} -> {tot['crash']}** | **{tot['timeout']}** | **{md5_tot_str}** |"
            )
        else:
            lines.append(
                f"| **Total External FLACs** | **{tot['total']}** | **{tot['ok']}** | **{tot['managed']}** | **{tot['crash']}** | **{tot['timeout']}** | **{md5_tot_str}** |"
            )
        lines.append("")

    if gt_rows:
        lines.append("### B. Ground-Truth Confusion Matrix")
        lines.append("")
        subcats = sorted({r["subcat"] for r in gt_rows})
        if base_gt:
            lines.append("| Subcategory | Total | Verdict Breakdown (Before) | Verdict Breakdown (After) | False-Fake (Before -> After) |")
            lines.append("| :--- | :---: | :--- | :--- | :---: |")
        else:
            lines.append("| Subcategory | Total | Correctly Flagged / Classified | `Inconclusive` (Abstain) | Escaped / Misclassified | PCM MD5 Match |")
            lines.append("| :--- | :---: | :--- | :---: | :--- | :---: |")

        for sub in subcats:
            cur_sub = [r for r in gt_rows if r["subcat"] == sub]
            cur_v = Counter(r.get("verdict") if r["status"] == "OK" else r["status"] for r in cur_sub)
            cur_cls = Counter(classify_gt_row(r) for r in cur_sub)
            md5_chk = [r for r in cur_sub if r.get("md5Match") is not None]
            md5_ok = sum(1 for r in md5_chk if r.get("md5Match") is True)
            md5_cell = f"{md5_ok}/{len(md5_chk)}"

            if base_gt:
                old_sub = [r for r in base_gt if r["subcat"] == sub]
                old_v = Counter(r.get("verdict") if r["status"] == "OK" else r["status"] for r in old_sub)
                old_ff = sum(1 for r in old_sub if classify_gt_row(r) == "false_fake")
                cur_ff = cur_cls["false_fake"]
                lines.append(
                    f"| `{sub}` | {len(cur_sub)} | {dict(old_v)} | {dict(cur_v)} | {old_ff} -> {cur_ff} |"
                )
            else:
                corr_parts = []
                esc_parts = []
                if sub in ("01_genuine", "01_genuine_low_sr"):
                    for k in ("VerifiedLossless", "AcousticNaturalRollOff"):
                        if cur_v[k]:
                            corr_parts.append(f"{cur_v[k]} {k}")
                    if cur_v["HybridMaster"]:
                        corr_parts.append(f"+ {cur_v['HybridMaster']} mislabel HybridMaster")
                    ff = cur_cls["false_fake"]
                    esc_parts.append(f"{ff} False-Fake")
                else:
                    for k, cnt in sorted(cur_v.items()):
                        if k == "Inconclusive":
                            continue
                        if (sub.startswith(("02_", "03_", "04_", "05_")) and k in ("SuspectedUpscaledLossy", "UpscaledFromCd")) or (
                            sub in ("06_fake_cd_upsample_soxr", "07_fake_cd_upsample_linear") and k == "UpscaledFromCd"
                        ) or (
                            sub in ("08_fake_zeropad_24b", "09_fake_dither_24b") and k in ("ZeroPadded24Bit", "Dithered16BitUpscale", "UpscaledFromCd")
                        ) or (
                            sub == "10_fake_hires_upsample" and k in ("UpscaledFromCd", "UpscaledHiRes")
                        ):
                            corr_parts.append(f"{cnt} {k}")
                        else:
                            esc_parts.append(f"{cnt} {k}")
                lines.append(
                    f"| `{sub}` | {len(cur_sub)} | {', '.join(corr_parts) or '0'} | {cur_v['Inconclusive']} | {', '.join(esc_parts) or '0'} | {md5_cell} |"
                )
        lines.append("")

    if sq_rows:
        lines.append("### C. SoniqTools CC0 Benchmark (8 Files)")
        lines.append("")
        lines.append("| File | Expected | Verdict | Status | PCM MD5 Match |")
        lines.append("| :--- | :---: | :--- | :---: | :---: |")
        for r in sq_rows:
            md5_s = "N/A" if r.get("md5Match") is None else ("MATCH" if r["md5Match"] else "MISMATCH")
            lines.append(f"| `{r['file']}` | `{r.get('expected')}` | `{r.get('verdict', r['status'])}` | {r['status']} | {md5_s} |")
        lines.append("")

    if lib_rows:
        lines.append(f"### Library Reality Check ({len(lib_rows)} Files)")
        lines.append("")
        c = Counter(r.get("verdict") if r["status"] == "OK" else r["status"] for r in lib_rows)
        md5_chk = [r for r in lib_rows if r.get("md5Match") is not None]
        md5_ok = sum(1 for r in md5_chk if r.get("md5Match") is True)
        lines.append(f"- Outcomes: `{dict(c)}`")
        lines.append(f"- Fatal crashes: `{sum(1 for r in lib_rows if r['status'] == 'FATAL_CRASH')}`, Timeouts: `{sum(1 for r in lib_rows if r['status'] == 'TIMEOUT')}`")
        lines.append(f"- PCM MD5 Match: `{md5_ok}/{len(md5_chk)}`")
        lines.append("")

    return "\n".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description="Crash-isolated benchmark harness for AuraAudio.")
    parser.add_argument("--corpus", default=os.environ.get("AURA_BENCHMARK_CORPUS", ""), help="Path to AuraAudio_benchmark_corpus (or set AURA_BENCHMARK_CORPUS)")
    parser.add_argument("--subset", default="all", choices=["all", "external", "ground_truth", "soniqtools", "dev", "holdout", "library"])
    parser.add_argument("--split", default="", help="Path to split.json for dev/holdout filtering")
    parser.add_argument("--library-list", default="", help="Path to newline-separated audio paths for --subset library")
    parser.add_argument("--out", default="", help="Write full JSON output to this path")
    parser.add_argument("--report", default="", help="Write Markdown summary table to this path")
    parser.add_argument("--compare", default="", help="Baseline JSON to render Before -> After columns")
    parser.add_argument("--workers", type=int, default=6, help="Parallel child-process workers")
    parser.add_argument("--timeout", type=float, default=25.0, help="Per-file timeout in seconds")
    parser.add_argument("--no-build", action="store_true", help="Skip building BenchmarkWorker before run")
    args = parser.parse_args()

    if args.subset != "library" and not args.corpus:
        parser.error("Provide --corpus <path> or set AURA_BENCHMARK_CORPUS.")

    corpus_root = Path(args.corpus) if args.corpus else Path(".")
    split_data = json.loads(Path(args.split).read_text(encoding="utf-8")) if args.split else None
    lib_list = Path(args.library_list) if args.library_list else None

    if not args.no_build:
        build_worker()

    entries = collect_corpus_entries(corpus_root, args.subset, split_data, lib_list)
    if not entries:
        raise RuntimeError(f"No benchmark files discovered for subset={args.subset} under {corpus_root}")

    t0 = time.perf_counter()
    results: list[dict[str, Any]] = []
    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as pool:
        futs = {pool.submit(run_worker_single, e, args.timeout): e for e in entries}
        for fut in as_completed(futs):
            res = fut.result()
            # Strip absolute host path before writing repo/portable JSON unless library subset
            if res.get("suite") != "library":
                res.pop("absPath", None)
            results.append(res)

    results.sort(key=lambda r: (r["suite"], r["subcat"], r["rel"]))
    elapsed = time.perf_counter() - t0

    baseline_rows = None
    if args.compare:
        base_doc = json.loads(Path(args.compare).read_text(encoding="utf-8"))
        baseline_rows = base_doc["results"] if isinstance(base_doc, dict) and "results" in base_doc else base_doc

    report_md = format_report(results, baseline_rows)
    print(f"Benchmark complete: {len(results)} files in {elapsed:.1f}s ({args.workers} workers, timeout={args.timeout}s)\n")
    print(report_md)

    if args.out:
        out_path = Path(args.out)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "meta": {
                "subset": args.subset,
                "count": len(results),
                "elapsedSec": round(elapsed, 2),
                "timeoutSec": args.timeout,
            },
            "results": results,
        }
        out_path.write_text(json.dumps(payload, indent=2), encoding="utf-8")

    if args.report:
        rep_path = Path(args.report)
        rep_path.parent.mkdir(parents=True, exist_ok=True)
        rep_path.write_text(report_md, encoding="utf-8")


if __name__ == "__main__":
    main()
