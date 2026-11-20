using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Windows;

/// <summary>
/// v2.0.0 Floating Always-On-Top Mini-Player (03_design_spec.md Part B.10 & C.3):
/// 350x160 Compact <-> 350x350 AlbumView toggle with expandable 260px Queue/Lyrics tray,
/// bound directly to the shared MainViewModel instance.
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const double CompactHeight = 160.0;
    private const double AlbumViewHeight = 350.0;
    private const double ExpandedTrayHeight = 260.0;

    private readonly MainViewModel _viewModel;
    private bool _isDraggingScrubber;

    public MiniPlayerWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _viewModel.Presentation.PropertyChanged += OnPresentationPropertyChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Activated += OnActivated;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
    }

    public void FocusWindowRoot()
    {
        FocusManager.SetFocusedElement(this, this);
        Keyboard.Focus(this);
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (Keyboard.FocusedElement == null)
        {
            FocusWindowRoot();
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && !IsInsideInteractiveControl(src))
        {
            FocusWindowRoot();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        DwmBackdropHelper.ApplyChrome(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncWindowDimensions(animate: false);

        // Anchor cleanly near bottom-right of current work area on initial open
        Rect workArea = SystemParameters.WorkArea;
        Left = Math.Max(workArea.Left + 16, workArea.Right - Width - 24);
        Top = Math.Max(workArea.Top + 16, workArea.Bottom - Height - 24);
        FocusWindowRoot();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SpringAnimator.Cancel(this, "miniDimensions");
        _viewModel.Presentation.PropertyChanged -= OnPresentationPropertyChanged;
        if (_viewModel.Presentation.IsMiniPlayerOpen)
        {
            _viewModel.Presentation.IsMiniPlayerOpen = false;
        }
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerPresentationViewModel.IsMiniPlayerAlbumView) ||
            e.PropertyName == nameof(PlayerPresentationViewModel.IsMiniPlayerPaneExpanded))
        {
            SyncWindowDimensions(animate: true);
        }
    }

    private void SyncWindowDimensions(bool animate)
    {
        double targetStage = _viewModel.Presentation.IsMiniPlayerAlbumView ? AlbumViewHeight : CompactHeight;
        bool trayOpen = _viewModel.Presentation.IsMiniPlayerPaneExpanded;
        double targetTray = trayOpen ? ExpandedTrayHeight : 0.0;
        double targetTotal = targetStage + targetTray;

        double startStage = PlayerStageRow.Height.Value + 2.0;
        double startTray = MiniTrayBorder.Visibility == Visibility.Visible ? MiniTrayBorder.Height : 0.0;
        double startTotal = double.IsNaN(Height) || Height <= 0 ? ActualHeight : Height;
        double startTop = Top;

        Rect workArea = SystemParameters.WorkArea;
        double targetTop = startTop + targetTotal > workArea.Bottom - 8.0
            ? Math.Max(workArea.Top + 8.0, workArea.Bottom - targetTotal - 16.0)
            : startTop;

        void ApplyDimensions(double stageH, double trayH, double totalH, double topPos)
        {
            PlayerStageRow.Height = new GridLength(Math.Max(1.0, Math.Round(stageH - 2.0)));
            MiniTrayBorder.Height = Math.Max(0.0, Math.Round(trayH));
            Height = Math.Max(CompactHeight, Math.Round(totalH));
            if (!double.IsNaN(topPos))
            {
                Top = Math.Round(topPos);
            }
        }

        if (!animate || SpringAnimator.IsReducedMotion)
        {
            SpringAnimator.Cancel(this, "miniDimensions");
            MiniTrayBorder.Visibility = trayOpen ? Visibility.Visible : Visibility.Collapsed;
            ApplyDimensions(targetStage, targetTray, targetTotal, targetTop);
            return;
        }

        if (trayOpen || MiniTrayBorder.Visibility == Visibility.Visible)
        {
            MiniTrayBorder.Visibility = Visibility.Visible;
        }

        if (Math.Abs(targetStage - startStage) < 0.5 && Math.Abs(targetTray - startTray) < 0.5)
        {
            MiniTrayBorder.Visibility = trayOpen ? Visibility.Visible : Visibility.Collapsed;
            ApplyDimensions(targetStage, targetTray, targetTotal, targetTop);
            return;
        }

        SpringAnimator.SpringValue(
            this,
            "miniDimensions",
            currentValue: 0.0,
            targetValue: 1.0,
            dampingRatio: 1.0,
            response: 0.26,
            tolerance: 0.002,
            resetProgressOnRetarget: true,
            apply: p =>
            {
                double clamped = Math.Clamp(p, 0.0, 1.0);
                double curStage = startStage + (targetStage - startStage) * clamped;
                double curTray = startTray + (targetTray - startTray) * clamped;
                double curTotal = startTotal + (targetTotal - startTotal) * clamped;
                double curTop = double.IsNaN(startTop) ? double.NaN : startTop + (targetTop - startTop) * clamped;
                ApplyDimensions(curStage, curTray, curTotal, curTop);
            },
            onComplete: () =>
            {
                ApplyDimensions(targetStage, targetTray, targetTotal, targetTop);
                if (!_viewModel.Presentation.IsMiniPlayerPaneExpanded)
                {
                    MiniTrayBorder.Visibility = Visibility.Collapsed;
                }
            });
    }

    private void MiniHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideInteractiveControl(e.OriginalSource as DependencyObject)) return;

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        Key effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (effectiveKey == Key.LeftAlt || effectiveKey == Key.RightAlt)
        {
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        Key effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;

        if (effectiveKey == Key.LeftAlt || effectiveKey == Key.RightAlt)
        {
            FocusWindowRoot();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Left when isCtrl:
                _viewModel.PreviousTrackCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Right when isCtrl:
                _viewModel.NextTrackCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.M when isCtrl && isShift:
                _viewModel.Presentation.CloseMiniPlayerCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                if (_viewModel.Presentation.IsMiniPlayerPaneExpanded)
                {
                    _viewModel.Presentation.MiniPlayerTrayMode = RightPaneMode.None;
                }
                else
                {
                    _viewModel.Presentation.CloseMiniPlayerCommand.Execute(null);
                }
                e.Handled = true;
                break;
        }
    }

    private void MiniScrubber_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isDraggingScrubber = true;
        _viewModel.OnSliderDragStarted();
    }

    private void MiniScrubber_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDraggingScrubber = false;
        if (sender is Slider slider)
        {
            _viewModel.OnSliderDragCompleted(slider.Value);
        }
    }

    private void MiniScrubber_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingScrubber = true;
        _viewModel.OnSliderDragStarted();
    }

    private void MiniScrubber_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingScrubber && sender is Slider slider)
        {
            _isDraggingScrubber = false;
            _viewModel.OnSliderDragCompleted(slider.Value);
        }
    }

    private static bool IsInsideInteractiveControl(DependencyObject? source)
    {
        DependencyObject? cur = source;
        while (cur != null)
        {
            if (cur is ButtonBase || cur is Slider || cur is TextBoxBase) return true;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }
}
