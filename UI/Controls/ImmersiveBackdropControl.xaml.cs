using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AuraAudio.UI.Helpers;
using Microsoft.Win32;

namespace AuraAudio.UI.Controls;

public enum BackdropScrimMode
{
    FullScreen,
    MiniCompact,
    MiniTray
}

/// <summary>
/// Artwork-derived ambient multi-blob backdrop for the Full-Screen player and Mini-Player.
/// Renders soft 5-stop RadialGradientBrush blobs animated strictly via RenderTransform
/// on SpringAnimator's deduplicated clock (zero BlurEffect, zero DropShadowEffect,
/// zero per-frame allocations in StepFrame), protected by a post-sRGB luminance ceiling
/// and a pure-neutral black readability scrim.
/// </summary>
public partial class ImmersiveBackdropControl : UserControl, SpringAnimator.IFrameTicker
{
    public const double CrossFadeDurationSeconds = 0.65;
    private const double TwoPi = Math.PI * 2.0;

    // Prime-ratio Lissajous periods in [20, 40] s for calm, non-repeating organic drift
    private const double Blob1PeriodX = 24.0;
    private const double Blob1PeriodY = 29.0;
    private const double Blob1ScalePeriod = 34.0;
    private const double Blob1RotPeriod = 40.0;

    private const double Blob2PeriodX = 31.0;
    private const double Blob2PeriodY = 37.0;
    private const double Blob2ScalePeriod = 27.0;
    private const double Blob2RotPeriod = 38.0;

    private const double Blob3PeriodX = 38.0;
    private const double Blob3PeriodY = 23.0;
    private const double Blob3ScalePeriod = 33.0;
    private const double Blob3RotPeriod = 36.0;

    // Rule 5 performance guard: rolling 2.0 s window, auto-freeze if p95 > 1.5x vsync for 2 consecutive windows
    private const double PerfWindowSeconds = 2.0;
    private const int PerfWindowCapacity = 256;

    public static readonly DependencyProperty CoverArtCacheKeyProperty =
        DependencyProperty.Register(
            nameof(CoverArtCacheKey),
            typeof(string),
            typeof(ImmersiveBackdropControl),
            new PropertyMetadata(null, OnCoverArtCacheKeyChanged));

    public static readonly DependencyProperty IsPlayingProperty =
        DependencyProperty.Register(
            nameof(IsPlaying),
            typeof(bool),
            typeof(ImmersiveBackdropControl),
            new PropertyMetadata(false, OnPlaybackOrStateChanged));

    public static readonly DependencyProperty IsAnimatedBackdropEnabledProperty =
        DependencyProperty.Register(
            nameof(IsAnimatedBackdropEnabled),
            typeof(bool),
            typeof(ImmersiveBackdropControl),
            new PropertyMetadata(true, OnPlaybackOrStateChanged));

    public static readonly DependencyProperty IsHostActiveProperty =
        DependencyProperty.Register(
            nameof(IsHostActive),
            typeof(bool),
            typeof(ImmersiveBackdropControl),
            new PropertyMetadata(true, OnPlaybackOrStateChanged));

    public static readonly DependencyProperty CompactModeProperty =
        DependencyProperty.Register(
            nameof(CompactMode),
            typeof(bool),
            typeof(ImmersiveBackdropControl),
            new PropertyMetadata(false, OnLayoutModeChanged));

    public static readonly DependencyProperty ScrimModeProperty =
        DependencyProperty.Register(
            nameof(ScrimMode),
            typeof(BackdropScrimMode),
            typeof(ImmersiveBackdropControl),
            new PropertyMetadata(BackdropScrimMode.FullScreen, OnScrimModeChanged));

    public string? CoverArtCacheKey
    {
        get => (string?)GetValue(CoverArtCacheKeyProperty);
        set => SetValue(CoverArtCacheKeyProperty, value);
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public bool IsAnimatedBackdropEnabled
    {
        get => (bool)GetValue(IsAnimatedBackdropEnabledProperty);
        set => SetValue(IsAnimatedBackdropEnabledProperty, value);
    }

    public bool IsHostActive
    {
        get => (bool)GetValue(IsHostActiveProperty);
        set => SetValue(IsHostActiveProperty, value);
    }

    public bool CompactMode
    {
        get => (bool)GetValue(CompactModeProperty);
        set => SetValue(CompactModeProperty, value);
    }

    public BackdropScrimMode ScrimMode
    {
        get => (BackdropScrimMode)GetValue(ScrimModeProperty);
        set => SetValue(ScrimModeProperty, value);
    }

    private BackdropTuningPreset _tuningPreset = ArtworkPaletteExtractor.ActiveDefault;
    private BackdropPalette _activePalette = BackdropPalette.NeutralFallback;
    private bool _showingLayerA = true;
    private bool _hasAppliedInitialPalette;
    private bool _isCrossFading;
    private double _crossFadeElapsed;
    private double _driftTimeSeconds;
    private bool _isTickerRegistered;
    private bool _isHostMinimized;
    private bool _isBatterySaverActive;
    private bool _autoFallbackFrozen;
    private bool _suppressColorForNeutralityAudit;

    private Window? _hostWindow;
    private bool _eventsSubscribed;
    private int _extractionRequestVersion;
    private CancellationTokenSource? _extractCts;
    private double _layoutWidth;
    private double _layoutHeight;
    private double _vsyncIntervalMs = 16.6667;

    // Zero-allocation rolling window buffers for Rule 5 auto-fallback
    private readonly double[] _perfFrameDtMs = new double[PerfWindowCapacity];
    private readonly double[] _perfSortScratch = new double[PerfWindowCapacity];
    private int _perfSampleCount;
    private double _perfWindowElapsed;
    private int _consecutiveSlowWindows;

    // Pre-allocated state tracking for drift phase transforms
    private double _lastAppliedDriftTime = double.NaN;

    public ImmersiveBackdropControl()
    {
        InitializeComponent();

        _vsyncIntervalMs = QueryDisplayVsyncMs();
        ApplyPaletteToLayer(isLayerA: true, BackdropPalette.NeutralFallback);
        LayerA.Opacity = 1.0;
        LayerB.Opacity = 0.0;
        ApplyScrimBrush();
        UpdateCompactModeVisibility();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnBackdropSizeChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    internal BackdropTuningPreset TuningPreset
    {
        get => _tuningPreset;
        set
        {
            if (_tuningPreset == value) return;
            _tuningPreset = value;
            ApplyScrimBrush();
            RequestPaletteForKey(CoverArtCacheKey);
        }
    }

    internal bool SuppressColorForNeutralityAudit
    {
        get => _suppressColorForNeutralityAudit;
        set
        {
            if (_suppressColorForNeutralityAudit == value) return;
            _suppressColorForNeutralityAudit = value;
            LayerA.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            LayerB.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            ScrimOverlay.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            if (value)
            {
                SpatialScrimTop.Visibility = Visibility.Collapsed;
                SpatialScrimLyrics.Visibility = Visibility.Collapsed;
                SpatialScrimBottom.Visibility = Visibility.Collapsed;
            }
            else
            {
                ApplyScrimBrush();
            }
            UpdateAnimationLifecycle();
        }
    }

    internal BackdropPalette ActivePalette => _activePalette;
    internal bool IsDriftAnimating => _isTickerRegistered && ShouldDriftAnimate;
    internal bool IsCrossFading => _isCrossFading;
    internal bool IsAutoFallbackFrozen => _autoFallbackFrozen;
    internal bool IsTickerRegisteredForTest => _isTickerRegistered;
    internal double DriftTimeSecondsForTest => _driftTimeSeconds;
    internal double LayerAOpacityForTest => LayerA.Opacity;
    internal double LayerBOpacityForTest => LayerB.Opacity;

    internal bool ShouldDriftAnimate =>
        IsLoaded &&
        IsVisible &&
        IsHostActive &&
        IsPlaying &&
        !_isHostMinimized &&
        !SpringAnimator.IsReducedMotion &&
        IsAnimatedBackdropEnabled &&
        !_isBatterySaverActive &&
        !_autoFallbackFrozen &&
        !_suppressColorForNeutralityAudit;

    internal void RefreshPowerStatusForTest()
    {
        _isBatterySaverActive = PowerStatusHelper.IsBatterySaverActive;
        UpdateAnimationLifecycle();
    }

    internal void SetHostMinimizedForTest(bool minimized)
    {
        _isHostMinimized = minimized;
        UpdateAnimationLifecycle();
    }

    internal void ResetAutoFallbackForTest()
    {
        _autoFallbackFrozen = false;
        _consecutiveSlowWindows = 0;
        _perfSampleCount = 0;
        _perfWindowElapsed = 0.0;
        UpdateAnimationLifecycle();
    }

    internal void RecordFrameDtForFallbackTest(double dtMs, double vsyncMs)
    {
        _vsyncIntervalMs = Math.Max(4.0, vsyncMs);
        RecordPerformanceSample(dtMs / 1000.0);
        if (_autoFallbackFrozen)
        {
            UpdateAnimationLifecycle();
        }
    }

    internal void SetDriftTimeForTest(double seconds)
    {
        _driftTimeSeconds = seconds;
        _lastAppliedDriftTime = double.NaN;
        ApplyDriftTransforms(_driftTimeSeconds);
    }

    internal void ApplyPaletteImmediatelyForTest(BackdropPalette palette)
    {
        _activePalette = palette;
        _hasAppliedInitialPalette = true;
        _isCrossFading = false;
        _crossFadeElapsed = 0.0;
        _showingLayerA = true;
        ApplyPaletteToLayer(isLayerA: true, palette);
        LayerA.Opacity = 1.0;
        LayerB.Opacity = 0.0;
        ApplyDriftTransforms(_driftTimeSeconds);
        UpdateAnimationLifecycle();
    }

    private static void OnCoverArtCacheKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ImmersiveBackdropControl ctrl)
        {
            ctrl.RequestPaletteForKey(e.NewValue as string);
        }
    }

    private static void OnPlaybackOrStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ImmersiveBackdropControl ctrl)
        {
            ctrl.UpdateAnimationLifecycle();
        }
    }

    private static void OnLayoutModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ImmersiveBackdropControl ctrl)
        {
            ctrl.UpdateCompactModeVisibility();
            ctrl.PositionBlobsForSize(ctrl.ActualWidth, ctrl.ActualHeight);
        }
    }

    private static void OnScrimModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ImmersiveBackdropControl ctrl)
        {
            ctrl.ApplyScrimBrush();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeHostAndSystemEvents();
        _isBatterySaverActive = PowerStatusHelper.IsBatterySaverActive;
        _isHostMinimized = _hostWindow?.WindowState == WindowState.Minimized;
        ApplyScrimBrush();
        UpdateCompactModeVisibility();
        PositionBlobsForSize(ActualWidth, ActualHeight);
        RequestPaletteForKey(CoverArtCacheKey);
        UpdateAnimationLifecycle();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelPendingExtraction();
        UnsubscribeHostAndSystemEvents();
        StopTicker();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _isBatterySaverActive = PowerStatusHelper.IsBatterySaverActive;
            if (ActualWidth > 1.0 && ActualHeight > 1.0)
            {
                PositionBlobsForSize(ActualWidth, ActualHeight);
            }
        }
        UpdateAnimationLifecycle();
    }

    private void OnBackdropSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PositionBlobsForSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void SubscribeHostAndSystemEvents()
    {
        if (_eventsSubscribed) return;
        _eventsSubscribed = true;

        _hostWindow = Window.GetWindow(this);
        if (_hostWindow != null)
        {
            _hostWindow.StateChanged += OnHostWindowStateChanged;
        }
        SpringAnimator.ReducedMotionChanged += OnReducedMotionChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void UnsubscribeHostAndSystemEvents()
    {
        if (!_eventsSubscribed) return;
        _eventsSubscribed = false;

        if (_hostWindow != null)
        {
            _hostWindow.StateChanged -= OnHostWindowStateChanged;
            _hostWindow = null;
        }
        SpringAnimator.ReducedMotionChanged -= OnReducedMotionChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnHostWindowStateChanged(object? sender, EventArgs e)
    {
        _isHostMinimized = _hostWindow?.WindowState == WindowState.Minimized;
        UpdateAnimationLifecycle();
    }

    private void OnReducedMotionChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnReducedMotionChanged);
            return;
        }

        if (SpringAnimator.IsReducedMotion && _isCrossFading)
        {
            CompleteCrossFadeImmediately();
        }
        UpdateAnimationLifecycle();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() =>
            {
                _isBatterySaverActive = PowerStatusHelper.IsBatterySaverActive;
                UpdateAnimationLifecycle();
            });
            return;
        }

        _isBatterySaverActive = PowerStatusHelper.IsBatterySaverActive;
        UpdateAnimationLifecycle();
    }

    private void UpdateCompactModeVisibility()
    {
        Visibility thirdBlobVis = CompactMode ? Visibility.Collapsed : Visibility.Visible;
        BlobA3.Visibility = thirdBlobVis;
        BlobB3.Visibility = thirdBlobVis;
    }

    private void ApplyScrimBrush()
    {
        SpatialScrimTop.Visibility = Visibility.Collapsed;
        SpatialScrimLyrics.Visibility = Visibility.Collapsed;
        SpatialScrimBottom.Visibility = Visibility.Collapsed;

        if (_suppressColorForNeutralityAudit)
        {
            return;
        }

        bool isSafeDefault = _tuningPreset == ArtworkPaletteExtractor.SafeDefault;
        if (isSafeDefault)
        {
            string key = ScrimMode switch
            {
                BackdropScrimMode.MiniCompact => "Brush.Overlay.MiniBackdropScrim",
                BackdropScrimMode.MiniTray => "Brush.Overlay.MiniTrayBackdropScrim",
                _ => "Brush.Overlay.BackdropScrim"
            };
            if (TryFindResource(key) is Brush tokenBrush)
            {
                ScrimOverlay.Fill = tokenBrush;
                return;
            }
        }

        // Spatially varying pure-black scrim preset (lighter in open ambient artwork area,
        // deeper strictly behind top chrome, lyrics reading column, and lower transport/metadata controls)
        if (_tuningPreset.UseSpatialScrim)
        {
            if (ScrimMode == BackdropScrimMode.FullScreen)
            {
                var ambient = new SolidColorBrush(Color.FromArgb(ToAlphaByte(_tuningPreset.SpatialAmbientOpacity), 0, 0, 0));
                ambient.Freeze();
                ScrimOverlay.Fill = ambient;

                byte aTop = ToAlphaByte(_tuningPreset.SpatialTopBarOpacity);
                var topGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                topGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aTop, 0, 0, 0), 0.00));
                topGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aTop, 0, 0, 0), 0.06));
                topGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.18));
                topGrad.Freeze();
                SpatialScrimTop.Fill = topGrad;
                SpatialScrimTop.Visibility = Visibility.Visible;

                byte aLyrics = ToAlphaByte(_tuningPreset.SpatialLyricsColumnOpacity);
                var lyricsGrad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                lyricsGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.00));
                lyricsGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.30));
                lyricsGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aLyrics, 0, 0, 0), 0.45));
                lyricsGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aLyrics, 0, 0, 0), 1.00));
                lyricsGrad.Freeze();
                SpatialScrimLyrics.Fill = lyricsGrad;
                SpatialScrimLyrics.Visibility = Visibility.Visible;

                byte aBot = ToAlphaByte(_tuningPreset.SpatialBottomControlsOpacity);
                var botGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                botGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.00));
                botGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.42));
                botGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aBot, 0, 0, 0), 0.62));
                botGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aBot, 0, 0, 0), 1.00));
                botGrad.Freeze();
                SpatialScrimBottom.Fill = botGrad;
                SpatialScrimBottom.Visibility = Visibility.Visible;
                return;
            }

            if (ScrimMode == BackdropScrimMode.MiniCompact)
            {
                var ambient = new SolidColorBrush(Color.FromArgb(ToAlphaByte(_tuningPreset.SpatialMiniLeftGlowOpacity), 0, 0, 0));
                ambient.Freeze();
                ScrimOverlay.Fill = ambient;

                byte aRight = ToAlphaByte(_tuningPreset.SpatialMiniRightControlsOpacity);
                var rightGrad = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                rightGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.00));
                rightGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aRight, 0, 0, 0), 0.22));
                rightGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aRight, 0, 0, 0), 1.00));
                rightGrad.Freeze();
                SpatialScrimLyrics.Fill = rightGrad;
                SpatialScrimLyrics.Visibility = Visibility.Visible;

                byte aBot = ToAlphaByte(_tuningPreset.SpatialBottomControlsOpacity);
                var botGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                botGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.00));
                botGrad.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.22));
                botGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aBot, 0, 0, 0), 0.45));
                botGrad.GradientStops.Add(new GradientStop(Color.FromArgb(aBot, 0, 0, 0), 1.00));
                botGrad.Freeze();
                SpatialScrimBottom.Fill = botGrad;
                SpatialScrimBottom.Visibility = Visibility.Visible;
                return;
            }

            // MiniTray: subtle edge glow fading into reading scrim behind lyrics/queue list
            var traySolid = new SolidColorBrush(Color.FromArgb(ToAlphaByte(_tuningPreset.MiniTrayScrimOpacity), 0, 0, 0));
            traySolid.Freeze();
            ScrimOverlay.Fill = traySolid;
            return;
        }

        // Uniform custom preset scrim (strictly pure black #000000 with preset alpha)
        if (ScrimMode == BackdropScrimMode.FullScreen)
        {
            byte aTop = ToAlphaByte(_tuningPreset.FullScreenScrimTopOpacity);
            byte aMid = ToAlphaByte(_tuningPreset.FullScreenScrimMidOpacity);
            byte aBot = ToAlphaByte(_tuningPreset.FullScreenScrimBottomOpacity);
            var grad = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(aTop, 0, 0, 0), 0.0));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(aMid, 0, 0, 0), 0.5));
            grad.GradientStops.Add(new GradientStop(Color.FromArgb(aBot, 0, 0, 0), 1.0));
            grad.Freeze();
            ScrimOverlay.Fill = grad;
        }
        else
        {
            double op = ScrimMode == BackdropScrimMode.MiniCompact
                ? _tuningPreset.MiniCompactScrimOpacity
                : _tuningPreset.MiniTrayScrimOpacity;
            var solid = new SolidColorBrush(Color.FromArgb(ToAlphaByte(op), 0, 0, 0));
            solid.Freeze();
            ScrimOverlay.Fill = solid;
        }
    }

    private static byte ToAlphaByte(double opacity) =>
        (byte)Math.Clamp((int)Math.Round(opacity * 255.0), 0, 255);

    private void PositionBlobsForSize(double width, double height)
    {
        if (width <= 1.0 || height <= 1.0) return;
        _layoutWidth = width;
        _layoutHeight = height;

        double diag = Math.Sqrt((width * width) + (height * height));
        if (CompactMode)
        {
            PlaceBlobPair(BlobA1, BlobB1, width * 0.22, height * 0.45, diag * 0.96);
            PlaceBlobPair(BlobA2, BlobB2, width * 0.80, height * 0.55, diag * 0.92);
        }
        else
        {
            PlaceBlobPair(BlobA1, BlobB1, width * 0.22, height * 0.30, diag * 0.94);
            PlaceBlobPair(BlobA2, BlobB2, width * 0.76, height * 0.42, diag * 0.98);
            PlaceBlobPair(BlobA3, BlobB3, width * 0.52, height * 0.80, diag * 0.88);
        }

        _lastAppliedDriftTime = double.NaN;
        ApplyDriftTransforms(_driftTimeSeconds);
    }

    private static void PlaceBlobPair(Ellipse a, Ellipse b, double centerX, double centerY, double diameter)
    {
        double left = centerX - (diameter * 0.5);
        double top = centerY - (diameter * 0.5);

        a.Width = diameter;
        a.Height = diameter;
        Canvas.SetLeft(a, left);
        Canvas.SetTop(a, top);

        b.Width = diameter;
        b.Height = diameter;
        Canvas.SetLeft(b, left);
        Canvas.SetTop(b, top);
    }

    private void CancelPendingExtraction()
    {
        _extractCts?.Cancel();
        _extractCts?.Dispose();
        _extractCts = null;
    }

    private void RequestPaletteForKey(string? coverCacheKey)
    {
        CancelPendingExtraction();
        int reqVersion = ++_extractionRequestVersion;

        if (string.IsNullOrWhiteSpace(coverCacheKey))
        {
            TransitionToPalette(BackdropPalette.NeutralFallback);
            return;
        }

        var cts = new CancellationTokenSource();
        _extractCts = cts;
        var preset = _tuningPreset;

        var task = ArtworkPaletteExtractor.ExtractForCoverKeyAsync(coverCacheKey, preset, cts.Token);
        if (task.IsCompletedSuccessfully)
        {
            TransitionToPalette(task.Result);
            return;
        }

        _ = task.ContinueWith(t =>
        {
            if (t.IsCanceled || t.IsFaulted) return;
            var extracted = t.Result;
            Dispatcher.BeginInvoke(() =>
            {
                if (reqVersion != _extractionRequestVersion) return;
                TransitionToPalette(extracted);
            });
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void TransitionToPalette(BackdropPalette incoming)
    {
        if (_hasAppliedInitialPalette && incoming == _activePalette)
        {
            return;
        }

        _activePalette = incoming;

        // First paint, hidden/unloaded host, or Reduced Motion: instant swap without cross-fade animation
        if (!_hasAppliedInitialPalette || !IsLoaded || !IsVisible || SpringAnimator.IsReducedMotion)
        {
            _hasAppliedInitialPalette = true;
            _isCrossFading = false;
            _crossFadeElapsed = 0.0;
            _showingLayerA = true;
            ApplyPaletteToLayer(isLayerA: true, incoming);
            LayerA.Opacity = 1.0;
            LayerB.Opacity = 0.0;
            ApplyDriftTransforms(_driftTimeSeconds);
            UpdateAnimationLifecycle();
            return;
        }

        // Cross-fade into the currently inactive A/B layer over 650 ms
        bool targetIsLayerA = !_showingLayerA;
        ApplyPaletteToLayer(targetIsLayerA, incoming);
        _showingLayerA = targetIsLayerA;
        _isCrossFading = true;
        _crossFadeElapsed = 0.0;

        if (targetIsLayerA)
        {
            LayerA.Opacity = 0.0;
            LayerB.Opacity = 1.0;
        }
        else
        {
            LayerB.Opacity = 0.0;
            LayerA.Opacity = 1.0;
        }

        ApplyDriftTransforms(_driftTimeSeconds);
        UpdateAnimationLifecycle();
    }

    private void CompleteCrossFadeImmediately()
    {
        _isCrossFading = false;
        _crossFadeElapsed = CrossFadeDurationSeconds;
        LayerA.Opacity = _showingLayerA ? 1.0 : 0.0;
        LayerB.Opacity = _showingLayerA ? 0.0 : 1.0;
    }

    private void ApplyPaletteToLayer(bool isLayerA, BackdropPalette palette)
    {
        var baseBrush = new SolidColorBrush(palette.BaseColor);
        baseBrush.Freeze();

        var b1 = CreateSoftBlobBrush(palette.Blob1Color);
        var b2 = CreateSoftBlobBrush(palette.Blob2Color);
        var b3 = CreateSoftBlobBrush(palette.Blob3Color);

        if (isLayerA)
        {
            BaseRectA.Fill = baseBrush;
            BlobA1.Fill = b1;
            BlobA2.Fill = b2;
            BlobA3.Fill = b3;
        }
        else
        {
            BaseRectB.Fill = baseBrush;
            BlobB1.Fill = b1;
            BlobB2.Fill = b2;
            BlobB3.Fill = b3;
        }
    }

    /// <summary>
    /// 5-stop Gaussian-approximated soft radial gradient brush, frozen once on track change
    /// so WPF's GPU pipeline renders cloud-soft blending with zero BlurEffect and zero per-frame allocations.
    /// </summary>
    private static RadialGradientBrush CreateSoftBlobBrush(Color color)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(224, color.R, color.G, color.B), 0.00));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(176, color.R, color.G, color.B), 0.28));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(92, color.R, color.G, color.B), 0.56));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(26, color.R, color.G, color.B), 0.80));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.00));
        brush.Freeze();
        return brush;
    }

    private bool NeedsTicker =>
        IsLoaded &&
        IsVisible &&
        IsHostActive &&
        !_isHostMinimized &&
        !_suppressColorForNeutralityAudit &&
        (ShouldDriftAnimate || (_isCrossFading && !SpringAnimator.IsReducedMotion));

    private void UpdateAnimationLifecycle()
    {
        if (NeedsTicker)
        {
            if (!_isTickerRegistered)
            {
                _isTickerRegistered = true;
                SpringAnimator.RegisterFrameTicker(this);
            }
        }
        else
        {
            if (_isCrossFading && SpringAnimator.IsReducedMotion)
            {
                CompleteCrossFadeImmediately();
            }
            StopTicker();
        }
    }

    private void StopTicker()
    {
        if (_isTickerRegistered)
        {
            _isTickerRegistered = false;
            SpringAnimator.UnregisterFrameTicker(this);
        }
    }

    /// <summary>
    /// Zero-allocation per-frame animation tick invoked by SpringAnimator's deduplicated rendering clock.
    /// Animates slow Lissajous blob drift via RenderTransform only and optional 650 ms A/B cross-fade.
    /// </summary>
    public bool StepFrame(double dtSeconds)
    {
        if (!NeedsTicker)
        {
            _isTickerRegistered = false;
            return false;
        }

        bool driftActive = ShouldDriftAnimate;

        // 1. Track cross-fade (650 ms smoothstep cubic ease)
        if (_isCrossFading)
        {
            _crossFadeElapsed += dtSeconds;
            double t = Math.Clamp(_crossFadeElapsed / CrossFadeDurationSeconds, 0.0, 1.0);
            double eased = t * t * (3.0 - (2.0 * t));

            if (_showingLayerA)
            {
                LayerA.Opacity = eased;
                LayerB.Opacity = 1.0 - eased;
            }
            else
            {
                LayerB.Opacity = eased;
                LayerA.Opacity = 1.0 - eased;
            }

            if (t >= 1.0)
            {
                CompleteCrossFadeImmediately();
            }
        }

        // 2. Slow organic RenderTransform-only blob drift (24-38 s prime periods)
        if (driftActive)
        {
            _driftTimeSeconds += dtSeconds;
            ApplyDriftTransforms(_driftTimeSeconds);

            if (!SpringAnimator.UseExternalClockForTest)
            {
                RecordPerformanceSample(dtSeconds);
            }
        }

        bool keepRunning = NeedsTicker;
        if (!keepRunning)
        {
            _isTickerRegistered = false;
        }
        return keepRunning;
    }

    private void ApplyDriftTransforms(double t)
    {
        if (_layoutWidth <= 1.0 || _layoutHeight <= 1.0) return;
        if (!double.IsNaN(_lastAppliedDriftTime) && Math.Abs(t - _lastAppliedDriftTime) < 1e-6) return;
        _lastAppliedDriftTime = t;

        double ampX = _layoutWidth * (CompactMode ? 0.08 : 0.14);
        double ampY = _layoutHeight * (CompactMode ? 0.08 : 0.12);

        double b1x = Math.Sin(t * (TwoPi / Blob1PeriodX)) * ampX;
        double b1y = Math.Cos(t * (TwoPi / Blob1PeriodY)) * ampY;
        double b1s = 1.02 + (0.05 * Math.Sin(t * (TwoPi / Blob1ScalePeriod)));
        double b1r = Math.Sin(t * (TwoPi / Blob1RotPeriod)) * 12.0;

        double b2x = Math.Cos(t * (TwoPi / Blob2PeriodX) + 1.1) * ampX;
        double b2y = Math.Sin(t * (TwoPi / Blob2PeriodY) + 0.6) * ampY;
        double b2s = 1.01 + (0.05 * Math.Cos(t * (TwoPi / Blob2ScalePeriod)));
        double b2r = Math.Cos(t * (TwoPi / Blob2RotPeriod)) * 14.0;

        double b3x = 0.0, b3y = 0.0, b3s = 1.0, b3r = 0.0;
        if (!CompactMode)
        {
            b3x = Math.Sin(t * (TwoPi / Blob3PeriodX) + 2.3) * ampX;
            b3y = Math.Cos(t * (TwoPi / Blob3PeriodY) + 1.7) * ampY;
            b3s = 1.015 + (0.06 * Math.Sin(t * (TwoPi / Blob3ScalePeriod) + 0.9));
            b3r = Math.Sin(t * (TwoPi / Blob3RotPeriod) + 0.4) * 10.0;
        }

        if (LayerA.Opacity > 0.001)
        {
            BlobA1Translate.X = b1x;
            BlobA1Translate.Y = b1y;
            BlobA1Scale.ScaleX = b1s;
            BlobA1Scale.ScaleY = b1s;
            BlobA1Rotate.Angle = b1r;

            BlobA2Translate.X = b2x;
            BlobA2Translate.Y = b2y;
            BlobA2Scale.ScaleX = b2s;
            BlobA2Scale.ScaleY = b2s;
            BlobA2Rotate.Angle = b2r;

            if (!CompactMode)
            {
                BlobA3Translate.X = b3x;
                BlobA3Translate.Y = b3y;
                BlobA3Scale.ScaleX = b3s;
                BlobA3Scale.ScaleY = b3s;
                BlobA3Rotate.Angle = b3r;
            }
        }

        if (LayerB.Opacity > 0.001)
        {
            BlobB1Translate.X = b1x;
            BlobB1Translate.Y = b1y;
            BlobB1Scale.ScaleX = b1s;
            BlobB1Scale.ScaleY = b1s;
            BlobB1Rotate.Angle = b1r;

            BlobB2Translate.X = b2x;
            BlobB2Translate.Y = b2y;
            BlobB2Scale.ScaleX = b2s;
            BlobB2Scale.ScaleY = b2s;
            BlobB2Rotate.Angle = b2r;

            if (!CompactMode)
            {
                BlobB3Translate.X = b3x;
                BlobB3Translate.Y = b3y;
                BlobB3Scale.ScaleX = b3s;
                BlobB3Scale.ScaleY = b3s;
                BlobB3Rotate.Angle = b3r;
            }
        }
    }

    private void RecordPerformanceSample(double dtSeconds)
    {
        double dtMs = dtSeconds * 1000.0;
        if (_perfSampleCount < PerfWindowCapacity)
        {
            _perfFrameDtMs[_perfSampleCount++] = dtMs;
        }
        _perfWindowElapsed += dtSeconds;

        if (_perfWindowElapsed < PerfWindowSeconds) return;

        if (_perfSampleCount >= 10)
        {
            int n = _perfSampleCount;
            Array.Copy(_perfFrameDtMs, _perfSortScratch, n);
            Array.Sort(_perfSortScratch, 0, n);

            int p95Idx = Math.Clamp((int)Math.Ceiling(0.95 * n) - 1, 0, n - 1);
            double p95Ms = _perfSortScratch[p95Idx];

            if (p95Ms > 1.5 * _vsyncIntervalMs)
            {
                _consecutiveSlowWindows++;
                if (_consecutiveSlowWindows >= 2)
                {
                    _autoFallbackFrozen = true;
                }
            }
            else
            {
                _consecutiveSlowWindows = 0;
            }
        }

        _perfSampleCount = 0;
        _perfWindowElapsed = 0.0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevModeW
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
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevModeW devMode);

    private static double QueryDisplayVsyncMs()
    {
        try
        {
            var dm = new DevModeW { dmSize = (ushort)Marshal.SizeOf<DevModeW>() };
            if (EnumDisplaySettingsW(null, -1, ref dm) && dm.dmDisplayFrequency >= 30 && dm.dmDisplayFrequency <= 500)
            {
                return 1000.0 / dm.dmDisplayFrequency;
            }
        }
        catch { }
        return 16.6667;
    }
}
