using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class MotionAuditDiagnosticsTests
{
    private static string PrepareTempLibraryCopy(out string tempDir)
    {
        tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_MotionProbe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string destDb = Path.Combine(tempDir, "library_probe.db");

        string realDb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AuraAudio",
            "library.db");

        if (File.Exists(realDb))
        {
            File.Copy(realDb, destDb, overwrite: true);
            return destDb;
        }

        // Fallback realistic heavy library: 80 albums, 320 tracks
        using var db = new DatabaseManager(destDb);
        db.Initialize();
        var spec = new AudioStreamSpec(AudioCodec.FLAC, 48000, BitDepth.Bit24, 2, 2304);
        var tracks = new List<Track>(320);
        int id = 1;
        for (int a = 1; a <= 80; a++)
        {
            for (int t = 1; t <= 4; t++)
            {
                tracks.Add(new Track(
                    id: id++,
                    filePath: Path.Combine(tempDir, $"Album{a:D2}_Track{t:D2}.flac"),
                    title: $"Track {t:D2} of Album {a:D2}",
                    artist: $"Artist {(a % 15) + 1:D2}",
                    sortArtist: $"Artist {(a % 15) + 1:D2}",
                    album: $"Album {a:D2}",
                    albumArtist: $"Artist {(a % 15) + 1:D2}",
                    year: 2020 + (a % 6),
                    trackNumber: t,
                    discNumber: 1,
                    duration: TimeSpan.FromMinutes(3.5),
                    streamSpec: spec,
                    fileSizeBytes: 42_000_000,
                    dateAdded: DateTime.UtcNow.AddDays(-a),
                    hasEmbeddedCover: false,
                    coverArtCacheKey: null));
            }
        }
        db.InsertTracksBatch(tracks);
        return destDb;
    }

    private static ParsedLyricsResult BuildHeavySyncedLyrics(int lineCount = 120)
    {
        var sb = new StringBuilder(lineCount * 48);
        sb.AppendLine("[au:Motion Probe Benchmark Composer]");
        for (int i = 0; i < lineCount; i++)
        {
            int sec = i * 2;
            sb.AppendLine($"[{sec / 60:D2}:{sec % 60:D2}.00]Line {i + 1:D3}: Pure monochrome synced lyrics continuity benchmark");
        }
        return LyricsParser.ParseLrcContent(sb.ToString(), LyricsSourceKind.SidecarLrc);
    }

    [SkippableFact]
    public void BenchmarkCandidatesABC()
    {
        string? runFlag = Environment.GetEnvironmentVariable("AURA_RUN_BENCH_ABC");
        Skip.IfNot(runFlag == "1", "Opt-in Candidate A/B/C benchmark (set AURA_RUN_BENCH_ABC=1).");

        string outRoot = Environment.GetEnvironmentVariable("AURA_MOTION_OUTDIR")
            ?? Path.Combine(Path.GetTempPath(), "AuraAudio_MotionOut", "candidates");
        Directory.CreateDirectory(outRoot);

        string dbPath = PrepareTempLibraryCopy(out string tempDir);
        var sb = new StringBuilder(8192);

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool? prevOverride = SpringAnimator.ForceReducedMotionOverride;
                SpringAnimator.ForceReducedMotionOverride = false;

                using var db = new DatabaseManager(dbPath);
                db.Initialize();
                using var vm = new MainViewModel(db: db);

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
                    MotionProbe.PumpDispatcher(450);

                    var heavyLyrics = BuildHeavySyncedLyrics(120);
                    if (vm.CurrentTrack == null && vm.DisplayedTracks.Count > 0)
                        vm.CurrentTrack = vm.DisplayedTracks[0];
                    vm.Lyrics.ApplyParsedResult(heavyLyrics);
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(10));

                    var workspace = (Grid)win.FindName("WorkspaceGrid");
                    var leftRail = (Border)win.FindName("LeftRailBorder");
                    var centerContent = (Grid)win.FindName("ContentStageHost");
                    var dockCapsule = (Border)win.FindName("FloatingDockCapsule");
                    var rightPane = (Border)win.FindName("RightSplitPaneBorder");

                    sb.AppendLine("=== CANDIDATE A vs B vs C MICRO-BENCHMARK (60Hz 16.67ms & 165Hz 6.06ms) ===");
                    sb.AppendLine($"Library: Tracks={vm.DisplayedTracks.Count}, Albums={vm.DisplayedAlbums.Count}, Lyrics={vm.Lyrics.Lines.Count}, Window={win.ActualWidth:F0}x{win.ActualHeight:F0}");
                    sb.AppendLine();

                    // Pre-compute critical-damped spring curve (damping=1.0, response=0.28s) at 60Hz (30 frames)
                    static double[] BuildSpringCurve(double response, double hz, int maxFrames)
                    {
                        double dt = 1.0 / hz;
                        double omega = 2.0 * Math.PI / response;
                        double k = omega * omega;
                        double c = 2.0 * 1.0 * omega;
                        double x = 0.0, v = 0.0;
                        var list = new List<double>(maxFrames + 1) { 0.0 };
                        for (int f = 1; f <= maxFrames; f++)
                        {
                            const int sub = 4;
                            double sdt = dt / sub;
                            for (int s = 0; s < sub; s++)
                            {
                                double a = -k * (x - 1.0) - c * v;
                                v += a * sdt;
                                x += v * sdt;
                            }
                            if (Math.Abs(1.0 - x) < 0.002 && Math.Abs(v) < 0.02)
                            {
                                list.Add(1.0);
                                break;
                            }
                            list.Add(Math.Clamp(x, 0.0, 1.0));
                        }
                        if (list[^1] < 1.0) list.Add(1.0);
                        return list.ToArray();
                    }

                    double[] curve60 = BuildSpringCurve(0.28, 60.0, 45);
                    double[] curve165 = BuildSpringCurve(0.28, 165.0, 110);

                    // Helper: run Candidate A (real layout Width animation on RightSplitPaneBorder, fixed 360px inner Grid)
                    void BenchCandidateA(string viewName, double hz, double[] curve, bool freezeInnerPaneWidth, bool alsoAnimateRail)
                    {
                        if (viewName != "Alb15" && viewName != "Alb15_1P" && viewName != "Alb15_NoOv" && viewName != "AlbDet")
                        {
                            vm.CurrentView = viewName;
                        }
                        vm.Presentation.ActiveRightPane = RightPaneMode.Lyrics; // mount LyricsView inside pane
                        SpringAnimator.Cancel(workspace, "shellRegions");
                        SpringAnimator.Cancel(rightPane, "opacity");
                        SpringAnimator.Cancel(rightPane, "translateX");
                        win.UpdateLayout();
                        MotionProbe.PumpDispatcher(60);

                        // Reset clean closed state (Width=0, Visible during anim)
                        var innerPaneGrid = (Grid)rightPane.Child;
                        double origInnerW = innerPaneGrid.Width;
                        HorizontalAlignment origAlign = innerPaneGrid.HorizontalAlignment;
                        bool origClip = rightPane.ClipToBounds;
                        if (freezeInnerPaneWidth)
                        {
                            rightPane.ClipToBounds = true;
                            innerPaneGrid.Width = 328.0; // 360 - 16*2 margin
                            innerPaneGrid.HorizontalAlignment = HorizontalAlignment.Left;
                        }

                        rightPane.CacheMode = null;
                        var (_, tr) = SpringAnimator.EnsureTransformGroup(rightPane);
                        tr.X = 0;
                        rightPane.Opacity = 1.0;
                        rightPane.Width = 0;
                        leftRail.Width = 248;
                        win.UpdateLayout();

                        var workMs = new List<double>(curve.Length);
                        double maxGap = 0, maxDockErr = 0, maxFirstPct = 0, maxStepPct = 0;
                        double prevContentR = MotionProbe.CaptureBounds(centerContent, workspace).Right;
                        double startContentR = prevContentR;

                        for (int i = 1; i < curve.Length; i++)
                        {
                            double p = curve[i];
                            long t0 = Stopwatch.GetTimestamp();
                            rightPane.Width = Math.Round(360.0 * p);
                            if (alsoAnimateRail)
                            {
                                leftRail.Width = Math.Round(248.0 - 192.0 * p);
                            }
                            win.UpdateLayout();
                            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                            workMs.Add(ms);

                            var c = MotionProbe.CaptureBounds(centerContent, workspace);
                            var d = MotionProbe.CaptureBounds(dockCapsule, workspace);
                            var rp = MotionProbe.CaptureBounds(rightPane, workspace);
                            var lr = MotionProbe.CaptureBounds(leftRail, workspace);

                            double gap = Math.Max(Math.Abs(c.Left - lr.Right), Math.Abs(rp.Left - c.Right));
                            double dockErr = Math.Max(Math.Abs(d.Left - (c.Left + 16.0)), Math.Abs((c.Right - 16.0) - d.Right));
                            if (gap > maxGap) maxGap = gap;
                            if (dockErr > maxDockErr) maxDockErr = dockErr;

                            double stepPct = Math.Abs(c.Right - prevContentR) / 360.0 * 100.0;
                            if (i == 1) maxFirstPct = stepPct;
                            if (stepPct > maxStepPct) maxStepPct = stepPct;
                            prevContentR = c.Right;
                        }

                        // Restore
                        innerPaneGrid.Width = origInnerW;
                        innerPaneGrid.HorizontalAlignment = origAlign;
                        rightPane.ClipToBounds = origClip;

                        workMs.Sort();
                        double p50 = workMs[workMs.Count / 2];
                        double p95 = workMs[(int)Math.Clamp(Math.Ceiling(0.95 * workMs.Count) - 1, 0, workMs.Count - 1)];
                        double max = workMs[^1];
                        double vsync = 1000.0 / hz;
                        sb.AppendLine($"CandA ({viewName,-6}, {hz,3:F0}Hz, freezeInner={freezeInnerPaneWidth,-5}, +rail={alsoAnimateRail,-5}) | " +
                                      $"1st={maxFirstPct:F1}%, maxStep={maxStepPct:F1}% | gap={maxGap:F2}, dockErr={maxDockErr:F2} | " +
                                      $"work p50={p50:F2}ms, p95={p95:F2}ms (lim {1.5 * vsync:F1}ms), max={max:F2}ms (lim {3.0 * vsync:F1}ms) | " +
                                      $"I5={(p95 <= 1.5 * vsync && max <= 3.0 * vsync ? "PASS" : "FAIL")}");
                    }

                    // Measure Candidate A on AlbumsView & SongsView (both with & without frozen inner pane width, 60Hz & 165Hz!)
                    BenchCandidateA("Albums", 60.0, curve60, freezeInnerPaneWidth: false, alsoAnimateRail: false);
                    BenchCandidateA("Albums", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                    BenchCandidateA("Albums", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: true);
                    BenchCandidateA("Albums", 165.0, curve165, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                    BenchCandidateA("Songs", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                    BenchCandidateA("Songs", 165.0, curve165, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                    BenchCandidateA("Home", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                    BenchCandidateA("Home", 165.0, curve165, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                    BenchCandidateA("Artists", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: false);

                    // Measure Candidate A on AlbumsView when virtualized to visible rows (~15 realized cards = 3 visible rows x 5 cols)
                    vm.CurrentView = "Albums";
                    win.UpdateLayout();
                    var albumsViewEl = FindVisualChild<AlbumsView>(centerContent);
                    if (albumsViewEl != null)
                    {
                        var ic = (ItemsControl)albumsViewEl.FindName("AlbumsItemsControl");
                        var origSrc = ic.ItemsSource;
                        ic.ItemsSource = vm.DisplayedAlbums.Take(15).ToList();
                        win.UpdateLayout();
                        BenchCandidateA("Alb15", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                        BenchCandidateA("Alb15", 165.0, curve165, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                        BenchCandidateA("Alb15", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: true);

                        // Also test 1-pass layout cost on 15 cards when GridWidthProbe doesn't trigger a 2nd TileHeight pass
                        var probeBorder = (Border)albumsViewEl.FindName("GridWidthProbe");
                        probeBorder.Visibility = Visibility.Collapsed;
                        win.UpdateLayout();
                        BenchCandidateA("Alb15_1P", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                        BenchCandidateA("Alb15_1P", 165.0, curve165, freezeInnerPaneWidth: true, alsoAnimateRail: false);

                        // Also test collapsing HoverOverlay on all 15 cards (eliminating 30 hidden buttons + 45 ellipses measured every frame)
                        var collapsedOverlays = new List<Border>();
                        for (int ci = 0; ci < 15; ci++)
                        {
                            if (ic.ItemContainerGenerator.ContainerFromIndex(ci) is DependencyObject cObj)
                            {
                                var ov = FindVisualChildByName<Border>(cObj, "HoverOverlay");
                                if (ov != null) { ov.Visibility = Visibility.Collapsed; collapsedOverlays.Add(ov); }
                            }
                        }
                        win.UpdateLayout();
                        BenchCandidateA("Alb15_NoOv", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                        BenchCandidateA("Alb15_NoOv", 165.0, curve165, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                        foreach (var ov in collapsedOverlays) ov.Visibility = Visibility.Visible;
                        probeBorder.Visibility = Visibility.Visible;

                        // Also test CandB single reflow & CandC RTB on 15 cards!
                        rightPane.Width = 0;
                        win.UpdateLayout();
                        long t15b = Stopwatch.GetTimestamp();
                        rightPane.Width = 360;
                        win.UpdateLayout();
                        double reflow15Ms = (Stopwatch.GetTimestamp() - t15b) * 1000.0 / Stopwatch.Frequency;

                        rightPane.Width = 0;
                        win.UpdateLayout();
                        long t15c = Stopwatch.GetTimestamp();
                        var rtb15 = new System.Windows.Media.Imaging.RenderTargetBitmap(
                            (int)Math.Round(centerContent.ActualWidth * 1.25),
                            (int)Math.Round(centerContent.ActualHeight * 1.25),
                            120, 120, PixelFormats.Pbgra32);
                        rtb15.Render(centerContent);
                        double rtb15Ms = (Stopwatch.GetTimestamp() - t15c) * 1000.0 / Stopwatch.Frequency;
                        sb.AppendLine($"CandB/C on Alb15 (virtualized): singleReflow={reflow15Ms:F2}ms, rtbOnly={rtb15Ms:F2}ms");

                        ic.ItemsSource = origSrc;
                        win.UpdateLayout();
                    }

                    // Measure Candidate A on AlbumDetailView (40-track album)
                    var biggestAlb = vm.DisplayedAlbums.OrderByDescending(a => a.TrackCount).FirstOrDefault();
                    if (biggestAlb != null)
                    {
                        vm.OpenAlbumDetailCommand.Execute(biggestAlb);
                        win.UpdateLayout();
                        BenchCandidateA("AlbDet", 60.0, curve60, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                        BenchCandidateA("AlbDet", 165.0, curve165, freezeInnerPaneWidth: true, alsoAnimateRail: false);
                        vm.CurrentView = "Albums";
                        win.UpdateLayout();
                    }

                    // Measure Candidate B (single layout reflow + clip/translate):
                    // Case B1: apply final layout at t=0 -> measure gap when pane slides in
                    // Case B2: defer layout reflow until t=end (clip center content during slide) -> measure end pop pixel diff & work
                    vm.CurrentView = "Albums";
                    rightPane.Width = 0;
                    leftRail.Width = 248;
                    win.UpdateLayout();
                    var (beforePx, bw, bh) = MotionProbe.CaptureRegionPixels(centerContent, 1.25);

                    long b0 = Stopwatch.GetTimestamp();
                    rightPane.Width = 360;
                    win.UpdateLayout();
                    double singleReflowMs = (Stopwatch.GetTimestamp() - b0) * 1000.0 / Stopwatch.Frequency;

                    // Measure pure GPU RenderTransform update cost (60Hz)
                    var gpuMs = new List<double>(curve60.Length);
                    var (_, paneTr) = SpringAnimator.EnsureTransformGroup(rightPane);
                    for (int i = 1; i < curve60.Length; i++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        paneTr.X = 360.0 * (1.0 - curve60[i]);
                        win.UpdateLayout();
                        gpuMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                    }
                    gpuMs.Sort();
                    sb.AppendLine($"CandB (Albums, 60Hz, single reflow + translate)           | singleReflow={singleReflowMs:F2}ms, gpuStep p50={gpuMs[gpuMs.Count / 2]:F3}ms, p95={gpuMs[(int)(gpuMs.Count * 0.95)]:F3}ms | NOTE: B1 has 334 DIP I2 gap at f1; B2 has 5-col->4-col late end pop!");

                    // Measure Candidate C (Hybrid: RenderTargetBitmap snapshot of centerContent + layout-animate pane/dock + cross-fade)
                    rightPane.Width = 0;
                    win.UpdateLayout();
                    long cSnap0 = Stopwatch.GetTimestamp();
                    var (snapPx, sw2, sh2) = MotionProbe.CaptureRegionPixels(centerContent, 1.25);
                    double snapshotMs = (Stopwatch.GetTimestamp() - cSnap0) * 1000.0 / Stopwatch.Frequency;

                    // Measure RenderTargetBitmap.Render ONLY (without CopyPixels, which Candidate C doesn't need!)
                    long cRtb0 = Stopwatch.GetTimestamp();
                    int rtbW = (int)Math.Round(centerContent.ActualWidth * 1.25);
                    int rtbH = (int)Math.Round(centerContent.ActualHeight * 1.25);
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(rtbW, rtbH, 120, 120, PixelFormats.Pbgra32);
                    rtb.Render(centerContent);
                    double rtbRenderOnlyMs = (Stopwatch.GetTimestamp() - cRtb0) * 1000.0 / Stopwatch.Frequency;

                    // Measure layout-animating ONLY RightPane + FloatingDock (while CenterContent stays frozen at fixed final width behind snapshot!)
                    var hybridMs = new List<double>(curve60.Length);
                    var fixedCenterW = centerContent.Width;
                    centerContent.Width = 928; // frozen during animation (no AlbumsView reflow!)
                    win.UpdateLayout();
                    for (int i = 1; i < curve60.Length; i++)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        rightPane.Width = Math.Round(360.0 * curve60[i]);
                        win.UpdateLayout();
                        hybridMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                    }
                    centerContent.Width = fixedCenterW;
                    hybridMs.Sort();
                    sb.AppendLine($"CandC (Albums, 60Hz, RTB snapshot + frozen center + pane) | rtbOnly={rtbRenderOnlyMs:F2}ms (withCopy={snapshotMs:F2}ms), shellAnim p50={hybridMs[hybridMs.Count / 2]:F2}ms, p95={hybridMs[(int)(hybridMs.Count * 0.95)]:F2}ms, max={hybridMs[^1]:F2}ms");

                    // Also measure BitmapCache on/off ClearType pixel diff on RightSplitPaneBorder (I4 pop test!)
                    rightPane.Width = 360;
                    rightPane.Opacity = 1.0;
                    paneTr.X = 0;
                    rightPane.CacheMode = new BitmapCache { EnableClearType = true, SnapsToDevicePixels = true };
                    win.UpdateLayout();
                    var (cachedPx, cw, ch) = MotionProbe.CaptureRegionPixels(rightPane, 1.25);
                    rightPane.CacheMode = null;
                    win.UpdateLayout();
                    var (uncachedPx, uw, uh) = MotionProbe.CaptureRegionPixels(rightPane, 1.25);
                    if (cachedPx != null && uncachedPx != null && cw == uw && ch == uh)
                    {
                        var cachePop = MotionProbe.ComputePixelDiff(cachedPx, uncachedPx, cw, ch);
                        sb.AppendLine($"BitmapCache Swap Diff (RightPane 360px, ClearType=true): Changed={cachePop.ChangedPixelsPct:F3}%, MaxDelta={cachePop.MaxChannelDelta}, MeanAbs={cachePop.MeanAbsDiff:F4}");
                    }
                }
                finally
                {
                    SpringAnimator.ForceReducedMotionOverride = prevOverride;
                    win.Close();
                }
            });
        }
        finally
        {
            string reportPath = Path.Combine(outRoot, "candidates_benchmark.txt");
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine(sb.ToString());
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void RunFullMotionProbeAndAudit()
    {
        string? runFlag = Environment.GetEnvironmentVariable("AURA_RUN_MOTION_DIAG");
        Skip.IfNot(runFlag == "1", "Opt-in real-time motion probe diagnostic (set AURA_RUN_MOTION_DIAG=1).");

        string phase = Environment.GetEnvironmentVariable("AURA_MOTION_PHASE") ?? "baseline";
        string outRoot = Environment.GetEnvironmentVariable("AURA_MOTION_OUTDIR")
            ?? Path.Combine(Path.GetTempPath(), "AuraAudio_MotionOut", phase);
        Directory.CreateDirectory(outRoot);

        string dbPath = PrepareTempLibraryCopy(out string tempDir);
        var report = new StringBuilder(16384);

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool? prevOverride = SpringAnimator.ForceReducedMotionOverride;
                SpringAnimator.ForceReducedMotionOverride = false;

                using var db = new DatabaseManager(dbPath);
                db.Initialize();
                using var vm = new MainViewModel(db: db);

                // Navigate to Albums (heaviest grid view)
                vm.CurrentView = "Albums";

                var win = new MainWindow(vm)
                {
                    Width = 1536,
                    Height = 864,
                    Left = 0,
                    Top = 0,
                    ShowInTaskbar = false
                };

                try
                {
                    win.Show();
                    win.UpdateLayout();
                    // Wait for async RestoreSession() to settle, THEN apply 120-line synced lyrics so they persist
                    MotionProbe.PumpDispatcher(450);

                    var heavyLyrics = BuildHeavySyncedLyrics(120);
                    if (vm.CurrentTrack == null && vm.DisplayedTracks.Count > 0)
                    {
                        vm.CurrentTrack = vm.DisplayedTracks[0];
                    }
                    vm.Lyrics.ApplyParsedResult(heavyLyrics);
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(10));
                    vm.CurrentView = "Albums";
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(150);

                    var dpiInfo = VisualTreeHelper.GetDpi(win);
                    double liveDpi = dpiInfo.DpiScaleX;
                    double idleVsync = MotionProbe.MeasureIdleVsyncMs(win, 18);

                    report.AppendLine($"=== AURA AUDIO MOTION PROBE REPORT ({phase.ToUpperInvariant()}) ===");
                    report.AppendLine($"Timestamp: {DateTimeOffset.Now:O}");
                    report.AppendLine($"LiveDpiScale: {liveDpi:F2} ({(int)Math.Round(liveDpi * 100)}%) | MeasuredIdleVsyncMs: {idleVsync:F2} ms (~{1000.0 / idleVsync:F1} Hz)");
                    report.AppendLine($"ReducedMotion: IsReducedMotion={SpringAnimator.IsReducedMotion}, ForceOverride={SpringAnimator.ForceReducedMotionOverride?.ToString() ?? "null"}, SystemClientAreaAnimation={SystemParameters.ClientAreaAnimation}");
                    report.AppendLine($"HeavyContentLoaded: Tracks={vm.DisplayedTracks.Count}, Albums={vm.DisplayedAlbums.Count}, SyncedLyricsLines={vm.Lyrics.Lines.Count}");
                    report.AppendLine();

                    var results = new List<MotionProbeResult>();

                    // ------------------------------------------------------------------
                    // STEP 1A: INLINE WIDE MODE (1536x864 — 1440..1679, AlbumsView, Auto-Compact Rail)
                    // ------------------------------------------------------------------
                    report.AppendLine("--- STEP 1: SHELL TRANSITIONS (HEAVY CONTENT: ALBUMS + SONGS + 120-LINE LYRICS) ---");

                    win.WindowState = WindowState.Normal;
                    win.Width = 1536;
                    win.Height = 860;
                    vm.Presentation.ActiveRightPane = RightPaneMode.None;
                    vm.Presentation.IsSidebarCollapsed = false;
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(220);

                    // 1. Wide Inline (1536 DIPs): Right Pane opens AND Left Rail auto-compacts (248 -> 56) simultaneously!
                    results.Add(MotionProbe.RecordShellTransition(
                        win, "1_Inline1536_Albums_PaneOpenAutoRail",
                        () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                        isInlineMode: true, expectedFinalRailWidth: 56, expectedFinalPaneWidth: 360,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    // 2. Queue <-> Lyrics cross-fade switch while pane stays open
                    results.Add(MotionProbe.RecordShellTransition(
                        win, "2_Inline1536_Albums_QueueLyricsSwitch",
                        () => vm.Presentation.ToggleQueuePaneCommand.Execute(null),
                        isInlineMode: true, expectedFinalRailWidth: 56, expectedFinalPaneWidth: 360,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi,
                        extraTarget: (FrameworkElement)win.FindName("RightPaneContentHost"), extraLabel: "RightPaneContentHost"));

                    // 3. Wide Inline Close (pane closes AND rail auto-expands 56 -> 248)
                    results.Add(MotionProbe.RecordShellTransition(
                        win, "3_Inline1536_Albums_PaneCloseAutoRail",
                        () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                        isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    // 4-5. Left Rail Collapse & Expand (sets manual override, isolating pure rail motion)
                    results.Add(MotionProbe.RecordShellTransition(
                        win, "4_Inline1536_Albums_LeftRailCollapse",
                        () => vm.Presentation.ToggleSidebarCommand.Execute(null),
                        isInlineMode: true, expectedFinalRailWidth: 56, expectedFinalPaneWidth: 0,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "5_Inline1536_Albums_LeftRailExpand",
                        () => vm.Presentation.ToggleSidebarCommand.Execute(null),
                        isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    // 6-7. Maximized Pure Inline Right Pane Open & Close (manual override active -> rail stays 248!)
                    win.WindowState = WindowState.Maximized;
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(220);
                    bool maxInline = win.ActualWidth >= 1440.0;
                    results.Add(MotionProbe.RecordShellTransition(
                        win, "6_Maximized_Albums_RightPaneOpen",
                        () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                        isInlineMode: maxInline, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "7_Maximized_Albums_RightPaneClose",
                        () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                        isInlineMode: maxInline, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    // 8-9. Songs DataGrid heavy content (Inline 1536, rail fixed 248)
                    win.WindowState = WindowState.Normal;
                    win.Width = 1536;
                    win.Height = 860;
                    vm.CurrentView = "Songs";
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(220);

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "8_Inline1536_Songs_RightPaneOpen",
                        () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                        isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "9_Inline1536_Songs_RightPaneClose",
                        () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                        isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    // 4. Overlay mode (< 1440 DIPs -> 1280x800, AlbumsView)
                    vm.CurrentView = "Albums";
                    win.Width = 1280;
                    win.Height = 800;
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(220);

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "10_Overlay1280_Albums_RightPaneOpen",
                        () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                        isInlineMode: false, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "11_Overlay1280_Albums_RightPaneClose",
                        () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                        isInlineMode: false, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "12_Overlay1280_Albums_LeftRailCollapse",
                        () => vm.Presentation.ToggleSidebarCommand.Execute(null),
                        isInlineMode: false, expectedFinalRailWidth: 56, expectedFinalPaneWidth: 0,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "13_Overlay1280_Albums_LeftRailExpand",
                        () => vm.Presentation.ToggleSidebarCommand.Execute(null),
                        isInlineMode: false, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    // 5. DPI 100% / 125% / 150% viewport-equivalent sweeps (1920px @ 100%=1920 DIP, 125%=1536 DIP, 150%=1280 DIP)
                    foreach (var (dpiLabel, simScale, dipW) in new[] { ("DPI100_1920", 1.0, 1920.0), ("DPI125_1536", 1.25, 1536.0), ("DPI150_1280", 1.5, 1280.0) })
                    {
                        win.Width = Math.Min(dipW, SystemParameters.WorkArea.Width);
                        win.Height = 800;
                        win.UpdateLayout();
                        MotionProbe.PumpDispatcher(180);
                        bool inline = win.ActualWidth >= 1440.0;
                        double railExp = vm.Presentation.IsSidebarCollapsed ? 56.0 : 248.0;

                        results.Add(MotionProbe.RecordShellTransition(
                            win, $"14_{dpiLabel}_RightPaneOpen",
                            () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                            isInlineMode: inline, expectedFinalRailWidth: railExp, expectedFinalPaneWidth: 360,
                            csvDirectory: outRoot, simulatedDpiScale: simScale, capturePopPixels: false));

                        results.Add(MotionProbe.RecordShellTransition(
                            win, $"15_{dpiLabel}_RightPaneClose",
                            () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                            isInlineMode: inline, expectedFinalRailWidth: railExp, expectedFinalPaneWidth: 0,
                            csvDirectory: outRoot, simulatedDpiScale: simScale, capturePopPixels: false));
                    }

                    // 6. Reduced Motion check (inline 1536)
                    win.Width = 1536;
                    win.Height = 860;
                    SpringAnimator.ForceReducedMotionOverride = true;
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(120);

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "16_ReducedMotion_Inline1536_PaneOpen",
                        () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                        isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    results.Add(MotionProbe.RecordShellTransition(
                        win, "17_ReducedMotion_Inline1536_PaneClose",
                        () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                        isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                        csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                    SpringAnimator.ForceReducedMotionOverride = false;

                    // 7. Deterministic 60Hz & 165Hz Probe Suite (Real CPU+WPF WorkMs, Zero DWM Occlusion Throttle)
                    report.AppendLine();
                    report.AppendLine("--- STEP 1B: DETERMINISTIC 60Hz & 165Hz SHELL PROBE (UNCONTAMINATED WORK MS) ---");
                    var detResults = new List<MotionProbeResult>();

                    foreach (double hz in new[] { 60.0, 165.0 })
                    {
                        string hzTag = $"{hz:F0}Hz";
                        win.WindowState = WindowState.Normal;
                        win.Width = 1536;
                        win.Height = 860;
                        vm.CurrentView = "Albums";
                        vm.Presentation.ActiveRightPane = RightPaneMode.None;
                        vm.Presentation.IsSidebarCollapsed = false;
                        win.UpdateLayout();
                        MotionProbe.PumpDispatcher(120);

                        detResults.Add(MotionProbe.RecordShellTransitionDeterministic(
                            win, $"Det_{hzTag}_Albums_PaneOpen",
                            () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                            isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                            hz: hz, csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                        detResults.Add(MotionProbe.RecordShellTransitionDeterministic(
                            win, $"Det_{hzTag}_Albums_QueueLyricsSwitch",
                            () => vm.Presentation.ToggleQueuePaneCommand.Execute(null),
                            isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                            hz: hz, csvDirectory: outRoot, simulatedDpiScale: liveDpi,
                            extraTarget: (FrameworkElement)win.FindName("RightPaneContentHost"), extraLabel: "RightPaneContentHost"));

                        detResults.Add(MotionProbe.RecordShellTransitionDeterministic(
                            win, $"Det_{hzTag}_Albums_PaneClose",
                            () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                            isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                            hz: hz, csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                        detResults.Add(MotionProbe.RecordShellTransitionDeterministic(
                            win, $"Det_{hzTag}_Albums_LeftRailCollapse",
                            () => vm.Presentation.ToggleSidebarCommand.Execute(null),
                            isInlineMode: true, expectedFinalRailWidth: 56, expectedFinalPaneWidth: 0,
                            hz: hz, csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                        detResults.Add(MotionProbe.RecordShellTransitionDeterministic(
                            win, $"Det_{hzTag}_Albums_LeftRailExpand",
                            () => vm.Presentation.ToggleSidebarCommand.Execute(null),
                            isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                            hz: hz, csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                        vm.CurrentView = "Songs";
                        win.UpdateLayout();
                        MotionProbe.PumpDispatcher(80);

                        detResults.Add(MotionProbe.RecordShellTransitionDeterministic(
                            win, $"Det_{hzTag}_Songs_PaneOpen",
                            () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                            isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                            hz: hz, csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                        detResults.Add(MotionProbe.RecordShellTransitionDeterministic(
                            win, $"Det_{hzTag}_Songs_PaneClose",
                            () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                            isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                            hz: hz, csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                        vm.CurrentView = "Albums";
                        win.Width = 1280;
                        win.Height = 800;
                        win.UpdateLayout();
                        MotionProbe.PumpDispatcher(80);

                        detResults.Add(MotionProbe.RecordShellTransitionDeterministic(
                            win, $"Det_{hzTag}_Overlay1280_PaneOpen",
                            () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                            isInlineMode: false, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                            hz: hz, csvDirectory: outRoot, simulatedDpiScale: liveDpi));

                        detResults.Add(MotionProbe.RecordShellTransitionDeterministic(
                            win, $"Det_{hzTag}_Overlay1280_PaneClose",
                            () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                            isInlineMode: false, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                            hz: hz, csvDirectory: outRoot, simulatedDpiScale: liveDpi));
                    }

                    // Reset back to 1536 Albums before Step 2
                    win.Width = 1536;
                    win.Height = 860;
                    vm.CurrentView = "Albums";
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(80);

                    foreach (var r in results)
                    {
                        report.AppendLine(r.FormatSummaryLine());
                        if (!r.AllPassed)
                        {
                            report.AppendLine($"   FAILURES: {r.FailureReasons}");
                        }
                    }

                    report.AppendLine();
                    report.AppendLine("--- DETERMINISTIC 60Hz & 165Hz RESULTS ---");
                    foreach (var r in detResults)
                    {
                        report.AppendLine(r.FormatSummaryLine());
                        if (!r.AllPassed)
                        {
                            report.AppendLine($"   FAILURES: {r.FailureReasons}");
                        }
                    }

                    // ------------------------------------------------------------------
                    // STEP 2: FULL MOTION AUDIT OF ALL OTHER TRANSITIONS
                    // ------------------------------------------------------------------
                    report.AppendLine();
                    report.AppendLine("--- STEP 2: FULL MOTION AUDIT (ALL OTHER TRANSITIONS) ---");

                    // 2.1 Page Navigation (Albums -> Home)
                    var contentHost = (FrameworkElement)win.FindName("ContentStageHost");
                    report.AppendLine(AuditSingleElementTransition(
                        win, "Audit_PageNav_AlbumsToHome", contentHost,
                        () => vm.CurrentView = "Home", outRoot));

                    // 2.2 Full-Screen Enter & Exit
                    var fsView = (FullScreenPlayerView)win.FindName("FullScreenStageOverlay");
                    var fsStageRoot = (FrameworkElement)fsView.FindName("StageRoot");
                    vm.Presentation.IsFullScreenLyricsOpen = false;
                    report.AppendLine(AuditSingleElementTransition(
                        win, "Audit_FullScreen_Enter", fsStageRoot,
                        () => vm.Presentation.ToggleFullScreenCommand.Execute(null), outRoot));

                    // 2.3 Full-Screen Centered <-> Split Lyrics morph
                    var fsArtworkCol = (FrameworkElement)fsView.FindName("FiveColumnStage");
                    var fsLyricsHost = (FrameworkElement)fsView.FindName("LyricsStageHost");
                    var fsArtPanel = (FrameworkElement)((Grid)fsArtworkCol).Children[0];
                    report.AppendLine(AuditTwoRegionTransition(
                        win, "Audit_FullScreen_SplitLyricsOpen", fsArtPanel, "ArtworkCol", fsLyricsHost, "LyricsHost",
                        () => vm.Presentation.ToggleFullScreenLyricsCommand.Execute(null), outRoot));

                    report.AppendLine(AuditTwoRegionTransition(
                        win, "Audit_FullScreen_SplitLyricsClose", fsArtPanel, "ArtworkCol", fsLyricsHost, "LyricsHost",
                        () => vm.Presentation.ToggleFullScreenLyricsCommand.Execute(null), outRoot));

                    report.AppendLine(AuditSingleElementTransition(
                        win, "Audit_FullScreen_Exit", fsStageRoot,
                        () => vm.Presentation.ExitFullScreenCommand.Execute(null), outRoot));

                    // 2.4 Modal Sheets: Get Info (Open & Close), AddToPlaylist (Open & Close), RenamePlaylist (Open & Close)
                    var infoSheet = FindByAutomationId<InfoInspectorSheet>(win, "Aura.Info.Sheet")!;
                    var infoCard = (FrameworkElement)infoSheet.FindName("SheetCard");
                    report.AppendLine(AuditSingleElementTransition(
                        win, "Audit_Sheet_GetInfo_Open", infoCard,
                        () => vm.OpenInfoForTrackCommand.Execute(null), outRoot));
                    report.AppendLine(AuditSingleElementTransition(
                        win, "Audit_Sheet_GetInfo_Close", infoCard,
                        () => vm.Presentation.CloseInfoSheetCommand.Execute(null), outRoot));

                    var addSheet = FindVisualChild<AddToPlaylistSheet>(win)!;
                    var addCard = (FrameworkElement)addSheet.FindName("SheetCard");
                    report.AppendLine(AuditSingleElementTransition(
                        win, "Audit_Sheet_AddToPlaylist_Open", addCard,
                        () => vm.IsAddToPlaylistSheetOpen = true, outRoot));
                    report.AppendLine(AuditSingleElementTransition(
                        win, "Audit_Sheet_AddToPlaylist_Close", addCard,
                        () => vm.CloseAddToPlaylistSheetCommand.Execute(null), outRoot));

                    var renameSheet = FindVisualChild<RenamePlaylistSheet>(win)!;
                    var renameCard = (FrameworkElement)renameSheet.FindName("SheetCard");
                    report.AppendLine(AuditSingleElementTransition(
                        win, "Audit_Sheet_Rename_Open", renameCard,
                        () => vm.CreatePlaylistCommand.Execute(string.Empty), outRoot));
                    report.AppendLine(AuditSingleElementTransition(
                        win, "Audit_Sheet_Rename_Close", renameCard,
                        () => vm.CancelRenameSheetCommand.Execute(null), outRoot));

                    // 2.5 Mini-Player Compact <-> AlbumView & Tray Expand/Collapse
                    var mini = new MiniPlayerWindow(vm) { ShowInTaskbar = false, Left = 100, Top = 100 };
                    try
                    {
                        mini.Show();
                        mini.UpdateLayout();
                        MotionProbe.PumpDispatcher(150);
                        report.AppendLine(AuditWindowHeightTransition(
                            mini, "Audit_MiniPlayer_CompactToAlbumView",
                            () => vm.Presentation.IsMiniPlayerAlbumView = true));
                        report.AppendLine(AuditWindowHeightTransition(
                            mini, "Audit_MiniPlayer_AlbumToCompact",
                            () => vm.Presentation.IsMiniPlayerAlbumView = false));
                        report.AppendLine(AuditWindowHeightTransition(
                            mini, "Audit_MiniPlayer_TrayExpand",
                            () => vm.Presentation.MiniPlayerTrayMode = RightPaneMode.Queue));
                        report.AppendLine(AuditWindowHeightTransition(
                            mini, "Audit_MiniPlayer_TrayCollapse",
                            () => vm.Presentation.MiniPlayerTrayMode = RightPaneMode.None));
                    }
                    finally
                    {
                        mini.Close();
                    }

                    // 2.6 ShelfCarousel paging & arrow toggle
                    vm.CurrentView = "Home";
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(200);
                    var shelf = FindVisualChild<ShelfCarousel>(win);
                    if (shelf != null)
                    {
                        var sv = (ScrollViewer)shelf.FindName("ShelfScroll");
                        report.AppendLine(AuditScrollOffsetTransition(
                            win, "Audit_ShelfCarousel_PageRight", sv, isHorizontal: true,
                            () =>
                            {
                                var rightBtn = (Button)shelf.FindName("RightArrowBtn");
                                rightBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            }));
                    }

                    // 2.7 Lyrics Auto-Scroll (Right Pane open on 120-line synced lyrics)
                    vm.Presentation.ActiveRightPane = RightPaneMode.Lyrics;
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(320);
                    var paneLyrics = FindVisualChild<LyricsView>((Border)win.FindName("RightSplitPaneBorder"));
                    if (paneLyrics != null)
                    {
                        var llist = (ListBox)paneLyrics.FindName("SyncedLinesList");
                        var lsv = FindVisualChild<ScrollViewer>(llist);
                        if (lsv != null)
                        {
                            report.AppendLine(AuditScrollOffsetTransition(
                                win, "Audit_Lyrics_AutoScroll", lsv, isHorizontal: false,
                                () => vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(70))));
                        }
                    }
                    vm.Presentation.CloseRightPaneCommand.Execute(null);
                    MotionProbe.PumpDispatcher(320);

                    // 2.8 Sticky Header Fade (AlbumDetailView — pick largest tracklist album so ScrollableHeight > 400)
                    if (vm.DisplayedAlbums.Count > 0)
                    {
                        var largestAlbum = vm.DisplayedAlbums.OrderByDescending(a => a.TrackCount).First();
                        vm.OpenAlbumDetailCommand.Execute(largestAlbum);
                        win.UpdateLayout();
                        MotionProbe.PumpDispatcher(220);
                        var albumDetail = FindVisualChild<AlbumDetailView>(win);
                        if (albumDetail != null)
                        {
                            var albumScroll = (ScrollViewer)albumDetail.FindName("AlbumScroll");
                            var stickyBar = (FrameworkElement)albumDetail.FindName("StickyHeaderBar");
                            report.AppendLine(AuditSingleElementTransition(
                                win, "Audit_StickyHeader_FadeIn", stickyBar,
                                () => albumScroll.ScrollToVerticalOffset(360), outRoot));
                            report.AppendLine(AuditSingleElementTransition(
                                win, "Audit_StickyHeader_FadeOut", stickyBar,
                                () => albumScroll.ScrollToVerticalOffset(0), outRoot));
                        }
                    }

                    // 2.9 Elastic Scrubber height spring (3px <-> 6px)
                    var dockScrubber = (Slider)win.FindName("DockScrubber");
                    var trackBg = dockScrubber.Template?.FindName("ElasticTrackBackground", dockScrubber) as Border;
                    if (trackBg != null)
                    {
                        report.AppendLine(AuditElementHeightTransition(
                            win, "Audit_Scrubber_Expand3To6", trackBg,
                            () => { dockScrubber.Focus(); FluidBehaviors.UpdateElasticScrubberVisuals(dockScrubber); }));
                        report.AppendLine(AuditElementHeightTransition(
                            win, "Audit_Scrubber_Collapse6To3", trackBg,
                            () => { win.FocusWindowRoot(); FluidBehaviors.UpdateElasticScrubberVisuals(dockScrubber); }));
                    }
                }
                finally
                {
                    SpringAnimator.ForceReducedMotionOverride = prevOverride;
                    win.Close();
                }
            });
        }
        finally
        {
            string reportPath = Path.Combine(outRoot, "summary_report.txt");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            Console.WriteLine(report.ToString());
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string AuditSingleElementTransition(Window win, string name, FrameworkElement target, Action trigger, string outRoot)
    {
        win.UpdateLayout();
        MotionProbe.PumpDispatcher(40);
        var samples = new List<(double t, double dt, double left, double top, double w, double h, double op, Visibility vis, bool cache)>();

        void Record(double t, double dt)
        {
            var r = MotionProbe.CaptureBounds(target, win);
            samples.Add((t, dt, r.Left, r.Top, r.Width, r.Height, r.Opacity, r.Visibility, r.HasBitmapCache));
        }

        Record(0, 0);
        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();
        long prev = Stopwatch.GetTimestamp();

        byte[]? preIdlePx = null;
        int pw = 0, ph = 0;
        bool capturedPreIdle = false;

        int snapTick = -1;
        void OnRender(object? s, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (double)(now - prev) * 1000.0 / Stopwatch.Frequency;
            prev = now;
            double t = sw.Elapsed.TotalMilliseconds;
            Record(t, dt);

            if (!capturedPreIdle && t >= 540 && target.IsVisible && target.ActualWidth > 10 && target.ActualHeight > 10)
            {
                capturedPreIdle = true;
                snapTick = samples.Count;
                (preIdlePx, pw, ph) = MotionProbe.CaptureRegionPixels(target, 1.0);
                prev = Stopwatch.GetTimestamp();
            }

            if (t >= 640)
            {
                CompositionTarget.Rendering -= OnRender;
                frame.Continue = false;
            }
        }

        trigger();
        CompositionTarget.Rendering += OnRender;
        Dispatcher.PushFrame(frame);
        MotionProbe.PumpDispatcher(80);

        PixelDiffMetrics? pop = null;
        if (preIdlePx != null && target.IsVisible && pw > 0 && ph > 0)
        {
            var (idlePx, iw, ih) = MotionProbe.CaptureRegionPixels(target, 1.0);
            if (idlePx != null && iw == pw && ih == ph)
            {
                pop = MotionProbe.ComputePixelDiff(preIdlePx, idlePx, pw, ph);
            }
        }

        var first = samples[0];
        var f1 = samples.Count > 1 ? samples[1] : first;
        var last = samples[^1];
        int framesWithChange = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            if (Math.Abs(samples[i].top - samples[i - 1].top) > 0.1 ||
                Math.Abs(samples[i].left - samples[i - 1].left) > 0.1 ||
                Math.Abs(samples[i].op - samples[i - 1].op) > 0.005)
                framesWithChange++;
        }
        bool usedCache = samples.Any(x => x.cache);
        bool cacheCleared = !last.cache;
        var dts = samples.Skip(1).Where((_, idx) => idx + 1 != snapTick).Select(x => x.dt).OrderBy(x => x).ToList();
        double p95 = dts.Count > 0 ? dts[(int)Math.Clamp(Math.Ceiling(0.95 * dts.Count) - 1, 0, dts.Count - 1)] : 0;

        // Check if closing snapped immediately (framesWithChange <= 1 while vis changed)
        bool instantSnap = framesWithChange <= 1 && (first.vis != last.vis || Math.Abs(first.op - last.op) > 0.5 || Math.Abs(first.left - last.left) > 5);

        return $"{name,-38} | frames={samples.Count,2} movingFrames={framesWithChange,2} | " +
               $"vis:{first.vis}->{f1.vis}->{last.vis} | op:{first.op:F2}->{f1.op:F2}->{last.op:F2} | " +
               $"Y:{first.top:F1}->{f1.top:F1}->{last.top:F1} | cache:{usedCache}/clr:{cacheCleared} | " +
               $"pop={(pop?.ChangedPixelsPct ?? 0):F2}%/d{pop?.MaxChannelDelta ?? 0} | p95={p95:F1}ms | {(instantSnap ? "SNAP_FAIL" : "ANIMATED")}";
    }

    private static string AuditTwoRegionTransition(Window win, string name, FrameworkElement elA, string labelA, FrameworkElement elB, string labelB, Action trigger, string outRoot)
    {
        win.UpdateLayout();
        MotionProbe.PumpDispatcher(40);
        var samples = new List<(double t, double dt, double aL, double aW, double bL, double bOp, Visibility bVis)>();

        void Record(double t, double dt)
        {
            var ra = MotionProbe.CaptureBounds(elA, win);
            var rb = MotionProbe.CaptureBounds(elB, win);
            samples.Add((t, dt, ra.Left, ra.Width, rb.Left, rb.Opacity, rb.Visibility));
        }

        Record(0, 0);
        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();
        long prev = Stopwatch.GetTimestamp();

        void OnRender(object? s, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (double)(now - prev) * 1000.0 / Stopwatch.Frequency;
            prev = now;
            double t = sw.Elapsed.TotalMilliseconds;
            Record(t, dt);
            if (t >= 480)
            {
                CompositionTarget.Rendering -= OnRender;
                frame.Continue = false;
            }
        }

        trigger();
        CompositionTarget.Rendering += OnRender;
        Dispatcher.PushFrame(frame);
        MotionProbe.PumpDispatcher(60);

        var f0 = samples[0];
        var f1 = samples.Count > 1 ? samples[1] : f0;
        var fl = samples[^1];
        double travelA = Math.Abs(fl.aL - f0.aL);
        double firstJumpA = travelA > 5 ? Math.Abs(f1.aL - f0.aL) / travelA * 100.0 : 0;
        int movingB = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            if (Math.Abs(samples[i].bOp - samples[i - 1].bOp) > 0.005 || Math.Abs(samples[i].bL - samples[i - 1].bL) > 0.2)
                movingB++;
        }
        return $"{name,-38} | {labelA}.L:{f0.aL:F1}->{f1.aL:F1}->{fl.aL:F1} (1stJump={firstJumpA:F1}%) | " +
               $"{labelB}.vis:{f0.bVis}->{fl.bVis} op:{f0.bOp:F2}->{fl.bOp:F2} movingFrames={movingB}";
    }

    private static string AuditWindowHeightTransition(Window win, string name, Action trigger)
    {
        win.UpdateLayout();
        MotionProbe.PumpDispatcher(30);
        var hs = new List<(double t, double h)> { (0, win.ActualHeight) };
        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();

        void OnRender(object? s, EventArgs e)
        {
            double t = sw.Elapsed.TotalMilliseconds;
            hs.Add((t, win.ActualHeight));
            if (t >= 400) { CompositionTarget.Rendering -= OnRender; frame.Continue = false; }
        }

        trigger();
        CompositionTarget.Rendering += OnRender;
        Dispatcher.PushFrame(frame);

        double travel = Math.Abs(hs[^1].h - hs[0].h);
        double firstPct = travel > 2 && hs.Count > 1 ? Math.Abs(hs[1].h - hs[0].h) / travel * 100.0 : 0;
        int steps = 0;
        for (int i = 1; i < hs.Count; i++) if (Math.Abs(hs[i].h - hs[i - 1].h) > 0.2) steps++;
        return $"{name,-38} | H:{hs[0].h:F0}->{hs[Math.Min(1, hs.Count - 1)].h:F0}->{hs[^1].h:F0} | 1stJump={firstPct:F1}% | movingFrames={steps}";
    }

    private static string AuditElementHeightTransition(Window win, string name, FrameworkElement el, Action trigger)
    {
        win.UpdateLayout();
        MotionProbe.PumpDispatcher(30);
        var hs = new List<double> { el.ActualHeight };
        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();

        void OnRender(object? s, EventArgs e)
        {
            hs.Add(el.ActualHeight);
            if (sw.Elapsed.TotalMilliseconds >= 360) { CompositionTarget.Rendering -= OnRender; frame.Continue = false; }
        }

        trigger();
        CompositionTarget.Rendering += OnRender;
        Dispatcher.PushFrame(frame);

        double travel = Math.Abs(hs[^1] - hs[0]);
        double firstPct = travel > 0.5 && hs.Count > 1 ? Math.Abs(hs[1] - hs[0]) / travel * 100.0 : 0;
        int steps = 0;
        for (int i = 1; i < hs.Count; i++) if (Math.Abs(hs[i] - hs[i - 1]) > 0.05) steps++;
        return $"{name,-38} | H:{hs[0]:F2}->{hs[Math.Min(1, hs.Count - 1)]:F2}->{hs[^1]:F2} | 1stJump={firstPct:F1}% | movingFrames={steps}";
    }

    private static string AuditScrollOffsetTransition(Window win, string name, ScrollViewer sv, bool isHorizontal, Action trigger)
    {
        win.UpdateLayout();
        MotionProbe.PumpDispatcher(30);
        double Read() => isHorizontal ? sv.HorizontalOffset : sv.VerticalOffset;
        var offs = new List<double> { Read() };
        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();

        void OnRender(object? s, EventArgs e)
        {
            offs.Add(Read());
            if (sw.Elapsed.TotalMilliseconds >= 450) { CompositionTarget.Rendering -= OnRender; frame.Continue = false; }
        }

        trigger();
        CompositionTarget.Rendering += OnRender;
        Dispatcher.PushFrame(frame);

        double travel = Math.Abs(offs[^1] - offs[0]);
        double firstPct = travel > 2 && offs.Count > 1 ? Math.Abs(offs[1] - offs[0]) / travel * 100.0 : 0;
        int steps = 0;
        for (int i = 1; i < offs.Count; i++) if (Math.Abs(offs[i] - offs[i - 1]) > 0.3) steps++;
        return $"{name,-38} | Off:{offs[0]:F1}->{offs[Math.Min(1, offs.Count - 1)]:F1}->{offs[^1]:F1} | 1stJump={firstPct:F1}% | movingFrames={steps}";
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
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

    private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && match.Name == name) return match;
            var nested = FindVisualChildByName<T>(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    private static T? FindByAutomationId<T>(DependencyObject parent, string automationId) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && System.Windows.Automation.AutomationProperties.GetAutomationId(child) == automationId)
                return match;
            var nested = FindByAutomationId<T>(child, automationId);
            if (nested != null) return nested;
        }
        return null;
    }
}
