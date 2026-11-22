using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Controls;

/// <summary>
/// Zero-layout, pooled, GPU-composited synced lyrics viewport driven by SpringAnimator's
/// deduplicated CompositionTarget.Rendering clock.
///
/// Architectural guarantees:
/// - Exact row heights measured once per track/width via WPF TextBlock.Measure (real font/CJK/wrap).
/// - Fixed font size and Bold weight across all states; emphasis strictly via Opacity + ScaleTransform(0, 0.5).
/// - Viewport scroll driven strictly via TranslateTransform.Y (zero ScrollToVerticalOffset, zero steady-state LayoutUpdated).
/// - Zero per-frame managed heap allocations in StepFrame.
/// - Display-clock interpolated 3-dot progress interlude for mid-track and intro gaps (>= 5.0 s).
/// - Full keyboard accessibility (Tab, Up, Down, Home, End, Enter, Space) with monochrome FocusVisualStyle.
/// </summary>
public sealed class SyncedLyricsViewport : Grid, SpringAnimator.IFrameTicker
{
    public const double FullScreenFontSize = 28.0;
    public const double FullScreenLineHeight = 37.0;
    public const double CompactFontSize = 19.0;
    public const double CompactLineHeight = 26.0;

    public const double FullScreenAnchorRatio = 0.32;
    public const double CompactAnchorRatio = 0.30;

    public const double OpacityActive = 1.00;
    public const double OpacityAdjacent = 0.56;
    public const double OpacityNear = 0.46;
    public const double OpacityFar = 0.40;
    public const double OpacityHover = 0.82;
    public const double OpacityDotUnfilled = 0.42;

    public const double ScaleActive = 1.00;
    public const double ScaleAdjacent = 0.93;
    public const double ScaleNear = 0.90;
    public const double ScaleFar = 0.88;
    public const double ScaleHover = 0.97;
    public const double ScaleInterludeInactive = 0.85;

    private const double ScrollResponseAuto = 0.42;
    private const double ScrollResponseWheel = 0.18;
    private const double EmphasisResponse = 0.26;
    private const double ScrollTolerancePx = 0.15;
    private const double EmphasisTolerance = 0.002;
    private const double ManualScrollPauseSeconds = 4.0;
    private const double MaxSubStepSeconds = 1.0 / 180.0;

    private static readonly Thickness FullScreenRowPadding = new(12.0, 10.0, 12.0, 10.0);
    private static readonly Thickness CompactRowPadding = new(10.0, 7.0, 10.0, 7.0);
    private const double FullScreenInterludeHeight = 40.0;
    private const double CompactInterludeHeight = 30.0;

    private sealed class RowSlot
    {
        public required Grid Container;
        public required ScaleTransform Scale;
        public required TranslateTransform Translate;
        public required Button Button;
        public required Border PillBorder;
        public required TextBlock TextBlock;
        public required StackPanel InterludePanel;
        public required Ellipse Dot1;
        public required Ellipse Dot2;
        public required Ellipse Dot3;
        public required ScaleTransform Dot1Scale;
        public required ScaleTransform Dot2Scale;
        public required ScaleTransform Dot3Scale;

        public int LogicalIndex = -1;
        public bool IsGapBound;
        public bool IsHovered;
        public bool IsFocused;

        public double CurrentOpacity;
        public double TargetOpacity;
        public double OpacityVelocity;
        public double CurrentScale = 1.0;
        public double TargetScale = 1.0;
        public double ScaleVelocity;
        public double DelayRemaining;

        public double AppliedY = double.NaN;
        public double AppliedOpacity = double.NaN;
        public double AppliedScale = double.NaN;
        public double AppliedDot1Op = double.NaN;
        public double AppliedDot2Op = double.NaN;
        public double AppliedDot3Op = double.NaN;
        public double AppliedDot1Scale = double.NaN;
        public double AppliedDot2Scale = double.NaN;
        public double AppliedDot3Scale = double.NaN;
        public bool AppliedPillHighlighted;
        public bool AppliedOnScreen;
    }

    private readonly Canvas _canvas;
    private readonly TranslateTransform _canvasTranslate;
    private readonly List<RowSlot> _pool = new(36);
    private readonly DispatcherTimer _manualScrollResumeTimer;
    private readonly TextBlock _measureTextBlock;

    private LyricsViewModel? _lyrics;
    private bool _isFullScreenMode;
    private bool _hasIntroGap;
    private int _logicalCount;
    private int _activeLogicalIndex = -1;
    private double[] _rowY = Array.Empty<double>();
    private double[] _rowHeight = Array.Empty<double>();
    private int[] _logicalToSlot = Array.Empty<int>();
    private double _totalExtent;
    private double _measuredWidth;
    private double _viewportHeight;
    private int _measuredLinesVersion = -1;

    private double _scrollY;
    private double _appliedCanvasScrollY = double.NaN;
    private double _targetScrollY;
    private double _scrollVelocity;
    private double _activeScrollResponse = ScrollResponseAuto;
    private bool _isScrollAnimating;
    private bool _isTickerRegistered;
    private DateTime _manualScrollPauseUntilUtc = DateTime.MinValue;
    private double _interludeBreathPhase;
    private double _externalTestPlaybackOffsetSeconds;
    private double _lastActiveInterludeProgress;
    private (double D1, double D2, double D3) _lastActiveInterludeDotOpacities;

    private FontFamily _displayFontFamily = new("Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI");
    private Brush _lyricsInkBrush = Brushes.Transparent;
    private Brush _hoverPillBrush = Brushes.Transparent;
    private Style? _focusVisualStyle;
    private CornerRadius _pillCornerRadius = new(8.0);
    private bool _resourcesResolved;
    private bool _isReducedMotionSubscribed;

    public SyncedLyricsViewport()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;
        UseLayoutRounding = false;

        _canvasTranslate = new TranslateTransform(0.0, 0.0);
        _canvas = new Canvas
        {
            ClipToBounds = false,
            Background = Brushes.Transparent,
            UseLayoutRounding = false,
            RenderTransform = _canvasTranslate
        };
        Children.Add(_canvas);

        _measureTextBlock = CreateConfiguredMeasureTextBlock(_displayFontFamily, CompactFontSize, CompactLineHeight);

        _manualScrollResumeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(ManualScrollPauseSeconds)
        };
        _manualScrollResumeTimer.Tick += OnManualScrollResumeTick;

        SizeChanged += OnViewportSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        PreviewMouseWheel += OnPreviewMouseWheel;
    }

    public bool IsFullScreenMode
    {
        get => _isFullScreenMode;
        set
        {
            if (_isFullScreenMode == value) return;
            _isFullScreenMode = value;
            ApplyTypographyTokensToPool();
            RebuildLayoutMetrics(preserveScroll: false, snapImmediate: true);
        }
    }

    internal double CurrentScrollOffset => _scrollY;
    internal double TargetScrollOffset => _targetScrollY;
    internal int LogicalRowCount => _logicalCount;
    internal int ActiveLogicalIndex => _activeLogicalIndex;
    internal bool HasIntroGapRow => _hasIntroGap;
    internal double TotalExtent => _totalExtent;
    internal double LastActiveInterludeProgressForTest => _lastActiveInterludeProgress;
    internal (double D1, double D2, double D3) LastActiveInterludeDotOpacitiesForTest => _lastActiveInterludeDotOpacities;

    internal int RealizedRowCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i].LogicalIndex >= 0) count++;
            }
            return count;
        }
    }

    internal double GetRowYForTest(int logicalIndex) =>
        logicalIndex >= 0 && logicalIndex < _logicalCount ? _rowY[logicalIndex] : double.NaN;

    internal double GetRowHeightForTest(int logicalIndex) =>
        logicalIndex >= 0 && logicalIndex < _logicalCount ? _rowHeight[logicalIndex] : double.NaN;

    internal Button? GetButtonForLogicalIndexForTest(int logicalIndex)
    {
        if (logicalIndex < 0 || logicalIndex >= _logicalCount) return null;
        int slotIdx = _logicalToSlot[logicalIndex];
        return slotIdx >= 0 ? _pool[slotIdx].Button : null;
    }

    internal TextBlock? GetTextBlockForLogicalIndexForTest(int logicalIndex)
    {
        if (logicalIndex < 0 || logicalIndex >= _logicalCount) return null;
        int slotIdx = _logicalToSlot[logicalIndex];
        return slotIdx >= 0 ? _pool[slotIdx].TextBlock : null;
    }

    internal (double Opacity, double Scale, double TranslateY) GetVisualStateForLogicalIndexForTest(int logicalIndex)
    {
        if (logicalIndex < 0 || logicalIndex >= _logicalCount) return (double.NaN, double.NaN, double.NaN);
        int slotIdx = _logicalToSlot[logicalIndex];
        if (slotIdx < 0) return (double.NaN, double.NaN, double.NaN);
        var slot = _pool[slotIdx];
        return (slot.Container.Opacity, slot.Scale.ScaleX, slot.Translate.Y + _canvasTranslate.Y);
    }

    /// <summary>
    /// Measures a lyric row height using the exact WPF TextBlock layout engine configuration
    /// used by rendered rows (same FontFamily, Bold weight, FontSize, LineHeight, MaxHeight stacking,
    /// ClearType/Ideal text options, TextWrapping.Wrap, and horizontal padding).
    /// </summary>
    internal static double MeasureLyricRowHeight(
        string text,
        FontFamily fontFamily,
        double fontSize,
        double lineHeight,
        double rowWidth,
        Thickness rowPadding)
    {
        var tb = CreateConfiguredMeasureTextBlock(fontFamily, fontSize, lineHeight);
        return MeasureLyricRowHeightCore(tb, text, lineHeight, rowWidth, rowPadding);
    }

    private static TextBlock CreateConfiguredMeasureTextBlock(FontFamily fontFamily, double fontSize, double lineHeight)
    {
        var tb = new TextBlock
        {
            FontFamily = fontFamily,
            FontWeight = FontWeights.Bold,
            FontSize = fontSize,
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            UseLayoutRounding = false
        };
        TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(tb, TextRenderingMode.ClearType);
        return tb;
    }

    private static double MeasureLyricRowHeightCore(
        TextBlock measureBlock,
        string text,
        double lineHeight,
        double rowWidth,
        Thickness rowPadding)
    {
        double wrapWidth = Math.Max(40.0, rowWidth - rowPadding.Left - rowPadding.Right);
        measureBlock.Text = string.IsNullOrEmpty(text) ? " " : text;
        measureBlock.Measure(new Size(wrapWidth, double.PositiveInfinity));
        double textH = Math.Max(lineHeight, measureBlock.DesiredSize.Height);
        return textH + rowPadding.Top + rowPadding.Bottom;
    }

    public void AttachLyrics(LyricsViewModel? lyrics)
    {
        if (ReferenceEquals(_lyrics, lyrics)) return;

        if (_lyrics != null)
        {
            _lyrics.LinesReloaded -= OnLinesReloaded;
            _lyrics.PlaybackClockUpdated -= OnPlaybackClockUpdated;
            _lyrics.PropertyChanged -= OnLyricsPropertyChanged;
        }

        _lyrics = lyrics;

        if (_lyrics != null)
        {
            _lyrics.LinesReloaded += OnLinesReloaded;
            _lyrics.PlaybackClockUpdated += OnPlaybackClockUpdated;
            _lyrics.PropertyChanged += OnLyricsPropertyChanged;
        }

        _manualScrollPauseUntilUtc = DateTime.MinValue;
        _manualScrollResumeTimer.Stop();
        _externalTestPlaybackOffsetSeconds = 0.0;
        RebuildLayoutMetrics(preserveScroll: false, snapImmediate: true);
    }

    public void DetachLyrics()
    {
        AttachLyrics(null);
        StopAnimationLoop();
    }

    public void SyncToActiveLine(bool immediate)
    {
        if (_lyrics == null || !_lyrics.IsSynced || _logicalCount == 0)
        {
            StopAnimationLoop();
            return;
        }

        UpdateActiveLogicalIndex(immediate: immediate, allowStagger: !immediate);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isReducedMotionSubscribed)
        {
            SpringAnimator.ReducedMotionChanged += OnReducedMotionChanged;
            _isReducedMotionSubscribed = true;
        }
        ResolveResources();
        ApplyTypographyTokensToPool();
        RebuildLayoutMetrics(preserveScroll: false, snapImmediate: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isReducedMotionSubscribed)
        {
            SpringAnimator.ReducedMotionChanged -= OnReducedMotionChanged;
            _isReducedMotionSubscribed = false;
        }
        _manualScrollResumeTimer.Stop();
        StopAnimationLoop();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ResolveResources();
            if (_measuredLinesVersion != (_lyrics?.LinesVersion ?? -1) ||
                (ActualWidth > 1.0 && Math.Abs(ActualWidth - _measuredWidth) >= 0.5))
            {
                RebuildLayoutMetrics(preserveScroll: false, snapImmediate: true);
            }
            else
            {
                SyncToActiveLine(immediate: true);
            }
        }
        else
        {
            _manualScrollResumeTimer.Stop();
            StopAnimationLoop();
        }
    }

    private void OnReducedMotionChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnReducedMotionChanged);
            return;
        }

        if (SpringAnimator.IsReducedMotion)
        {
            SnapAllToTargets();
        }
        else
        {
            EnsureTickerIfNeeded();
        }
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double newW = e.NewSize.Width;
        double newH = e.NewSize.Height;
        bool widthChanged = Math.Abs(newW - _measuredWidth) >= 0.5;
        bool heightChanged = Math.Abs(newH - _viewportHeight) >= 0.5;
        _viewportHeight = newH;

        if (widthChanged && newW > 1.0)
        {
            RebuildLayoutMetrics(preserveScroll: false, snapImmediate: true);
        }
        else if (heightChanged && newH > 1.0 && _logicalCount > 0)
        {
            SyncToActiveLine(immediate: true);
        }
    }

    private void OnLinesReloaded()
    {
        _manualScrollPauseUntilUtc = DateTime.MinValue;
        _manualScrollResumeTimer.Stop();
        _externalTestPlaybackOffsetSeconds = 0.0;
        RebuildLayoutMetrics(preserveScroll: false, snapImmediate: true);
    }

    private void OnLyricsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.ActiveLineIndex))
        {
            if (_lyrics == null || !_lyrics.IsSynced || _lyrics.Lines.Count == 0 || _measuredLinesVersion != _lyrics.LinesVersion)
            {
                return;
            }
            UpdateActiveLogicalIndex(immediate: false, allowStagger: true);
        }
        else if (e.PropertyName == nameof(LyricsViewModel.State))
        {
            if (_lyrics?.IsTimeBasedLoaded == true)
            {
                RebuildLayoutMetrics(preserveScroll: false, snapImmediate: true);
            }
            else
            {
                StopAnimationLoop();
            }
        }
    }

    private void OnPlaybackClockUpdated()
    {
        _externalTestPlaybackOffsetSeconds = 0.0;
        if (!IsVisible || _lyrics == null || !_lyrics.IsSynced || _logicalCount == 0) return;

        // Re-evaluate active logical index in case intro gap just started/ended
        int computedActive = ComputeActiveLogicalIndex();
        if (computedActive != _activeLogicalIndex)
        {
            UpdateActiveLogicalIndex(immediate: false, allowStagger: true);
        }

        if (IsActiveRowInterlude())
        {
            UpdateActiveInterludeVisuals();
            EnsureTickerIfNeeded();
        }
    }

    private void ResolveResources()
    {
        if (_resourcesResolved) return;

        if (TryFindResource("Font.Display") is FontFamily ff) _displayFontFamily = ff;
        if (TryFindResource("Brush.Lyrics.Current") is Brush ink) _lyricsInkBrush = ink;
        if (TryFindResource("Brush.Overlay.LyricsHoverPill") is Brush pill) _hoverPillBrush = pill;
        if (TryFindResource("Style.FocusVisual.Monochrome") is Style fv) _focusVisualStyle = fv;
        if (TryFindResource("Radius.Card") is CornerRadius cr) _pillCornerRadius = cr;

        _resourcesResolved = true;
    }

    private double CurrentFontSize => _isFullScreenMode ? FullScreenFontSize : CompactFontSize;
    private double CurrentLineHeight => _isFullScreenMode ? FullScreenLineHeight : CompactLineHeight;
    private Thickness CurrentRowPadding => _isFullScreenMode ? FullScreenRowPadding : CompactRowPadding;
    private double CurrentInterludeHeight => _isFullScreenMode ? FullScreenInterludeHeight : CompactInterludeHeight;
    private double CurrentAnchorRatio => _isFullScreenMode ? FullScreenAnchorRatio : CompactAnchorRatio;
    private double CurrentDotSize => _isFullScreenMode ? 10.0 : 8.0;
    private double CurrentDotGap => _isFullScreenMode ? 10.0 : 8.0;

    private int ComputeActiveLogicalIndex()
    {
        if (_lyrics == null || !_lyrics.IsSynced || _logicalCount == 0) return -1;
        int vmIdx = _lyrics.ActiveLineIndex;
        if (_hasIntroGap)
        {
            return vmIdx < 0 ? 0 : Math.Min(vmIdx + 1, _logicalCount - 1);
        }
        return vmIdx;
    }

    private bool IsLogicalRowGap(int logicalIndex)
    {
        if (_lyrics == null || logicalIndex < 0 || logicalIndex >= _logicalCount) return false;
        if (_hasIntroGap)
        {
            if (logicalIndex == 0) return true;
            int srcIdx = logicalIndex - 1;
            return srcIdx >= 0 && srcIdx < _lyrics.Lines.Count && _lyrics.Lines[srcIdx].IsInstrumentalGap;
        }
        return logicalIndex < _lyrics.Lines.Count && _lyrics.Lines[logicalIndex].IsInstrumentalGap;
    }

    private LyricsLineItem? GetSourceLine(int logicalIndex)
    {
        if (_lyrics == null || logicalIndex < 0 || logicalIndex >= _logicalCount) return null;
        int srcIdx = _hasIntroGap ? logicalIndex - 1 : logicalIndex;
        if (srcIdx < 0 || srcIdx >= _lyrics.Lines.Count) return null;
        return _lyrics.Lines[srcIdx];
    }

    private bool IsActiveRowInterlude() =>
        _activeLogicalIndex >= 0 && _activeLogicalIndex < _logicalCount && IsLogicalRowGap(_activeLogicalIndex);

    private void RebuildLayoutMetrics(bool preserveScroll, bool snapImmediate)
    {
        ResolveResources();

        if (_lyrics == null || !_lyrics.IsSynced || _lyrics.Lines.Count == 0)
        {
            _logicalCount = 0;
            _hasIntroGap = false;
            _activeLogicalIndex = -1;
            _totalExtent = 0.0;
            _measuredLinesVersion = _lyrics?.LinesVersion ?? -1;
            ClearAllPoolSlots();
            StopAnimationLoop();
            return;
        }

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 1.0)
        {
            width = _isFullScreenMode ? 520.0 : 312.0;
        }
        if (height <= 1.0)
        {
            height = _isFullScreenMode ? 480.0 : 420.0;
        }

        _measuredWidth = width;
        _viewportHeight = height;
        _measuredLinesVersion = _lyrics.LinesVersion;

        _hasIntroGap = _lyrics.HasIntroGap;
        _logicalCount = _lyrics.Lines.Count + (_hasIntroGap ? 1 : 0);

        if (_rowY.Length < _logicalCount)
        {
            _rowY = new double[_logicalCount];
            _rowHeight = new double[_logicalCount];
            _logicalToSlot = new int[_logicalCount];
        }

        for (int i = 0; i < _logicalCount; i++)
        {
            _logicalToSlot[i] = -1;
        }

        // Configure exact shared TextBlock measurer once per track/width
        double fontSize = CurrentFontSize;
        double lineHeight = CurrentLineHeight;
        Thickness padding = CurrentRowPadding;
        double interludeHeight = CurrentInterludeHeight;

        _measureTextBlock.FontFamily = _displayFontFamily;
        _measureTextBlock.FontWeight = FontWeights.Bold;
        _measureTextBlock.FontSize = fontSize;
        _measureTextBlock.LineHeight = lineHeight;
        _measureTextBlock.LineStackingStrategy = LineStackingStrategy.MaxHeight;

        double y = 0.0;
        for (int li = 0; li < _logicalCount; li++)
        {
            _rowY[li] = y;
            double rowH;
            if (IsLogicalRowGap(li))
            {
                rowH = interludeHeight;
            }
            else
            {
                var line = GetSourceLine(li)!;
                rowH = MeasureLyricRowHeightCore(_measureTextBlock, line.Text, lineHeight, width, padding);
            }
            _rowHeight[li] = rowH;
            y += rowH;
        }
        _totalExtent = y;

        // Ensure fixed bounded pool capacity (always virtualized < 36 rows even for 500-line lyrics)
        double minRowH = Math.Max(26.0, lineHeight + padding.Top + padding.Bottom);
        int desiredPool = Math.Clamp((int)Math.Ceiling(height / minRowH) + 14, 18, 34);
        int targetPool = Math.Min(_logicalCount, desiredPool);
        EnsurePoolSize(targetPool);

        // Reset slot bindings and set exact container width once (zero width changes during scroll)
        for (int s = 0; s < _pool.Count; s++)
        {
            var slot = _pool[s];
            slot.LogicalIndex = -1;
            slot.IsHovered = false;
            slot.IsFocused = false;
            slot.Container.Width = width;
            slot.Button.Width = width;
            slot.AppliedY = double.NaN;
            slot.AppliedOpacity = double.NaN;
            slot.AppliedScale = double.NaN;
            if (s >= targetPool)
            {
                slot.Container.Visibility = Visibility.Collapsed;
                slot.Button.IsTabStop = false;
            }
        }

        _activeLogicalIndex = ComputeActiveLogicalIndex();

        if (!preserveScroll)
        {
            double anchorTarget = ComputeAnchorScrollForIndex(_activeLogicalIndex >= 0 ? _activeLogicalIndex : 0);
            _scrollY = anchorTarget;
            _targetScrollY = anchorTarget;
            _scrollVelocity = 0.0;
            _isScrollAnimating = false;
        }

        PopulatePoolAroundScroll(snapSlotEmphasis: true);
        ApplyAllSlotTransforms(updateTabStops: true);
        if (IsActiveRowInterlude())
        {
            UpdateActiveInterludeVisuals();
        }

        if (snapImmediate)
        {
            SnapAllToTargets();
        }
        else
        {
            EnsureTickerIfNeeded();
        }
    }

    private void ClearAllPoolSlots()
    {
        for (int i = 0; i < _pool.Count; i++)
        {
            var slot = _pool[i];
            slot.LogicalIndex = -1;
            slot.Container.Visibility = Visibility.Collapsed;
            slot.Button.IsTabStop = false;
        }
    }

    private void EnsurePoolSize(int count)
    {
        while (_pool.Count < count)
        {
            var slot = CreateRowSlot();
            _pool.Add(slot);
            _canvas.Children.Add(slot.Container);
        }
    }

    private RowSlot CreateRowSlot()
    {
        var scale = new ScaleTransform(1.0, 1.0);
        var translate = new TranslateTransform(0.0, 0.0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(scale);
        transformGroup.Children.Add(translate);

        var textBlock = new TextBlock
        {
            FontFamily = _displayFontFamily,
            FontWeight = FontWeights.Bold,
            FontSize = CurrentFontSize,
            LineHeight = CurrentLineHeight,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
            Foreground = _lyricsInkBrush,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            UseLayoutRounding = false
        };
        TextOptions.SetTextFormattingMode(textBlock, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(textBlock, TextRenderingMode.ClearType);

        var pillBorder = new Border
        {
            Padding = CurrentRowPadding,
            CornerRadius = _pillCornerRadius,
            Background = Brushes.Transparent,
            UseLayoutRounding = false,
            Child = textBlock
        };

        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = true,
            IsTabStop = true,
            FocusVisualStyle = _focusVisualStyle,
            UseLayoutRounding = false,
            Template = CreatePlainButtonTemplate(),
            Content = pillBorder
        };

        double dotSize = CurrentDotSize;
        double dotGap = CurrentDotGap;
        var d1Scale = new ScaleTransform(1.0, 1.0);
        var d2Scale = new ScaleTransform(1.0, 1.0);
        var d3Scale = new ScaleTransform(1.0, 1.0);

        var dot1 = CreateInterludeDot(dotSize, dotGap, d1Scale);
        var dot2 = CreateInterludeDot(dotSize, dotGap, d2Scale);
        var dot3 = CreateInterludeDot(dotSize, 0.0, d3Scale);

        var interludePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(CurrentRowPadding.Left, 0, 0, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        interludePanel.Children.Add(dot1);
        interludePanel.Children.Add(dot2);
        interludePanel.Children.Add(dot3);

        var container = new Grid
        {
            Background = Brushes.Transparent,
            UseLayoutRounding = false,
            RenderTransformOrigin = new Point(0.0, 0.5),
            RenderTransform = transformGroup,
            Visibility = Visibility.Collapsed
        };
        Canvas.SetLeft(container, 0.0);
        Canvas.SetTop(container, 0.0);
        container.Children.Add(button);
        container.Children.Add(interludePanel);

        var slot = new RowSlot
        {
            Container = container,
            Scale = scale,
            Translate = translate,
            Button = button,
            PillBorder = pillBorder,
            TextBlock = textBlock,
            InterludePanel = interludePanel,
            Dot1 = dot1,
            Dot2 = dot2,
            Dot3 = dot3,
            Dot1Scale = d1Scale,
            Dot2Scale = d2Scale,
            Dot3Scale = d3Scale
        };

        button.Click += (_, _) => OnSlotClicked(slot);
        button.MouseEnter += (_, _) => OnSlotHoverChanged(slot, true);
        button.MouseLeave += (_, _) => OnSlotHoverChanged(slot, false);
        button.GotKeyboardFocus += (_, _) => OnSlotFocusChanged(slot, true);
        button.LostKeyboardFocus += (_, _) => OnSlotFocusChanged(slot, false);
        button.PreviewKeyDown += (_, e) => OnSlotPreviewKeyDown(slot, e);

        return slot;
    }

    private Ellipse CreateInterludeDot(double size, double rightMargin, ScaleTransform scaleTransform)
    {
        return new Ellipse
        {
            Width = size,
            Height = size,
            Margin = new Thickness(0, 0, rightMargin, 0),
            Fill = _lyricsInkBrush,
            Opacity = OpacityDotUnfilled,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scaleTransform,
            UseLayoutRounding = false
        };
    }

    private static ControlTemplate CreatePlainButtonTemplate()
    {
        var presenterFactory = new FrameworkElementFactory(typeof(ContentPresenter));
        presenterFactory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenterFactory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

        var template = new ControlTemplate(typeof(Button))
        {
            VisualTree = presenterFactory
        };
        template.Seal();
        return template;
    }

    private void ApplyTypographyTokensToPool()
    {
        ResolveResources();
        double fontSize = CurrentFontSize;
        double lineHeight = CurrentLineHeight;
        Thickness padding = CurrentRowPadding;
        double dotSize = CurrentDotSize;
        double dotGap = CurrentDotGap;

        for (int i = 0; i < _pool.Count; i++)
        {
            var slot = _pool[i];
            slot.TextBlock.FontFamily = _displayFontFamily;
            slot.TextBlock.FontSize = fontSize;
            slot.TextBlock.LineHeight = lineHeight;
            slot.TextBlock.Foreground = _lyricsInkBrush;
            slot.PillBorder.Padding = padding;
            slot.PillBorder.CornerRadius = _pillCornerRadius;
            slot.Button.FocusVisualStyle = _focusVisualStyle;

            slot.InterludePanel.Margin = new Thickness(padding.Left, 0, 0, 0);
            slot.Dot1.Width = dotSize;
            slot.Dot1.Height = dotSize;
            slot.Dot1.Margin = new Thickness(0, 0, dotGap, 0);
            slot.Dot1.Fill = _lyricsInkBrush;

            slot.Dot2.Width = dotSize;
            slot.Dot2.Height = dotSize;
            slot.Dot2.Margin = new Thickness(0, 0, dotGap, 0);
            slot.Dot2.Fill = _lyricsInkBrush;

            slot.Dot3.Width = dotSize;
            slot.Dot3.Height = dotSize;
            slot.Dot3.Fill = _lyricsInkBrush;
        }
    }

    private double ComputeAnchorScrollForIndex(int logicalIndex)
    {
        if (_logicalCount == 0) return 0.0;
        int clampedIdx = Math.Clamp(logicalIndex, 0, _logicalCount - 1);
        double anchorY = _viewportHeight * CurrentAnchorRatio;
        double desired = _rowY[clampedIdx] - anchorY;
        return ClampScrollOffset(desired);
    }

    private double ClampScrollOffset(double scrollY)
    {
        double anchorY = _viewportHeight * CurrentAnchorRatio;
        double minY = -anchorY;
        double maxY = Math.Max(minY, _totalExtent - (_viewportHeight * 0.58));
        return Math.Clamp(scrollY, minY, maxY);
    }

    private void UpdateActiveLogicalIndex(bool immediate, bool allowStagger)
    {
        int prevActive = _activeLogicalIndex;
        int nextActive = ComputeActiveLogicalIndex();
        _activeLogicalIndex = nextActive;

        bool isForwardStep = allowStagger && prevActive >= 0 && nextActive == prevActive + 1 && !SpringAnimator.IsReducedMotion;
        bool manualPaused = !immediate && DateTime.UtcNow < _manualScrollPauseUntilUtc;

        if (!manualPaused)
        {
            double desired = ComputeAnchorScrollForIndex(nextActive >= 0 ? nextActive : 0);
            _targetScrollY = desired;
            _activeScrollResponse = ScrollResponseAuto;

            if (immediate || SpringAnimator.IsReducedMotion)
            {
                _scrollY = desired;
                _scrollVelocity = 0.0;
                _isScrollAnimating = false;
            }
            else if (Math.Abs(_targetScrollY - _scrollY) > ScrollTolerancePx)
            {
                _isScrollAnimating = true;
            }
        }

        // Update emphasis targets for all currently bound slots
        for (int s = 0; s < _pool.Count; s++)
        {
            var slot = _pool[s];
            if (slot.LogicalIndex < 0) continue;

            ComputeEmphasisTargets(slot, out double targetOp, out double targetScale, out bool highlightPill);
            bool changed = Math.Abs(slot.TargetOpacity - targetOp) > 1e-4 || Math.Abs(slot.TargetScale - targetScale) > 1e-4;
            slot.TargetOpacity = targetOp;
            slot.TargetScale = targetScale;
            ApplyPillHighlight(slot, highlightPill);

            if (immediate || SpringAnimator.IsReducedMotion)
            {
                slot.CurrentOpacity = targetOp;
                slot.OpacityVelocity = 0.0;
                slot.CurrentScale = targetScale;
                slot.ScaleVelocity = 0.0;
                slot.DelayRemaining = 0.0;
            }
            else if (changed && isForwardStep)
            {
                int dist = Math.Abs(slot.LogicalIndex - nextActive);
                slot.DelayRemaining = Math.Min(4, dist) * 0.018;
            }
            else if (changed)
            {
                slot.DelayRemaining = 0.0;
            }
        }

        EnsureVisibleSlotsBound(snapNewlyBound: immediate || SpringAnimator.IsReducedMotion);
        ApplyAllSlotTransforms(updateTabStops: true);

        if (IsActiveRowInterlude())
        {
            UpdateActiveInterludeVisuals();
        }

        EnsureTickerIfNeeded();
    }

    private void ComputeEmphasisTargets(RowSlot slot, out double opacity, out double scale, out bool highlightPill)
    {
        int li = slot.LogicalIndex;
        if (IsLogicalRowGap(li))
        {
            highlightPill = false;
            if (li == _activeLogicalIndex)
            {
                opacity = OpacityActive;
                scale = ScaleActive;
            }
            else
            {
                opacity = 0.0;
                scale = ScaleInterludeInactive;
            }
            return;
        }

        if (li == _activeLogicalIndex)
        {
            opacity = OpacityActive;
            scale = ScaleActive;
            highlightPill = false;
            return;
        }

        if (slot.IsHovered || slot.IsFocused)
        {
            opacity = OpacityHover;
            scale = ScaleHover;
            highlightPill = true;
            return;
        }

        highlightPill = false;
        int distance = _activeLogicalIndex >= 0
            ? Math.Abs(li - _activeLogicalIndex)
            : li + 1;

        if (distance <= 1)
        {
            opacity = OpacityAdjacent;
            scale = ScaleAdjacent;
        }
        else if (distance == 2)
        {
            opacity = OpacityNear;
            scale = ScaleNear;
        }
        else
        {
            opacity = OpacityFar;
            scale = ScaleFar;
        }
    }

    private void ApplyPillHighlight(RowSlot slot, bool highlight)
    {
        if (slot.AppliedPillHighlighted == highlight) return;
        slot.AppliedPillHighlighted = highlight;
        slot.PillBorder.Background = highlight ? _hoverPillBrush : Brushes.Transparent;
    }

    private int FindFirstRowAtOrAfter(double docY)
    {
        int lo = 0;
        int hi = _logicalCount - 1;
        int ans = _logicalCount;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_rowY[mid] + _rowHeight[mid] >= docY)
            {
                ans = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }
        return ans;
    }

    /// <summary>
    /// Initial population of the fixed pool around the current scroll window, biased downward
    /// so upcoming lines are already realized and steady-state forward scrolling triggers zero layout passes.
    /// </summary>
    private void PopulatePoolAroundScroll(bool snapSlotEmphasis)
    {
        if (_logicalCount == 0 || _pool.Count == 0) return;

        int activeCount = Math.Min(_logicalCount, _pool.Count);
        int firstVis = FindFirstRowAtOrAfter(_scrollY);
        if (firstVis >= _logicalCount) firstVis = _logicalCount - 1;
        int start = Math.Clamp(firstVis - 4, 0, Math.Max(0, _logicalCount - activeCount));

        for (int s = 0; s < activeCount; s++)
        {
            BindSlotToLogicalIndex(s, start + s, snapSlotEmphasis);
        }
    }

    /// <summary>
    /// Hysteresis-guarded recycling: ONLY recycles a farthest-away offscreen unfocused slot when
    /// a line entering the near-viewport band [scrollY - 120, scrollY + viewportH + 180] is unbound.
    /// Zero allocations and zero unnecessary layout churn during steady-state scrolling.
    /// </summary>
    private void EnsureVisibleSlotsBound(bool snapNewlyBound)
    {
        if (_logicalCount == 0 || _pool.Count == 0) return;

        double needTop = _scrollY - 120.0;
        double needBottom = _scrollY + _viewportHeight + 180.0;

        int firstNeed = FindFirstRowAtOrAfter(needTop);
        if (firstNeed >= _logicalCount) return;

        double viewportMid = _scrollY + (_viewportHeight * 0.5);

        for (int li = firstNeed; li < _logicalCount && _rowY[li] <= needBottom; li++)
        {
            if (_logicalToSlot[li] >= 0) continue;

            // Find the best slot to recycle: prefer an unused slot, else farthest offscreen unfocused slot
            int bestSlot = -1;
            double bestScore = -1.0;
            bool needGap = IsLogicalRowGap(li);

            for (int s = 0; s < _pool.Count; s++)
            {
                var candidate = _pool[s];
                if (candidate.LogicalIndex < 0)
                {
                    bestSlot = s;
                    break;
                }
                if (candidate.IsFocused) continue;

                int cLi = candidate.LogicalIndex;
                double cTop = _rowY[cLi];
                double cBot = cTop + _rowHeight[cLi];
                if (cBot >= needTop && cTop <= needBottom) continue;

                double dist = Math.Abs((cTop + cBot) * 0.5 - viewportMid);
                if (candidate.IsGapBound == needGap) dist += 10.0; // slight preference for same kind
                if (dist > bestScore)
                {
                    bestScore = dist;
                    bestSlot = s;
                }
            }

            if (bestSlot >= 0)
            {
                BindSlotToLogicalIndex(bestSlot, li, snapNewlyBound);
            }
        }
    }

    private void BindSlotToLogicalIndex(int slotIndex, int logicalIndex, bool snapEmphasis)
    {
        var slot = _pool[slotIndex];
        if (slot.LogicalIndex >= 0 && slot.LogicalIndex < _logicalCount && _logicalToSlot[slot.LogicalIndex] == slotIndex)
        {
            _logicalToSlot[slot.LogicalIndex] = -1;
        }

        slot.LogicalIndex = logicalIndex;
        slot.IsHovered = false;
        _logicalToSlot[logicalIndex] = slotIndex;

        bool isGap = IsLogicalRowGap(logicalIndex);
        slot.IsGapBound = isGap;

        if (isGap)
        {
            slot.Button.Visibility = Visibility.Collapsed;
            slot.Button.IsTabStop = false;
            slot.Button.Focusable = false;
            slot.InterludePanel.Visibility = Visibility.Visible;
            slot.Container.Height = _rowHeight[logicalIndex];
            slot.AppliedDot1Op = double.NaN;
            slot.AppliedDot2Op = double.NaN;
            slot.AppliedDot3Op = double.NaN;
        }
        else
        {
            var line = GetSourceLine(logicalIndex)!;
            slot.InterludePanel.Visibility = Visibility.Collapsed;
            slot.Button.Visibility = Visibility.Visible;
            slot.Button.Focusable = true;
            slot.Container.Height = double.NaN;
            string displayText = string.IsNullOrEmpty(line.Text) ? " " : line.Text;
            if (!string.Equals(slot.TextBlock.Text, displayText, StringComparison.Ordinal))
            {
                slot.TextBlock.Text = displayText;
            }
            slot.Button.ToolTip = line.TimestampText;
            AutomationProperties.SetName(slot.Button, line.Text);
            KeyboardNavigation.SetTabIndex(slot.Button, logicalIndex);
        }

        if (slot.Container.Visibility != Visibility.Visible)
        {
            slot.Container.Visibility = Visibility.Visible;
        }

        ComputeEmphasisTargets(slot, out double targetOp, out double targetScale, out bool highlightPill);
        slot.TargetOpacity = targetOp;
        slot.TargetScale = targetScale;
        ApplyPillHighlight(slot, highlightPill);

        if (snapEmphasis || SpringAnimator.IsReducedMotion)
        {
            slot.CurrentOpacity = targetOp;
            slot.OpacityVelocity = 0.0;
            slot.CurrentScale = targetScale;
            slot.ScaleVelocity = 0.0;
            slot.DelayRemaining = 0.0;
        }
        else
        {
            slot.CurrentOpacity = targetOp;
            slot.OpacityVelocity = 0.0;
            slot.CurrentScale = targetScale;
            slot.ScaleVelocity = 0.0;
            slot.DelayRemaining = 0.0;
        }

        slot.AppliedY = double.NaN;
        slot.AppliedOpacity = double.NaN;
        slot.AppliedScale = double.NaN;
    }

    private double ComputeEdgeFade(double viewportY, double rowHeight)
    {
        if (_viewportHeight <= 1.0) return 1.0;

        double topZone = _viewportHeight * (_isFullScreenMode ? 0.13 : 0.10);
        double bottomZone = _viewportHeight * (_isFullScreenMode ? 0.16 : 0.14);

        // Smoothly fade rows out before their glyphs hit the top or bottom viewport clip boundary
        double topClearance = viewportY + (rowHeight * 0.35);
        double bottomClearance = _viewportHeight - (viewportY + (rowHeight * 0.65));

        double fade = 1.0;
        if (topZone > 1.0 && topClearance < topZone)
        {
            double t = Math.Clamp(topClearance / topZone, 0.0, 1.0);
            fade = Math.Min(fade, t * t * (3.0 - (2.0 * t)));
        }
        if (bottomZone > 1.0 && bottomClearance < bottomZone)
        {
            double t = Math.Clamp(bottomClearance / bottomZone, 0.0, 1.0);
            fade = Math.Min(fade, t * t * (3.0 - (2.0 * t)));
        }
        return fade;
    }

    private void ApplyAllSlotTransforms(bool updateTabStops)
    {
        if (double.IsNaN(_appliedCanvasScrollY) || Math.Abs(_scrollY - _appliedCanvasScrollY) > 1e-4)
        {
            _canvasTranslate.Y = -_scrollY;
            _appliedCanvasScrollY = _scrollY;
        }

        for (int s = 0; s < _pool.Count; s++)
        {
            var slot = _pool[s];
            int li = slot.LogicalIndex;
            if (li < 0 || li >= _logicalCount) continue;

            double docY = _rowY[li];
            double rowH = _rowHeight[li];
            double viewportY = docY - _scrollY;

            if (double.IsNaN(slot.AppliedY) || Math.Abs(docY - slot.AppliedY) > 1e-4)
            {
                slot.Translate.Y = docY;
                slot.AppliedY = docY;
            }

            double edgeFade = li == _activeLogicalIndex ? 1.0 : ComputeEdgeFade(viewportY, rowH);
            double effectiveOpacity = slot.CurrentOpacity * edgeFade;
            if (double.IsNaN(slot.AppliedOpacity) || Math.Abs(effectiveOpacity - slot.AppliedOpacity) >= 0.0015 ||
                (slot.CurrentOpacity == slot.TargetOpacity && Math.Abs(slot.AppliedOpacity - effectiveOpacity) > 1e-4))
            {
                slot.Container.Opacity = effectiveOpacity;
                slot.AppliedOpacity = effectiveOpacity;
            }

            if (double.IsNaN(slot.AppliedScale) || Math.Abs(slot.CurrentScale - slot.AppliedScale) >= 0.0005 ||
                (slot.CurrentScale == slot.TargetScale && slot.AppliedScale != slot.CurrentScale))
            {
                slot.Scale.ScaleX = slot.CurrentScale;
                slot.Scale.ScaleY = slot.CurrentScale;
                slot.AppliedScale = slot.CurrentScale;
            }

            if (updateTabStops && !slot.IsGapBound)
            {
                bool onScreen = (viewportY + rowH) > 0.0 && viewportY < _viewportHeight;
                if (slot.AppliedOnScreen != onScreen)
                {
                    slot.AppliedOnScreen = onScreen;
                    slot.Button.IsTabStop = onScreen;
                    slot.Button.IsHitTestVisible = onScreen;
                }
            }
        }
    }

    private void UpdateActiveInterludeVisuals()
    {
        if (_lyrics == null || !IsActiveRowInterlude()) return;
        int slotIdx = _logicalToSlot[_activeLogicalIndex];
        if (slotIdx < 0) return;
        var slot = _pool[slotIdx];

        TimeSpan start;
        TimeSpan duration;
        if (_hasIntroGap && _activeLogicalIndex == 0)
        {
            start = TimeSpan.Zero;
            duration = _lyrics.IntroGapDuration;
        }
        else
        {
            var line = GetSourceLine(_activeLogicalIndex);
            if (line == null) return;
            start = line.Timestamp;
            duration = line.Duration;
        }

        TimeSpan pos = _lyrics.PlaybackPositionAnchor;
        if (_lyrics.IsPlaybackActive)
        {
            if (SpringAnimator.UseExternalClockForTest)
            {
                pos += TimeSpan.FromSeconds(_externalTestPlaybackOffsetSeconds);
            }
            else if (_lyrics.PlaybackAnchorTicks > 0)
            {
                double elapsedSec = (double)(Stopwatch.GetTimestamp() - _lyrics.PlaybackAnchorTicks) / Stopwatch.Frequency;
                if (elapsedSec > 0.0 && elapsedSec < 2.0)
                {
                    pos += TimeSpan.FromSeconds(elapsedSec);
                }
            }
        }

        double p = duration.TotalSeconds > 0.01
            ? Math.Clamp((pos - start).TotalSeconds / duration.TotalSeconds, 0.0, 1.0)
            : 0.0;

        double f1 = Math.Clamp(p * 3.0, 0.0, 1.0);
        double f2 = Math.Clamp((p * 3.0) - 1.0, 0.0, 1.0);
        double f3 = Math.Clamp((p * 3.0) - 2.0, 0.0, 1.0);

        double op1 = OpacityDotUnfilled + ((1.0 - OpacityDotUnfilled) * f1);
        double op2 = OpacityDotUnfilled + ((1.0 - OpacityDotUnfilled) * f2);
        double op3 = OpacityDotUnfilled + ((1.0 - OpacityDotUnfilled) * f3);

        _lastActiveInterludeProgress = p;
        _lastActiveInterludeDotOpacities = (op1, op2, op3);

        if (double.IsNaN(slot.AppliedDot1Op) || Math.Abs(op1 - slot.AppliedDot1Op) > 1e-4)
        {
            slot.Dot1.Opacity = op1;
            slot.AppliedDot1Op = op1;
        }
        if (double.IsNaN(slot.AppliedDot2Op) || Math.Abs(op2 - slot.AppliedDot2Op) > 1e-4)
        {
            slot.Dot2.Opacity = op2;
            slot.AppliedDot2Op = op2;
        }
        if (double.IsNaN(slot.AppliedDot3Op) || Math.Abs(op3 - slot.AppliedDot3Op) > 1e-4)
        {
            slot.Dot3.Opacity = op3;
            slot.AppliedDot3Op = op3;
        }

        double s1 = 1.0, s2 = 1.0, s3 = 1.0;
        if (_lyrics.IsPlaybackActive && !SpringAnimator.IsReducedMotion)
        {
            s1 = 1.0 + (0.06 * (0.5 + 0.5 * Math.Sin(_interludeBreathPhase)));
            s2 = 1.0 + (0.06 * (0.5 + 0.5 * Math.Sin(_interludeBreathPhase - 0.65)));
            s3 = 1.0 + (0.06 * (0.5 + 0.5 * Math.Sin(_interludeBreathPhase - 1.30)));
        }

        if (double.IsNaN(slot.AppliedDot1Scale) || Math.Abs(s1 - slot.AppliedDot1Scale) > 1e-4)
        {
            slot.Dot1Scale.ScaleX = s1;
            slot.Dot1Scale.ScaleY = s1;
            slot.AppliedDot1Scale = s1;
        }
        if (double.IsNaN(slot.AppliedDot2Scale) || Math.Abs(s2 - slot.AppliedDot2Scale) > 1e-4)
        {
            slot.Dot2Scale.ScaleX = s2;
            slot.Dot2Scale.ScaleY = s2;
            slot.AppliedDot2Scale = s2;
        }
        if (double.IsNaN(slot.AppliedDot3Scale) || Math.Abs(s3 - slot.AppliedDot3Scale) > 1e-4)
        {
            slot.Dot3Scale.ScaleX = s3;
            slot.Dot3Scale.ScaleY = s3;
            slot.AppliedDot3Scale = s3;
        }
    }

    private void SnapAllToTargets()
    {
        _scrollY = _targetScrollY;
        _scrollVelocity = 0.0;
        _isScrollAnimating = false;

        for (int s = 0; s < _pool.Count; s++)
        {
            var slot = _pool[s];
            if (slot.LogicalIndex < 0) continue;
            slot.CurrentOpacity = slot.TargetOpacity;
            slot.OpacityVelocity = 0.0;
            slot.CurrentScale = slot.TargetScale;
            slot.ScaleVelocity = 0.0;
            slot.DelayRemaining = 0.0;
        }

        EnsureVisibleSlotsBound(snapNewlyBound: true);
        ApplyAllSlotTransforms(updateTabStops: true);
        if (IsActiveRowInterlude())
        {
            UpdateActiveInterludeVisuals();
        }
        EnsureTickerIfNeeded();
    }

    private bool HasPendingAnimations()
    {
        if (!IsVisible || _lyrics == null || !_lyrics.IsSynced || _logicalCount == 0) return false;

        if (_isScrollAnimating) return true;

        for (int s = 0; s < _pool.Count; s++)
        {
            var slot = _pool[s];
            if (slot.LogicalIndex < 0) continue;
            if (slot.DelayRemaining > 0.0 ||
                Math.Abs(slot.CurrentOpacity - slot.TargetOpacity) > EmphasisTolerance ||
                Math.Abs(slot.OpacityVelocity) > EmphasisTolerance * 10.0 ||
                Math.Abs(slot.CurrentScale - slot.TargetScale) > EmphasisTolerance ||
                Math.Abs(slot.ScaleVelocity) > EmphasisTolerance * 10.0)
            {
                return true;
            }
        }

        if (IsActiveRowInterlude() && _lyrics.IsPlaybackActive)
        {
            return true;
        }

        return false;
    }

    private void EnsureTickerIfNeeded()
    {
        if (HasPendingAnimations())
        {
            if (!_isTickerRegistered)
            {
                _isTickerRegistered = true;
                SpringAnimator.RegisterFrameTicker(this);
            }
        }
        else
        {
            StopAnimationLoop();
        }
    }

    private void StopAnimationLoop()
    {
        if (_isTickerRegistered)
        {
            _isTickerRegistered = false;
            SpringAnimator.UnregisterFrameTicker(this);
        }
    }

    /// <summary>
    /// Zero-allocation deduplicated display frame step invoked by SpringAnimator.
    /// </summary>
    public bool StepFrame(double dtSeconds)
    {
        if (!IsVisible || _lyrics == null || !_lyrics.IsSynced || _logicalCount == 0)
        {
            _isTickerRegistered = false;
            return false;
        }

        int steps = Math.Max(1, (int)Math.Ceiling(dtSeconds / MaxSubStepSeconds));
        double subDt = dtSeconds / steps;

        bool anyActive = false;

        // 1. Critically-damped viewport scroll spring (damping = 1.0)
        if (_isScrollAnimating)
        {
            double omega = 2.0 * Math.PI / Math.Max(0.08, _activeScrollResponse);
            double stiffness = omega * omega;
            double damping = 2.0 * omega;

            for (int i = 0; i < steps; i++)
            {
                double disp = _scrollY - _targetScrollY;
                double accel = (-stiffness * disp) - (damping * _scrollVelocity);
                _scrollVelocity += accel * subDt;
                _scrollY += _scrollVelocity * subDt;
            }

            if (Math.Abs(_scrollY - _targetScrollY) <= ScrollTolerancePx &&
                Math.Abs(_scrollVelocity) <= ScrollTolerancePx * 10.0)
            {
                _scrollY = _targetScrollY;
                _scrollVelocity = 0.0;
                _isScrollAnimating = false;
            }
            else
            {
                anyActive = true;
            }

            EnsureVisibleSlotsBound(snapNewlyBound: false);
        }

        // 2. Per-row emphasis springs (Opacity + ScaleTransform, critically damped)
        double emphOmega = 2.0 * Math.PI / EmphasisResponse;
        double emphStiff = emphOmega * emphOmega;
        double emphDamp = 2.0 * emphOmega;

        for (int s = 0; s < _pool.Count; s++)
        {
            var slot = _pool[s];
            if (slot.LogicalIndex < 0) continue;

            bool needsOp = Math.Abs(slot.CurrentOpacity - slot.TargetOpacity) > EmphasisTolerance ||
                           Math.Abs(slot.OpacityVelocity) > EmphasisTolerance * 10.0;
            bool needsSc = Math.Abs(slot.CurrentScale - slot.TargetScale) > EmphasisTolerance ||
                           Math.Abs(slot.ScaleVelocity) > EmphasisTolerance * 10.0;

            if (!needsOp && !needsSc) continue;

            if (slot.DelayRemaining > 0.0)
            {
                slot.DelayRemaining -= dtSeconds;
                anyActive = true;
                continue;
            }

            if (needsOp)
            {
                for (int i = 0; i < steps; i++)
                {
                    double disp = slot.CurrentOpacity - slot.TargetOpacity;
                    double accel = (-emphStiff * disp) - (emphDamp * slot.OpacityVelocity);
                    slot.OpacityVelocity += accel * subDt;
                    slot.CurrentOpacity += slot.OpacityVelocity * subDt;
                }

                slot.CurrentOpacity = Math.Clamp(slot.CurrentOpacity, 0.0, 1.0);
                if (Math.Abs(slot.CurrentOpacity - slot.TargetOpacity) <= EmphasisTolerance &&
                    Math.Abs(slot.OpacityVelocity) <= EmphasisTolerance * 10.0)
                {
                    slot.CurrentOpacity = slot.TargetOpacity;
                    slot.OpacityVelocity = 0.0;
                }
                else
                {
                    anyActive = true;
                }
            }

            if (needsSc)
            {
                for (int i = 0; i < steps; i++)
                {
                    double disp = slot.CurrentScale - slot.TargetScale;
                    double accel = (-emphStiff * disp) - (emphDamp * slot.ScaleVelocity);
                    slot.ScaleVelocity += accel * subDt;
                    slot.CurrentScale += slot.ScaleVelocity * subDt;
                }

                if (Math.Abs(slot.CurrentScale - slot.TargetScale) <= EmphasisTolerance &&
                    Math.Abs(slot.ScaleVelocity) <= EmphasisTolerance * 10.0)
                {
                    slot.CurrentScale = slot.TargetScale;
                    slot.ScaleVelocity = 0.0;
                }
                else
                {
                    anyActive = true;
                }
            }
        }

        ApplyAllSlotTransforms(updateTabStops: true);

        // 3. 3-dot progress interlude smooth fill + subtle breathing
        if (IsActiveRowInterlude() && _lyrics.IsPlaybackActive)
        {
            if (SpringAnimator.UseExternalClockForTest)
            {
                _externalTestPlaybackOffsetSeconds += dtSeconds;
            }
            _interludeBreathPhase += dtSeconds * (2.0 * Math.PI * 0.75);
            if (_interludeBreathPhase > 2.0 * Math.PI)
            {
                _interludeBreathPhase -= 2.0 * Math.PI;
            }
            UpdateActiveInterludeVisuals();
            anyActive = true;
        }

        if (!anyActive)
        {
            _isTickerRegistered = false;
        }
        return anyActive;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_lyrics == null || !_lyrics.IsSynced || _logicalCount == 0) return;

        double stepPx = _isFullScreenMode ? 96.0 : 64.0;
        double deltaPx = -(e.Delta / 120.0) * stepPx;
        _targetScrollY = ClampScrollOffset(_targetScrollY + deltaPx);
        _activeScrollResponse = ScrollResponseWheel;

        _manualScrollPauseUntilUtc = DateTime.UtcNow.AddSeconds(ManualScrollPauseSeconds);
        _manualScrollResumeTimer.Stop();
        _manualScrollResumeTimer.Start();

        if (SpringAnimator.IsReducedMotion)
        {
            _scrollY = _targetScrollY;
            _scrollVelocity = 0.0;
            _isScrollAnimating = false;
            EnsureVisibleSlotsBound(snapNewlyBound: true);
            ApplyAllSlotTransforms(updateTabStops: true);
        }
        else
        {
            _isScrollAnimating = true;
            EnsureTickerIfNeeded();
        }

        e.Handled = true;
    }

    private void OnManualScrollResumeTick(object? sender, EventArgs e)
    {
        _manualScrollResumeTimer.Stop();
        _manualScrollPauseUntilUtc = DateTime.MinValue;
        SyncToActiveLine(immediate: false);
    }

    private void OnSlotClicked(RowSlot slot)
    {
        if (_lyrics == null || slot.LogicalIndex < 0 || slot.IsGapBound) return;
        var line = GetSourceLine(slot.LogicalIndex);
        if (line == null || line.IsInstrumentalGap) return;

        _manualScrollPauseUntilUtc = DateTime.MinValue;
        _manualScrollResumeTimer.Stop();

        if (_lyrics.SeekToLineCommand.CanExecute(line))
        {
            _lyrics.SeekToLineCommand.Execute(line);
        }

        UpdateActiveLogicalIndex(immediate: false, allowStagger: false);
    }

    private void OnSlotHoverChanged(RowSlot slot, bool isHovered)
    {
        if (slot.IsHovered == isHovered) return;
        slot.IsHovered = isHovered;
        RefreshSlotEmphasisTarget(slot);
    }

    private void OnSlotFocusChanged(RowSlot slot, bool isFocused)
    {
        if (slot.IsFocused == isFocused) return;
        slot.IsFocused = isFocused;
        RefreshSlotEmphasisTarget(slot);
    }

    private void RefreshSlotEmphasisTarget(RowSlot slot)
    {
        if (slot.LogicalIndex < 0) return;
        ComputeEmphasisTargets(slot, out double targetOp, out double targetScale, out bool highlightPill);
        slot.TargetOpacity = targetOp;
        slot.TargetScale = targetScale;
        slot.DelayRemaining = 0.0;
        ApplyPillHighlight(slot, highlightPill);

        if (SpringAnimator.IsReducedMotion)
        {
            slot.CurrentOpacity = targetOp;
            slot.OpacityVelocity = 0.0;
            slot.CurrentScale = targetScale;
            slot.ScaleVelocity = 0.0;
            ApplyAllSlotTransforms(updateTabStops: false);
        }
        else
        {
            EnsureTickerIfNeeded();
        }
    }

    private void OnSlotPreviewKeyDown(RowSlot slot, KeyEventArgs e)
    {
        if (_logicalCount == 0 || slot.LogicalIndex < 0) return;

        int targetIdx = -1;
        if (e.Key == Key.Down)
        {
            targetIdx = FindNextVocalLogicalIndex(slot.LogicalIndex + 1, +1);
        }
        else if (e.Key == Key.Up)
        {
            targetIdx = FindNextVocalLogicalIndex(slot.LogicalIndex - 1, -1);
        }
        else if (e.Key == Key.Home)
        {
            targetIdx = FindNextVocalLogicalIndex(0, +1);
        }
        else if (e.Key == Key.End)
        {
            targetIdx = FindNextVocalLogicalIndex(_logicalCount - 1, -1);
        }

        if (targetIdx < 0) return;

        e.Handled = true;
        FocusVocalLogicalIndex(targetIdx);
    }

    private int FindNextVocalLogicalIndex(int startIdx, int direction)
    {
        int i = startIdx;
        while (i >= 0 && i < _logicalCount)
        {
            if (!IsLogicalRowGap(i)) return i;
            i += direction;
        }
        return -1;
    }

    internal bool FocusVocalLogicalIndex(int logicalIndex)
    {
        if (logicalIndex < 0 || logicalIndex >= _logicalCount || IsLogicalRowGap(logicalIndex)) return false;

        // Scroll target cleanly into view and pause auto-scroll while keyboard exploring
        _manualScrollPauseUntilUtc = DateTime.UtcNow.AddSeconds(ManualScrollPauseSeconds);
        _manualScrollResumeTimer.Stop();
        _manualScrollResumeTimer.Start();

        double targetScroll = ComputeAnchorScrollForIndex(logicalIndex);
        _targetScrollY = targetScroll;
        _activeScrollResponse = ScrollResponseWheel;

        if (SpringAnimator.IsReducedMotion)
        {
            _scrollY = targetScroll;
            _scrollVelocity = 0.0;
            _isScrollAnimating = false;
        }
        else
        {
            _isScrollAnimating = true;
        }

        // Guarantee the target row is bound right now so Keyboard.Focus succeeds immediately
        if (_logicalToSlot[logicalIndex] < 0)
        {
            int recycleSlot = FindRecycleSlotForTarget(logicalIndex);
            if (recycleSlot >= 0)
            {
                BindSlotToLogicalIndex(recycleSlot, logicalIndex, snapEmphasis: true);
            }
        }

        int slotIdx = _logicalToSlot[logicalIndex];
        if (slotIdx < 0) return false;

        var targetSlot = _pool[slotIdx];
        targetSlot.Button.IsTabStop = true;
        targetSlot.Button.IsHitTestVisible = true;
        ApplyAllSlotTransforms(updateTabStops: false);
        EnsureTickerIfNeeded();
        return targetSlot.Button.Focus();
    }

    private int FindRecycleSlotForTarget(int logicalIndex)
    {
        double targetY = _rowY[logicalIndex];
        int bestSlot = -1;
        double bestDist = -1.0;
        for (int s = 0; s < _pool.Count; s++)
        {
            var c = _pool[s];
            if (c.LogicalIndex < 0) return s;
            if (c.IsFocused) continue;
            double d = Math.Abs(_rowY[c.LogicalIndex] - targetY);
            if (d > bestDist)
            {
                bestDist = d;
                bestSlot = s;
            }
        }
        return bestSlot;
    }
}
