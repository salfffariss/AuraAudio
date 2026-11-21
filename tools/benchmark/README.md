# AuraAudio Crash-Isolated Benchmark Harness

Runs every audio file in a benchmark corpus inside an isolated child process (`AuraAudio.BenchmarkWorker.exe`) with a hard timeout, recording:
- Decode outcome (`OK`, `MANAGED_EXCEPTION`, `FATAL_CRASH` with exit code, or `TIMEOUT`)
- Decode and analysis timings (`decodeMs`, `analyzeMs`)
- Decoded PCM MD5 verification against FLAC `STREAMINFO` MD5 (when non-zero)
- Full `NyquistSpectrumAnalyzer` verdict and forensic telemetry (`knee`, `envReach`, `slopeCd`, `kneeSteep`, `dropKnee`, `cliff`, `noise`, `lsb`, `imgR`, etc.)

No third-party audio files or host-specific paths are stored in this folder. Pass the corpus path via `--corpus` or the `AURA_BENCHMARK_CORPUS` environment variable.

## Usage

```powershell
# Full 450-file corpus
python tools/benchmark/run_benchmark.py --corpus "..\AuraAudio_benchmark_corpus" --split "..\AuraAudio_benchmark_corpus\split.json" --out "..\AuraAudio_benchmark_corpus\results\baseline_v2_0_1.json"

# DEV split only (during Phase B tuning)
python tools/benchmark/run_benchmark.py --corpus "..\AuraAudio_benchmark_corpus" --split "..\AuraAudio_benchmark_corpus\split.json" --subset dev --compare "..\AuraAudio_benchmark_corpus\results\baseline_v2_0_1.json"

# HOLDOUT split (evaluated once at end of Phase B)
python tools/benchmark/run_benchmark.py --corpus "..\AuraAudio_benchmark_corpus" --split "..\AuraAudio_benchmark_corpus\split.json" --subset holdout --compare "..\AuraAudio_benchmark_corpus\results\baseline_v2_0_1.json"
```
