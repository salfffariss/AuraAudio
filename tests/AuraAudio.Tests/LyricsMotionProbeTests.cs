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
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using AuraAudio.Audio;
using AuraAudio.Audio.Controller;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Metadata;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;
using AuraAudio.UI.Views;
using AuraAudio.UI.Windows;
using Xunit;
using Xunit.Abstractions;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public sealed class LyricsMotionProbeTests
{
    private readonly ITestOutputHelper _output;

    public LyricsMotionProbeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    private static (int Width, int Height, int RefreshHz) GetPrimaryDisplayMode()
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(null, -1, ref dm) && dm.dmDisplayFrequency > 0)
        {
            return (dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency);
        }
        return (1920, 1080, 60);
    }

    private static ParsedLyricsResult BuildRealistic120LineSyncedLyrics()
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine("[au:Test Composer]");
        double t = 2.0;
        for (int i = 0; i < 120; i++)
        {
            // Insert three realistic 8-second instrumental gaps at lines 20, 55, 90
            if (i == 20 || i == 55 || i == 90)
            {
                t += 8.0;
            }
            int min = (int)(t / 60);
            double sec = t - min * 60;
            string text = (i % 4) switch
            {
                0 => $"Line {i + 1:D3}: Walking past the quiet harbor lights while the midnight train pulls away from town",
                1 => $"Line {i + 1:D3}: Short refrain in the dark",
                2 => $"Line {i + 1:D3}: Every distant streetlamp echoes across the wet avenue beneath a low silver cloud",
                _ => $"Line {i + 1:D3}: Hold onto the melody before it fades"
            };
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[{0:D2}:{1:00.00}]{2}", min, sec, text));
            t += 2.2;
        }
        return LyricsParser.ParseLrcContent(sb.ToString(), LyricsSourceKind.SidecarLrc);
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent == null) return null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void AuditVisualTree(
        DependencyObject? root,
        ref int totalNodes,
        ref int effectNodes,
        ref int bitmapCacheNodes,
        ref int realizedListBoxItems,
        ref int textBlockNodes)
    {
        if (root == null) return;
        totalNodes++;
        if (root is UIElement ue)
        {
            if (ue.Effect != null) effectNodes++;
            if (ue.CacheMode is BitmapCache) bitmapCacheNodes++;
        }
        if (root is ListBoxItem) realizedListBoxItems++;
        if (root is TextBlock) textBlockNodes++;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            AuditVisualTree(
                VisualTreeHelper.GetChild(root, i),
                ref totalNodes,
                ref effectNodes,
                ref bitmapCacheNodes,
                ref realizedListBoxItems,
                ref textBlockNodes);
        }
    }

    [SkippableFact]
    public void MeasureBaselineLyricsMotion_FullScreenAndRightPane()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("AURA_RUN_LYRICS_PROBE") == "1",
            "Opt-in Phase 1 baseline lyrics smoothness probe (set AURA_RUN_LYRICS_PROBE=1).");

        string outDir = Environment.GetEnvironmentVariable("AURA_LYRICS_PROBE_OUTDIR")
            ?? Path.Combine(Path.GetTempPath(), "AuraAudio_v21_LyricsProbe");
        Directory.CreateDirectory(outDir);

        var (dispW, dispH, refreshHz) = GetPrimaryDisplayMode();
        double vsyncMs = 1000.0 / refreshHz;

        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_LyricsProbeDb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "probe.db");
        var report = new StringBuilder(8192);

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool? prevOverride = SpringAnimator.ForceReducedMotionOverride;
                SpringAnimator.ForceReducedMotionOverride = false;

                using var db = new DatabaseManager(dbPath);
                db.Initialize();
                using var controller = new AudiophilePlaybackController(new NullAudioOutputEngine());
                using var vm = new MainViewModel(controller: controller, db: db);

                var lyrics = BuildRealistic120LineSyncedLyrics();
                var dummyTrack = new Track(
                    id: 1,
                    filePath: Path.Combine(tempDir, "probe_track.flac"),
                    title: "Probe Song (120 Synced Lines)",
                    artist: "Measurement Harness",
                    sortArtist: "Measurement Harness",
                    album: "v2.1 Motion Baseline",
                    albumArtist: "Measurement Harness",
                    year: 2026,
                    trackNumber: 1,
                    discNumber: 1,
                    duration: TimeSpan.FromSeconds(320),
                    streamSpec: new AudioStreamSpec(AudioCodec.FLAC, 48000, BitDepth.Bit24, 2, 1600),
                    fileSizeBytes: 25_000_000,
                    dateAdded: DateTime.UtcNow,
                    hasEmbeddedCover: false,
                    coverArtCacheKey: null);

                vm.CurrentTrack = dummyTrack;
                vm.Lyrics.ApplyParsedResult(lyrics);
                vm.Lyrics.UpdatePlaybackPosition(lyrics.Lines[2].Timestamp + TimeSpan.FromMilliseconds(100));

                var win = new MainWindow(vm)
                {
                    Width = 1536,
                    Height = 860,
                    Left = 0,
                    Top = 0,
                    ShowInTaskbar = false
                };

                try
                {
                    win.Show();
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(120);

                    double idleCompositionMs = MotionProbe.MeasureIdleVsyncMs(win, 24);
                    int renderTier = RenderCapability.Tier >> 16;

                    report.AppendLine("=== Phase 2 Restyled Lyrics Motion Probe (SyncedLyricsViewport) ===");
                    report.AppendLine($"DisplayMode={dispW}x{dispH} @ {refreshHz} Hz (vsync={vsyncMs:F2} ms), IdleCompositionTargetMs={idleCompositionMs:F2} ms ({1000.0 / idleCompositionMs:F1} Hz), RenderTier={renderTier}, ReducedMotion={SpringAnimator.IsReducedMotion}");
                    report.AppendLine($"SyncedLinesTotal={vm.Lyrics.Lines.Count} (including {vm.Lyrics.Lines.Count(l => l.IsInstrumentalGap)} instrumental gap markers)");
                    report.AppendLine();

                    // Helper: run BOTH real-time CompositionTarget.Rendering capture AND deterministic display-rate micro-probe
                    void ProbeLyricsHost(string hostLabel, LyricsView lyricsView, bool drive20HzSnapshotTicks)
                    {
                        win.UpdateLayout();
                        MotionProbe.PumpDispatcher(80);

                        var viewport = lyricsView.SyncedViewportControl;
                        Assert.NotNull(viewport);

                        // Reset cleanly to line 5
                        vm.Lyrics.UpdatePlaybackPosition(lyrics.Lines[5].Timestamp + TimeSpan.FromMilliseconds(50), isPlaying: true);
                        viewport.SyncToActiveLine(immediate: true);
                        MotionProbe.PumpDispatcher(120);
                        win.UpdateLayout();

                        // 1. Visual tree audit
                        int nodes = 0, effects = 0, caches = 0, realizedItems = 0, textBlocks = 0;
                        AuditVisualTree(lyricsView, ref nodes, ref effects, ref caches, ref realizedItems, ref textBlocks);

                        // 2. Real-time CompositionTarget.Rendering probe over 13 consecutive line transitions (every 260ms)
                        var rtDts = new List<double>(4096);
                        var uniqueRenderingDts = new List<double>(1024);
                        var uniqueWallDts = new List<double>(1024);
                        var uniqueOffsets = new List<(double tMs, double offset)>(1024);
                        var rtOffsets = new List<(double tMs, double offset)>(4096);
                        int rtLayoutPasses = 0;
                        int duplicateRenderingTimeCount = 0;
                        void OnLayoutUpdated(object? s, EventArgs e) => rtLayoutPasses++;
                        lyricsView.LayoutUpdated += OnLayoutUpdated;

                        SpringAnimator.ResetRenderingDedupForTest();
                        var frame = new DispatcherFrame();
                        long prevTicks = Stopwatch.GetTimestamp();
                        long prevUniqueWallTicks = prevTicks;
                        TimeSpan lastRenderingTime = TimeSpan.MinValue;
                        long allocStart = GC.GetAllocatedBytesForCurrentThread();
                        var sw = Stopwatch.StartNew();
                        int nextLineStep = 6;
                        int maxLineStep = 18;
                        double nextSwitchMs = 80.0;
                        double nextPollMs = 50.0;
                        double totalRunMs = 3600.0;

                        void OnRender(object? s, EventArgs e)
                        {
                            long nowTicks = Stopwatch.GetTimestamp();
                            double dtMs = (double)(nowTicks - prevTicks) * 1000.0 / Stopwatch.Frequency;
                            prevTicks = nowTicks;
                            double tMs = sw.Elapsed.TotalMilliseconds;

                            TimeSpan rTime = (e is RenderingEventArgs rea) ? rea.RenderingTime : TimeSpan.Zero;
                            if (rTime == lastRenderingTime)
                            {
                                duplicateRenderingTimeCount++;
                            }
                            else
                            {
                                if (lastRenderingTime != TimeSpan.MinValue)
                                {
                                    uniqueRenderingDts.Add((rTime - lastRenderingTime).TotalMilliseconds);
                                    uniqueWallDts.Add((double)(nowTicks - prevUniqueWallTicks) * 1000.0 / Stopwatch.Frequency);
                                }
                                lastRenderingTime = rTime;
                                prevUniqueWallTicks = nowTicks;
                                uniqueOffsets.Add((tMs, viewport.CurrentScrollOffset));
                            }

                            rtDts.Add(dtMs);
                            rtOffsets.Add((tMs, viewport.CurrentScrollOffset));

                            if (drive20HzSnapshotTicks && tMs >= nextPollMs)
                            {
                                nextPollMs += 50.0;
                                vm.CurrentTime = lyrics.Lines[Math.Min(nextLineStep, lyrics.Lines.Count - 1)].Timestamp;
                                vm.SeekSliderValue = vm.CurrentTime.TotalSeconds;
                            }

                            if (nextLineStep <= maxLineStep && tMs >= nextSwitchMs)
                            {
                                vm.Lyrics.UpdatePlaybackPosition(lyrics.Lines[nextLineStep].Timestamp + TimeSpan.FromMilliseconds(20), isPlaying: true);
                                nextLineStep++;
                                nextSwitchMs += 260.0;
                            }

                            if (tMs >= totalRunMs)
                            {
                                CompositionTarget.Rendering -= OnRender;
                                frame.Continue = false;
                            }
                        }

                        CompositionTarget.Rendering += OnRender;
                        Dispatcher.PushFrame(frame);
                        long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocStart;
                        lyricsView.LayoutUpdated -= OnLayoutUpdated;

                        var sortedRt = rtDts.Skip(2).OrderBy(x => x).ToList();
                        var sortedUniqueRender = uniqueRenderingDts.Skip(1).OrderBy(x => x).ToList();
                        var sortedUniqueWall = uniqueWallDts.Skip(1).OrderBy(x => x).ToList();
                        double rtP50 = Percentile(sortedRt, 0.50);
                        double rtP95 = Percentile(sortedRt, 0.95);
                        double rtP99 = Percentile(sortedRt, 0.99);
                        double rtMax = sortedRt.Count > 0 ? sortedRt[^1] : 0;

                        double uP50 = Percentile(sortedUniqueWall, 0.50);
                        double uP95 = Percentile(sortedUniqueWall, 0.95);
                        double uP99 = Percentile(sortedUniqueWall, 0.99);
                        double uMax = sortedUniqueWall.Count > 0 ? sortedUniqueWall[^1] : 0;
                        int uDrop15x = sortedUniqueWall.Count(x => x > 1.5 * vsyncMs);
                        int uDrop3x = sortedUniqueWall.Count(x => x > 3.0 * vsyncMs);
                        int uDrop15x60 = sortedUniqueWall.Count(x => x > 25.0);
                        int uDrop3x60 = sortedUniqueWall.Count(x => x > 50.0);

                        int uniqueMovedFrames = 0;
                        for (int i = 1; i < uniqueOffsets.Count; i++)
                        {
                            if (Math.Abs(uniqueOffsets[i].offset - uniqueOffsets[i - 1].offset) > 1e-4)
                                uniqueMovedFrames++;
                        }

                        int offsetChangedFrames = 0;
                        int tinyStepFrames = 0;
                        for (int i = 1; i < rtOffsets.Count; i++)
                        {
                            double diff = Math.Abs(rtOffsets[i].offset - rtOffsets[i - 1].offset);
                            if (diff > 1e-4)
                            {
                                offsetChangedFrames++;
                                if (diff < 1.0) tinyStepFrames++;
                            }
                        }
                        double effectiveUniqueFps = uP50 > 0 ? 1000.0 / uP50 : 0;
                        double effectiveMovedFps = totalRunMs > 0 ? uniqueMovedFrames / (totalRunMs / 1000.0) : 0;

                        // Save real-time CSV for inspection
                        string csvPath = Path.Combine(outDir, $"{hostLabel}_realtime.csv");
                        var csv = new StringBuilder(16384);
                        csv.AppendLine("frame,t_ms,dt_ms,scroll_y");
                        for (int i = 0; i < rtOffsets.Count; i++)
                        {
                            double dt = i < rtDts.Count ? rtDts[i] : 0;
                            csv.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1:F2},{2:F2},{3:F3}", i, rtOffsets[i].tMs, dt, rtOffsets[i].offset));
                        }
                        File.WriteAllText(csvPath, csv.ToString());

                        // 3. Deterministic steady-state StepFrame benchmark at display refresh rate (120 frames)
                        const int scrollFrames = 120;
                        double dtSec = 1.0 / refreshHz;
                        var scrollWorkMs = new List<double>(scrollFrames);
                        var scrollAllocBytes = new List<long>(scrollFrames);
                        int detLayoutPassesScroll = 0;
                        void OnScrollLayout(object? s, EventArgs e) => detLayoutPassesScroll++;

                        bool prevExt = SpringAnimator.UseExternalClockForTest;
                        SpringAnimator.UseExternalClockForTest = true;
                        try
                        {
                            // Start at line 5, trigger auto-scroll to line 9 (within already-bound pool window)
                            vm.Lyrics.UpdatePlaybackPosition(lyrics.Lines[5].Timestamp + TimeSpan.FromMilliseconds(20), isPlaying: true);
                            viewport.SyncToActiveLine(immediate: true);
                            win.UpdateLayout();

                            vm.Lyrics.UpdatePlaybackPosition(lyrics.Lines[9].Timestamp + TimeSpan.FromMilliseconds(20), isPlaying: true);
                            // Warm up 2 frames
                            SpringAnimator.StepExternalClockForTest(dtSec);
                            SpringAnimator.StepExternalClockForTest(dtSec);
                            win.UpdateLayout();

                            lyricsView.LayoutUpdated += OnScrollLayout;
                            for (int f = 0; f < scrollFrames; f++)
                            {
                                long a0 = GC.GetAllocatedBytesForCurrentThread();
                                long t0 = Stopwatch.GetTimestamp();
                                SpringAnimator.StepExternalClockForTest(dtSec);
                                win.UpdateLayout();
                                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                                long aBytes = GC.GetAllocatedBytesForCurrentThread() - a0;

                                scrollWorkMs.Add(ms);
                                scrollAllocBytes.Add(aBytes);
                            }
                            lyricsView.LayoutUpdated -= OnScrollLayout;

                            // Measure B: 20 line transitions (zero FontSize/FontWeight reflow)
                            var transitionWorkMs = new List<double>(20);
                            var transitionAllocBytes = new List<long>(20);
                            int transitionLayoutPasses = 0;
                            void OnTransLayout(object? s, EventArgs e) => transitionLayoutPasses++;
                            lyricsView.LayoutUpdated += OnTransLayout;
                            for (int idx = 20; idx < 40; idx++)
                            {
                                long a0 = GC.GetAllocatedBytesForCurrentThread();
                                long t0 = Stopwatch.GetTimestamp();
                                vm.Lyrics.UpdatePlaybackPosition(lyrics.Lines[idx].Timestamp + TimeSpan.FromMilliseconds(20), isPlaying: true);
                                SpringAnimator.StepExternalClockForTest(dtSec);
                                win.UpdateLayout();
                                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                                long aBytes = GC.GetAllocatedBytesForCurrentThread() - a0;
                                transitionWorkMs.Add(ms);
                                transitionAllocBytes.Add(aBytes);
                            }
                            lyricsView.LayoutUpdated -= OnTransLayout;

                            scrollWorkMs.Sort();
                            transitionWorkMs.Sort();

                            report.AppendLine($"--- HOST: {hostLabel} (Viewport={viewport.ActualWidth:F1}x{viewport.ActualHeight:F1}, ExtentH={viewport.TotalExtent:F1}) ---");
                            report.AppendLine($"  VisualTree: Nodes={nodes}, TextBlocks={textBlocks}, PooledRows={viewport.RealizedRowCount}/{viewport.LogicalRowCount}, Effects={effects}, BitmapCache={caches}");
                            report.AppendLine($"  RealTime CompositionTarget.Rendering (RawCallbacks={sortedRt.Count}, UniqueRenderingTimeFrames={sortedUniqueWall.Count}, DuplicateCallbacks={duplicateRenderingTimeCount} over {totalRunMs:F0} ms, 13 line transitions):");
                            report.AppendLine($"    UniqueCompositorWallDt: p50={uP50:F2} ms ({effectiveUniqueFps:F1} fps), p95={uP95:F2} ms, p99={uP99:F2} ms, max={uMax:F2} ms");
                            report.AppendLine($"    UniqueRenderingTimeDt : p50={Percentile(sortedUniqueRender, 0.50):F2} ms, p95={Percentile(sortedUniqueRender, 0.95):F2} ms, p99={Percentile(sortedUniqueRender, 0.99):F2} ms, max={(sortedUniqueRender.Count > 0 ? sortedUniqueRender[^1] : 0):F2} ms");
                            report.AppendLine($"    RawCallbackDt         : p50={rtP50:F2} ms, p95={rtP95:F2} ms, p99={rtP99:F2} ms, max={rtMax:F2} ms");
                            report.AppendLine($"    Dropped (Unique) @ {refreshHz}Hz: >1.5x({1.5 * vsyncMs:F2}ms)={uDrop15x}/{sortedUniqueWall.Count} ({100.0 * uDrop15x / Math.Max(1, sortedUniqueWall.Count):F1}%), >3.0x({3.0 * vsyncMs:F2}ms)={uDrop3x}/{sortedUniqueWall.Count} ({100.0 * uDrop3x / Math.Max(1, sortedUniqueWall.Count):F1}%)");
                            report.AppendLine($"    Dropped (Unique) @ 60Hz: >25ms={uDrop15x60}/{sortedUniqueWall.Count}, >50ms={uDrop3x60}/{sortedUniqueWall.Count}");
                            report.AppendLine($"    UniqueFramesWithScrollMove={uniqueMovedFrames}/{uniqueOffsets.Count} ({effectiveMovedFps:F1} fps), RawMovedCallbacks={offsetChangedFrames}/{rtOffsets.Count}, TinySubPxMoves(<1px)={tinyStepFrames}, LayoutUpdatedPasses={rtLayoutPasses}, TotalAlloc={allocDelta / 1024.0:F1} KB ({allocDelta / Math.Max(1, sortedUniqueWall.Count):N0} B/uniqueFrame)");
                            report.AppendLine($"  Det SteadyState [SyncedLyricsViewport.StepFrame ({scrollFrames} frames @ {refreshHz}Hz)]:");
                            report.AppendLine($"    WorkMs p50={Percentile(scrollWorkMs, 0.50):F4} ms, p95={Percentile(scrollWorkMs, 0.95):F4} ms, p99={Percentile(scrollWorkMs, 0.99):F4} ms, max={scrollWorkMs[^1]:F4} ms | LayoutPasses={detLayoutPassesScroll} | AvgAlloc={scrollAllocBytes.Average():F1} B/frame");
                            report.AppendLine($"  Det LineTransitions [20 line switches (indices 20..39)]:");
                            report.AppendLine($"    WorkMs p50={Percentile(transitionWorkMs, 0.50):F3} ms, p95={Percentile(transitionWorkMs, 0.95):F3} ms, max={transitionWorkMs[^1]:F3} ms | LayoutPasses={transitionLayoutPasses} ({(double)transitionLayoutPasses / 20.0:F2}/trans) | AvgAlloc={transitionAllocBytes.Average():F0} B/trans");
                            report.AppendLine($"  CSV: {csvPath}");
                            report.AppendLine();
                        }
                        finally
                        {
                            SpringAnimator.UseExternalClockForTest = prevExt;
                        }
                    }

                    // SCENARIO 1: Full-Screen Split Lyrics View
                    vm.Presentation.IsFullScreenLyricsOpen = true;
                    vm.Presentation.IsFullScreenOpen = true;
                    MotionProbe.PumpDispatcher(450);
                    win.UpdateLayout();

                    var fsOverlay = (FullScreenPlayerView)win.FindName("FullScreenStageOverlay");
                    var fsLyricsView = FindVisualChild<LyricsView>(fsOverlay);
                    Assert.NotNull(fsLyricsView);
                    ProbeLyricsHost("FullScreenSplit", fsLyricsView!, drive20HzSnapshotTicks: true);

                    // Close Full-Screen, Open Right Pane Lyrics (360px)
                    vm.Presentation.IsFullScreenOpen = false;
                    MotionProbe.PumpDispatcher(350);
                    vm.Presentation.ActiveRightPane = RightPaneMode.Lyrics;
                    MotionProbe.PumpDispatcher(450);
                    win.UpdateLayout();

                    var rightPane = (Border)win.FindName("RightSplitPaneBorder");
                    var paneLyricsView = FindVisualChild<LyricsView>(rightPane);
                    Assert.NotNull(paneLyricsView);
                    ProbeLyricsHost("RightPane360px", paneLyricsView!, drive20HzSnapshotTicks: true);
                }
                finally
                {
                    SpringAnimator.ForceReducedMotionOverride = prevOverride;
                    win.Close();
                }
            });

            string summaryFile = Environment.GetEnvironmentVariable("AURA_LYRICS_PROBE_FILE") ?? "phase2_lyrics_probe.txt";
            string summaryPath = Path.Combine(outDir, summaryFile);
            File.WriteAllText(summaryPath, report.ToString());
            _output.WriteLine(report.ToString());
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
