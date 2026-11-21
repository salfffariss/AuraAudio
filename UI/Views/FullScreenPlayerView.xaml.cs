using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Full-Screen Now Playing Stage (03_design_spec.md Part B.10 & C.3):
/// 5-column symmetric Apple Music modal stage hiding navigation chrome,
/// morphing cleanly between Centered Artwork and Split-Screen Artwork + Synced Lyrics.
/// </summary>
public partial class FullScreenPlayerView : UserControl
{
    public static readonly DependencyProperty ArtworkSizeProperty =
        DependencyProperty.Register(
            nameof(ArtworkSize),
            typeof(double),
            typeof(FullScreenPlayerView),
            new PropertyMetadata(340.0));

    public double ArtworkSize
    {
        get => (double)GetValue(ArtworkSizeProperty);
        private set => SetValue(ArtworkSizeProperty, value);
    }

    private PlayerPresentationViewModel? _subscribedPresentation;
    private LyricsViewModel? _subscribedLyrics;
    private bool _isDraggingScrubber;
    private double _lyricsSplitProgress;

    public FullScreenPlayerView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachPresentation();
        ApplyLyricsSplitLayout(animate: false);
        UpdateResponsiveArtworkSize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachPresentation();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded)
        {
            AttachPresentation();
            ApplyLyricsSplitLayout(animate: false);
        }
    }

    private void AttachPresentation()
    {
        DetachPresentation();
        if (DataContext is MainViewModel vm)
        {
            _subscribedPresentation = vm.Presentation;
            _subscribedPresentation.PropertyChanged += OnPresentationPropertyChanged;
            _subscribedLyrics = vm.Lyrics;
            _subscribedLyrics.PropertyChanged += OnLyricsPropertyChanged;
        }
    }

    private void DetachPresentation()
    {
        if (_subscribedPresentation != null)
        {
            _subscribedPresentation.PropertyChanged -= OnPresentationPropertyChanged;
            _subscribedPresentation = null;
        }
        if (_subscribedLyrics != null)
        {
            _subscribedLyrics.PropertyChanged -= OnLyricsPropertyChanged;
            _subscribedLyrics = null;
        }
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerPresentationViewModel.IsFullScreenLyricsOpen))
        {
            ApplyLyricsSplitLayout(animate: true);
        }
    }

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.HasLyrics) ||
            e.PropertyName == nameof(LyricsViewModel.State))
        {
            ApplyLyricsSplitLayout(animate: true);
        }
    }

    private bool ShouldShowLyricsSplit =>
        _subscribedPresentation?.IsFullScreenLyricsOpen == true &&
        _subscribedLyrics?.HasLyrics == true;

    private void ApplyLyricsSplitLayout(bool animate)
    {
        bool splitOpen = ShouldShowLyricsSplit;
        double target = splitOpen ? 1.0 : 0.0;

        if (!animate || SpringAnimator.IsReducedMotion)
        {
            SpringAnimator.Cancel(FiveColumnStage, "fsSplitMorph");
            ApplySplitMorphProgress(target);

            if (splitOpen)
            {
                bool wasCollapsed = LyricsStageHost.Visibility != Visibility.Visible;
                LyricsStageHost.Visibility = Visibility.Visible;
                if (animate && wasCollapsed)
                {
                    LyricsStageHost.Opacity = 0.0;
                    SpringAnimator.SpringOpacity(LyricsStageHost, 1.0, response: 0.14);
                }
                else
                {
                    LyricsStageHost.Opacity = 1.0;
                }
            }
            else
            {
                LyricsStageHost.Opacity = 0.0;
                LyricsStageHost.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (splitOpen && LyricsStageHost.Visibility != Visibility.Visible)
        {
            LyricsStageHost.Opacity = _lyricsSplitProgress;
            LyricsStageHost.Visibility = Visibility.Visible;
        }

        SpringAnimator.SpringValue(
            FiveColumnStage,
            "fsSplitMorph",
            currentValue: _lyricsSplitProgress,
            targetValue: target,
            dampingRatio: 1.0,
            response: 0.30,
            tolerance: 0.002,
            apply: ApplySplitMorphProgress,
            onComplete: () =>
            {
                ApplySplitMorphProgress(target);
                if (!ShouldShowLyricsSplit)
                {
                    LyricsStageHost.Visibility = Visibility.Collapsed;
                }
            });
    }

    private void ApplySplitMorphProgress(double progress)
    {
        double p = Math.Clamp(progress, 0.0, 1.0);
        _lyricsSplitProgress = p;

        ColLeftPad.Width = new GridLength(2.0 - 1.4 * p, GridUnitType.Star);
        ColMiddleGap.Width = new GridLength(Math.Round(36.0 * p), GridUnitType.Pixel);
        ColLyrics.Width = new GridLength(2.2 * p, GridUnitType.Star);
        ColRightPad.Width = new GridLength(2.0 - 1.4 * p, GridUnitType.Star);

        var (_, lyricsTr) = SpringAnimator.EnsureTransformGroup(LyricsStageHost);
        lyricsTr.X = Math.Round(24.0 * (1.0 - p));
        LyricsStageHost.Opacity = p;

        UpdateResponsiveArtworkSize();
    }

    private void FullScreenPlayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveArtworkSize();
    }

    private void UpdateResponsiveArtworkSize()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        double availableHeightForArt = ActualHeight - 310.0;
        double centeredSize = Math.Clamp(Math.Min(availableHeightForArt, ActualWidth * 0.56), 220.0, 440.0);
        double splitSize = Math.Clamp(Math.Min(availableHeightForArt, ActualWidth * 0.38), 220.0, 400.0);
        double targetSize = Math.Round(centeredSize + (splitSize - centeredSize) * _lyricsSplitProgress);
        if (Math.Abs(ArtworkSize - targetSize) >= 0.5)
        {
            ArtworkSize = targetSize;
        }
    }

    public void PlayEntranceAnimation()
    {
        SpringAnimator.Cancel(StageRoot, "opacity");
        SpringAnimator.Cancel(StageRoot, "scale");
        SpringAnimator.Cancel(StageRoot, "translateY");
        ApplyLyricsSplitLayout(animate: false);
        UpdateResponsiveArtworkSize();
        SpringAnimator.AnimateSpatialExpand(
            StageRoot,
            origin: new Point(0.5, 0.92),
            fromScale: 0.94,
            fromY: 22.0,
            dampingRatio: 1.0,
            response: 0.34);
    }

    public void PlayExitAnimation(Action onComplete)
    {
        SpringAnimator.AnimateSpatialCollapse(
            StageRoot,
            origin: new Point(0.5, 0.92),
            toScale: 0.95,
            toY: 18.0,
            dampingRatio: 1.0,
            response: 0.22,
            onComplete: onComplete);
    }

    private void TopChrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        var win = Window.GetWindow(this);
        if (win == null) return;

        if (e.ClickCount == 2)
        {
            win.WindowState = win.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else if (e.ButtonState == MouseButtonState.Pressed)
        {
            win.DragMove();
        }
    }

    private void ArtistLink_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CurrentTrack != null)
        {
            vm.Presentation.ExitFullScreenCommand.Execute(null);
            vm.NavigateToArtistLinkCommand.Execute(vm.CurrentTrack.Artist);
        }
    }

    private void FullScreenScrubber_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isDraggingScrubber = true;
        if (DataContext is MainViewModel vm) vm.OnSliderDragStarted();
    }

    private void FullScreenScrubber_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDraggingScrubber = false;
        if (DataContext is MainViewModel vm) vm.OnSliderDragCompleted(FullScreenScrubber.Value);
    }

    private void FullScreenScrubber_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingScrubber = true;
        if (DataContext is MainViewModel vm) vm.OnSliderDragStarted();
    }

    private void FullScreenScrubber_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingScrubber)
        {
            _isDraggingScrubber = false;
            if (DataContext is MainViewModel vm) vm.OnSliderDragCompleted(FullScreenScrubber.Value);
        }
    }

    private static bool IsInsideButton(DependencyObject? source) =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<ButtonBase>(source) != null;
}
