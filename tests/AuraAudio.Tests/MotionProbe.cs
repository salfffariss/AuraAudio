using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.Windows;

namespace AuraAudio.Tests;

internal readonly record struct RegionRect(
    double Left,
    double Top,
    double Width,
    double Height,
    double LayoutWidth,
    double Opacity,
    Visibility Visibility,
    bool HasBitmapCache)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

internal sealed record MotionFrameSample(
    int FrameIndex,
    double ElapsedMs,
    double FrameDtMs,
    double WorkspaceWidth,
    double WorkspaceHeight,
    RegionRect LeftRail,
    RegionRect CenterContent,
    RegionRect DockCapsule,
    RegionRect RightPane,
    RegionRect? ExtraTarget,
    string? ExtraLabel);

internal sealed record PixelDiffMetrics(
    int Width,
    int Height,
    int ChangedPixels,
    double ChangedPixelsPct,
    int MaxChannelDelta,
    double MeanAbsDiff);

internal sealed record MotionProbeResult(
    string ScenarioName,
    bool IsInlineMode,
    double WindowWidthDip,
    WindowState WindowState,
    double SimulatedDpiScale,
    bool ReducedMotionActive,
    bool? ForceReducedMotionOverride,
    bool SystemClientAreaAnimation,
    double MeasuredVsyncMs,
    IReadOnlyList<MotionFrameSample> Frames,
    double MaxFirstFrameJumpPct,
    double MaxStepDeltaPct,
    double MaxRailContentGapDip,
    double MaxPaneContentGapDip,
    double MaxDockMarginErrorDip,
    double MaxOvershootDip,
    double MaxReversalDip,
    double FinalGeometryErrorDip,
    PixelDiffMetrics? EndPopDiff,
    double P50FrameMs,
    double P95FrameMs,
    double MaxFrameMs,
    bool PassI1NoJump,
    bool PassI2Adjacency,
    bool PassI3Monotonic,
    bool PassI4Completion,
    bool PassI5FrameTime,
    string FailureReasons,
    string CsvPath)
{
    public bool AllPassed => PassI1NoJump && PassI2Adjacency && PassI3Monotonic && PassI4Completion && PassI5FrameTime;

    public string FormatSummaryLine()
    {
        return $"{ScenarioName,-38} | RM={ReducedMotionActive,-5} | " +
               $"I1:{(PassI1NoJump ? "PASS" : "FAIL")}(1st={MaxFirstFrameJumpPct:F1}%,step={MaxStepDeltaPct:F1}%) | " +
               $"I2:{(PassI2Adjacency ? "PASS" : "FAIL")}(rail={MaxRailContentGapDip:F1},pane={MaxPaneContentGapDip:F1},dock={MaxDockMarginErrorDip:F1}) | " +
               $"I3:{(PassI3Monotonic ? "PASS" : "FAIL")}(ov={MaxOvershootDip:F1},rev={MaxReversalDip:F1}) | " +
               $"I4:{(PassI4Completion ? "PASS" : "FAIL")}(err={FinalGeometryErrorDip:F1},pop={(EndPopDiff?.ChangedPixelsPct ?? 0):F2}%/d{EndPopDiff?.MaxChannelDelta ?? 0}) | " +
               $"I5:{(PassI5FrameTime ? "PASS" : "FAIL")}(p50={P50FrameMs:F1},p95={P95FrameMs:F1},max={MaxFrameMs:F1},vsync={MeasuredVsyncMs:F1})";
    }
}

internal static class MotionProbe
{
    public static double MeasureIdleVsyncMs(Window window, int sampleFrames = 18)
    {
        var dts = new List<double>(sampleFrames);
        var frame = new DispatcherFrame();
        long prev = Stopwatch.GetTimestamp();
        int count = 0;

        void OnRender(object? s, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (double)(now - prev) * 1000.0 / Stopwatch.Frequency;
            prev = now;
            if (count > 2) dts.Add(dt);
            count++;
            if (count >= sampleFrames + 3)
            {
                CompositionTarget.Rendering -= OnRender;
                frame.Continue = false;
            }
        }

        // CompositionTarget.Rendering fires automatically on every WPF composition tick without InvalidateVisual()
        CompositionTarget.Rendering += OnRender;
        Dispatcher.PushFrame(frame);

        if (dts.Count == 0) return 16.67;
        dts.Sort();
        double median = dts[dts.Count / 2];
        return Math.Clamp(median, 4.0, 20.0);
    }

    public static RegionRect CaptureBounds(FrameworkElement? el, Visual reference)
    {
        if (el == null)
        {
            return new RegionRect(0, 0, 0, 0, 0, 0, Visibility.Collapsed, false);
        }

        if (el.Visibility != Visibility.Visible || el.ActualWidth <= 0 || el.ActualHeight <= 0)
        {
            // Still compute transformed origin if possible so collapsed elements report clean 0-width edge
            double tx = 0, ty = 0;
            try
            {
                if (PresentationSource.FromVisual(el) != null && PresentationSource.FromVisual(reference) != null)
                {
                    Point p = el.TransformToVisual(reference).Transform(new Point(0, 0));
                    tx = p.X;
                    ty = p.Y;
                }
            }
            catch { }

            return new RegionRect(
                tx, ty,
                el.Visibility == Visibility.Visible ? el.ActualWidth : 0.0,
                el.Visibility == Visibility.Visible ? el.ActualHeight : 0.0,
                el.Visibility == Visibility.Visible ? el.ActualWidth : 0.0,
                el.Opacity,
                el.Visibility,
                el.CacheMode is BitmapCache);
        }

        Rect bounds;
        try
        {
            bounds = el.TransformToVisual(reference).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        }
        catch
        {
            bounds = new Rect(0, 0, el.ActualWidth, el.ActualHeight);
        }

        return new RegionRect(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            el.ActualWidth,
            el.Opacity,
            el.Visibility,
            el.CacheMode is BitmapCache);
    }

    public static MotionProbeResult RecordShellTransition(
        MainWindow window,
        string scenarioName,
        Action triggerAction,
        bool isInlineMode,
        double expectedFinalRailWidth,
        double expectedFinalPaneWidth,
        string csvDirectory,
        double simulatedDpiScale = 1.25,
        int captureDurationMs = 620,
        bool capturePopPixels = true,
        FrameworkElement? extraTarget = null,
        string? extraLabel = null)
    {
        var workspace = (Grid)window.FindName("WorkspaceGrid");
        var leftRail = (Border)window.FindName("LeftRailBorder");
        var centerContent = (Grid)window.FindName("ContentStageHost");
        var dockCapsule = (Border)window.FindName("FloatingDockCapsule");
        var rightPane = (Border)window.FindName("RightSplitPaneBorder");

        window.UpdateLayout();
        PumpDispatcher(35);
        double vsyncMs = MeasureIdleVsyncMs(window, 10);

        bool reducedMotion = SpringAnimator.IsReducedMotion;
        bool? forceOverride = SpringAnimator.ForceReducedMotionOverride;
        bool sysAnim = SystemParameters.ClientAreaAnimation;

        var samples = new List<MotionFrameSample>(120);

        // Sample 0: pre-trigger resting baseline
        samples.Add(new MotionFrameSample(
            FrameIndex: 0,
            ElapsedMs: 0.0,
            FrameDtMs: 0.0,
            WorkspaceWidth: workspace.ActualWidth,
            WorkspaceHeight: workspace.ActualHeight,
            LeftRail: CaptureBounds(leftRail, workspace),
            CenterContent: CaptureBounds(centerContent, workspace),
            DockCapsule: CaptureBounds(dockCapsule, workspace),
            RightPane: CaptureBounds(rightPane, workspace),
            ExtraTarget: extraTarget != null ? CaptureBounds(extraTarget, workspace) : null,
            ExtraLabel: extraLabel));

        byte[]? lastAnimatedPixels = null;
        int popW = 0, popH = 0;
        int popSnapshotFrameIndex = -1;

        var frame = new DispatcherFrame();
        var sw = Stopwatch.StartNew();
        long prevTicks = Stopwatch.GetTimestamp();
        int idx = 0;
        bool capturedAnimatedEnd = false;

        void OnRender(object? s, EventArgs e)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            double dtMs = (double)(nowTicks - prevTicks) * 1000.0 / Stopwatch.Frequency;
            prevTicks = nowTicks;
            double elapsedMs = sw.Elapsed.TotalMilliseconds;
            idx++;

            var sample = new MotionFrameSample(
                FrameIndex: idx,
                ElapsedMs: elapsedMs,
                FrameDtMs: dtMs,
                WorkspaceWidth: workspace.ActualWidth,
                WorkspaceHeight: workspace.ActualHeight,
                LeftRail: CaptureBounds(leftRail, workspace),
                CenterContent: CaptureBounds(centerContent, workspace),
                DockCapsule: CaptureBounds(dockCapsule, workspace),
                RightPane: CaptureBounds(rightPane, workspace),
                ExtraTarget: extraTarget != null ? CaptureBounds(extraTarget, workspace) : null,
                ExtraLabel: extraLabel);

            samples.Add(sample);

            // Capture "last animated frame" snapshot once near end of transition (only after >= 360ms,
            // or right while BitmapCache is still active near settle) and exclude the snapshot tick itself from I5.
            if (capturePopPixels && !capturedAnimatedEnd && elapsedMs >= 320.0)
            {
                bool anyCache = sample.RightPane.HasBitmapCache || sample.LeftRail.HasBitmapCache ||
                                (sample.ExtraTarget?.HasBitmapCache ?? false);
                if (anyCache || elapsedMs >= 380.0)
                {
                    capturedAnimatedEnd = true;
                    popSnapshotFrameIndex = idx + 1;
                    (lastAnimatedPixels, popW, popH) = CaptureRegionPixels(workspace, simulatedDpiScale);
                    prevTicks = Stopwatch.GetTimestamp(); // do not charge RenderTargetBitmap cost to next frame
                }
            }

            if (elapsedMs >= captureDurationMs)
            {
                CompositionTarget.Rendering -= OnRender;
                frame.Continue = false;
            }
        }

        // Fire trigger, immediately subscribe to CompositionTarget.Rendering to capture pure uncontaminated frames
        triggerAction();
        CompositionTarget.Rendering += OnRender;
        Dispatcher.PushFrame(frame);

        // Settle idle frame & compute pixel diff for I4 (detects BitmapCache on/off pop)
        PumpDispatcher(80);
        PixelDiffMetrics? popDiff = null;
        if (capturePopPixels && lastAnimatedPixels != null && popW > 0 && popH > 0)
        {
            var (idlePixels, iw, ih) = CaptureRegionPixels(workspace, simulatedDpiScale);
            if (idlePixels != null && iw == popW && ih == popH)
            {
                popDiff = ComputePixelDiff(lastAnimatedPixels, idlePixels, popW, popH);
            }
        }

        // Write CSV
        Directory.CreateDirectory(csvDirectory);
        string safeName = string.Concat(scenarioName.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
        string csvPath = Path.Combine(csvDirectory, $"{safeName}.csv");
        WriteCsv(csvPath, samples, reducedMotion, forceOverride, sysAnim, vsyncMs);

        return EvaluateInvariants(
            scenarioName,
            isInlineMode,
            window.ActualWidth,
            window.WindowState,
            simulatedDpiScale,
            reducedMotion,
            forceOverride,
            sysAnim,
            vsyncMs,
            expectedFinalRailWidth,
            expectedFinalPaneWidth,
            samples,
            popSnapshotFrameIndex,
            popDiff,
            csvPath);
    }

    /// <summary>
    /// Deterministic frame-clock shell probe using SpringAnimator's exact zero-allocation integrator
    /// stepped at fixed <paramref name="hz"/> intervals (60 Hz or 165 Hz). Measures real CPU + WPF
    /// Measure/Arrange work time per frame (uncontaminated by Windows occluded-session DWM sleep)
    /// alongside real TransformToVisual bounds and RenderTargetBitmap end-pop checks (I1-I5).
    /// </summary>
    public static MotionProbeResult RecordShellTransitionDeterministic(
        MainWindow window,
        string scenarioName,
        Action triggerAction,
        bool isInlineMode,
        double expectedFinalRailWidth,
        double expectedFinalPaneWidth,
        double hz = 60.0,
        string? csvDirectory = null,
        double simulatedDpiScale = 1.0,
        FrameworkElement? extraTarget = null,
        string? extraLabel = null,
        bool capturePopPixels = true)
    {
        var workspace = (Grid)window.FindName("WorkspaceGrid");
        var leftRail = (Border)window.FindName("LeftRailBorder");
        var centerContent = (Grid)window.FindName("ContentStageHost");
        var dockCapsule = (Border)window.FindName("FloatingDockCapsule");
        var rightPane = (Border)window.FindName("RightSplitPaneBorder");

        window.UpdateLayout();
        PumpDispatcher(30);

        bool reducedMotion = SpringAnimator.IsReducedMotion;
        bool? forceOverride = SpringAnimator.ForceReducedMotionOverride;
        bool sysAnim = SystemParameters.ClientAreaAnimation;
        double vsyncMs = 1000.0 / hz;
        double dtSec = 1.0 / hz;

        var samples = new List<MotionFrameSample>(120);
        MotionFrameSample TakeSample(int idx, double elapsedMs, double workMs) => new(
            FrameIndex: idx,
            ElapsedMs: elapsedMs,
            FrameDtMs: workMs,
            WorkspaceWidth: workspace.ActualWidth,
            WorkspaceHeight: workspace.ActualHeight,
            LeftRail: CaptureBounds(leftRail, workspace),
            CenterContent: CaptureBounds(centerContent, workspace),
            DockCapsule: CaptureBounds(dockCapsule, workspace),
            RightPane: CaptureBounds(rightPane, workspace),
            ExtraTarget: extraTarget != null ? CaptureBounds(extraTarget, workspace) : null,
            ExtraLabel: extraLabel);

        samples.Add(TakeSample(0, 0.0, 0.0));

        byte[]? lastAnimatedPixels = null;
        int popW = 0, popH = 0;
        int popSnapshotFrameIndex = -1;

        bool prevExtClock = SpringAnimator.UseExternalClockForTest;
        SpringAnimator.UseExternalClockForTest = true;
        try
        {
            long tTrigger = Stopwatch.GetTimestamp();
            triggerAction();
            window.UpdateLayout();
            double triggerWorkMs = (Stopwatch.GetTimestamp() - tTrigger) * 1000.0 / Stopwatch.Frequency;

            // If Reduced Motion or instant transition with no active springs, record the immediate post-trigger state
            int maxFrames = (int)Math.Ceiling(hz * 0.90);
            if (!SpringAnimator.HasActiveSprings)
            {
                samples.Add(TakeSample(1, vsyncMs, triggerWorkMs));
            }
            else
            {
                for (int f = 1; f <= maxFrames; f++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    SpringAnimator.StepExternalClockForTest(dtSec);
                    window.UpdateLayout();
                    double workMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (f == 1)
                    {
                        workMs += triggerWorkMs;
                    }

                    samples.Add(TakeSample(f, f * vsyncMs, workMs));

                    if (!SpringAnimator.HasActiveSprings)
                    {
                        if (capturePopPixels)
                        {
                            popSnapshotFrameIndex = f + 1;
                            (lastAnimatedPixels, popW, popH) = CaptureRegionPixels(workspace, simulatedDpiScale);
                        }
                        break;
                    }
                }
            }
        }
        finally
        {
            SpringAnimator.UseExternalClockForTest = prevExtClock;
        }

        PumpDispatcher(30);
        window.UpdateLayout();

        PixelDiffMetrics? popDiff = null;
        if (capturePopPixels && lastAnimatedPixels != null && popW > 0 && popH > 0)
        {
            var (idlePixels, iw, ih) = CaptureRegionPixels(workspace, simulatedDpiScale);
            if (idlePixels != null && iw == popW && ih == popH)
            {
                popDiff = ComputePixelDiff(lastAnimatedPixels, idlePixels, popW, popH);
            }
        }

        string csvPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(csvDirectory))
        {
            Directory.CreateDirectory(csvDirectory);
            string safeName = string.Concat(scenarioName.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'));
            csvPath = Path.Combine(csvDirectory, $"{safeName}.csv");
            WriteCsv(csvPath, samples, reducedMotion, forceOverride, sysAnim, vsyncMs);
        }

        return EvaluateInvariants(
            scenarioName,
            isInlineMode,
            window.ActualWidth,
            window.WindowState,
            simulatedDpiScale,
            reducedMotion,
            forceOverride,
            sysAnim,
            vsyncMs,
            expectedFinalRailWidth,
            expectedFinalPaneWidth,
            samples,
            popSnapshotFrameIndex,
            popDiff,
            csvPath);
    }

    private static MotionProbeResult EvaluateInvariants(
        string scenarioName,
        bool isInlineMode,
        double windowWidth,
        WindowState windowState,
        double dpiScale,
        bool reducedMotion,
        bool? forceOverride,
        bool sysAnim,
        double vsyncMs,
        double expectedRailW,
        double expectedPaneW,
        List<MotionFrameSample> frames,
        int popSnapshotFrameIndex,
        PixelDiffMetrics? popDiff,
        string csvPath)
    {
        var reasons = new List<string>();
        var first = frames[0];
        var last = frames[^1];

        // Extract edge trajectories for I1 (No Jump) and I3 (Monotonic)
        double[] railRight = frames.Select(f => f.LeftRail.Right).ToArray();
        double[] contentLeft = frames.Select(f => f.CenterContent.Left).ToArray();
        double[] contentRight = frames.Select(f => f.CenterContent.Right).ToArray();
        double[] dockLeft = frames.Select(f => f.DockCapsule.Left).ToArray();
        double[] dockRight = frames.Select(f => f.DockCapsule.Right).ToArray();

        // For right pane left edge: when collapsed, its visual edge is workspace right edge (WorkspaceWidth)
        double[] paneLeft = frames.Select(f =>
            f.RightPane.Visibility == Visibility.Visible ? f.RightPane.Left : f.WorkspaceWidth).ToArray();

        double[] dts = frames.Select(f => f.FrameDtMs).ToArray();

        double maxFirstJumpPct = 0;
        double maxStepPct = 0;
        double maxOvershoot = 0;
        double maxReversal = 0;
        bool i1Pass = true;
        bool i3Pass = true;

        var tracks = new (string Name, double[] Values)[]
        {
            ("LeftRail.Right", railRight),
            ("Content.Left", contentLeft),
            ("Content.Right", contentRight),
            ("Dock.Left", dockLeft),
            ("Dock.Right", dockRight),
            ("RightPane.Left", paneLeft)
        };

        foreach (var (name, vals) in tracks)
        {
            double start = vals[0];
            double end = vals[^1];
            double totalTravel = Math.Abs(end - start);
            if (totalTravel < 5.0) continue;

            double dir = Math.Sign(end - start);

            // First frame jump check (frame 0 -> frame 1):
            // Limit is 10% at 60Hz (scaled down if higher refresh rate, NEVER inflated by frame drops!)
            double refreshScale = Math.Clamp(vsyncMs / 16.67, 0.35, 1.0);
            double firstFrameLimit = 10.0 * refreshScale;
            double stepLimit = 25.0 * refreshScale;

            if (vals.Length > 1)
            {
                double firstDelta = Math.Abs(vals[1] - vals[0]);
                double firstPct = (firstDelta / totalTravel) * 100.0;
                if (firstPct > maxFirstJumpPct) maxFirstJumpPct = firstPct;

                if (!reducedMotion && firstPct > firstFrameLimit)
                {
                    i1Pass = false;
                    reasons.Add($"I1:{name} 1stFrame={firstPct:F1}% (limit {firstFrameLimit:F1}%)");
                }
            }

            // Per-frame delta + monotonicity
            for (int i = 1; i < vals.Length; i++)
            {
                double signedDelta = vals[i] - vals[i - 1];
                double absDelta = Math.Abs(signedDelta);
                double stepPct = (absDelta / totalTravel) * 100.0;
                if (stepPct > maxStepPct) maxStepPct = stepPct;

                if (!reducedMotion && stepPct > stepLimit)
                {
                    i1Pass = false;
                    reasons.Add($"I1:{name} f{i} step={stepPct:F1}% (limit {stepLimit:F1}%)");
                }

                // Reversal opposite to travel direction
                if (signedDelta * dir < -0.5)
                {
                    double rev = Math.Abs(signedDelta);
                    if (rev > maxReversal) maxReversal = rev;
                    i3Pass = false;
                    reasons.Add($"I3:{name} f{i} rev={rev:F1} DIP");
                }

                // Overshoot past target
                double overshoot = (vals[i] - end) * dir;
                if (overshoot > 0.5)
                {
                    if (overshoot > maxOvershoot) maxOvershoot = overshoot;
                    i3Pass = false;
                    reasons.Add($"I3:{name} f{i} overshoot={overshoot:F1} DIP");
                }
            }
        }

        // I2: Adjacency on every frame
        double maxRailContentGap = 0;
        double maxPaneContentGap = 0;
        double maxDockMarginErr = 0;
        bool i2Pass = true;

        foreach (var f in frames)
        {
            double railGap = Math.Abs(f.CenterContent.Left - f.LeftRail.Right);
            if (railGap > maxRailContentGap) maxRailContentGap = railGap;

            double dockLeftErr = Math.Abs(f.DockCapsule.Left - (f.CenterContent.Left + 16.0));
            double dockRightErr = Math.Abs((f.CenterContent.Right - 16.0) - f.DockCapsule.Right);
            double dockErr = Math.Max(dockLeftErr, dockRightErr);
            if (dockErr > maxDockMarginErr) maxDockMarginErr = dockErr;

            if (isInlineMode)
            {
                double paneLeftEdge = f.RightPane.Visibility == Visibility.Visible ? f.RightPane.Left : f.WorkspaceWidth;
                double paneGap = Math.Abs(paneLeftEdge - f.CenterContent.Right);
                if (paneGap > maxPaneContentGap) maxPaneContentGap = paneGap;
            }
        }

        if (maxRailContentGap > 1.0)
        {
            i2Pass = false;
            reasons.Add($"I2:Rail-Content gap={maxRailContentGap:F1} DIP");
        }
        if (isInlineMode && maxPaneContentGap > 1.0)
        {
            i2Pass = false;
            reasons.Add($"I2:Pane-Content gap={maxPaneContentGap:F1} DIP");
        }
        if (maxDockMarginErr > 1.0)
        {
            i2Pass = false;
            reasons.Add($"I2:Dock margin err={maxDockMarginErr:F1} DIP");
        }

        // I4: Final geometry completion & pop check
        double railErr = Math.Abs(last.LeftRail.Width - expectedRailW);
        double actualFinalPaneW = last.RightPane.Visibility == Visibility.Visible ? last.RightPane.Width : 0.0;
        double paneErr = Math.Abs(actualFinalPaneW - expectedPaneW);
        double finalGeomErr = Math.Max(railErr, paneErr);
        bool i4Pass = finalGeomErr <= 1.0;
        if (!i4Pass)
        {
            reasons.Add($"I4:Final geom err={finalGeomErr:F1} DIP (rail={last.LeftRail.Width:F1}/{expectedRailW}, pane={actualFinalPaneW:F1}/{expectedPaneW})");
        }

        // I5: Pure frame time distribution during active transition (excluding snapshot tick if any)
        var activeDts = frames.Skip(1)
            .Where(f => f.ElapsedMs <= 420.0 && f.FrameIndex != popSnapshotFrameIndex)
            .Select(f => f.FrameDtMs)
            .OrderBy(x => x)
            .ToList();
        double p50 = Percentile(activeDts, 0.50);
        double p95 = Percentile(activeDts, 0.95);
        double maxDt = activeDts.Count > 0 ? activeDts[^1] : 0;

        bool i5Pass = p95 <= (1.5 * vsyncMs) && maxDt <= (3.0 * vsyncMs);
        if (!i5Pass)
        {
            reasons.Add($"I5:p95={p95:F1}ms (limit {1.5 * vsyncMs:F1}ms), max={maxDt:F1}ms (limit {3.0 * vsyncMs:F1}ms)");
        }

        return new MotionProbeResult(
            ScenarioName: scenarioName,
            IsInlineMode: isInlineMode,
            WindowWidthDip: windowWidth,
            WindowState: windowState,
            SimulatedDpiScale: dpiScale,
            ReducedMotionActive: reducedMotion,
            ForceReducedMotionOverride: forceOverride,
            SystemClientAreaAnimation: sysAnim,
            MeasuredVsyncMs: vsyncMs,
            Frames: frames,
            MaxFirstFrameJumpPct: maxFirstJumpPct,
            MaxStepDeltaPct: maxStepPct,
            MaxRailContentGapDip: maxRailContentGap,
            MaxPaneContentGapDip: maxPaneContentGap,
            MaxDockMarginErrorDip: maxDockMarginErr,
            MaxOvershootDip: maxOvershoot,
            MaxReversalDip: maxReversal,
            FinalGeometryErrorDip: finalGeomErr,
            EndPopDiff: popDiff,
            P50FrameMs: p50,
            P95FrameMs: p95,
            MaxFrameMs: maxDt,
            PassI1NoJump: i1Pass,
            PassI2Adjacency: i2Pass,
            PassI3Monotonic: i3Pass,
            PassI4Completion: i4Pass,
            PassI5FrameTime: i5Pass,
            FailureReasons: string.Join("; ", reasons.Distinct().Take(6)),
            CsvPath: csvPath);
    }

    public static (byte[]? Pixels, int Width, int Height) CaptureRegionPixels(FrameworkElement target, double dpiScale)
    {
        int w = (int)Math.Max(1, Math.Round(target.ActualWidth * dpiScale));
        int h = (int)Math.Max(1, Math.Round(target.ActualHeight * dpiScale));
        if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return (null, 0, 0);

        try
        {
            var rtb = new RenderTargetBitmap(w, h, 96.0 * dpiScale, 96.0 * dpiScale, PixelFormats.Pbgra32);
            rtb.Render(target);
            byte[] buf = new byte[w * h * 4];
            rtb.CopyPixels(buf, w * 4, 0);
            return (buf, w, h);
        }
        catch
        {
            return (null, 0, 0);
        }
    }

    public static PixelDiffMetrics ComputePixelDiff(byte[] a, byte[] b, int width, int height)
    {
        int totalPixels = width * height;
        int changed = 0;
        int maxDelta = 0;
        long sumDelta = 0;

        for (int i = 0; i < a.Length; i += 4)
        {
            int db = Math.Abs(a[i] - b[i]);
            int dg = Math.Abs(a[i + 1] - b[i + 1]);
            int dr = Math.Abs(a[i + 2] - b[i + 2]);
            int m = Math.Max(dr, Math.Max(dg, db));
            if (m > maxDelta) maxDelta = m;
            if (m > 2) changed++;
            sumDelta += dr + dg + db;
        }

        double pct = totalPixels > 0 ? (changed * 100.0 / totalPixels) : 0.0;
        double mean = totalPixels > 0 ? (sumDelta / (totalPixels * 3.0)) : 0.0;
        return new PixelDiffMetrics(width, height, changed, pct, maxDelta, mean);
    }

    public static void PumpDispatcher(int durationMs)
    {
        var sw = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(15, durationMs))
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        _ = sw.Elapsed;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        int idx = (int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static void WriteCsv(
        string path,
        List<MotionFrameSample> samples,
        bool reducedMotion,
        bool? forceOverride,
        bool sysAnim,
        double vsyncMs)
    {
        var sb = new StringBuilder(8192);
        sb.AppendLine($"# ReducedMotion={reducedMotion}, ForceOverride={forceOverride?.ToString() ?? "null"}, SystemClientAreaAnimation={sysAnim}, MeasuredVsyncMs={vsyncMs.ToString("F2", CultureInfo.InvariantCulture)}");
        sb.AppendLine("frame,elapsed_ms,dt_ms,ws_w,rail_L,rail_R,rail_W,rail_op,rail_cache,content_L,content_R,content_W,dock_L,dock_R,dock_W,pane_L,pane_R,pane_W,pane_layoutW,pane_op,pane_vis,pane_cache,extra_L,extra_T,extra_W,extra_H,extra_op,extra_cache");

        foreach (var s in samples)
        {
            var ex = s.ExtraTarget;
            sb.AppendLine(string.Format(
                CultureInfo.InvariantCulture,
                "{0},{1:F2},{2:F2},{3:F1},{4:F1},{5:F1},{6:F1},{7:F3},{8},{9:F1},{10:F1},{11:F1},{12:F1},{13:F1},{14:F1},{15:F1},{16:F1},{17:F1},{18:F1},{19:F3},{20},{21},{22:F1},{23:F1},{24:F1},{25:F1},{26:F3},{27}",
                s.FrameIndex, s.ElapsedMs, s.FrameDtMs, s.WorkspaceWidth,
                s.LeftRail.Left, s.LeftRail.Right, s.LeftRail.Width, s.LeftRail.Opacity, s.LeftRail.HasBitmapCache ? 1 : 0,
                s.CenterContent.Left, s.CenterContent.Right, s.CenterContent.Width,
                s.DockCapsule.Left, s.DockCapsule.Right, s.DockCapsule.Width,
                s.RightPane.Left, s.RightPane.Right, s.RightPane.Width, s.RightPane.LayoutWidth, s.RightPane.Opacity, s.RightPane.Visibility, s.RightPane.HasBitmapCache ? 1 : 0,
                ex?.Left ?? 0, ex?.Top ?? 0, ex?.Width ?? 0, ex?.Height ?? 0, ex?.Opacity ?? 0, (ex?.HasBitmapCache ?? false) ? 1 : 0));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}
