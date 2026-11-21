using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Lyrics View (03_design_spec.md Part B.11 & D.8):
/// Hosted inside both the 360px Right Split Pane and the Full-Screen Stage split (and Mini-Player tray).
/// Supports all 5 LyricsGroups states, WCAG-verified distance luminance tiers,
/// click-to-seek in synced mode, 4-second manual-scroll pause, and plain-text fallback.
/// </summary>
public partial class LyricsView : UserControl
{
    public static readonly DependencyProperty IsFullScreenModeProperty =
        DependencyProperty.Register(
            nameof(IsFullScreenMode),
            typeof(bool),
            typeof(LyricsView),
            new PropertyMetadata(false, OnFullScreenModeChanged));

    public static readonly DependencyProperty ActiveFontSizeProperty =
        DependencyProperty.Register(nameof(ActiveFontSize), typeof(double), typeof(LyricsView), new PropertyMetadata(18.0));

    public static readonly DependencyProperty InactiveFontSizeProperty =
        DependencyProperty.Register(nameof(InactiveFontSize), typeof(double), typeof(LyricsView), new PropertyMetadata(16.0));

    public static readonly DependencyProperty ActiveLineHeightProperty =
        DependencyProperty.Register(nameof(ActiveLineHeight), typeof(double), typeof(LyricsView), new PropertyMetadata(25.0));

    public static readonly DependencyProperty InactiveLineHeightProperty =
        DependencyProperty.Register(nameof(InactiveLineHeight), typeof(double), typeof(LyricsView), new PropertyMetadata(22.0));

    public bool IsFullScreenMode
    {
        get => (bool)GetValue(IsFullScreenModeProperty);
        set => SetValue(IsFullScreenModeProperty, value);
    }

    public double ActiveFontSize
    {
        get => (double)GetValue(ActiveFontSizeProperty);
        private set => SetValue(ActiveFontSizeProperty, value);
    }

    public double InactiveFontSize
    {
        get => (double)GetValue(InactiveFontSizeProperty);
        private set => SetValue(InactiveFontSizeProperty, value);
    }

    public double ActiveLineHeight
    {
        get => (double)GetValue(ActiveLineHeightProperty);
        private set => SetValue(ActiveLineHeightProperty, value);
    }

    public double InactiveLineHeight
    {
        get => (double)GetValue(InactiveLineHeightProperty);
        private set => SetValue(InactiveLineHeightProperty, value);
    }

    private LyricsViewModel? _subscribedLyrics;
    private ScrollViewer? _cachedScrollViewer;
    private DateTime _manualScrollPauseUntilUtc = DateTime.MinValue;
    private double _targetScrollOffset;
    private bool _isAutoScrolling;
    private long _lastAutoScrollTicks;

    public LyricsView()
    {
        InitializeComponent();
        ApplyTypographyScale(false);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private static void OnFullScreenModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LyricsView view && e.NewValue is bool fs)
        {
            view.ApplyTypographyScale(fs);
        }
    }

    private void ApplyTypographyScale(bool isFullScreen)
    {
        if (isFullScreen)
        {
            ActiveFontSize = 24.0;
            InactiveFontSize = 20.0;
            ActiveLineHeight = 32.0;
            InactiveLineHeight = 28.0;
        }
        else
        {
            ActiveFontSize = 18.0;
            InactiveFontSize = 16.0;
            ActiveLineHeight = 25.0;
            InactiveLineHeight = 22.0;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachToViewModel();
        ScrollActiveLineIntoView(immediate: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAutoScrollAnimation();
        DetachFromViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachToViewModel();
            ScrollActiveLineIntoView(immediate: true);
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            ScrollActiveLineIntoView(immediate: true);
        }
        else
        {
            StopAutoScrollAnimation();
        }
    }

    private void AttachToViewModel()
    {
        DetachFromViewModel();
        if (DataContext is MainViewModel vm)
        {
            _subscribedLyrics = vm.Lyrics;
            _subscribedLyrics.PropertyChanged += OnLyricsPropertyChanged;
        }
    }

    private void DetachFromViewModel()
    {
        if (_subscribedLyrics != null)
        {
            _subscribedLyrics.PropertyChanged -= OnLyricsPropertyChanged;
            _subscribedLyrics = null;
        }
    }

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.ActiveLineIndex))
        {
            ScrollActiveLineIntoView(immediate: false);
        }
        else if (e.PropertyName == nameof(LyricsViewModel.State))
        {
            _manualScrollPauseUntilUtc = DateTime.MinValue;
            StopAutoScrollAnimation();
            GetSyncedScrollViewer()?.ScrollToTop();
        }
    }

    private ScrollViewer? GetSyncedScrollViewer()
    {
        if (_cachedScrollViewer != null) return _cachedScrollViewer;
        _cachedScrollViewer = FindVisualChild<ScrollViewer>(SyncedLinesList);
        return _cachedScrollViewer;
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

    private void SyncedScrollViewer_UserInteraction(object sender, InputEventArgs e)
    {
        // Only treat wheel scrolling or ScrollBar dragging as manual scroll pause (do not pause when clicking a lyric line to seek!)
        if (e is MouseWheelEventArgs)
        {
            _manualScrollPauseUntilUtc = DateTime.UtcNow.AddSeconds(4.0);
            StopAutoScrollAnimation();
            return;
        }

        if (e is MouseButtonEventArgs && IsInsideScrollBar(e.OriginalSource as DependencyObject))
        {
            _manualScrollPauseUntilUtc = DateTime.UtcNow.AddSeconds(4.0);
            StopAutoScrollAnimation();
        }
    }

    private static bool IsInsideScrollBar(DependencyObject? source) =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<ScrollBar>(source) != null;

    private void ScrollActiveLineIntoView(bool immediate)
    {
        if (!IsLoaded || !IsVisible || _subscribedLyrics == null) return;
        if (!_subscribedLyrics.IsSynced) return;
        if (!immediate && DateTime.UtcNow < _manualScrollPauseUntilUtc) return;

        int activeIdx = _subscribedLyrics.ActiveLineIndex;
        if (activeIdx < 0 || activeIdx >= _subscribedLyrics.Lines.Count) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded || !IsVisible || _subscribedLyrics == null) return;
            if (activeIdx < 0 || activeIdx >= _subscribedLyrics.Lines.Count) return;

            var scrollViewer = GetSyncedScrollViewer();
            if (scrollViewer == null) return;

            var container = SyncedLinesList.ItemContainerGenerator.ContainerFromIndex(activeIdx) as FrameworkElement;
            if (container == null)
            {
                // Realize virtualized target line before computing its centered pixel offset
                SyncedLinesList.ScrollIntoView(_subscribedLyrics.Lines[activeIdx]);
                SyncedLinesList.UpdateLayout();
                container = SyncedLinesList.ItemContainerGenerator.ContainerFromIndex(activeIdx) as FrameworkElement;
                if (container == null) return;
            }

            try
            {
                Point relativeInViewport = container.TransformToAncestor(scrollViewer).Transform(new Point(0, 0));
                double absoluteY = scrollViewer.VerticalOffset + relativeInViewport.Y;
                double viewport = scrollViewer.ViewportHeight;
                double anchorY = viewport * 0.32;
                double desired = Math.Clamp(absoluteY - anchorY, 0.0, scrollViewer.ScrollableHeight);

                if (immediate || SpringAnimator.IsReducedMotion)
                {
                    StopAutoScrollAnimation();
                    scrollViewer.ScrollToVerticalOffset(desired);
                }
                else
                {
                    _targetScrollOffset = desired;
                    if (!_isAutoScrolling)
                    {
                        _isAutoScrolling = true;
                        _lastAutoScrollTicks = Stopwatch.GetTimestamp();
                        CompositionTarget.Rendering += OnAutoScrollTick;
                    }
                }
            }
            catch
            {
                // Ignore transient layout transform errors during view teardown
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnAutoScrollTick(object? sender, EventArgs e)
    {
        var scrollViewer = GetSyncedScrollViewer();
        if (!IsLoaded || !IsVisible || scrollViewer == null || DateTime.UtcNow < _manualScrollPauseUntilUtc)
        {
            StopAutoScrollAnimation();
            return;
        }

        long now = Stopwatch.GetTimestamp();
        double dt = Math.Clamp((double)(now - _lastAutoScrollTicks) / Stopwatch.Frequency, 0.001, 0.05);
        _lastAutoScrollTicks = now;

        double current = scrollViewer.VerticalOffset;
        double delta = _targetScrollOffset - current;

        if (Math.Abs(delta) < 0.75)
        {
            scrollViewer.ScrollToVerticalOffset(_targetScrollOffset);
            StopAutoScrollAnimation();
            return;
        }

        // Frame-rate-independent exponential smoothing (identical feel at 60Hz, 120Hz, 144Hz+)
        double alpha = 1.0 - Math.Exp(-12.0 * dt);
        scrollViewer.ScrollToVerticalOffset(current + (delta * alpha));
    }

    private void StopAutoScrollAnimation()
    {
        if (_isAutoScrolling)
        {
            CompositionTarget.Rendering -= OnAutoScrollTick;
            _isAutoScrolling = false;
        }
    }
}
