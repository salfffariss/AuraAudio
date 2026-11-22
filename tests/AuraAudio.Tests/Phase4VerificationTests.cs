using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Metadata;
using AuraAudio.UI.Controls;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;
using AuraAudio.UI.Views;
using AuraAudio.UI.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Path = System.IO.Path;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class Phase4VerificationTests
{
    private readonly ITestOutputHelper _output;

    public Phase4VerificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    internal static readonly string[] All10CoverNames =
    [
        "white",
        "black",
        "red",
        "pastel",
        "grayscale",
        "noisy",
        "missing",
        "teal_orange",
        "warm_sunset",
        "dark_neon"
    ];

    internal static readonly BackdropTuningPreset[] All3Presets =
    [
        ArtworkPaletteExtractor.SafeDefault,
        ArtworkPaletteExtractor.BalancedVivid,
        ArtworkPaletteExtractor.AppleMusicSpatial
    ];

    private static readonly double[] DriftPhasesSeconds = [0.0, 6.0, 12.0, 18.0, 24.0, 30.0];

    private const string SampleVisualLrc = """
        [au:AuraAudio Synthetic Test Suite]
        [00:02.00]Lewat sudah
        [00:05.00]Pukul dua
        [00:08.50]Makin banyak bicara kita
        [00:15.50]Kau semakin merah delima
        [00:20.00]次 とその次 とその次 と線を引き続けた
        [00:26.00]次の目的地 を目的地 を描くんだ
        [00:31.00]宝島
        """;

    internal static WriteableBitmap? CreatePhase4Cover(string name, int size = 256)
    {
        if (name is "white" or "black" or "red" or "pastel" or "grayscale" or "noisy" or "missing")
        {
            return ImmersiveBackdropTests.CreateSyntheticCover(name, size);
        }

        var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[size * size * 4];

        for (int y = 0; y < size; y++)
        {
            double ny = (double)y / Math.Max(1, size - 1);
            for (int x = 0; x < size; x++)
            {
                double nx = (double)x / Math.Max(1, size - 1);
                byte r, g, b;

                switch (name)
                {
                    case "teal_orange":
                    {
                        // Cinema poster aesthetic: deep cyan-teal sky/water with warm amber-orange sunburst
                        // and dark geometric urban horizon + thin light inner poster frame
                        double diag = Math.Clamp((nx * 0.65) + ((1.0 - ny) * 0.35), 0.0, 1.0);
                        double sunDx = nx - 0.72;
                        double sunDy = ny - 0.68;
                        double sunDist = Math.Sqrt((sunDx * sunDx) + (sunDy * sunDy));
                        double sunGlow = Math.Exp(-sunDist * sunDist * 8.5);

                        double baseR = Lerp(8.0, 24.0, diag) + (sunGlow * 235.0);
                        double baseG = Lerp(92.0, 158.0, 1.0 - ny * 0.65) + (sunGlow * 105.0);
                        double baseB = Lerp(112.0, 168.0, 1.0 - nx * 0.55) - (sunGlow * 85.0);

                        // Stylized dark skyline bands at bottom third
                        double wave = 0.72 + (0.06 * Math.Sin(nx * 19.0)) + (0.04 * Math.Cos(nx * 37.0));
                        if (ny > wave)
                        {
                            baseR *= 0.28;
                            baseG *= 0.30;
                            baseB *= 0.34;
                        }

                        // Thin framed white-ish border like classic EP artwork
                        bool border = (nx > 0.08 && nx < 0.92 && ny > 0.08 && ny < 0.92) &&
                                      (nx < 0.105 || nx > 0.895 || ny < 0.105 || ny > 0.895);
                        if (border)
                        {
                            baseR = 235.0;
                            baseG = 242.0;
                            baseB = 240.0;
                        }

                        r = ClampByte(baseR);
                        g = ClampByte(baseG);
                        b = ClampByte(baseB);
                        break;
                    }
                    case "warm_sunset":
                    {
                        // Golden-hour sunset: deep violet zenith -> rose-crimson -> coral-orange -> gold horizon -> dark sea reflection
                        double rVal, gVal, bVal;
                        if (ny < 0.62)
                        {
                            double t = ny / 0.62;
                            if (t < 0.38)
                            {
                                double u = t / 0.38;
                                rVal = Lerp(42.0, 168.0, u);
                                gVal = Lerp(20.0, 52.0, u);
                                bVal = Lerp(86.0, 108.0, u);
                            }
                            else if (t < 0.74)
                            {
                                double u = (t - 0.38) / 0.36;
                                rVal = Lerp(168.0, 246.0, u);
                                gVal = Lerp(52.0, 128.0, u);
                                bVal = Lerp(108.0, 62.0, u);
                            }
                            else
                            {
                                double u = (t - 0.74) / 0.26;
                                rVal = Lerp(246.0, 255.0, u);
                                gVal = Lerp(128.0, 206.0, u);
                                bVal = Lerp(62.0, 102.0, u);
                            }

                            // Glowing solar disc near center-horizon
                            double dx = nx - 0.50;
                            double dy = ny - 0.52;
                            double d = Math.Sqrt((dx * dx) + (dy * dy));
                            double disc = Math.Exp(-d * d * 48.0);
                            rVal += disc * 70.0;
                            gVal += disc * 95.0;
                            bVal += disc * 110.0;
                        }
                        else
                        {
                            // Ocean water with warm shimmer reflections
                            double u = (ny - 0.62) / 0.38;
                            double ripple = 0.5 + (0.5 * Math.Sin((ny * 95.0) + Math.Sin(nx * 24.0)));
                            double colGlow = Math.Exp(-Math.Pow((nx - 0.50) * 4.5, 2.0)) * (1.0 - (u * 0.55));
                            rVal = Lerp(68.0, 18.0, u) + (colGlow * (135.0 + (60.0 * ripple)));
                            gVal = Lerp(26.0, 12.0, u) + (colGlow * (78.0 + (45.0 * ripple)));
                            bVal = Lerp(56.0, 32.0, u) + (colGlow * (30.0 + (18.0 * ripple)));
                        }

                        r = ClampByte(rVal);
                        g = ClampByte(gVal);
                        b = ClampByte(bVal);
                        break;
                    }
                    case "dark_neon":
                    {
                        // Midnight synthwave: deep navy-black canvas with vivid magenta & electric cyan beams + neon ring
                        double rVal = 10.0 + (18.0 * ny);
                        double gVal = 8.0 + (10.0 * (1.0 - ny));
                        double bVal = 26.0 + (34.0 * (1.0 - Math.Abs(nx - 0.5)));

                        // Diagonal electric magenta laser band
                        double dMag = Math.Abs((ny - (0.78 - (0.55 * nx))));
                        double magBeam = Math.Exp(-dMag * dMag * 85.0);
                        rVal += magBeam * 235.0;
                        gVal += magBeam * 32.0;
                        bVal += magBeam * 178.0;

                        // Counter-diagonal cyan neon band
                        double dCyan = Math.Abs((ny - (0.24 + (0.50 * nx))));
                        double cyanBeam = Math.Exp(-dCyan * dCyan * 90.0);
                        rVal += cyanBeam * 16.0;
                        gVal += cyanBeam * 218.0;
                        bVal += cyanBeam * 245.0;

                        // Glowing neon ring in center
                        double dx = nx - 0.50;
                        double dy = ny - 0.48;
                        double rad = Math.Sqrt((dx * dx) + (dy * dy));
                        double ring = Math.Exp(-Math.Pow((rad - 0.24) * 26.0, 2.0));
                        rVal += ring * 185.0;
                        gVal += ring * 95.0;
                        bVal += ring * 250.0;

                        r = ClampByte(rVal);
                        g = ClampByte(gVal);
                        b = ClampByte(bVal);
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown Phase 4 cover name.");
                }

                int idx = ((y * size) + x) * 4;
                pixels[idx] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 255;
            }
        }

        wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        wb.Freeze();
        return wb;
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * Math.Clamp(t, 0.0, 1.0));
    private static byte ClampByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static string Build123LineLrc()
    {
        var sb = new StringBuilder(6000);
        sb.AppendLine("[au:Phase 4 Combined Probe]");
        double t = 1.0;
        for (int i = 1; i <= 120; i++)
        {
            int mins = (int)(t / 60.0);
            double secs = t - (mins * 60.0);
            sb.AppendFormat(CultureInfo.InvariantCulture, "[{0:D2}:{1:00.00}]", mins, secs);
            sb.AppendLine((i % 6) switch
            {
                0 => $"Line {i:D3}: 次 とその次 とその次 と線を引き続けた",
                1 => $"Line {i:D3}: Lewat sudah pukul dua makin banyak bicara kita",
                2 => $"Line {i:D3}: Wrapping line test across compact right pane and full screen stage",
                3 => $"Line {i:D3}: 次の目的地 を目的地 を描くんだ",
                4 => $"Line {i:D3}: Kau semakin merah delima merayakan tahun kesekian",
                _ => $"Line {i:D3}: Short vocal anchor"
            });

            // 3 instrumental gaps >= 5 s -> 120 vocal + 3 gap markers = 123 lines
            t += (i == 15 || i == 45 || i == 85) ? 7.0 : 2.1;
        }
        return sb.ToString();
    }

    // =========================================================================
    // FAST ALWAYS-ON GATE 1: ADJUSTMENT 3 (C2) NEUTRALITY & ISOLATION PIXEL AUDIT
    // =========================================================================

    [Fact]
    public void NeutralityAudit_C2_ForegroundOverNeutralBackdropAndIsolation_ZeroChromaticPixels()
    {
        var summary = ExecuteNeutralityAuditC2();
        _output.WriteLine(summary.Report);

        Assert.Equal(0, summary.FullScreenSuppressedChromaticPixels);
        Assert.Equal(0, summary.MiniPlayerSuppressedChromaticPixels);
        Assert.Equal(0, summary.MainShellChromaticPixels);
        Assert.Equal(0, summary.NonNeutralBrushesOutsideBackdrop);
        Assert.Equal(0, summary.ChromaticPixelsOutsideBackdropLayer);
        Assert.True(summary.FullScreenActiveBackdropChromaticPixels > 1000, "Expected colored pixels inside active FullScreenBackdrop for saturated red cover.");
        Assert.True(summary.MiniAlbumViewBackdropCollapsed, "MiniPlayer AlbumView must collapse StageBackdropControl.");
    }

    // =========================================================================
    // FAST ALWAYS-ON GATE 2: PER-ZONE CONTRAST AUDIT ACROSS ALL 10 COVERS x 3 PRESETS x 6 PHASES
    // =========================================================================

    [Fact]
    public void ContrastAudit_All10Covers_All3Presets_PerZoneAcrossAnimationPhases_PassHardGates()
    {
        WpfStaTestRunner.Run(() =>
        {
            // Confirm AppleMusicSpatial is the active default preset (SafeDefault & BalancedVivid preserved as constants)
            var freshBackdrop = new ImmersiveBackdropControl();
            Assert.Equal(ArtworkPaletteExtractor.AppleMusicSpatial, freshBackdrop.TuningPreset);
            Assert.Equal(ArtworkPaletteExtractor.ActiveDefault, freshBackdrop.TuningPreset);

            var results = RunPerZoneContrastMatrix(captureScreenshots: false, outDir: null);
            var failures = results.Where(r => !r.AllGatesPassed).ToList();
            if (failures.Count > 0)
            {
                string summary = string.Join(Environment.NewLine, failures.Select(f =>
                    $"{f.Surface}/{f.Cover}/{f.Preset}: OpenY={f.OpenAmbientY:F4} TextY={f.WorstTextZoneY:F4} -> {f.FailureDetails}"));
                Assert.Fail($"Contrast gate failures ({failures.Count}/{results.Count}):{Environment.NewLine}{summary}");
            }

            // Enforce >= +0.30 margin over 3.0:1 (>= 3.30:1) on unfilled interlude dots across all 10 covers and both surfaces under AppleMusicSpatial
            var spatialRows = results.Where(r => r.Preset == ArtworkPaletteExtractor.AppleMusicSpatial.Name).ToList();
            Assert.Equal(All10CoverNames.Length * 2, spatialRows.Count);
            foreach (var sr in spatialRows)
            {
                _output.WriteLine($"  [{sr.Surface,-10}] {sr.Cover,-11} | OpenY={sr.OpenAmbientY:F4} TextY={sr.WorstTextZoneY:F4} | Tert={sr.MinTertiaryCr:F2}({sr.MinTertiaryCr - 4.5:+0.00}) D1={sr.MinLyricD1Cr:F2}({sr.MinLyricD1Cr - 4.5:+0.00}) Dot={sr.MinDotCr:F2}({sr.MinDotCr - 3.0:+0.00})");
                Assert.True(
                    sr.MinDotCr >= 3.30,
                    $"Unfilled dot margin < +0.30 on {sr.Surface}/{sr.Cover}/{sr.Preset}: Dot={sr.MinDotCr:F2}:1 (< 3.30:1)");
            }

            double minSpatialDot = spatialRows.Min(r => r.MinDotCr);
            double minSpatialTert = spatialRows.Min(r => r.MinTertiaryCr);
            _output.WriteLine(
                $"Verified {results.Count} surface/cover/preset combinations across {DriftPhasesSeconds.Length} drift phases: 100% PASS. " +
                $"AppleMusicSpatial worst Dot={minSpatialDot:F2}:1 ({minSpatialDot - 3.0:+0.00}), worst Tert={minSpatialTert:F2}:1 ({minSpatialTert - 4.5:+0.00}).");
        });
    }

    // =========================================================================
    // FAST ALWAYS-ON GATE 3: COMBINED RULE 5 AUTO-FALLBACK
    // =========================================================================

    [Fact]
    public void CombinedRule5AutoFallback_FreezesBackdropWhileLyricsContinueSmoothly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_Phase4_Fallback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool prevExt = SpringAnimator.UseExternalClockForTest;
                bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
                bool? prevBatt = PowerStatusHelper.ForceBatterySaverOverrideForTest;
                SpringAnimator.UseExternalClockForTest = true;
                SpringAnimator.ForceReducedMotionOverride = false;
                PowerStatusHelper.ForceBatterySaverOverrideForTest = false;

                using var db = new DatabaseManager(Path.Combine(tempDir, "fallback.db"));
                db.Initialize();
                using var vm = new MainViewModel(db: db);
                var win = new MainWindow(vm)
                {
                    Width = 1536,
                    Height = 860,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false
                };

                try
                {
                    win.Show();
                    var redBmp = CreatePhase4Cover("red", 128)!;
                    CoverArtLoader.RegisterSyntheticCoverForTest("p4_fallback_red", redBmp);
                    var palette = ArtworkPaletteExtractor.ExtractFromBitmapSource(redBmp, ArtworkPaletteExtractor.ActiveDefault);

                    vm.CurrentTrack = MakeTrack(tempDir, "p4_fallback_red", "Fallback Test", hasCover: true);
                    vm.IsPlaying = true;
                    vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(Build123LineLrc(), LyricsSourceKind.SidecarLrc));
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(10.0), isPlaying: true);
                    vm.Presentation.IsFullScreenLyricsOpen = true;
                    vm.Presentation.IsFullScreenOpen = true;
                    win.UpdateLayout();

                    var fsView = (FullScreenPlayerView)win.FindName("FullScreenStageOverlay");
                    var viewport = FindFirstVisualChild<SyncedLyricsViewport>(fsView)!;
                    fsView.BackdropControl.ApplyPaletteImmediatelyForTest(palette);
                    win.UpdateLayout();

                    Assert.True(fsView.BackdropControl.IsDriftAnimating);
                    Assert.True(fsView.BackdropControl.IsTickerRegisteredForTest);

                    // Simultaneous lyric scroll + backdrop drift before fallback
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(14.5), isPlaying: true);
                    double driftBefore = fsView.BackdropControl.DriftTimeSecondsForTest;
                    double scrollBefore = viewport.CurrentScrollOffset;
                    for (int i = 0; i < 40; i++)
                    {
                        SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                    }
                    Assert.True(fsView.BackdropControl.DriftTimeSecondsForTest > driftBefore + 0.15);
                    Assert.NotEqual(scrollBefore, viewport.CurrentScrollOffset);

                    // Feed two consecutive slow 2.0 s windows (dt = 15.0 ms > 1.5x * 6.06 ms) -> Rule 5 auto-fallback triggers
                    for (int i = 0; i < 280; i++)
                    {
                        fsView.BackdropControl.RecordFrameDtForFallbackTest(dtMs: 15.0, vsyncMs: 6.06);
                    }
                    Assert.True(fsView.BackdropControl.IsAutoFallbackFrozen);
                    Assert.False(fsView.BackdropControl.IsDriftAnimating);
                    Assert.False(fsView.BackdropControl.IsTickerRegisteredForTest);

                    // Verify backdrop stays frozen while lyrics keep scrolling smoothly with 0 LayoutUpdated passes
                    double frozenDrift = fsView.BackdropControl.DriftTimeSecondsForTest;
                    double scrollAfterFallbackStart = viewport.CurrentScrollOffset;
                    int layoutPasses = 0;
                    EventHandler onLayout = (_, _) => layoutPasses++;
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(19.0), isPlaying: true);
                    win.UpdateLayout();

                    viewport.LayoutUpdated += onLayout;
                    try
                    {
                        for (int i = 0; i < 60; i++)
                        {
                            SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                            win.UpdateLayout();
                        }
                    }
                    finally
                    {
                        viewport.LayoutUpdated -= onLayout;
                    }

                    Assert.Equal(frozenDrift, fsView.BackdropControl.DriftTimeSecondsForTest, precision: 6);
                    Assert.NotEqual(scrollAfterFallbackStart, viewport.CurrentScrollOffset);
                    Assert.Equal(0, layoutPasses);
                }
                finally
                {
                    CoverArtLoader.UnregisterSyntheticCoverForTest("p4_fallback_red");
                    win.Close();
                    SpringAnimator.UseExternalClockForTest = prevExt;
                    SpringAnimator.ForceReducedMotionOverride = prevRm;
                    PowerStatusHelper.ForceBatterySaverOverrideForTest = prevBatt;
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // OPT-IN FULL PHASE 4 AUDIT & VISUAL TUNING REPORT RUNNER
    // =========================================================================

    [SkippableFact]
    public void RunPhase4FullAudit_RealtimeCombinedCostNeutralityAndTuningReport()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("AURA_RUN_PHASE4_AUDIT") == "1",
            "Opt-in Phase 4 full audit & visual tuning report (set AURA_RUN_PHASE4_AUDIT=1).");

        string outDir = Environment.GetEnvironmentVariable("AURA_PHASE4_OUTDIR")
            ?? Path.Combine(Path.GetTempPath(), "AuraAudio_Phase4_Audit");
        Directory.CreateDirectory(outDir);

        var masterReport = new StringBuilder(32768);
        masterReport.AppendLine("=== AuraAudio v2.1 Phase 4 Verification & Visual Tuning Audit ===");
        masterReport.AppendLine($"Timestamp   : {DateTimeOffset.Now:O}");
        masterReport.AppendLine($"RenderTier  : {RenderCapability.Tier >> 16}");

        var disp = QueryDisplayMetrics();
        masterReport.AppendLine($"Display     : {disp.Width}x{disp.Height} @ {disp.RefreshHz:F1} Hz (vsync = {disp.VsyncMs:F2} ms, 1.5x = {disp.VsyncMs * 1.5:F2} ms, 3.0x = {disp.VsyncMs * 3.0:F2} ms)");
        masterReport.AppendLine();

        // ---------------------------------------------------------------------
        // 1. REAL-TIME FOREGROUND DWM PRESENTATION MEASUREMENT
        // ---------------------------------------------------------------------
        masterReport.AppendLine("--- 1. REAL-TIME PRESENTATION MEASUREMENT (FOREGROUND + DWM TIMING) ---");
        string rtReport = RunRealtimeDwmPresentationProbe(disp.VsyncMs);
        masterReport.AppendLine(rtReport);
        masterReport.AppendLine();

        // Reclaim closed Section 1 test windows before Section 2 deterministic timing
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // ---------------------------------------------------------------------
        // 2. COMBINED DETERMINISTIC 165 Hz PROBE (3 HOSTS, 123 LINES + ANIMATING BACKDROP)
        // ---------------------------------------------------------------------
        masterReport.AppendLine("--- 2. COMBINED DETERMINISTIC 165 Hz PROBE (123 LINES + ANIMATING BACKDROP) ---");
        string combinedReport = RunCombinedDeterministicProbe(disp.RefreshHz, disp.VsyncMs);
        masterReport.AppendLine(combinedReport);
        masterReport.AppendLine();

        // ---------------------------------------------------------------------
        // 3. COST MEASUREMENT: 10s CPU (STATIC vs ANIMATING), 300f ALLOCS, GPU COUNTERS
        // ---------------------------------------------------------------------
        masterReport.AppendLine("--- 3. CPU (10 s STATIC vs ANIMATING), 300-FRAME ALLOCS & GPU COST ---");
        string costReport = RunCpuGpuAndAllocationCostBenchmark(disp.RefreshHz);
        masterReport.AppendLine(costReport);
        masterReport.AppendLine();

        // ---------------------------------------------------------------------
        // 4. NEUTRALITY AUDIT (C2)
        // ---------------------------------------------------------------------
        masterReport.AppendLine("--- 4. NEUTRALITY AUDIT (ADJUSTMENT 3 / C2) ---");
        var c2 = ExecuteNeutralityAuditC2();
        masterReport.AppendLine(c2.Report);
        masterReport.AppendLine();

        // ---------------------------------------------------------------------
        // 5 & 6. PER-ZONE CONTRAST AUDIT + VISUAL TUNING REPORT (10 COVERS x 3 PRESETS) + CONTACT SHEETS
        // ---------------------------------------------------------------------
        masterReport.AppendLine("--- 5 & 6. PER-ZONE CONTRAST AUDIT & VISUAL TUNING REPORT (10 COVERS x 3 PRESETS) ---");
        string shotsDir = Path.Combine(outDir, "shots");
        Directory.CreateDirectory(shotsDir);

        List<ZoneContrastMatrixRow> rows = [];
        WpfStaTestRunner.Run(() =>
        {
            rows = RunPerZoneContrastMatrix(captureScreenshots: true, outDir: shotsDir);
            BuildContactSheet(
                title: "AuraAudio v2.1 Full-Screen Split Backdrop Tuning Comparison (10 Covers x 3 Presets)",
                surfacePrefix: "fs",
                rows: rows.Where(r => r.Surface == "FullScreen").ToList(),
                shotsDir: shotsDir,
                outputPath: Path.Combine(outDir, "contact_sheet_fullscreen.png"),
                cellWidth: 480,
                cellHeight: 270);

            BuildContactSheet(
                title: "AuraAudio v2.1 Mini-Player + Lyrics Tray Backdrop Tuning Comparison (10 Covers x 3 Presets)",
                surfacePrefix: "mini",
                rows: rows.Where(r => r.Surface == "MiniPlayer").ToList(),
                shotsDir: shotsDir,
                outputPath: Path.Combine(outDir, "contact_sheet_miniplayer.png"),
                cellWidth: 320,
                cellHeight: 386);
        });

        var tuningCsv = new StringBuilder(16384);
        tuningCsv.AppendLine("Surface,Cover,Preset,BaseRGB,Blob1RGB,Blob2RGB,Blob3RGB,AmbientOpenY,WorstTextY,PeakCR,PrimaryCR,SecondaryCR,TertiaryCR,TertiaryHeadroom,LyricD1CR,LyricD1Headroom,LyricD2CR,LyricD3CR,DotCR,DotHeadroom,AllPassed");

        foreach (var r in rows)
        {
            Assert.True(r.AllGatesPassed, $"Phase 4 contrast gate failure: {r.Surface}/{r.Cover}/{r.Preset}: {r.FailureDetails}");
            masterReport.AppendLine(
                $"  [{r.Surface,-10}] {r.Cover,-11} | {r.Preset,-17} | " +
                $"Base=({r.Palette.BaseColor.R,3},{r.Palette.BaseColor.G,3},{r.Palette.BaseColor.B,3}) " +
                $"B1=({r.Palette.Blob1Color.R,3},{r.Palette.Blob1Color.G,3},{r.Palette.Blob1Color.B,3}) " +
                $"B2=({r.Palette.Blob2Color.R,3},{r.Palette.Blob2Color.G,3},{r.Palette.Blob2Color.B,3}) " +
                $"B3=({r.Palette.Blob3Color.R,3},{r.Palette.Blob3Color.G,3},{r.Palette.Blob3Color.B,3}) | " +
                $"OpenY={r.OpenAmbientY:F4} TextY={r.WorstTextZoneY:F4} | " +
                $"Peak={r.MinPeakCr:F2} Pri={r.MinPrimaryCr:F2} Sec={r.MinSecondaryCr:F2} " +
                $"Tert={r.MinTertiaryCr:F2}({r.MinTertiaryCr - 4.5:+0.00;-0.00}) " +
                $"D1={r.MinLyricD1Cr:F2}({r.MinLyricD1Cr - 4.5:+0.00;-0.00}) " +
                $"D2={r.MinLyricD2Cr:F2} D3={r.MinLyricD3Cr:F2} " +
                $"Dot={r.MinDotCr:F2}({r.MinDotCr - 3.0:+0.00;-0.00}) | PASS");

            tuningCsv.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1},{2},\"({3},{4},{5})\",\"({6},{7},{8})\",\"({9},{10},{11})\",\"({12},{13},{14})\",{15:F4},{16:F4},{17:F2},{18:F2},{19:F2},{20:F2},{21:F2},{22:F2},{23:F2},{24:F2},{25:F2},{26:F2},{27:F2},{28}",
                r.Surface, r.Cover, r.Preset,
                r.Palette.BaseColor.R, r.Palette.BaseColor.G, r.Palette.BaseColor.B,
                r.Palette.Blob1Color.R, r.Palette.Blob1Color.G, r.Palette.Blob1Color.B,
                r.Palette.Blob2Color.R, r.Palette.Blob2Color.G, r.Palette.Blob2Color.B,
                r.Palette.Blob3Color.R, r.Palette.Blob3Color.G, r.Palette.Blob3Color.B,
                r.OpenAmbientY, r.WorstTextZoneY,
                r.MinPeakCr, r.MinPrimaryCr, r.MinSecondaryCr, r.MinTertiaryCr, r.MinTertiaryCr - 4.5,
                r.MinLyricD1Cr, r.MinLyricD1Cr - 4.5,
                r.MinLyricD2Cr, r.MinLyricD3Cr, r.MinDotCr, r.MinDotCr - 3.0,
                r.AllGatesPassed));
        }

        File.WriteAllText(Path.Combine(outDir, "phase4_master_report.txt"), masterReport.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "phase4_tuning_matrix.csv"), tuningCsv.ToString(), new UTF8Encoding(false));
        _output.WriteLine(masterReport.ToString());
    }

    // =========================================================================
    // IMPLEMENTATION: SECTION 1 (REAL-TIME FOREGROUND + DWM PRESENTATION PROBE)
    // =========================================================================

    private string RunRealtimeDwmPresentationProbe(double vsyncMs)
    {
        var sb = new StringBuilder(4096);
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_Phase4_RT_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

            // Direct DwmFlush cadence sanity check (60 consecutive vsync flushes)
            var flushDts = new List<double>(60);
            int dwmFlushHr = DwmFlush();
            long prevFlush = Stopwatch.GetTimestamp();
            for (int i = 0; i < 60 && dwmFlushHr == 0; i++)
            {
                dwmFlushHr = DwmFlush();
                long now = Stopwatch.GetTimestamp();
                flushDts.Add((now - prevFlush) * 1000.0 / Stopwatch.Frequency);
                prevFlush = now;
            }
            if (flushDts.Count > 0)
            {
                flushDts.Sort();
                sb.AppendLine($"  DwmFlush() 60-tick probe: hr=0x{dwmFlushHr:X8}, p50={Pct(flushDts, 50):F2} ms, p95={Pct(flushDts, 95):F2} ms, max={flushDts[^1]:F2} ms (effective ~{1000.0 / Math.Max(0.1, Pct(flushDts, 50)):F1} Hz)");
            }
            else
            {
                sb.AppendLine($"  DwmFlush() failed: hr=0x{dwmFlushHr:X8}");
            }

            WpfStaTestRunner.Run(() =>
            {
                bool prevExt = SpringAnimator.UseExternalClockForTest;
                bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
                bool? prevBatt = PowerStatusHelper.ForceBatterySaverOverrideForTest;
                SpringAnimator.UseExternalClockForTest = false;
                SpringAnimator.ForceReducedMotionOverride = false;
                PowerStatusHelper.ForceBatterySaverOverrideForTest = false;

                try
                {
                    // 1a. Blank 1-Rectangle Foreground Control Window (2.0 s)
                    var blankRect = new Rectangle { Width = 320, Height = 180 };
                    var blankTrans = new TranslateTransform();
                    blankRect.RenderTransform = blankTrans;
                    var blankCanvas = new Canvas();
                    blankCanvas.Children.Add(blankRect);
                    var blankWin = new Window
                    {
                        Title = "Phase4 Blank DWM Control",
                        Width = 420,
                        Height = 260,
                        Left = 40,
                        Top = 40,
                        Topmost = true,
                        ShowInTaskbar = true,
                        Content = blankCanvas
                    };
                    try
                    {
                        blankWin.Show();
                        BringWindowToForeground(blankWin);
                        var blankStats = MeasureRealtimeWindow(blankWin, durationMs: 2000, onFrame: step => blankTrans.X = Math.Sin(step * 0.1) * 20.0, onTickMs: null);
                        sb.AppendLine(FormatRealtimeRow("Blank1RectControl (Foreground, 2.0s)", blankStats, vsyncMs));
                    }
                    finally
                    {
                        blankWin.Close();
                    }

                    // 1b. Live AuraAudio Hosts (FullScreen Split, RightPane 360px, MiniPlayer Tray)
                    using var db = new DatabaseManager(Path.Combine(tempDir, "rt.db"));
                    db.Initialize();
                    using var vm = new MainViewModel(db: db);
                    var redBmp = CreatePhase4Cover("teal_orange", 128)!;
                    CoverArtLoader.RegisterSyntheticCoverForTest("p4_rt_cover", redBmp);
                    var palette = ArtworkPaletteExtractor.ExtractFromBitmapSource(redBmp, ArtworkPaletteExtractor.ActiveDefault);

                    vm.CurrentTrack = MakeTrack(tempDir, "p4_rt_cover", "Real-Time Presentation Probe", hasCover: true);
                    vm.IsPlaying = true;
                    var parsed = LyricsParser.ParseLrcContent(Build123LineLrc(), LyricsSourceKind.SidecarLrc);
                    vm.Lyrics.ApplyParsedResult(parsed);

                    var win = new MainWindow(vm)
                    {
                        Width = 1536,
                        Height = 860,
                        Left = 40,
                        Top = 40,
                        Topmost = true,
                        ShowInTaskbar = true
                    };
                    var mini = new MiniPlayerWindow(vm)
                    {
                        Left = 100,
                        Top = 100,
                        Topmost = true,
                        ShowInTaskbar = true
                    };

                    try
                    {
                        // Host 1: Full-Screen Split (123 lines + animating FullScreenBackdrop)
                        win.Show();
                        BringWindowToForeground(win);
                        vm.Presentation.IsFullScreenLyricsOpen = true;
                        vm.Presentation.IsFullScreenOpen = true;
                        vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(1.5), isPlaying: true);
                        win.UpdateLayout();

                        var fsView = (FullScreenPlayerView)win.FindName("FullScreenStageOverlay");
                        fsView.BackdropControl.ApplyPaletteImmediatelyForTest(palette);
                        fsView.BackdropControl.ResetAutoFallbackForTest();
                        win.UpdateLayout();

                        var fsStats = MeasureRealtimeWindow(
                            win,
                            durationMs: 3600,
                            onFrame: null,
                            onTickMs: elapsedMs =>
                            {
                                // Advance through 13 lyric transitions over 3.6 s
                                int idx = Math.Min(parsed.Lines.Count - 1, 2 + (int)(elapsedMs / 270.0));
                                vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[idx].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                            });
                        sb.AppendLine(FormatRealtimeRow("FullScreenSplit (123 lines + Backdrop)", fsStats, vsyncMs));

                        // Host 2: 360 px Right Pane (FullScreen closed -> FullScreenBackdrop verified frozen)
                        vm.Presentation.IsFullScreenOpen = false;
                        vm.Presentation.ActiveRightPane = RightPaneMode.Lyrics;
                        vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(1.5), isPlaying: true);
                        win.UpdateLayout();
                        Assert.False(fsView.BackdropControl.IsDriftAnimating);

                        var rpStats = MeasureRealtimeWindow(
                            win,
                            durationMs: 3600,
                            onFrame: null,
                            onTickMs: elapsedMs =>
                            {
                                int idx = Math.Min(parsed.Lines.Count - 1, 2 + (int)(elapsedMs / 270.0));
                                vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[idx].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                            });
                        sb.AppendLine(FormatRealtimeRow("RightPane360px (123 lines, FS frozen)", rpStats, vsyncMs));

                        // Host 3: Mini-Player Expanded Lyrics Tray (123 lines + MiniBackdrop & MiniTrayBackdrop animating)
                        win.Hide();
                        mini.Show();
                        BringWindowToForeground(mini);
                        vm.Presentation.IsMiniPlayerAlbumView = false;
                        vm.Presentation.MiniPlayerTrayMode = RightPaneMode.Lyrics;
                        vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(1.5), isPlaying: true);
                        mini.UpdateLayout();
                        mini.StageBackdropControl.ApplyPaletteImmediatelyForTest(palette);
                        mini.TrayBackdropControl.ApplyPaletteImmediatelyForTest(palette);
                        mini.StageBackdropControl.ResetAutoFallbackForTest();
                        mini.TrayBackdropControl.ResetAutoFallbackForTest();
                        mini.UpdateLayout();

                        var miniStats = MeasureRealtimeWindow(
                            mini,
                            durationMs: 3600,
                            onFrame: null,
                            onTickMs: elapsedMs =>
                            {
                                int idx = Math.Min(parsed.Lines.Count - 1, 2 + (int)(elapsedMs / 270.0));
                                vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[idx].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                            });
                        sb.AppendLine(FormatRealtimeRow("MiniPlayerTray (123 lines + 2 Backdrops)", miniStats, vsyncMs));
                    }
                    finally
                    {
                        CoverArtLoader.UnregisterSyntheticCoverForTest("p4_rt_cover");
                        mini.Close();
                        win.Close();
                    }
                }
                finally
                {
                    SpringAnimator.UseExternalClockForTest = prevExt;
                    SpringAnimator.ForceReducedMotionOverride = prevRm;
                    PowerStatusHelper.ForceBatterySaverOverrideForTest = prevBatt;
                }
            });
        }
        finally
        {
            SetThreadExecutionState(ES_CONTINUOUS);
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        return sb.ToString();
    }

    private sealed record RealtimeStats(
        int RawCallbacks,
        int UniqueFrames,
        int DuplicateCallbacks,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaxMs,
        int Dropped15x,
        int Dropped30x,
        int LayoutPasses,
        long AllocBytes,
        bool DwmInfoValid,
        ulong DwmRefreshDelta,
        ulong DwmFramesDisplayedDelta,
        ulong DwmFramesDroppedDelta,
        ulong DwmFramesMissedDelta,
        double DwmRefreshHz);

    private static RealtimeStats MeasureRealtimeWindow(
        Window win,
        int durationMs,
        Action<int>? onFrame,
        Action<double>? onTickMs)
    {
        // Warmup settle 150 ms
        PumpDispatcherFor(150);

        DWM_TIMING_INFO dwmStart = default;
        dwmStart.cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>();
        bool dwmOkStart = DwmGetCompositionTimingInfo(IntPtr.Zero, ref dwmStart) == 0;

        int rawCallbacks = 0;
        int duplicateCallbacks = 0;
        int layoutPasses = 0;
        TimeSpan lastRenderTime = TimeSpan.MinValue;
        long lastUniqueTicks = 0;
        var uniqueDts = new List<double>(1024);

        EventHandler onLayout = (_, _) => layoutPasses++;
        EventHandler onRendering = (_, e) =>
        {
            rawCallbacks++;
            if (e is RenderingEventArgs rea)
            {
                if (rea.RenderingTime == lastRenderTime)
                {
                    duplicateCallbacks++;
                    return;
                }
                lastRenderTime = rea.RenderingTime;
            }

            long now = Stopwatch.GetTimestamp();
            if (lastUniqueTicks != 0)
            {
                uniqueDts.Add((now - lastUniqueTicks) * 1000.0 / Stopwatch.Frequency);
            }
            lastUniqueTicks = now;
            onFrame?.Invoke(uniqueDts.Count);
        };

        var sw = Stopwatch.StartNew();
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var tickTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
        tickTimer.Tick += (_, _) =>
        {
            // Keep session active with subtle synthetic input ping every 500 ms
            if (((int)sw.ElapsedMilliseconds / 50) % 10 == 0)
            {
                SendSyntheticNopMouseMove();
            }
            onTickMs?.Invoke(sw.Elapsed.TotalMilliseconds);
        };

        win.LayoutUpdated += onLayout;
        CompositionTarget.Rendering += onRendering;
        tickTimer.Start();
        try
        {
            var frame = new DispatcherFrame();
            var stopTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(durationMs) };
            stopTimer.Tick += (_, _) =>
            {
                stopTimer.Stop();
                frame.Continue = false;
            };
            stopTimer.Start();
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            tickTimer.Stop();
            CompositionTarget.Rendering -= onRendering;
            win.LayoutUpdated -= onLayout;
        }

        long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        DWM_TIMING_INFO dwmEnd = default;
        dwmEnd.cbSize = (uint)Marshal.SizeOf<DWM_TIMING_INFO>();
        bool dwmOkEnd = DwmGetCompositionTimingInfo(IntPtr.Zero, ref dwmEnd) == 0;

        uniqueDts.Sort();
        double vsyncMs = dwmOkEnd && dwmEnd.rateRefresh.uiNumerator > 0
            ? (1000.0 * dwmEnd.rateRefresh.uiDenominator / dwmEnd.rateRefresh.uiNumerator)
            : 6.06;

        int d15 = uniqueDts.Count(v => v > vsyncMs * 1.5);
        int d30 = uniqueDts.Count(v => v > vsyncMs * 3.0);

        double dwmHz = dwmOkEnd && dwmEnd.rateRefresh.uiDenominator > 0
            ? (double)dwmEnd.rateRefresh.uiNumerator / dwmEnd.rateRefresh.uiDenominator
            : 0.0;

        return new RealtimeStats(
            RawCallbacks: rawCallbacks,
            UniqueFrames: uniqueDts.Count,
            DuplicateCallbacks: duplicateCallbacks,
            P50Ms: Pct(uniqueDts, 50),
            P95Ms: Pct(uniqueDts, 95),
            P99Ms: Pct(uniqueDts, 99),
            MaxMs: uniqueDts.Count > 0 ? uniqueDts[^1] : 0.0,
            Dropped15x: d15,
            Dropped30x: d30,
            LayoutPasses: layoutPasses,
            AllocBytes: allocDelta,
            DwmInfoValid: dwmOkStart && dwmOkEnd,
            DwmRefreshDelta: (dwmOkStart && dwmOkEnd && dwmEnd.cRefresh >= dwmStart.cRefresh) ? dwmEnd.cRefresh - dwmStart.cRefresh : 0,
            DwmFramesDisplayedDelta: (dwmOkStart && dwmOkEnd && dwmEnd.cFramesDisplayed >= dwmStart.cFramesDisplayed) ? dwmEnd.cFramesDisplayed - dwmStart.cFramesDisplayed : 0,
            DwmFramesDroppedDelta: (dwmOkStart && dwmOkEnd && dwmEnd.cFramesDropped >= dwmStart.cFramesDropped) ? dwmEnd.cFramesDropped - dwmStart.cFramesDropped : 0,
            DwmFramesMissedDelta: (dwmOkStart && dwmOkEnd && dwmEnd.cFramesMissed >= dwmStart.cFramesMissed) ? dwmEnd.cFramesMissed - dwmStart.cFramesMissed : 0,
            DwmRefreshHz: dwmHz);
    }

    private static string FormatRealtimeRow(string label, RealtimeStats s, double vsyncMs)
    {
        double avgBpf = s.UniqueFrames > 0 ? (double)s.AllocBytes / s.UniqueFrames : 0.0;
        return $"  {label,-42} | unique={s.UniqueFrames,4} raw={s.RawCallbacks,4} dup={s.DuplicateCallbacks,3} | " +
               $"dt p50/p95/p99/max={s.P50Ms:F2}/{s.P95Ms:F2}/{s.P99Ms:F2}/{s.MaxMs:F2} ms | " +
               $">1.5x({vsyncMs * 1.5:F1}ms)={s.Dropped15x} >3x({vsyncMs * 3.0:F1}ms)={s.Dropped30x} | " +
               $"Layout={s.LayoutPasses} | Alloc={s.AllocBytes / 1024.0:F1} KB ({avgBpf:F0} B/f) | " +
               $"DWM(ok={s.DwmInfoValid}, cRefresh={s.DwmRefreshDelta}, disp={s.DwmFramesDisplayedDelta}, drop={s.DwmFramesDroppedDelta}, miss={s.DwmFramesMissedDelta}, {s.DwmRefreshHz:F1}Hz)";
    }

    // =========================================================================
    // IMPLEMENTATION: SECTION 2 (COMBINED 165 Hz DETERMINISTIC PROBE - 3 HOSTS)
    // =========================================================================

    private string RunCombinedDeterministicProbe(double refreshHz, double vsyncMs)
    {
        var sb = new StringBuilder(4096);
        double dtSec = 1.0 / Math.Max(60.0, refreshHz);
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_Phase4_Comb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool prevExt = SpringAnimator.UseExternalClockForTest;
                bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
                bool? prevBatt = PowerStatusHelper.ForceBatterySaverOverrideForTest;
                SpringAnimator.UseExternalClockForTest = true;
                SpringAnimator.ForceReducedMotionOverride = false;
                PowerStatusHelper.ForceBatterySaverOverrideForTest = false;

                using var db = new DatabaseManager(Path.Combine(tempDir, "comb.db"));
                db.Initialize();
                using var vm = new MainViewModel(db: db);
                var coverBmp = CreatePhase4Cover("teal_orange", 128)!;
                CoverArtLoader.RegisterSyntheticCoverForTest("p4_comb_cover", coverBmp);
                var palette = ArtworkPaletteExtractor.ExtractFromBitmapSource(coverBmp, ArtworkPaletteExtractor.ActiveDefault);

                vm.CurrentTrack = MakeTrack(tempDir, "p4_comb_cover", "Combined Probe", hasCover: true);
                vm.IsPlaying = true;
                var parsed = LyricsParser.ParseLrcContent(Build123LineLrc(), LyricsSourceKind.SidecarLrc);
                vm.Lyrics.ApplyParsedResult(parsed);

                var win = new MainWindow(vm)
                {
                    Width = 1536,
                    Height = 860,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false
                };
                var mini = new MiniPlayerWindow(vm)
                {
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false
                };

                try
                {
                    // 1. Full-Screen Split (123 lines + FullScreenBackdrop animating)
                    win.Show();
                    vm.Presentation.IsFullScreenLyricsOpen = true;
                    vm.Presentation.IsFullScreenOpen = true;
                    vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[10].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                    for (int f = 0; f < 200 && SpringAnimator.HasActiveSprings; f++)
                    {
                        SpringAnimator.StepExternalClockForTest(dtSec);
                    }
                    win.UpdateLayout();

                    var fsView = (FullScreenPlayerView)win.FindName("FullScreenStageOverlay");
                    fsView.BackdropControl.ApplyPaletteImmediatelyForTest(palette);
                    fsView.BackdropControl.ResetAutoFallbackForTest();
                    win.UpdateLayout();
                    Assert.True(fsView.BackdropControl.IsDriftAnimating);

                    sb.AppendLine(MeasureDeterministicHost("FullScreenSplit (123L + AnimBackdrop)", win, fsView, vm, parsed, dtSec, vsyncMs, steadyFrames: 300));

                    // 2. 360 px Right Pane (123 lines, FullScreen closed -> FullScreenBackdrop frozen)
                    vm.Presentation.IsFullScreenOpen = false;
                    vm.Presentation.ActiveRightPane = RightPaneMode.Lyrics;
                    vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[10].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                    for (int f = 0; f < 220 && SpringAnimator.HasActiveSprings; f++)
                    {
                        SpringAnimator.StepExternalClockForTest(dtSec);
                    }
                    win.UpdateLayout();
                    Assert.False(fsView.BackdropControl.IsDriftAnimating);

                    var rpLyrics = FindByAutomationId<FrameworkElement>(win, "Aura.Lyrics.List") ?? win;
                    sb.AppendLine(MeasureDeterministicHost("RightPane360px (123L, FS frozen)", win, rpLyrics, vm, parsed, dtSec, vsyncMs, steadyFrames: 300));

                    // 3. Mini-Player Expanded Lyrics Tray (123 lines + MiniBackdrop & MiniTrayBackdrop animating)
                    mini.Show();
                    vm.Presentation.IsMiniPlayerAlbumView = false;
                    vm.Presentation.MiniPlayerTrayMode = RightPaneMode.Lyrics;
                    vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[10].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                    for (int f = 0; f < 220 && SpringAnimator.HasActiveSprings; f++)
                    {
                        SpringAnimator.StepExternalClockForTest(dtSec);
                    }
                    mini.UpdateLayout();
                    mini.StageBackdropControl.ApplyPaletteImmediatelyForTest(palette);
                    mini.TrayBackdropControl.ApplyPaletteImmediatelyForTest(palette);
                    mini.StageBackdropControl.ResetAutoFallbackForTest();
                    mini.TrayBackdropControl.ResetAutoFallbackForTest();
                    mini.UpdateLayout();
                    Assert.True(mini.StageBackdropControl.IsDriftAnimating);
                    Assert.True(mini.TrayBackdropControl.IsDriftAnimating);

                    sb.AppendLine(MeasureDeterministicHost("MiniPlayerTray (123L + 2 AnimBackdrops)", mini, mini, vm, parsed, dtSec, vsyncMs, steadyFrames: 300));
                }
                finally
                {
                    CoverArtLoader.UnregisterSyntheticCoverForTest("p4_comb_cover");
                    mini.Close();
                    win.Close();
                    SpringAnimator.UseExternalClockForTest = prevExt;
                    SpringAnimator.ForceReducedMotionOverride = prevRm;
                    PowerStatusHelper.ForceBatterySaverOverrideForTest = prevBatt;
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        return sb.ToString();
    }

    private static string MeasureDeterministicHost(
        string label,
        Window hostWin,
        FrameworkElement layoutScope,
        MainViewModel vm,
        ParsedLyricsResult parsed,
        double dtSec,
        double vsyncMs,
        int steadyFrames)
    {
        // Trigger a fresh lyric scroll transition and warm up 15 frames
        vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[18].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
        hostWin.UpdateLayout();
        for (int i = 0; i < 15; i++)
        {
            SpringAnimator.StepExternalClockForTest(dtSec);
            hostWin.UpdateLayout();
        }

        // Normal 1-line lyric auto-scroll steady state (300 frames @ 165 Hz, zero mass row recycling)
        vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[19].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
        hostWin.UpdateLayout();

        var frameTimes = new List<double>(steadyFrames);
        int steadyLayouts = 0;
        EventHandler onLayout = (_, _) => steadyLayouts++;

        layoutScope.LayoutUpdated += onLayout;
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            for (int f = 0; f < steadyFrames; f++)
            {
                long t0 = Stopwatch.GetTimestamp();
                SpringAnimator.StepExternalClockForTest(dtSec);
                hostWin.UpdateLayout();
                long t1 = Stopwatch.GetTimestamp();
                frameTimes.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            }
        }
        finally
        {
            layoutScope.LayoutUpdated -= onLayout;
        }
        long steadyAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        // 20 consecutive 1-line transitions with simultaneous animating backdrop (lines 20..39)
        var switchTimes = new List<double>(20);
        int switchLayouts = 0;
        EventHandler onSwitchLayout = (_, _) => switchLayouts++;
        layoutScope.LayoutUpdated += onSwitchLayout;
        long swAllocBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            for (int s = 0; s < 20; s++)
            {
                int idx = 20 + s;
                long t0 = Stopwatch.GetTimestamp();
                vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[idx].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                SpringAnimator.StepExternalClockForTest(dtSec);
                hostWin.UpdateLayout();
                long t1 = Stopwatch.GetTimestamp();
                switchTimes.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            }
        }
        finally
        {
            layoutScope.LayoutUpdated -= onSwitchLayout;
        }
        long switchAlloc = GC.GetAllocatedBytesForCurrentThread() - swAllocBefore;

        frameTimes.Sort();
        switchTimes.Sort();
        int d15 = frameTimes.Count(v => v > vsyncMs * 1.5);
        int d30 = frameTimes.Count(v => v > vsyncMs * 3.0);
        int sw15 = switchTimes.Count(v => v > vsyncMs * 1.5);
        int sw30 = switchTimes.Count(v => v > vsyncMs * 3.0);

        Assert.True(Pct(frameTimes, 95) <= vsyncMs * 1.5, $"{label} steady p95={Pct(frameTimes, 95):F3}ms > {vsyncMs * 1.5:F2}ms");
        Assert.True(d30 == 0, $"{label} steady had {d30} frame(s) > 3x vsync ({vsyncMs * 3.0:F2}ms), max={frameTimes[^1]:F3}ms");
        Assert.True(sw30 == 0, $"{label} line-switch had {sw30} frame(s) > 3x vsync ({vsyncMs * 3.0:F2}ms), max={switchTimes[^1]:F3}ms");

        return
            $"  {label,-40} | Steady({steadyFrames}f): p50/p95/p99/max={Pct(frameTimes, 50):F4}/{Pct(frameTimes, 95):F4}/{Pct(frameTimes, 99):F4}/{frameTimes[^1]:F4} ms " +
            $"(>1.5x={d15}, >3x={d30}) Layout={steadyLayouts} Alloc={(double)steadyAlloc / steadyFrames:F1} B/f | " +
            $"LineSwitch(20x): p95={Pct(switchTimes, 95):F4} ms max={switchTimes[^1]:F4} ms (>1.5x={sw15}, >3x={sw30}) Layout={switchLayouts} Alloc={(double)switchAlloc / 20.0:F0} B/sw";
    }

    // =========================================================================
    // IMPLEMENTATION: SECTION 3 (10s CPU STATIC vs ANIMATING, 300f ALLOCS, GPU)
    // =========================================================================

    private string RunCpuGpuAndAllocationCostBenchmark(double refreshHz)
    {
        var sb = new StringBuilder(4096);
        double dtSec = 1.0 / Math.Max(60.0, refreshHz);
        int frames10s = (int)Math.Round(refreshHz * 10.0);
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_Phase4_Cost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool prevExt = SpringAnimator.UseExternalClockForTest;
                bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
                bool? prevBatt = PowerStatusHelper.ForceBatterySaverOverrideForTest;
                SpringAnimator.UseExternalClockForTest = true;
                SpringAnimator.ForceReducedMotionOverride = false;
                PowerStatusHelper.ForceBatterySaverOverrideForTest = false;

                using var db = new DatabaseManager(Path.Combine(tempDir, "cost.db"));
                db.Initialize();
                using var vm = new MainViewModel(db: db);
                var coverBmp = CreatePhase4Cover("warm_sunset", 128)!;
                CoverArtLoader.RegisterSyntheticCoverForTest("p4_cost_cover", coverBmp);
                var palette = ArtworkPaletteExtractor.ExtractFromBitmapSource(coverBmp, ArtworkPaletteExtractor.ActiveDefault);

                vm.CurrentTrack = MakeTrack(tempDir, "p4_cost_cover", "Cost Benchmark", hasCover: true);
                vm.IsPlaying = true;
                var parsed = LyricsParser.ParseLrcContent(Build123LineLrc(), LyricsSourceKind.SidecarLrc);
                vm.Lyrics.ApplyParsedResult(parsed);

                var win = new MainWindow(vm)
                {
                    Width = 1536,
                    Height = 860,
                    Left = 40,
                    Top = 40,
                    Topmost = true,
                    ShowInTaskbar = true
                };

                try
                {
                    win.Show();
                    BringWindowToForeground(win);
                    vm.Presentation.IsFullScreenLyricsOpen = true;
                    vm.Presentation.IsFullScreenOpen = true;
                    vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[12].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                    win.UpdateLayout();

                    var fsView = (FullScreenPlayerView)win.FindName("FullScreenStageOverlay");
                    fsView.BackdropControl.ApplyPaletteImmediatelyForTest(palette);
                    fsView.BackdropControl.ResetAutoFallbackForTest();
                    win.UpdateLayout();

                    // --- A. 300 Steady-State Frame Allocation Isolation ---
                    // A1: Backdrop animating ONLY (lyrics idle)
                    for (int i = 0; i < 120; i++) SpringAnimator.StepExternalClockForTest(dtSec);
                    win.UpdateLayout();

                    vm.Settings.AnimatedBackdropEnabled = true;
                    fsView.BackdropControl.IsAnimatedBackdropEnabled = true;
                    win.UpdateLayout();
                    Assert.True(fsView.BackdropControl.IsDriftAnimating);

                    int bLayouts = 0;
                    EventHandler onBLayout = (_, _) => bLayouts++;
                    fsView.LayoutUpdated += onBLayout;
                    long bAlloc0 = GC.GetAllocatedBytesForCurrentThread();
                    long bT0 = Stopwatch.GetTimestamp();
                    try
                    {
                        for (int i = 0; i < 300; i++)
                        {
                            SpringAnimator.StepExternalClockForTest(dtSec);
                            win.UpdateLayout();
                        }
                    }
                    finally
                    {
                        fsView.LayoutUpdated -= onBLayout;
                    }
                    long bT1 = Stopwatch.GetTimestamp();
                    long bAlloc = GC.GetAllocatedBytesForCurrentThread() - bAlloc0;
                    double bMsPerFrame = (bT1 - bT0) * 1000.0 / Stopwatch.Frequency / 300.0;

                    // A2: Lyrics scrolling ONLY (backdrop static, normal 1-line auto-scroll transition)
                    fsView.BackdropControl.IsAnimatedBackdropEnabled = false;
                    vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[13].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                    win.UpdateLayout();
                    int lLayouts = 0;
                    EventHandler onLLayout = (_, _) => lLayouts++;
                    fsView.LayoutUpdated += onLLayout;
                    long lAlloc0 = GC.GetAllocatedBytesForCurrentThread();
                    long lT0 = Stopwatch.GetTimestamp();
                    try
                    {
                        for (int i = 0; i < 300; i++)
                        {
                            SpringAnimator.StepExternalClockForTest(dtSec);
                            win.UpdateLayout();
                        }
                    }
                    finally
                    {
                        fsView.LayoutUpdated -= onLLayout;
                    }
                    long lT1 = Stopwatch.GetTimestamp();
                    long lAlloc = GC.GetAllocatedBytesForCurrentThread() - lAlloc0;
                    double lMsPerFrame = (lT1 - lT0) * 1000.0 / Stopwatch.Frequency / 300.0;

                    // A3: Combined Backdrop animating + Lyrics scrolling (normal 1-line auto-scroll transition)
                    fsView.BackdropControl.IsAnimatedBackdropEnabled = true;
                    vm.Lyrics.UpdatePlaybackPosition(parsed.Lines[14].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                    win.UpdateLayout();
                    int cLayouts = 0;
                    EventHandler onCLayout = (_, _) => cLayouts++;
                    fsView.LayoutUpdated += onCLayout;
                    long cAlloc0 = GC.GetAllocatedBytesForCurrentThread();
                    long cT0 = Stopwatch.GetTimestamp();
                    try
                    {
                        for (int i = 0; i < 300; i++)
                        {
                            SpringAnimator.StepExternalClockForTest(dtSec);
                            win.UpdateLayout();
                        }
                    }
                    finally
                    {
                        fsView.LayoutUpdated -= onCLayout;
                    }
                    long cT1 = Stopwatch.GetTimestamp();
                    long cAlloc = GC.GetAllocatedBytesForCurrentThread() - cAlloc0;
                    double cMsPerFrame = (cT1 - cT0) * 1000.0 / Stopwatch.Frequency / 300.0;

                    sb.AppendLine($"  300-frame Steady-State Allocations (WPF DependencyObject.SetValue boxes double ~24 B per DP write; 0 user objects):");
                    sb.AppendLine($"    Backdrop Animating Only (300f) : {bAlloc,8} B total ({bAlloc / 300.0:F1} B/frame), Layout={bLayouts}, avg CPU = {bMsPerFrame:F4} ms/frame");
                    sb.AppendLine($"    Lyrics Scrolling Only   (300f) : {lAlloc,8} B total ({lAlloc / 300.0:F1} B/frame), Layout={lLayouts}, avg CPU = {lMsPerFrame:F4} ms/frame");
                    sb.AppendLine($"    Combined Backdrop+Lyric (300f) : {cAlloc,8} B total ({cAlloc / 300.0:F1} B/frame), Layout={cLayouts}, avg CPU = {cMsPerFrame:F4} ms/frame");

                    // --- B. Deterministic 10.0 s Equivalent (1,650 frames @ 165 Hz) Process CPU Time ---
                    var proc = Process.GetCurrentProcess();

                    fsView.BackdropControl.IsAnimatedBackdropEnabled = false;
                    win.UpdateLayout();
                    TimeSpan detStaticCpu0 = proc.TotalProcessorTime;
                    long detStaticWall0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < frames10s; i++)
                    {
                        SpringAnimator.StepExternalClockForTest(dtSec);
                        win.UpdateLayout();
                    }
                    double detStaticCpuMs = (proc.TotalProcessorTime - detStaticCpu0).TotalMilliseconds;
                    double detStaticWallMs = (Stopwatch.GetTimestamp() - detStaticWall0) * 1000.0 / Stopwatch.Frequency;

                    fsView.BackdropControl.IsAnimatedBackdropEnabled = true;
                    win.UpdateLayout();
                    TimeSpan detAnimCpu0 = proc.TotalProcessorTime;
                    long detAnimWall0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < frames10s; i++)
                    {
                        SpringAnimator.StepExternalClockForTest(dtSec);
                        win.UpdateLayout();
                    }
                    double detAnimCpuMs = (proc.TotalProcessorTime - detAnimCpu0).TotalMilliseconds;
                    double detAnimWallMs = (Stopwatch.GetTimestamp() - detAnimWall0) * 1000.0 / Stopwatch.Frequency;

                    sb.AppendLine($"  Deterministic 10.0 s ({frames10s} frames @ {refreshHz:F0} Hz) Process CPU Time:");
                    sb.AppendLine($"    Static Backdrop    : TotalProcessorTime = {detStaticCpuMs:F2} ms (wall = {detStaticWallMs:F2} ms, {detStaticCpuMs / 10000.0 * 100.0:F3}% of 1 core over 10 s)");
                    sb.AppendLine($"    Animating Backdrop : TotalProcessorTime = {detAnimCpuMs:F2} ms (wall = {detAnimWallMs:F2} ms, {detAnimCpuMs / 10000.0 * 100.0:F3}% of 1 core over 10 s, delta = {detAnimCpuMs - detStaticCpuMs:+0.00;-0.00} ms)");

                    // --- C. Real-Time Wall-Clock 10.0 s Process CPU + Windows GPU Engine 3D Utilization ---
                    SpringAnimator.UseExternalClockForTest = false;

                    fsView.BackdropControl.IsAnimatedBackdropEnabled = false;
                    win.UpdateLayout();
                    var rtStatic = Measure10sWallClockCpuAndGpu(proc);

                    fsView.BackdropControl.IsAnimatedBackdropEnabled = true;
                    fsView.BackdropControl.ResetAutoFallbackForTest();
                    win.UpdateLayout();
                    var rtAnim = Measure10sWallClockCpuAndGpu(proc);

                    sb.AppendLine($"  Real-Time 10.0 s Wall-Clock Process CPU & Hardware GPU 3D Utilization (Foreground Full-Screen 1536x860):");
                    sb.AppendLine($"    Static Backdrop    : CPU={rtStatic.CpuMs:F1} ms (User={rtStatic.UserMs:F1}, Priv={rtStatic.PrivMs:F1}, {rtStatic.CpuMs / 100.0:F2}% 1-core) | App GPU 3D avg={rtStatic.AppGpu3dPct:F2}% max={rtStatic.AppGpu3dMaxPct:F2}% | DWM GPU 3D avg={rtStatic.DwmGpu3dPct:F2}% (samples={rtStatic.GpuSamples})");
                    sb.AppendLine($"    Animating Backdrop : CPU={rtAnim.CpuMs:F1} ms (User={rtAnim.UserMs:F1}, Priv={rtAnim.PrivMs:F1}, {rtAnim.CpuMs / 100.0:F2}% 1-core) | App GPU 3D avg={rtAnim.AppGpu3dPct:F2}% max={rtAnim.AppGpu3dMaxPct:F2}% | DWM GPU 3D avg={rtAnim.DwmGpu3dPct:F2}% (samples={rtAnim.GpuSamples})");
                    sb.AppendLine($"    Delta (Anim-Static): CPU={rtAnim.CpuMs - rtStatic.CpuMs:+0.0;-0.0} ms | App GPU 3D={rtAnim.AppGpu3dPct - rtStatic.AppGpu3dPct:+0.00;-0.00}% | DWM GPU 3D={rtAnim.DwmGpu3dPct - rtStatic.DwmGpu3dPct:+0.00;-0.00}%");

                    // --- D. Palette Extraction Background Cost + Analytical GPU Fill-Rate Math ---
                    var swExt = Stopwatch.StartNew();
                    for (int i = 0; i < 20; i++)
                    {
                        _ = ArtworkPaletteExtractor.ExtractFromBitmapSource(coverBmp, ArtworkPaletteExtractor.SafeDefault);
                    }
                    swExt.Stop();
                    double avgExtractMs = swExt.Elapsed.TotalMilliseconds / 20.0;

                    sb.AppendLine($"  Palette Extraction (32x32 OKLCH, 20-run avg): {avgExtractMs:F3} ms/track (background thread, cached 64-entry LRU)");
                    sb.AppendLine($"  Analytical GPU Fill-Rate Estimate vs Hardware:");
                    sb.AppendLine($"    Full-Screen 1920x1080 = 2.07 MP; 1 base rect + 3 radial ellipses (~1.65x area clipped to viewport) + 1 scrim = ~3.5x overdraw (~7.25 MP/frame, ~1.20 GP/s @ 165 Hz).");
                    sb.AppendLine($"    AppleMusicSpatial adds 3 partial pure-black gradient scrim rects (~+1.6x overdraw -> ~5.1x total, ~10.6 MP/frame, ~1.75 GP/s @ 165 Hz).");
                    sb.AppendLine($"    Mini-Player Compact 350x160 = 0.056 MP (~3.0x overdraw -> ~0.17 MP/frame, ~0.028 GP/s @ 165 Hz, ~43x less fill than Full-Screen).");
                }
                finally
                {
                    CoverArtLoader.UnregisterSyntheticCoverForTest("p4_cost_cover");
                    win.Close();
                    SpringAnimator.UseExternalClockForTest = prevExt;
                    SpringAnimator.ForceReducedMotionOverride = prevRm;
                    PowerStatusHelper.ForceBatterySaverOverrideForTest = prevBatt;
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        return sb.ToString();
    }

    private sealed record Wall10sCost(
        double CpuMs,
        double UserMs,
        double PrivMs,
        double AppGpu3dPct,
        double AppGpu3dMaxPct,
        double DwmGpu3dPct,
        int GpuSamples);

    private static Wall10sCost Measure10sWallClockCpuAndGpu(Process proc)
    {
        int pid = proc.Id;
        int dwmPid = Process.GetProcessesByName("dwm").FirstOrDefault()?.Id ?? -1;

        var appGpuCounters = new List<PerformanceCounter>();
        var dwmGpuCounters = new List<PerformanceCounter>();
        try
        {
            if (PerformanceCounterCategory.Exists("GPU Engine"))
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                string pidToken = $"pid_{pid}_";
                string dwmToken = dwmPid > 0 ? $"pid_{dwmPid}_" : "__none__";
                foreach (string inst in cat.GetInstanceNames())
                {
                    if (!inst.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                    if (inst.StartsWith(pidToken, StringComparison.OrdinalIgnoreCase))
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                        _ = pc.NextValue(); // prime counter
                        appGpuCounters.Add(pc);
                    }
                    else if (inst.StartsWith(dwmToken, StringComparison.OrdinalIgnoreCase))
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true);
                        _ = pc.NextValue();
                        dwmGpuCounters.Add(pc);
                    }
                }
            }
        }
        catch
        {
            // Performance counters unavailable; handled transparently with 0 samples
        }

        try
        {
            proc.Refresh();
            TimeSpan cpu0 = proc.TotalProcessorTime;
            TimeSpan user0 = proc.UserProcessorTime;
            TimeSpan priv0 = proc.PrivilegedProcessorTime;

            var appSamples = new List<double>(12);
            var dwmSamples = new List<double>(12);

            for (int sec = 0; sec < 10; sec++)
            {
                SendSyntheticNopMouseMove();
                PumpDispatcherFor(1000);

                if (appGpuCounters.Count > 0)
                {
                    double appSum = 0.0;
                    foreach (var pc in appGpuCounters)
                    {
                        try { appSum += pc.NextValue(); } catch { }
                    }
                    appSamples.Add(appSum);
                }
                if (dwmGpuCounters.Count > 0)
                {
                    double dwmSum = 0.0;
                    foreach (var pc in dwmGpuCounters)
                    {
                        try { dwmSum += pc.NextValue(); } catch { }
                    }
                    dwmSamples.Add(dwmSum);
                }
            }

            proc.Refresh();
            double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            double userMs = (proc.UserProcessorTime - user0).TotalMilliseconds;
            double privMs = (proc.PrivilegedProcessorTime - priv0).TotalMilliseconds;

            return new Wall10sCost(
                CpuMs: cpuMs,
                UserMs: userMs,
                PrivMs: privMs,
                AppGpu3dPct: appSamples.Count > 0 ? appSamples.Average() : 0.0,
                AppGpu3dMaxPct: appSamples.Count > 0 ? appSamples.Max() : 0.0,
                DwmGpu3dPct: dwmSamples.Count > 0 ? dwmSamples.Average() : 0.0,
                GpuSamples: appSamples.Count);
        }
        finally
        {
            foreach (var pc in appGpuCounters) pc.Dispose();
            foreach (var pc in dwmGpuCounters) pc.Dispose();
        }
    }

    // =========================================================================
    // IMPLEMENTATION: SECTION 4 (ADJUSTMENT 3 / C2 NEUTRALITY & ISOLATION AUDIT)
    // =========================================================================

    private sealed record NeutralityAuditSummary(
        int FullScreenSuppressedChromaticPixels,
        int FullScreenSuppressedPixelsScanned,
        int MiniPlayerSuppressedChromaticPixels,
        int MiniPlayerSuppressedPixelsScanned,
        int MainShellChromaticPixels,
        int MainShellPixelsScanned,
        int NonNeutralBrushesOutsideBackdrop,
        int BrushesCheckedOutsideBackdrop,
        int FullScreenActiveBackdropChromaticPixels,
        int ChromaticPixelsOutsideBackdropLayer,
        bool MiniAlbumViewBackdropCollapsed,
        string Report);

    private static NeutralityAuditSummary ExecuteNeutralityAuditC2()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_Phase4_C2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        NeutralityAuditSummary? result = null;

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool prevExt = SpringAnimator.UseExternalClockForTest;
                bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
                SpringAnimator.UseExternalClockForTest = true;
                SpringAnimator.ForceReducedMotionOverride = false;

                using var db = new DatabaseManager(Path.Combine(tempDir, "c2.db"));
                db.Initialize();
                using var vm = new MainViewModel(db: db);
                var redBmp = CreatePhase4Cover("red", 128)!;
                CoverArtLoader.RegisterSyntheticCoverForTest("p4_c2_red", redBmp);
                var palette = ArtworkPaletteExtractor.ExtractFromBitmapSource(redBmp, ArtworkPaletteExtractor.ActiveDefault);

                vm.CurrentTrack = MakeTrack(tempDir, "p4_c2_red", "C2 Neutrality Test", hasCover: true);
                vm.IsPlaying = true;
                vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(SampleVisualLrc, LyricsSourceKind.SidecarLrc));
                vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(5.4), isPlaying: true);

                var win = new MainWindow(vm)
                {
                    Width = 1536,
                    Height = 860,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false
                };
                var mini = new MiniPlayerWindow(vm)
                {
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false
                };

                try
                {
                    win.Show();
                    mini.Show();
                    vm.Presentation.ActiveRightPane = RightPaneMode.Lyrics;
                    vm.Presentation.IsFullScreenLyricsOpen = true;
                    vm.Presentation.IsFullScreenOpen = false;
                    vm.Presentation.IsMiniPlayerAlbumView = false;
                    vm.Presentation.MiniPlayerTrayMode = RightPaneMode.Lyrics;

                    for (int f = 0; f < 160 && SpringAnimator.HasActiveSprings; f++)
                    {
                        SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                    }
                    win.UpdateLayout();
                    mini.UpdateLayout();

                    // 1. Main Shell (Full-Screen closed, Right Pane Lyrics open) -> 0 chromatic pixels outside cover art thumbnails
                    var shellArtRects = CollectImageBrushRects(win);
                    var (shellChroma, shellScanned) = CountChromaticPixelsExcludingRects(win, shellArtRects);

                    // 2. Full-Screen Split with Backdrop replaced by neutral gray (SuppressColorForNeutralityAudit = true)
                    vm.Presentation.IsFullScreenOpen = true;
                    for (int f = 0; f < 160 && SpringAnimator.HasActiveSprings; f++)
                    {
                        SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                    }
                    win.UpdateLayout();

                    var fsView = (FullScreenPlayerView)win.FindName("FullScreenStageOverlay");
                    fsView.BackdropControl.ApplyPaletteImmediatelyForTest(palette);
                    fsView.BackdropControl.SuppressColorForNeutralityAudit = true;
                    win.UpdateLayout();

                    var fsArtRects = CollectImageBrushRects(fsView);
                    var (fsSuppressedChroma, fsSuppressedScanned) = CountChromaticPixelsExcludingRects(fsView, fsArtRects);

                    // 3. Mini-Player (Compact + Expanded Lyrics Tray) with Backdrops replaced by neutral gray
                    mini.StageBackdropControl.ApplyPaletteImmediatelyForTest(palette);
                    mini.TrayBackdropControl.ApplyPaletteImmediatelyForTest(palette);
                    mini.StageBackdropControl.SuppressColorForNeutralityAudit = true;
                    mini.TrayBackdropControl.SuppressColorForNeutralityAudit = true;
                    mini.UpdateLayout();

                    var miniArtRects = CollectImageBrushRects(mini);
                    var (miniSuppressedChroma, miniSuppressedScanned) = CountChromaticPixelsExcludingRects(mini, miniArtRects);

                    // 4. Visual-tree brush audit outside ImmersiveBackdropControl on both windows
                    int brushesChecked = 0;
                    int nonNeutralBrushes = 0;
                    var nonNeutralNotes = new List<string>();
                    AuditVisualTreeBrushesOutsideBackdrop(win, ref brushesChecked, ref nonNeutralBrushes, nonNeutralNotes);
                    AuditVisualTreeBrushesOutsideBackdrop(mini, ref brushesChecked, ref nonNeutralBrushes, nonNeutralNotes);

                    // 5. Isolation audit: re-enable colored backdrop, verify chroma exists inside FullScreenBackdrop and 0 outside
                    fsView.BackdropControl.SuppressColorForNeutralityAudit = false;
                    mini.StageBackdropControl.SuppressColorForNeutralityAudit = false;
                    mini.TrayBackdropControl.SuppressColorForNeutralityAudit = false;
                    win.UpdateLayout();
                    mini.UpdateLayout();

                    var (fsActiveChromaInside, _) = CountChromaticPixelsExcludingRects(fsView.BackdropControl, []);

                    // Diff check: backdrop bounds vs entire MainWindow -> confirm 0 colored pixels outside backdrop + artwork rects
                    var backdropRects = new List<Rect>(fsArtRects);
                    backdropRects.Add(GetElementBoundsInRoot(fsView.BackdropControl, win));
                    var (chromaOutsideBackdrop, _) = CountChromaticPixelsExcludingRects(win, backdropRects);

                    // 6. Mini-Player AlbumView collapses StageBackdropControl
                    vm.Presentation.IsMiniPlayerAlbumView = true;
                    mini.UpdateLayout();
                    bool albumBackdropCollapsed = mini.StageBackdropControl.Visibility == Visibility.Collapsed &&
                                                  !mini.StageBackdropControl.IsDriftAnimating;

                    var rep = new StringBuilder(1024);
                    rep.AppendLine($"  C2a Foreground over Neutral Backdrop (Full-Screen Split) : {fsSuppressedChroma} chromatic / {fsSuppressedScanned:N0} pixels scanned (artwork masked) -> {(fsSuppressedChroma == 0 ? "PASS" : "FAIL")}");
                    rep.AppendLine($"  C2b Foreground over Neutral Backdrop (Mini+Lyrics Tray)  : {miniSuppressedChroma} chromatic / {miniSuppressedScanned:N0} pixels scanned (artwork masked) -> {(miniSuppressedChroma == 0 ? "PASS" : "FAIL")}");
                    rep.AppendLine($"  C2c Main Shell + Right Pane Lyrics (FS Closed, Red Track): {shellChroma} chromatic / {shellScanned:N0} pixels scanned (artwork masked) -> {(shellChroma == 0 ? "PASS" : "FAIL")}");
                    rep.AppendLine($"  C2d Visual-Tree Brush Isolation (Outside Backdrop)       : {nonNeutralBrushes} non-neutral / {brushesChecked:N0} brushes checked -> {(nonNeutralBrushes == 0 ? "PASS" : "FAIL")}");
                    foreach (string note in nonNeutralNotes.Take(10))
                    {
                        rep.AppendLine($"      non-neutral detail: {note}");
                    }
                    rep.AppendLine($"  C2e Backdrop Layer Containment (Chroma Outside Backdrop) : {chromaOutsideBackdrop} leaked pixels (active backdrop chromatic pixels = {fsActiveChromaInside:N0}) -> {(chromaOutsideBackdrop == 0 ? "PASS" : "FAIL")}");
                    rep.AppendLine($"  C2f Mini-Player AlbumView Backdrop Collapsed & Frozen    : {albumBackdropCollapsed} -> {(albumBackdropCollapsed ? "PASS" : "FAIL")}");

                    result = new NeutralityAuditSummary(
                        FullScreenSuppressedChromaticPixels: fsSuppressedChroma,
                        FullScreenSuppressedPixelsScanned: fsSuppressedScanned,
                        MiniPlayerSuppressedChromaticPixels: miniSuppressedChroma,
                        MiniPlayerSuppressedPixelsScanned: miniSuppressedScanned,
                        MainShellChromaticPixels: shellChroma,
                        MainShellPixelsScanned: shellScanned,
                        NonNeutralBrushesOutsideBackdrop: nonNeutralBrushes,
                        BrushesCheckedOutsideBackdrop: brushesChecked,
                        FullScreenActiveBackdropChromaticPixels: fsActiveChromaInside,
                        ChromaticPixelsOutsideBackdropLayer: chromaOutsideBackdrop,
                        MiniAlbumViewBackdropCollapsed: albumBackdropCollapsed,
                        Report: rep.ToString());
                }
                finally
                {
                    CoverArtLoader.UnregisterSyntheticCoverForTest("p4_c2_red");
                    mini.Close();
                    win.Close();
                    SpringAnimator.UseExternalClockForTest = prevExt;
                    SpringAnimator.ForceReducedMotionOverride = prevRm;
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            GC.Collect();
        }

        return result!;
    }

    // =========================================================================
    // IMPLEMENTATION: SECTIONS 4 & 6 (PER-ZONE CONTRAST MATRIX + SCREENSHOTS)
    // =========================================================================

    private sealed record ZoneContrastMatrixRow(
        string Surface,
        string Cover,
        string Preset,
        BackdropPalette Palette,
        double OpenAmbientY,
        double WorstTextZoneY,
        double MinPeakCr,
        double MinPrimaryCr,
        double MinSecondaryCr,
        double MinTertiaryCr,
        double MinLyricD1Cr,
        double MinLyricD2Cr,
        double MinLyricD3Cr,
        double MinDotCr,
        bool AllGatesPassed,
        string FailureDetails);

    private static List<ZoneContrastMatrixRow> RunPerZoneContrastMatrix(bool captureScreenshots, string? outDir)
    {
        var results = new List<ZoneContrastMatrixRow>(All10CoverNames.Length * All3Presets.Length * 2);
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_Phase4_Matrix_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        bool prevExt = SpringAnimator.UseExternalClockForTest;
        bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
        SpringAnimator.UseExternalClockForTest = true;
        SpringAnimator.ForceReducedMotionOverride = false;

        using var db = new DatabaseManager(Path.Combine(tempDir, "matrix.db"));
        db.Initialize();
        using var vm = new MainViewModel(db: db);

        var win = new MainWindow(vm)
        {
            Width = 1536,
            Height = 860,
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false
        };
        var mini = new MiniPlayerWindow(vm)
        {
            Left = -20000,
            Top = -20000,
            ShowInTaskbar = false
        };

        try
        {
            win.Show();
            mini.Show();
            vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(SampleVisualLrc, LyricsSourceKind.SidecarLrc));
            vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(5.4), isPlaying: true);
            vm.Presentation.IsFullScreenLyricsOpen = true;
            vm.Presentation.IsFullScreenOpen = true;
            vm.Presentation.IsMiniPlayerAlbumView = false;
            vm.Presentation.MiniPlayerTrayMode = RightPaneMode.Lyrics;

            for (int f = 0; f < 200 && SpringAnimator.HasActiveSprings; f++)
            {
                SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
            }
            win.UpdateLayout();
            mini.UpdateLayout();

            var fsView = (FullScreenPlayerView)win.FindName("FullScreenStageOverlay");
            var fsBackdrop = fsView.BackdropControl;
            var miniStageBackdrop = mini.StageBackdropControl;
            var miniTrayBackdrop = mini.TrayBackdropControl;

            // Compute actual live UI element bounding rectangles in backdrop coordinate space
            double fsW = fsBackdrop.ActualWidth;
            double fsH = fsBackdrop.ActualHeight;
            var fsTopBarRect = new Rect(0, 0, fsW, 52.0);
            var fsLyricsHost = (FrameworkElement)fsView.FindName("LyricsStageHost");
            var fsLyricsRect = GetElementBoundsInRoot(fsLyricsHost, fsBackdrop);
            var fsScrubber = (FrameworkElement)fsView.FindName("FullScreenScrubber");
            var fsScrubberRect = GetElementBoundsInRoot(fsScrubber, fsBackdrop);
            // Left metadata (title + artist sits ~68 px above scrubber) through volume row (~125 px below scrubber)
            var fsLeftControlsRect = new Rect(
                Math.Max(0, fsScrubberRect.Left - 8),
                Math.Max(52, fsScrubberRect.Top - 72),
                Math.Min(fsW, fsScrubberRect.Width + 16),
                Math.Min(fsH - Math.Max(52, fsScrubberRect.Top - 72), fsScrubberRect.Height + 132));
            // Open ambient glow zone around left hero artwork (no text or icons here)
            var fsOpenAmbientRect = new Rect(0, 64, Math.Max(10, fsLyricsRect.Left - 16), Math.Max(10, fsLeftControlsRect.Top - 72));

            double msW = miniStageBackdrop.ActualWidth;
            double msH = miniStageBackdrop.ActualHeight;
            var miniMetaRect = new Rect(76, 8, Math.Max(10, msW - 84), 64);
            var miniControlsRect = new Rect(0, 72, msW, Math.Max(10, msH - 72));
            var miniOpenGlowRect = new Rect(0, 0, 74, 70);
            var miniTrayRect = new Rect(0, 0, miniTrayBackdrop.ActualWidth, miniTrayBackdrop.ActualHeight);

            Color peak = (Color)Application.Current.FindResource("Color.Neutral.100");
            Color primary = (Color)Application.Current.FindResource("Color.Neutral.92");
            Color secondary = (Color)Application.Current.FindResource("Color.Neutral.80");
            Color tertiary = (Color)Application.Current.FindResource("Color.Neutral.65");

            for (int ci = 0; ci < All10CoverNames.Length; ci++)
            {
                string coverName = All10CoverNames[ci];
                string? key = coverName == "missing" ? null : $"p4_mat_{coverName}";
                var bmp = CreatePhase4Cover(coverName, 256);
                if (key != null && bmp != null)
                {
                    CoverArtLoader.RegisterSyntheticCoverForTest(key, bmp);
                }

                try
                {
                    vm.CurrentTrack = MakeTrack(tempDir, key, $"Cover: {coverName.ToUpperInvariant()}", hasCover: bmp != null);

                    foreach (var preset in All3Presets)
                    {
                        var palette = ArtworkPaletteExtractor.ExtractFromBitmapSource(bmp, preset);

                        fsBackdrop.TuningPreset = preset;
                        fsBackdrop.ApplyPaletteImmediatelyForTest(palette);
                        miniStageBackdrop.TuningPreset = preset;
                        miniStageBackdrop.ApplyPaletteImmediatelyForTest(palette);
                        miniTrayBackdrop.TuningPreset = preset;
                        miniTrayBackdrop.ApplyPaletteImmediatelyForTest(palette);

                        // Capture individual screenshots at phase t = 0
                        fsBackdrop.SetDriftTimeForTest(0.0);
                        miniStageBackdrop.SetDriftTimeForTest(0.0);
                        miniTrayBackdrop.SetDriftTimeForTest(0.0);
                        win.UpdateLayout();
                        mini.UpdateLayout();

                        if (captureScreenshots && outDir != null)
                        {
                            SavePng(win, Path.Combine(outDir, $"fs_{ci + 1:D2}_{coverName}_{preset.Name}.png"));
                            SavePng(mini, Path.Combine(outDir, $"mini_{ci + 1:D2}_{coverName}_{preset.Name}.png"));
                        }

                        // Evaluate Full-Screen per-zone worst-case across 6 drift phases
                        double fsOpenY = 0.0;
                        double fsWorstTextY = 0.0;
                        double fsMinPeak = 99, fsMinPri = 99, fsMinSec = 99, fsMinTert = 99;
                        double fsMinD1 = 99, fsMinD2 = 99, fsMinD3 = 99, fsMinDot = 99;

                        double mOpenY = 0.0;
                        double mWorstTextY = 0.0;
                        double mMinPeak = 99, mMinPri = 99, mMinSec = 99, mMinTert = 99;
                        double mMinD1 = 99, mMinD2 = 99, mMinD3 = 99, mMinDot = 99;

                        foreach (double phase in DriftPhasesSeconds)
                        {
                            fsBackdrop.SetDriftTimeForTest(phase);
                            var fsSnap = CaptureBackdropPixels(fsBackdrop);

                            var (_, _, _, openY) = ScanRectBrightest(fsSnap, fsOpenAmbientRect);
                            fsOpenY = Math.Max(fsOpenY, openY);

                            // Text/Icon zones: TopBar, LeftControls, LyricsHost (includes WritersFooter)
                            Rect[] fsTextZones = [fsTopBarRect, fsLeftControlsRect, fsLyricsRect];
                            foreach (var zr in fsTextZones)
                            {
                                var (r, g, b, zy) = ScanRectBrightest(fsSnap, zr);
                                fsWorstTextY = Math.Max(fsWorstTextY, zy);
                                fsMinPeak = Math.Min(fsMinPeak, ArtworkPaletteExtractor.ComputeContrastRatio(peak.R, peak.G, peak.B, r, g, b));
                                fsMinPri = Math.Min(fsMinPri, ArtworkPaletteExtractor.ComputeContrastRatio(primary.R, primary.G, primary.B, r, g, b));
                                fsMinSec = Math.Min(fsMinSec, ArtworkPaletteExtractor.ComputeContrastRatio(secondary.R, secondary.G, secondary.B, r, g, b));
                                fsMinTert = Math.Min(fsMinTert, ArtworkPaletteExtractor.ComputeContrastRatio(tertiary.R, tertiary.G, tertiary.B, r, g, b));
                            }

                            // Lyrics dimmed tiers strictly over LyricsHost rect
                            var (lr, lg, lb, _) = ScanRectBrightest(fsSnap, fsLyricsRect);
                            fsMinD1 = Math.Min(fsMinD1, ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityAdjacent, lr, lg, lb));
                            fsMinD2 = Math.Min(fsMinD2, ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityNear, lr, lg, lb));
                            fsMinD3 = Math.Min(fsMinD3, ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityFar, lr, lg, lb));
                            fsMinDot = Math.Min(fsMinDot, ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityDotUnfilled, lr, lg, lb));

                            // Mini-Player Stage + Tray
                            miniStageBackdrop.SetDriftTimeForTest(phase);
                            miniTrayBackdrop.SetDriftTimeForTest(phase);
                            var msSnap = CaptureBackdropPixels(miniStageBackdrop);
                            var mtSnap = CaptureBackdropPixels(miniTrayBackdrop);

                            var (_, _, _, moY) = ScanRectBrightest(msSnap, miniOpenGlowRect);
                            mOpenY = Math.Max(mOpenY, moY);

                            Rect[] mStageZones = [miniMetaRect, miniControlsRect];
                            foreach (var zr in mStageZones)
                            {
                                var (r, g, b, zy) = ScanRectBrightest(msSnap, zr);
                                mWorstTextY = Math.Max(mWorstTextY, zy);
                                mMinPeak = Math.Min(mMinPeak, ArtworkPaletteExtractor.ComputeContrastRatio(peak.R, peak.G, peak.B, r, g, b));
                                mMinPri = Math.Min(mMinPri, ArtworkPaletteExtractor.ComputeContrastRatio(primary.R, primary.G, primary.B, r, g, b));
                                mMinSec = Math.Min(mMinSec, ArtworkPaletteExtractor.ComputeContrastRatio(secondary.R, secondary.G, secondary.B, r, g, b));
                                mMinTert = Math.Min(mMinTert, ArtworkPaletteExtractor.ComputeContrastRatio(tertiary.R, tertiary.G, tertiary.B, r, g, b));
                            }

                            var (tr, tg, tb, ty) = ScanRectBrightest(mtSnap, miniTrayRect);
                            mWorstTextY = Math.Max(mWorstTextY, ty);
                            mMinPeak = Math.Min(mMinPeak, ArtworkPaletteExtractor.ComputeContrastRatio(peak.R, peak.G, peak.B, tr, tg, tb));
                            mMinPri = Math.Min(mMinPri, ArtworkPaletteExtractor.ComputeContrastRatio(primary.R, primary.G, primary.B, tr, tg, tb));
                            mMinSec = Math.Min(mMinSec, ArtworkPaletteExtractor.ComputeContrastRatio(secondary.R, secondary.G, secondary.B, tr, tg, tb));
                            mMinTert = Math.Min(mMinTert, ArtworkPaletteExtractor.ComputeContrastRatio(tertiary.R, tertiary.G, tertiary.B, tr, tg, tb));
                            mMinD1 = Math.Min(mMinD1, ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityAdjacent, tr, tg, tb));
                            mMinD2 = Math.Min(mMinD2, ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityNear, tr, tg, tb));
                            mMinD3 = Math.Min(mMinD3, ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityFar, tr, tg, tb));
                            mMinDot = Math.Min(mMinDot, ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityDotUnfilled, tr, tg, tb));
                        }

                        results.Add(BuildMatrixRow("FullScreen", coverName, preset.Name, palette, fsOpenY, fsWorstTextY, fsMinPeak, fsMinPri, fsMinSec, fsMinTert, fsMinD1, fsMinD2, fsMinD3, fsMinDot));
                        results.Add(BuildMatrixRow("MiniPlayer", coverName, preset.Name, palette, mOpenY, mWorstTextY, mMinPeak, mMinPri, mMinSec, mMinTert, mMinD1, mMinD2, mMinD3, mMinDot));
                    }
                }
                finally
                {
                    if (key != null) CoverArtLoader.UnregisterSyntheticCoverForTest(key);
                }
            }
        }
        finally
        {
            mini.Close();
            win.Close();
            SpringAnimator.UseExternalClockForTest = prevExt;
            SpringAnimator.ForceReducedMotionOverride = prevRm;
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            GC.Collect();
        }

        return results;
    }

    private static ZoneContrastMatrixRow BuildMatrixRow(
        string surface,
        string cover,
        string preset,
        BackdropPalette palette,
        double openY,
        double worstTextY,
        double minPeak,
        double minPri,
        double minSec,
        double minTert,
        double minD1,
        double minD2,
        double minD3,
        double minDot)
    {
        var fails = new List<string>();
        if (minPeak < 4.5) fails.Add($"Peak={minPeak:F2}<4.5");
        if (minPri < 4.5) fails.Add($"Pri={minPri:F2}<4.5");
        if (minSec < 4.5) fails.Add($"Sec={minSec:F2}<4.5");
        if (minTert < 4.5) fails.Add($"Tert={minTert:F2}<4.5");
        if (minD1 < 4.5) fails.Add($"D1={minD1:F2}<4.5");
        if (minD2 < 3.0) fails.Add($"D2={minD2:F2}<3.0");
        if (minD3 < 3.0) fails.Add($"D3={minD3:F2}<3.0");
        if (minDot < 3.0) fails.Add($"Dot={minDot:F2}<3.0");

        return new ZoneContrastMatrixRow(
            Surface: surface,
            Cover: cover,
            Preset: preset,
            Palette: palette,
            OpenAmbientY: openY,
            WorstTextZoneY: worstTextY,
            MinPeakCr: minPeak,
            MinPrimaryCr: minPri,
            MinSecondaryCr: minSec,
            MinTertiaryCr: minTert,
            MinLyricD1Cr: minD1,
            MinLyricD2Cr: minD2,
            MinLyricD3Cr: minD3,
            MinDotCr: minDot,
            AllGatesPassed: fails.Count == 0,
            FailureDetails: string.Join(", ", fails));
    }

    // =========================================================================
    // CONTACT SHEET BUILDER (PURE-NEUTRAL WPF RENDERING, ZERO EXTERNAL DEPS)
    // =========================================================================

    private static void BuildContactSheet(
        string title,
        string surfacePrefix,
        List<ZoneContrastMatrixRow> rows,
        string shotsDir,
        string outputPath,
        int cellWidth,
        int cellHeight)
    {
        var bgBrush = (Brush)Application.Current.FindResource("Brush.Rail");
        var surfaceBrush = (Brush)Application.Current.FindResource("Brush.Surface");
        var borderBrush = (Brush)Application.Current.FindResource("Brush.Border.Hairline");
        var peakBrush = (Brush)Application.Current.FindResource("Brush.Text.Peak");
        var secBrush = (Brush)Application.Current.FindResource("Brush.Text.Secondary");
        var tertBrush = (Brush)Application.Current.FindResource("Brush.Text.Tertiary");
        var fontDisplay = (FontFamily)Application.Current.FindResource("Font.Display");
        var fontMono = (FontFamily)Application.Current.FindResource("Font.Mono");

        const int labelColW = 136;
        const int pad = 12;
        const int captionH = 26;
        const int headerH = 68;
        int totalW = pad + labelColW + (All3Presets.Length * (cellWidth + pad));
        int rowH = cellHeight + captionH + pad;
        int totalH = headerH + (All10CoverNames.Length * rowH) + pad;

        var root = new Grid
        {
            Width = totalW,
            Height = totalH,
            Background = bgBrush
        };
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);

        var mainStack = new StackPanel { Margin = new Thickness(pad) };

        // Sheet Title
        mainStack.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = fontDisplay,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = peakBrush,
            Margin = new Thickness(0, 0, 0, 8)
        });

        // Column headers
        var colHeader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        colHeader.Children.Add(new TextBlock
        {
            Text = "COVER",
            Width = labelColW,
            FontFamily = fontMono,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = tertBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        foreach (var p in All3Presets)
        {
            string desc = p.UseSpatialScrim
                ? $"{p.Name}  (Y<={p.MaxLinearLuminance:F3}, spatial scrim {p.SpatialAmbientOpacity * 100:F0}%/{p.SpatialLyricsColumnOpacity * 100:F0}%)"
                : $"{p.Name}  (Y<={p.MaxLinearLuminance:F3}, scrim {p.FullScreenScrimTopOpacity * 100:F0}%/{p.FullScreenScrimBottomOpacity * 100:F0}%)";
            colHeader.Children.Add(new TextBlock
            {
                Text = desc,
                Width = cellWidth,
                Margin = new Thickness(0, 0, pad, 0),
                FontFamily = fontMono,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = secBrush
            });
        }
        mainStack.Children.Add(colHeader);

        // Rows = 10 covers
        for (int ci = 0; ci < All10CoverNames.Length; ci++)
        {
            string cover = All10CoverNames[ci];
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, pad) };

            var labelBox = new Border
            {
                Width = labelColW - 8,
                Margin = new Thickness(0, 0, 8, 0),
                Background = surfaceBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8)
            };
            var labelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labelStack.Children.Add(new TextBlock
            {
                Text = $"{ci + 1:D2}. {cover}",
                FontFamily = fontDisplay,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = peakBrush
            });
            labelStack.Children.Add(new TextBlock
            {
                Text = ci < 7 ? "Synthetic" : "Generated Art",
                FontFamily = fontMono,
                FontSize = 10,
                Foreground = tertBrush,
                Margin = new Thickness(0, 2, 0, 0)
            });
            labelBox.Child = labelStack;
            rowPanel.Children.Add(labelBox);

            foreach (var preset in All3Presets)
            {
                string pngPath = Path.Combine(shotsDir, $"{surfacePrefix}_{ci + 1:D2}_{cover}_{preset.Name}.png");
                var metric = rows.First(r => r.Cover == cover && r.Preset == preset.Name);

                var cellStack = new StackPanel { Width = cellWidth, Margin = new Thickness(0, 0, pad, 0) };
                var imgFrame = new Border
                {
                    Width = cellWidth,
                    Height = cellHeight,
                    BorderBrush = borderBrush,
                    BorderThickness = new Thickness(1),
                    Background = bgBrush
                };
                if (File.Exists(pngPath))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(pngPath, UriKind.Absolute);
                    bmp.DecodePixelWidth = cellWidth;
                    bmp.EndInit();
                    bmp.Freeze();

                    var img = new Image { Source = bmp, Stretch = Stretch.Uniform };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    imgFrame.Child = img;
                }
                cellStack.Children.Add(imgFrame);

                cellStack.Children.Add(new TextBlock
                {
                    Height = captionH,
                    Padding = new Thickness(2, 4, 2, 0),
                    FontFamily = fontMono,
                    FontSize = 10,
                    Foreground = secBrush,
                    Text = $"OpenY={metric.OpenAmbientY:F3} TextY={metric.WorstTextZoneY:F3} | Tert={metric.MinTertiaryCr:F2}:1 Dot={metric.MinDotCr:F2}:1"
                });

                rowPanel.Children.Add(cellStack);
            }

            mainStack.Children.Add(rowPanel);
        }

        root.Children.Add(mainStack);
        root.Measure(new Size(totalW, totalH));
        root.Arrange(new Rect(0, 0, totalW, totalH));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(totalW, totalH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outputPath);
        enc.Save(fs);
    }

    // =========================================================================
    // HELPERS: PIXEL SCANNING, VISUAL TREE INSPECTION, WIN32 / DWM TIMING
    // =========================================================================

    private sealed record PixelSnapshot(byte[] Pixels, int Width, int Height, int Stride, double DpiScale);

    private static PixelSnapshot CaptureBackdropPixels(FrameworkElement ctrl)
    {
        ctrl.UpdateLayout();
        int width = Math.Max(1, (int)Math.Round(ctrl.ActualWidth));
        int height = Math.Max(1, (int)Math.Round(ctrl.ActualHeight));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(ctrl);
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        rtb.CopyPixels(pixels, stride, 0);
        return new PixelSnapshot(pixels, width, height, stride, 1.0);
    }

    private static (byte R, byte G, byte B, double MaxY) ScanRectBrightest(PixelSnapshot snap, Rect dipRect)
    {
        int x0 = Math.Clamp((int)Math.Floor(dipRect.Left * snap.DpiScale), 0, snap.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(dipRect.Top * snap.DpiScale), 0, snap.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(dipRect.Right * snap.DpiScale), x0 + 1, snap.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(dipRect.Bottom * snap.DpiScale), y0 + 1, snap.Height);

        double maxY = -1.0;
        byte maxR = 0, maxG = 0, maxB = 0;

        for (int y = y0; y < y1; y++)
        {
            int row = y * snap.Stride;
            for (int x = x0; x < x1; x++)
            {
                int i = row + (x * 4);
                byte b = snap.Pixels[i];
                byte g = snap.Pixels[i + 1];
                byte r = snap.Pixels[i + 2];
                double lum = ArtworkPaletteExtractor.ComputeRelativeLuminance(r, g, b);
                if (lum > maxY)
                {
                    maxY = lum;
                    maxR = r;
                    maxG = g;
                    maxB = b;
                }
            }
        }

        return (maxR, maxG, maxB, Math.Max(0.0, maxY));
    }

    private static List<Rect> CollectImageBrushRects(FrameworkElement root)
    {
        var rects = new List<Rect>();
        void Walk(DependencyObject node)
        {
            if (node is FrameworkElement fe && fe.IsVisible && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            {
                bool hasArtwork =
                    (fe is Border b && b.Background is ImageBrush ib && ib.ImageSource != null) ||
                    (fe is Image img && img.Source != null);
                if (hasArtwork)
                {
                    var r = GetElementBoundsInRoot(fe, root);
                    // Pad by 2 DIP for rounded-corner anti-aliased edge pixels around cover art
                    r.Inflate(2.0, 2.0);
                    rects.Add(r);
                }
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        return rects;
    }

    private static (int ChromaticPixels, int ScannedPixels) CountChromaticPixelsExcludingRects(
        FrameworkElement root,
        List<Rect> excludedDipRects)
    {
        root.UpdateLayout();
        int width = Math.Max(1, (int)Math.Round(root.ActualWidth));
        int height = Math.Max(1, (int)Math.Round(root.ActualHeight));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        rtb.CopyPixels(pixels, stride, 0);

        int chromatic = 0;
        int scanned = 0;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                bool masked = false;
                for (int r = 0; r < excludedDipRects.Count; r++)
                {
                    if (excludedDipRects[r].Contains(x, y))
                    {
                        masked = true;
                        break;
                    }
                }
                if (masked) continue;

                scanned++;
                int i = row + (x * 4);
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte rCh = pixels[i + 2];
                if (rCh != g || g != b)
                {
                    chromatic++;
                }
            }
        }

        return (chromatic, scanned);
    }

    private static void AuditVisualTreeBrushesOutsideBackdrop(
        DependencyObject node,
        ref int brushesChecked,
        ref int nonNeutralBrushes,
        List<string> notes)
    {
        if (node is ImmersiveBackdropControl)
        {
            return; // Rule 3 allowlisted layer
        }
        if (node is UIElement ui && !ui.IsVisible)
        {
            return;
        }

        string tag = node is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)
            ? $"{node.GetType().Name}#{fe.Name}"
            : node.GetType().Name;

        switch (node)
        {
            case Border b:
                if (b.Background is not ImageBrush) InspectBrushNeutrality(b.Background, $"{tag}.Background", ref brushesChecked, ref nonNeutralBrushes, notes);
                InspectBrushNeutrality(b.BorderBrush, $"{tag}.BorderBrush", ref brushesChecked, ref nonNeutralBrushes, notes);
                break;
            case Panel p:
                InspectBrushNeutrality(p.Background, $"{tag}.Background", ref brushesChecked, ref nonNeutralBrushes, notes);
                break;
            case TextBlock tb:
                InspectBrushNeutrality(tb.Foreground, $"{tag}.Foreground", ref brushesChecked, ref nonNeutralBrushes, notes);
                InspectBrushNeutrality(tb.Background, $"{tag}.Background", ref brushesChecked, ref nonNeutralBrushes, notes);
                break;
            case Control ctrl:
                InspectBrushNeutrality(ctrl.Background, $"{tag}.Background", ref brushesChecked, ref nonNeutralBrushes, notes);
                InspectBrushNeutrality(ctrl.Foreground, $"{tag}.Foreground", ref brushesChecked, ref nonNeutralBrushes, notes);
                if (ctrl.BorderThickness.Left > 0 || ctrl.BorderThickness.Top > 0 || ctrl.BorderThickness.Right > 0 || ctrl.BorderThickness.Bottom > 0)
                {
                    InspectBrushNeutrality(ctrl.BorderBrush, $"{tag}.BorderBrush", ref brushesChecked, ref nonNeutralBrushes, notes);
                }
                break;
            case Shape s:
                InspectBrushNeutrality(s.Fill, $"{tag}.Fill", ref brushesChecked, ref nonNeutralBrushes, notes);
                InspectBrushNeutrality(s.Stroke, $"{tag}.Stroke", ref brushesChecked, ref nonNeutralBrushes, notes);
                break;
        }

        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            AuditVisualTreeBrushesOutsideBackdrop(VisualTreeHelper.GetChild(node, i), ref brushesChecked, ref nonNeutralBrushes, notes);
        }
    }

    private static void InspectBrushNeutrality(Brush? brush, string where, ref int brushesChecked, ref int nonNeutralBrushes, List<string> notes)
    {
        if (brush == null) return;
        if (brush is SolidColorBrush scb)
        {
            brushesChecked++;
            Color c = scb.Color;
            if (c.A > 0 && (c.R != c.G || c.G != c.B))
            {
                nonNeutralBrushes++;
                notes.Add($"{where}=ARGB({c.A},{c.R},{c.G},{c.B})");
            }
        }
        else if (brush is GradientBrush gb)
        {
            foreach (var stop in gb.GradientStops)
            {
                brushesChecked++;
                Color c = stop.Color;
                if (c.A > 0 && (c.R != c.G || c.G != c.B))
                {
                    nonNeutralBrushes++;
                    notes.Add($"{where}.Stop=ARGB({c.A},{c.R},{c.G},{c.B})");
                }
            }
        }
    }

    private static Rect GetElementBoundsInRoot(FrameworkElement element, Visual root)
    {
        try
        {
            var transform = element.TransformToVisual(root);
            return transform.TransformBounds(new Rect(0, 0, Math.Max(0, element.ActualWidth), Math.Max(0, element.ActualHeight)));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private static void SavePng(FrameworkElement target, string path)
    {
        target.UpdateLayout();
        double dpiScale = VisualTreeHelper.GetDpi(target).DpiScaleX;
        int pw = Math.Max(1, (int)Math.Round(target.ActualWidth * dpiScale));
        int ph = Math.Max(1, (int)Math.Round(target.ActualHeight * dpiScale));
        var rtb = new RenderTargetBitmap(pw, ph, 96.0 * dpiScale, 96.0 * dpiScale, PixelFormats.Pbgra32);
        rtb.Render(target);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static Track MakeTrack(string dir, string? coverKey, string title, bool hasCover)
    {
        var spec = new AudioStreamSpec(AudioCodec.FLAC, 96000, BitDepth.Bit24, 2, 2816);
        return new Track(
            id: 1,
            filePath: Path.Combine(dir, "track.flac"),
            title: title,
            artist: "AuraAudio v2.1 Verification",
            sortArtist: "AuraAudio",
            album: "Phase 4 Verification",
            albumArtist: "AuraAudio",
            year: 2026,
            trackNumber: 1,
            discNumber: 1,
            duration: TimeSpan.FromMinutes(4.2),
            streamSpec: spec,
            fileSizeBytes: 42_000_000,
            dateAdded: DateTime.UtcNow,
            hasEmbeddedCover: hasCover,
            coverArtCacheKey: coverKey);
    }

    private static T? FindFirstVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) return match;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindFirstVisualChild<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private static T? FindByAutomationId<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is T match && string.Equals(System.Windows.Automation.AutomationProperties.GetAutomationId(root), id, StringComparison.Ordinal))
        {
            return match;
        }
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindByAutomationId<T>(VisualTreeHelper.GetChild(root, i), id);
            if (found != null) return found;
        }
        return null;
    }

    private static void PumpDispatcherFor(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static double Pct(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0.0;
        double idx = (p / 100.0) * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx);
        int hi = Math.Min(sorted.Count - 1, lo + 1);
        double frac = idx - lo;
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * frac);
    }

    private static void BringWindowToForeground(Window win)
    {
        win.WindowState = WindowState.Normal;
        win.Activate();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetForegroundWindow(hwnd);
        }
    }

    private static void SendSyntheticNopMouseMove()
    {
        var input = new INPUT
        {
            type = 0, // INPUT_MOUSE
            mi = new MOUSEINPUT { dx = 0, dy = 0, dwFlags = 0x0001 } // MOUSEEVENTF_MOVE
        };
        _ = SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    private static (int Width, int Height, double RefreshHz, double VsyncMs) QueryDisplayMetrics()
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(null, -1, ref dm) && dm.dmDisplayFrequency >= 30)
        {
            return (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, 1000.0 / dm.dmDisplayFrequency);
        }
        return (1920, 1080, 60.0, 16.67);
    }

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO pTimingInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNSIGNED_RATIO
    {
        public uint uiNumerator;
        public uint uiDenominator;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DWM_TIMING_INFO
    {
        public uint cbSize;
        public UNSIGNED_RATIO rateRefresh;
        public ulong qpcRefreshPeriod;
        public UNSIGNED_RATIO rateCompose;
        public ulong qpcVBlank;
        public ulong cRefresh;
        public uint cDXRefresh;
        public ulong qpcCompose;
        public ulong cFrame;
        public uint cDXPresent;
        public ulong cRefreshFrame;
        public ulong cFrameSubmitted;
        public uint cDXPresentSubmitted;
        public ulong cFrameConfirmed;
        public uint cDXPresentConfirmed;
        public ulong cRefreshConfirmed;
        public uint cDXRefreshConfirmed;
        public ulong cFramesLate;
        public uint cFramesOutstanding;
        public ulong cFrameDisplayed;
        public ulong qpcFrameDisplayed;
        public ulong cRefreshFrameDisplayed;
        public ulong cFrameComplete;
        public ulong qpcFrameComplete;
        public ulong cFramePending;
        public ulong qpcFramePending;
        public ulong cFramesDisplayed;
        public ulong cFramesComplete;
        public ulong cFramesPending;
        public ulong cFramesAvailable;
        public ulong cFramesDropped;
        public ulong cFramesMissed;
        public ulong cRefreshNextDisplayed;
        public ulong cRefreshNextPresented;
        public ulong cRefreshesDisplayed;
        public ulong cRefreshesPresented;
        public ulong cRefreshStarted;
        public ulong cPixelsReceived;
        public ulong cPixelsDrawn;
        public ulong cBuffersEmpty;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public uint dmDisplayFlags;
        public int dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
