using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using AuraAudio.Core.Models;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.UI.Windows;

/// <summary>
/// v2.0.0 Pure-Monochrome Application Shell (03_design_spec.md Part C.1 & D.2):
/// Top 48px Navigation/Search Bar, Collapsible 248/56px Navigation Rail,
/// Center Content Viewport, Adaptive 360px Inline/Overlay Right Split Pane,
/// and Floating Inset Rounded Bottom Transport & LCD Dock Capsule.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private WindowsMediaControlsManager? _mediaControlsManager;
    private HwndSource? _hwndSource;
    private MiniPlayerWindow? _miniPlayerWindow;
    private bool _isRightPaneVisibleState;
    private bool _wasRightPaneOverlay;
    private RightPaneMode _lastActiveRightPaneMode = RightPaneMode.None;
    private bool _isFullScreenVisibleState;
    private bool _isClosingMainWindow;
    private bool _isDraggingScrubber;

    public MainWindow() : this(new MainViewModel())
    {
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Presentation.PropertyChanged += OnPresentationPropertyChanged;

        SourceInitialized += OnWindowSourceInitialized;
        Loaded += OnWindowLoaded;
        Activated += OnWindowActivated;
        SizeChanged += OnWindowSizeChanged;
        StateChanged += OnWindowStateChanged;
        Closed += OnWindowClosed;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewKeyUp += OnWindowPreviewKeyUp;
        PreviewMouseLeftButtonDown += OnWindowPreviewMouseLeftButtonDown;
        PreviewMouseUp += OnWindowPreviewMouseUp;
    }

    /// <summary>
    /// Anchors keyboard focus on the MainWindow root (Focusable=True, FocusVisualStyle={x:Null})
    /// instead of calling Keyboard.ClearFocus(). Clearing focus sets Keyboard.FocusedElement to null,
    /// which stops WPF from routing PreviewKeyDown shortcuts (Ctrl+Shift+F, Ctrl+Shift+L, Ctrl+I, Space)
    /// until the user clicks a focusable child button again.
    /// </summary>
    public void FocusWindowRoot()
    {
        if (_isClosingMainWindow) return;
        FocusManager.SetFocusedElement(this, this);
        Keyboard.Focus(this);
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (Keyboard.FocusedElement == null)
        {
            FocusWindowRoot();
        }
    }

    // =========================================================================
    // WINDOW LIFECYCLE, DWM PURE-NEUTRAL CHROME & WINDOWS 11 SMTC
    // =========================================================================
    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        DwmBackdropHelper.ApplyChrome(this);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(HwndMessageHook);

            _mediaControlsManager ??= new WindowsMediaControlsManager(hwnd, _viewModel.Controller);
            _viewModel.SetMediaControlsManager(_mediaControlsManager);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        UpdateMaximizeRestoreIcons();
        SyncWindowChromeResizeBorder();
        _viewModel.Presentation.UpdateWindowWidth(ActualWidth);
        LeftRailBorder.Width = _viewModel.Presentation.SidebarWidth;
        SyncShellRegions(animate: false);
        SyncModalSheets(animate: false);
        FocusWindowRoot();
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
        {
            _viewModel.Presentation.UpdateWindowWidth(e.NewSize.Width);
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        DwmBackdropHelper.SetCornerPreference(this, WindowState == WindowState.Maximized);
        UpdateMaximizeRestoreIcons();
        SyncWindowChromeResizeBorder();
    }

    /// <summary>
    /// Removes WindowChrome's 6px Win32 resize hit-test strip while Maximized so Fitts's Law
    /// screen-edge clicks reach the right-edge ScrollBar and top-right Close button all the way
    /// to the outermost physical pixel, while restoring 6px resize borders in Normal mode.
    /// </summary>
    private void SyncWindowChromeResizeBorder()
    {
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        if (chrome != null)
        {
            chrome.ResizeBorderThickness = WindowState == WindowState.Maximized
                ? new Thickness(0)
                : new Thickness(6);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _isClosingMainWindow = true;
        if (_miniPlayerWindow != null)
        {
            var mini = _miniPlayerWindow;
            _miniPlayerWindow = null;
            mini.Close();
        }

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Presentation.PropertyChanged -= OnPresentationPropertyChanged;

        _viewModel.SaveSession();
        _hwndSource?.RemoveHook(HwndMessageHook);
        _mediaControlsManager?.Dispose();
        _viewModel.Dispose();
    }

    private void UpdateMaximizeRestoreIcons()
    {
        bool isMax = WindowState == WindowState.Maximized;
        MaximizeIconPath.Visibility = isMax ? Visibility.Collapsed : Visibility.Visible;
        RestoreIconPath.Visibility = isMax ? Visibility.Visible : Visibility.Collapsed;
    }

    // =========================================================================
    // RESPONSIVE RAIL & RIGHT SPLIT PANE SPRING TRANSITIONS (03_design_spec C.1)
    // =========================================================================
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentView))
        {
            if (SpringAnimator.IsReducedMotion)
            {
                var (_, translate) = SpringAnimator.EnsureTransformGroup(ContentStageHost);
                translate.Y = 0.0;
                ContentStageHost.Opacity = 0.35;
                SpringAnimator.SpringOpacity(ContentStageHost, 1.0, response: 0.14);
            }
            else
            {
                var (_, translate) = SpringAnimator.EnsureTransformGroup(ContentStageHost);
                translate.Y = 8.0;
                ContentStageHost.Opacity = 0.60;
                SpringAnimator.SpringTranslateY(ContentStageHost, 0.0, dampingRatio: 1.0, response: 0.24);
                SpringAnimator.SpringOpacity(ContentStageHost, 1.0, response: 0.18);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsAddToPlaylistSheetOpen))
        {
            SyncModalSheet(
                AddToPlaylistSheetOverlay,
                _viewModel.IsAddToPlaylistSheetOpen,
                AddToPlaylistSheetOverlay.PlayOpenAnimation,
                AddToPlaylistSheetOverlay.PlayCloseAnimation,
                () => _viewModel.IsAddToPlaylistSheetOpen);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsRenamePlaylistSheetOpen))
        {
            SyncModalSheet(
                RenamePlaylistSheetOverlay,
                _viewModel.IsRenamePlaylistSheetOpen,
                RenamePlaylistSheetOverlay.PlayOpenAnimation,
                RenamePlaylistSheetOverlay.PlayCloseAnimation,
                () => _viewModel.IsRenamePlaylistSheetOpen);
        }
    }

    private void OnPresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerPresentationViewModel.SidebarWidth) ||
            e.PropertyName == nameof(PlayerPresentationViewModel.ActiveRightPane) ||
            e.PropertyName == nameof(PlayerPresentationViewModel.IsRightPaneOverlay))
        {
            SyncShellRegions(animate: true);
        }
        else if (e.PropertyName == nameof(PlayerPresentationViewModel.IsFullScreenOpen))
        {
            SyncFullScreenStage();
        }
        else if (e.PropertyName == nameof(PlayerPresentationViewModel.IsMiniPlayerOpen))
        {
            SyncMiniPlayerWindow();
        }
        else if (e.PropertyName == nameof(PlayerPresentationViewModel.IsInfoSheetOpen))
        {
            SyncModalSheet(
                InfoInspectorSheetOverlay,
                _viewModel.Presentation.IsInfoSheetOpen,
                InfoInspectorSheetOverlay.PlayOpenAnimation,
                InfoInspectorSheetOverlay.PlayCloseAnimation,
                () => _viewModel.Presentation.IsInfoSheetOpen);
        }
    }

    private void SyncModalSheets(bool animate)
    {
        if (_viewModel.Presentation.IsInfoSheetOpen)
        {
            InfoInspectorSheetOverlay.Visibility = Visibility.Visible;
            if (animate) InfoInspectorSheetOverlay.PlayOpenAnimation();
        }
        if (_viewModel.IsAddToPlaylistSheetOpen)
        {
            AddToPlaylistSheetOverlay.Visibility = Visibility.Visible;
            if (animate) AddToPlaylistSheetOverlay.PlayOpenAnimation();
        }
        if (_viewModel.IsRenamePlaylistSheetOpen)
        {
            RenamePlaylistSheetOverlay.Visibility = Visibility.Visible;
            if (animate) RenamePlaylistSheetOverlay.PlayOpenAnimation();
        }
    }

    private void SyncModalSheet(
        FrameworkElement sheet,
        bool isOpen,
        Action playOpen,
        Action<Action> playClose,
        Func<bool> isStillOpen)
    {
        if (isOpen)
        {
            sheet.Visibility = Visibility.Visible;
            playOpen();
        }
        else
        {
            FocusWindowRoot();
            if (sheet.Visibility != Visibility.Visible) return;

            playClose(() =>
            {
                if (!isStillOpen())
                {
                    sheet.Visibility = Visibility.Collapsed;
                    if (Keyboard.FocusedElement == null)
                    {
                        FocusWindowRoot();
                    }
                }
            });
        }
    }

    private void SyncFullScreenStage()
    {
        bool open = _viewModel.Presentation.IsFullScreenOpen;
        _isFullScreenVisibleState = open;

        if (open)
        {
            FullScreenStageOverlay.Opacity = 1.0;
            FullScreenStageOverlay.Visibility = Visibility.Visible;
            FullScreenStageOverlay.PlayEntranceAnimation();
            FocusWindowRoot();
        }
        else
        {
            if (FullScreenStageOverlay.Visibility != Visibility.Visible)
            {
                FocusWindowRoot();
                return;
            }

            FocusWindowRoot();
            FullScreenStageOverlay.PlayExitAnimation(() =>
            {
                if (!_isFullScreenVisibleState)
                {
                    FullScreenStageOverlay.Visibility = Visibility.Collapsed;
                    if (Keyboard.FocusedElement == null)
                    {
                        FocusWindowRoot();
                    }
                }
            });
        }
    }

    private void SyncMiniPlayerWindow()
    {
        if (_isClosingMainWindow) return;

        bool open = _viewModel.Presentation.IsMiniPlayerOpen;
        if (open)
        {
            if (_miniPlayerWindow == null)
            {
                _viewModel.RefreshQueue();
                _viewModel.RefreshPlayHistory();
                _miniPlayerWindow = new MiniPlayerWindow(_viewModel);
                _miniPlayerWindow.Closed += OnMiniPlayerClosed;
                _miniPlayerWindow.Show();
            }
            else
            {
                _miniPlayerWindow.Activate();
            }

            Hide();
        }
        else
        {
            if (_miniPlayerWindow != null)
            {
                var mini = _miniPlayerWindow;
                _miniPlayerWindow = null;
                mini.Closed -= OnMiniPlayerClosed;
                mini.Close();
            }

            if (!IsVisible)
            {
                Show();
            }
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            Activate();
            FocusWindowRoot();
        }
    }

    private void OnMiniPlayerClosed(object? sender, EventArgs e)
    {
        _miniPlayerWindow = null;
        if (_isClosingMainWindow) return;

        if (_viewModel.Presentation.IsMiniPlayerOpen)
        {
            _viewModel.Presentation.IsMiniPlayerOpen = false;
        }
        else
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            FocusWindowRoot();
        }
    }

    private void SyncShellRegions(bool animate)
    {
        const double paneSpecWidth = 360.0;

        RightPaneMode activeMode = _viewModel.Presentation.ActiveRightPane;
        bool isOpen = _viewModel.Presentation.IsRightPaneOpen;
        bool isOverlay = _viewModel.Presentation.IsRightPaneOverlay;
        double targetRailW = _viewModel.Presentation.SidebarWidth;

        bool wasOpen = _isRightPaneVisibleState;
        bool prevOverlay = _wasRightPaneOverlay;
        bool switchedContentWhileOpen = isOpen &&
                                        wasOpen &&
                                        activeMode != _lastActiveRightPaneMode &&
                                        _lastActiveRightPaneMode != RightPaneMode.None;

        _isRightPaneVisibleState = isOpen;
        _wasRightPaneOverlay = isOverlay;
        _lastActiveRightPaneMode = activeMode;

        // Capture live geometry BEFORE changing column placement so retargets remain continuous
        double startRailW = double.IsNaN(LeftRailBorder.Width) ? LeftRailBorder.ActualWidth : LeftRailBorder.Width;
        var (_, paneTr) = SpringAnimator.EnsureTransformGroup(RightSplitPaneBorder);
        bool paneCurrentlyVisible = RightSplitPaneBorder.Visibility == Visibility.Visible;
        double startInlinePaneW = paneCurrentlyVisible && !prevOverlay
            ? (double.IsNaN(RightSplitPaneBorder.Width) ? RightSplitPaneBorder.ActualWidth : RightSplitPaneBorder.Width)
            : (isOpen && prevOverlay && !isOverlay ? paneSpecWidth : 0.0);
        double startOverlayX = paneCurrentlyVisible && prevOverlay
            ? paneTr.X
            : (isOpen ? paneSpecWidth : 0.0);

        if (isOverlay)
        {
            Grid.SetColumn(RightSplitPaneBorder, 1);
            Grid.SetColumnSpan(RightSplitPaneBorder, 2);
            RightSplitPaneBorder.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else
        {
            Grid.SetColumn(RightSplitPaneBorder, 2);
            Grid.SetColumnSpan(RightSplitPaneBorder, 1);
            RightSplitPaneBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        // Smooth overlay scrim fade-in / fade-out
        bool showScrim = isOpen && isOverlay;
        if (showScrim)
        {
            if (RightPaneOverlayScrim.Visibility != Visibility.Visible)
            {
                RightPaneOverlayScrim.Opacity = animate ? 0.0 : 1.0;
                RightPaneOverlayScrim.Visibility = Visibility.Visible;
            }
            if (animate)
            {
                SpringAnimator.SpringOpacity(RightPaneOverlayScrim, 1.0, response: 0.18);
            }
            else
            {
                SpringAnimator.Cancel(RightPaneOverlayScrim, "opacity");
                RightPaneOverlayScrim.Opacity = 1.0;
            }
        }
        else if (RightPaneOverlayScrim.Visibility == Visibility.Visible)
        {
            if (animate)
            {
                SpringAnimator.SpringOpacity(
                    RightPaneOverlayScrim,
                    0.0,
                    response: 0.16,
                    onComplete: () =>
                    {
                        if (!(_isRightPaneVisibleState && _viewModel.Presentation.IsRightPaneOverlay))
                        {
                            RightPaneOverlayScrim.Visibility = Visibility.Collapsed;
                        }
                    });
            }
            else
            {
                SpringAnimator.Cancel(RightPaneOverlayScrim, "opacity");
                RightPaneOverlayScrim.Visibility = Visibility.Collapsed;
            }
        }

        // Queue <-> Lyrics cross-fade while pane stays open
        if (switchedContentWhileOpen && animate)
        {
            RightPaneContentHost.Opacity = 0.25;
            SpringAnimator.SpringOpacity(
                RightPaneContentHost,
                1.0,
                response: SpringAnimator.IsReducedMotion ? 0.14 : 0.16);
        }
        else if (!animate)
        {
            SpringAnimator.Cancel(RightPaneContentHost, "opacity");
            RightPaneContentHost.Opacity = 1.0;
        }

        void CollapseRightPane()
        {
            if (!_isRightPaneVisibleState)
            {
                paneTr.X = 0.0;
                RightSplitPaneBorder.Width = 0.0;
                RightSplitPaneBorder.Opacity = 1.0;
                RightSplitPaneBorder.Visibility = Visibility.Collapsed;
                if (Keyboard.FocusedElement == null)
                {
                    FocusWindowRoot();
                }
            }
        }

        // Non-animated or Reduced Motion fallback: immediate layout geometry (zero gap, zero late reflow),
        // paired with a calm opacity fade.
        if (!animate || SpringAnimator.IsReducedMotion)
        {
            SpringAnimator.Cancel(WorkspaceGrid, "shellRegions");
            SpringAnimator.Cancel(RightSplitPaneBorder, "opacity");
            SpringAnimator.Cancel(RightSplitPaneBorder, "translateX");

            LeftRailContent.Width = double.NaN;
            LeftRailContent.HorizontalAlignment = HorizontalAlignment.Stretch;
            LeftRailBorder.Width = targetRailW;
            paneTr.X = 0.0;

            if (isOpen)
            {
                RightSplitPaneBorder.Width = paneSpecWidth;
                if (animate && !paneCurrentlyVisible)
                {
                    RightSplitPaneBorder.Opacity = 0.0;
                    RightSplitPaneBorder.Visibility = Visibility.Visible;
                    SpringAnimator.SpringOpacity(RightSplitPaneBorder, 1.0, response: 0.14);
                }
                else
                {
                    RightSplitPaneBorder.Opacity = 1.0;
                    RightSplitPaneBorder.Visibility = Visibility.Visible;
                }
            }
            else
            {
                if (isOverlay && animate && paneCurrentlyVisible)
                {
                    // Overlay sits above content (zero gap / zero reflow), so calm fade-out is gapless
                    RightSplitPaneBorder.Width = paneSpecWidth;
                    SpringAnimator.SpringOpacity(RightSplitPaneBorder, 0.0, response: 0.14, onComplete: CollapseRightPane);
                }
                else
                {
                    // Inline close under Reduced Motion: immediate reflow avoids any 360px gap or late pop
                    bool hadInlinePane = paneCurrentlyVisible && !prevOverlay;
                    CollapseRightPane();
                    if (animate && hadInlinePane)
                    {
                        ContentStageHost.Opacity = 0.88;
                        SpringAnimator.SpringOpacity(ContentStageHost, 1.0, response: 0.12);
                    }
                }
            }
            return;
        }

        // Animated continuity path (Candidate A): drive Left Rail, Center Content, Dock Capsule,
        // and Right Pane from ONE shared critically-damped spring progress value (damping=1.0, response=0.28s)
        // so all neighbouring edges stay in exact lockstep (I2 <= 1 DIP on every frame).
        SpringAnimator.Cancel(RightSplitPaneBorder, "opacity");
        SpringAnimator.Cancel(RightSplitPaneBorder, "translateX");

        double targetInlinePaneW = (isOpen && !isOverlay) ? paneSpecWidth : 0.0;
        double targetOverlayX = (isOpen && isOverlay) ? 0.0 : paneSpecWidth;

        if (!isOverlay)
        {
            paneTr.X = 0.0;
            if (isOpen || paneCurrentlyVisible)
            {
                RightSplitPaneBorder.Opacity = 1.0;
                RightSplitPaneBorder.Width = startInlinePaneW;
                RightSplitPaneBorder.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (isOpen || paneCurrentlyVisible)
            {
                RightSplitPaneBorder.Width = paneSpecWidth;
                RightSplitPaneBorder.Opacity = 1.0;
                paneTr.X = startOverlayX;
                RightSplitPaneBorder.Visibility = Visibility.Visible;
            }
        }

        bool railMoves = Math.Abs(targetRailW - startRailW) >= 0.5;
        bool inlinePaneMoves = !isOverlay && (isOpen || paneCurrentlyVisible) && Math.Abs(targetInlinePaneW - startInlinePaneW) >= 0.5;
        bool overlayPaneMoves = isOverlay && (isOpen || paneCurrentlyVisible) && Math.Abs(targetOverlayX - startOverlayX) >= 0.5;

        if (!railMoves && !inlinePaneMoves && !overlayPaneMoves)
        {
            LeftRailContent.Width = double.NaN;
            LeftRailContent.HorizontalAlignment = HorizontalAlignment.Stretch;
            LeftRailBorder.Width = targetRailW;
            if (!isOpen)
            {
                CollapseRightPane();
            }
            return;
        }

        if (railMoves)
        {
            // Pin inner rail content to exact target width during animation so icons/pills render
            // at final geometry (zero I4 end-pop) with zero per-frame inner rail re-layout.
            LeftRailContent.Width = Math.Max(0.0, targetRailW - 1.0);
            LeftRailContent.HorizontalAlignment = HorizontalAlignment.Left;
        }

        SpringAnimator.SpringValue(
            WorkspaceGrid,
            "shellRegions",
            currentValue: 0.0,
            targetValue: 1.0,
            dampingRatio: 1.0,
            response: 0.28,
            tolerance: 0.001,
            resetProgressOnRetarget: true,
            apply: p =>
            {
                double clamped = Math.Clamp(p, 0.0, 1.0);
                if (railMoves)
                {
                    LeftRailBorder.Width = Math.Round(startRailW + (targetRailW - startRailW) * clamped);
                }

                if (inlinePaneMoves)
                {
                    RightSplitPaneBorder.Width = Math.Round(startInlinePaneW + (targetInlinePaneW - startInlinePaneW) * clamped);
                    paneTr.X = 0.0;
                }
                else if (overlayPaneMoves)
                {
                    paneTr.X = Math.Round(startOverlayX + (targetOverlayX - startOverlayX) * clamped);
                }
            },
            onComplete: () =>
            {
                if (railMoves)
                {
                    LeftRailBorder.Width = targetRailW;
                    LeftRailContent.Width = double.NaN;
                    LeftRailContent.HorizontalAlignment = HorizontalAlignment.Stretch;
                }

                if (!isOverlay)
                {
                    paneTr.X = 0.0;
                    if (_isRightPaneVisibleState)
                    {
                        RightSplitPaneBorder.Width = paneSpecWidth;
                        RightSplitPaneBorder.Opacity = 1.0;
                        RightSplitPaneBorder.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        CollapseRightPane();
                    }
                }
                else
                {
                    if (_isRightPaneVisibleState)
                    {
                        RightSplitPaneBorder.Width = paneSpecWidth;
                        paneTr.X = 0.0;
                        RightSplitPaneBorder.Opacity = 1.0;
                        RightSplitPaneBorder.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        CollapseRightPane();
                    }
                }
            });
    }

    private void RightPaneOverlayScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.Presentation.CloseRightPaneCommand.Execute(null);
    }

    // =========================================================================
    // TOP BAR WINDOW CHROME & GLOBAL SEARCH INTERACTIONS
    // =========================================================================
    private void TopBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && IsInsideInteractiveControl(src))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
        }
        else if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private static bool IsInsideInteractiveControl(DependencyObject element) =>
        TreeNavigationHelper.HasAncestor(element, static current => current is ButtonBase || current is TextBoxBase);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e) =>
        ToggleMaximizeRestore();

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void GlobalSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        SearchPillBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.Border.Strong");
        if (_viewModel.Search.HasQuery && _viewModel.Search.HasQuickHints)
        {
            _viewModel.Search.IsHintsOpen = true;
        }
    }

    private void GlobalSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SearchPillBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.Border.Control");
    }

    private void GlobalSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _viewModel.Search.IsHintsOpen && SearchHintsList.Items.Count > 0)
        {
            int next = SearchHintsList.SelectedIndex + 1;
            SearchHintsList.SelectedIndex = next >= SearchHintsList.Items.Count ? 0 : next;
            SearchHintsList.ScrollIntoView(SearchHintsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && _viewModel.Search.IsHintsOpen && SearchHintsList.Items.Count > 0)
        {
            int prev = SearchHintsList.SelectedIndex - 1;
            SearchHintsList.SelectedIndex = prev < 0 ? SearchHintsList.Items.Count - 1 : prev;
            SearchHintsList.ScrollIntoView(SearchHintsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_viewModel.Search.IsHintsOpen && SearchHintsList.SelectedItem is SearchHintItem hint)
            {
                _viewModel.Search.SelectHintCommand.Execute(hint);
                SearchHintsList.SelectedIndex = -1;
            }
            else
            {
                _viewModel.Search.CommitSearchCommand.Execute(null);
            }
            FocusWindowRoot();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel.Search.IsHintsOpen = false;
            SearchHintsList.SelectedIndex = -1;
            FocusWindowRoot();
            e.Handled = true;
        }
    }

    private void SearchHintsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src)
        {
            var container = ItemsControl.ContainerFromElement(SearchHintsList, src) as ListBoxItem;
            if (container?.DataContext is SearchHintItem hint)
            {
                _viewModel.Search.SelectHintCommand.Execute(hint);
                SearchHintsList.SelectedIndex = -1;
                FocusWindowRoot();
                e.Handled = true;
            }
        }
    }

    // =========================================================================
    // SIDEBAR PLAYLIST DRAG-DROP TARGETS (03_design_spec.md Part C.1)
    // =========================================================================
    private void SidebarNewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CreatePlaylistCommand.Execute(string.Empty);
    }

    private void PlaylistNavItem_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(Track)) ||
            e.Data.GetDataPresent("AuraTrackList") ||
            e.Data.GetDataPresent(typeof(Album)))
        {
            e.Effects = DragDropEffects.Copy;
            if (sender is Control ctrl)
            {
                ctrl.Background = (System.Windows.Media.Brush)FindResource("Brush.SelectedRow");
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void PlaylistNavItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Control ctrl)
        {
            ctrl.ClearValue(Control.BackgroundProperty);
        }
    }

    private void PlaylistNavItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            fe.ClearValue(Control.BackgroundProperty);
            if (fe.DataContext is Playlist targetPlaylist)
            {
                if (e.Data.GetData("AuraTrackList") is System.Collections.Generic.IEnumerable<Track> multiTracks)
                {
                    _viewModel.AddTracksToPlaylistById(targetPlaylist.Id, multiTracks);
                }
                else if (e.Data.GetData(typeof(Track)) is Track droppedTrack)
                {
                    _viewModel.AddTrackToPlaylistById(targetPlaylist.Id, droppedTrack);
                }
                else if (e.Data.GetData(typeof(Album)) is Album droppedAlbum)
                {
                    _viewModel.AddAlbumToPlaylistById(targetPlaylist.Id, droppedAlbum);
                }
            }
        }
        e.Handled = true;
    }

    // =========================================================================
    // BOTTOM DOCK SCRUBBER SEEK HANDLERS
    // =========================================================================
    private void DockScrubber_DragStarted(object sender, DragStartedEventArgs e)
    {
        _isDraggingScrubber = true;
        _viewModel.OnSliderDragStarted();
    }

    private void DockScrubber_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDraggingScrubber = false;
        _viewModel.OnSliderDragCompleted(DockScrubber.Value);
    }

    private void DockScrubber_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDraggingScrubber = true;
        _viewModel.OnSliderDragStarted();
    }

    private void DockScrubber_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingScrubber)
        {
            _isDraggingScrubber = false;
            _viewModel.OnSliderDragCompleted(DockScrubber.Value);
        }
    }

    // =========================================================================
    // GLOBAL KEYBOARD ACCELERATORS (WITH TextBoxBase FOCUS GUARD) & MOUSE XBUTTON
    // =========================================================================
    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && !IsInsideFocusableInput(src))
        {
            FocusWindowRoot();
        }
    }

    private static bool IsInsideFocusableInput(DependencyObject element) =>
        TreeNavigationHelper.HasAncestor(element, static current =>
            current is TextBoxBase ||
            current is ComboBox ||
            current is ListBoxItem ||
            current is DataGridCell ||
            current is Slider);

    private static bool IsInsideQueueList(DependencyObject? element) =>
        TreeNavigationHelper.HasAncestor(element, static current =>
            current is FrameworkElement fe && fe.Name == "QueueListBox");

    private void OnWindowPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1 && _viewModel.CanNavigateBack)
        {
            _viewModel.NavigateBackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.XButton2 && _viewModel.CanNavigateForward)
        {
            _viewModel.NavigateForwardCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyUp(object sender, KeyEventArgs e)
    {
        // Swallow bare Alt keyup so WPF KeyboardNavigation never enters menu-cue mode
        Key effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;
        if (effectiveKey == Key.LeftAlt || effectiveKey == Key.RightAlt)
        {
            e.Handled = true;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool isAlt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        Key effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;

        // Allow Ctrl+F (without Shift) to focus search from anywhere
        if (isCtrl && !isShift && e.Key == Key.F)
        {
            GlobalSearchBox.Focus();
            GlobalSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // Guard: never intercept text-entry keys while typing in any TextBoxBase
        if (Keyboard.FocusedElement is TextBoxBase)
        {
            if (e.Key == Key.Escape)
            {
                if (_viewModel.IsRenamePlaylistSheetOpen)
                {
                    _viewModel.CancelRenameSheetCommand.Execute(null);
                }
                else if (_viewModel.IsAddToPlaylistSheetOpen)
                {
                    _viewModel.CloseAddToPlaylistSheetCommand.Execute(null);
                }
                else if (_viewModel.Search.IsHintsOpen)
                {
                    _viewModel.Search.IsHintsOpen = false;
                }
                FocusWindowRoot();
                e.Handled = true;
            }
            return;
        }

        // Prevent bare Alt press from entering Win32 menu-cue mode and painting a stuck
        // white focus outline around previously mouse-clicked dock/rail buttons.
        // Preserves Alt+Left/Right navigation, Alt+Up/Down queue reorder, Alt+Tab, and Alt+F4.
        if (effectiveKey == Key.LeftAlt || effectiveKey == Key.RightAlt)
        {
            DependencyObject? focused = (Keyboard.FocusedElement as DependencyObject)
                                        ?? (FocusManager.GetFocusedElement(this) as DependencyObject);
            if (focused != null && !ReferenceEquals(focused, this) && !IsInsideQueueList(focused))
            {
                FocusWindowRoot();
            }
            e.Handled = true;
            return;
        }

        if (isAlt && e.SystemKey == Key.Left && _viewModel.CanNavigateBack)
        {
            _viewModel.NavigateBackCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (isAlt && e.SystemKey == Key.Right && _viewModel.CanNavigateForward)
        {
            _viewModel.NavigateForwardCommand.Execute(null);
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

            case Key.Left:
                var backTarget = _viewModel.CurrentTime - TimeSpan.FromSeconds(5);
                _viewModel.Controller.Seek(backTarget < TimeSpan.Zero ? TimeSpan.Zero : backTarget);
                e.Handled = true;
                break;

            case Key.Right:
                var fwdTarget = _viewModel.CurrentTime + TimeSpan.FromSeconds(5);
                _viewModel.Controller.Seek(fwdTarget > _viewModel.TotalTime ? _viewModel.TotalTime : fwdTarget);
                e.Handled = true;
                break;

            case Key.Up:
                _viewModel.Volume = Math.Min(1.0f, _viewModel.Volume + 0.05f);
                e.Handled = true;
                break;

            case Key.Down:
                _viewModel.Volume = Math.Max(0.0f, _viewModel.Volume - 0.05f);
                e.Handled = true;
                break;

            case Key.M when isCtrl && isShift:
                _viewModel.Presentation.ToggleMiniPlayerCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.F when isCtrl && isShift:
                _viewModel.Presentation.ToggleFullScreenCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.M when !isCtrl && !isAlt:
                _viewModel.ToggleMuteCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.S when !isCtrl && !isAlt && !isShift:
                _viewModel.ToggleShuffleCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.R when !isCtrl && !isAlt && !isShift:
                _viewModel.ToggleRepeatCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.I when isCtrl && !isShift && !isAlt:
                _viewModel.OpenInfoForTrackCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.U when isCtrl:
                _viewModel.Presentation.ToggleQueuePaneCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.L when isCtrl && isShift:
                if (_viewModel.Presentation.IsFullScreenOpen)
                    _viewModel.Presentation.ToggleFullScreenLyricsCommand.Execute(null);
                else
                    _viewModel.Presentation.ToggleLyricsPaneCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.OemComma when isCtrl:
                _viewModel.NavigateCommand.Execute("Settings");
                e.Handled = true;
                break;

            case Key.Escape:
                if (_viewModel.IsRenamePlaylistSheetOpen)
                {
                    _viewModel.CancelRenameSheetCommand.Execute(null);
                    e.Handled = true;
                }
                else if (_viewModel.IsAddToPlaylistSheetOpen)
                {
                    _viewModel.CloseAddToPlaylistSheetCommand.Execute(null);
                    e.Handled = true;
                }
                else if (_viewModel.Presentation.IsInfoSheetOpen)
                {
                    _viewModel.Presentation.CloseInfoSheetCommand.Execute(null);
                    e.Handled = true;
                }
                else if (_viewModel.Search.IsHintsOpen)
                {
                    _viewModel.Search.IsHintsOpen = false;
                    e.Handled = true;
                }
                else if (_viewModel.Presentation.IsSignalPathFlyoutOpen)
                {
                    _viewModel.Presentation.IsSignalPathFlyoutOpen = false;
                    e.Handled = true;
                }
                else if (_viewModel.Presentation.IsFullScreenOpen)
                {
                    _viewModel.Presentation.ExitFullScreenCommand.Execute(null);
                    e.Handled = true;
                }
                else if (_viewModel.Presentation.IsRightPaneOpen)
                {
                    _viewModel.Presentation.CloseRightPaneCommand.Execute(null);
                    e.Handled = true;
                }
                else if ((Keyboard.FocusedElement != null && !ReferenceEquals(Keyboard.FocusedElement, this)) ||
                         (FocusManager.GetFocusedElement(this) != null && !ReferenceEquals(FocusManager.GetFocusedElement(this), this)))
                {
                    FocusWindowRoot();
                    e.Handled = true;
                }
                break;
        }
    }

    // =========================================================================
    // WIN32 MESSAGE HOOK (WM_GETMINMAXINFO + WM_SYSCOMMAND + WM_APPCOMMAND)
    // =========================================================================
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;
    private const int WM_APPCOMMAND = 0x0319;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    private const int APPCOMMAND_VOLUME_MUTE = 8;
    private const int APPCOMMAND_VOLUME_DOWN = 9;
    private const int APPCOMMAND_VOLUME_UP = 10;
    private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    private const int APPCOMMAND_MEDIA_STOP = 13;
    private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
    private const int APPCOMMAND_MEDIA_PLAY = 46;
    private const int APPCOMMAND_MEDIA_PAUSE = 47;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_GETTASKBARPOS = 0x00000005;
    private const int ABS_AUTOHIDE = 0x0000001;
    private const uint ABE_LEFT = 0;
    private const uint ABE_TOP = 1;
    private const uint ABE_RIGHT = 2;
    private const uint ABE_BOTTOM = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    private double GetWindowDpiScale(IntPtr hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi >= 96) return dpi / 96.0;
        }
        catch { }

        var fallback = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return fallback.DpiScaleX > 0 ? fallback.DpiScaleX : 1.0;
    }

    private static uint GetAutoHideTaskbarEdge()
    {
        try
        {
            var abd = new APPBARDATA
            {
                cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
                hWnd = FindWindow("Shell_TrayWnd", null)
            };
            if (SHAppBarMessage(ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
            {
                return abd.uEdge;
            }
        }
        catch { }

        return ABE_BOTTOM;
    }

    private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Swallow bare Alt release (SC_KEYMENU with lParam == 0) since this borderless window
        // has no Win32 MenuBar; prevents stuck menu-cue focus rings while keeping Alt+Space/Alt+F4 intact.
        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_KEYMENU && lParam == IntPtr.Zero)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

            // Enforce DPI-scaled minimum track size (Option A: 960x640 DIPs) so Win32 never shrinks
            // the window below WPF's MinWidth/MinHeight and clips the right half of the UI.
            double dpiScale = GetWindowDpiScale(hwnd);
            mmi.ptMinTrackSize.x = (int)Math.Ceiling(MinWidth * dpiScale);
            mmi.ptMinTrackSize.y = (int)Math.Ceiling(MinHeight * dpiScale);

            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    RECT rcWork = monitorInfo.rcWork;
                    RECT rcMonitor = monitorInfo.rcMonitor;

                    int maxX = rcWork.left - rcMonitor.left;
                    int maxY = rcWork.top - rcMonitor.top;
                    int maxW = rcWork.right - rcWork.left;
                    int maxH = rcWork.bottom - rcWork.top;

                    // When Windows Auto-Hide Taskbar is enabled, rcWork == rcMonitor.
                    // Filling the exact full monitor rect makes DWM treat borderless WPF windows
                    // as fullscreen exclusive apps, blocking the auto-hide taskbar hover trigger.
                    // Insetting the taskbar edge by 2 physical pixels restores normal taskbar access.
                    bool workEqualsMonitor =
                        rcWork.left == rcMonitor.left &&
                        rcWork.top == rcMonitor.top &&
                        rcWork.right == rcMonitor.right &&
                        rcWork.bottom == rcMonitor.bottom;

                    bool autoHideDetected = workEqualsMonitor;
                    if (!autoHideDetected)
                    {
                        try
                        {
                            var stateData = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
                            autoHideDetected = (SHAppBarMessage(ABM_GETSTATE, ref stateData).ToInt64() & ABS_AUTOHIDE) != 0;
                        }
                        catch { }
                    }

                    if (workEqualsMonitor && autoHideDetected)
                    {
                        const int taskbarTriggerGapPx = 2;
                        switch (GetAutoHideTaskbarEdge())
                        {
                            case ABE_LEFT:
                                maxX += taskbarTriggerGapPx;
                                maxW -= taskbarTriggerGapPx;
                                break;
                            case ABE_TOP:
                                maxY += taskbarTriggerGapPx;
                                maxH -= taskbarTriggerGapPx;
                                break;
                            case ABE_RIGHT:
                                maxW -= taskbarTriggerGapPx;
                                break;
                            default: // ABE_BOTTOM
                                maxH -= taskbarTriggerGapPx;
                                break;
                        }
                    }

                    mmi.ptMaxPosition.x = maxX;
                    mmi.ptMaxPosition.y = maxY;
                    mmi.ptMaxSize.x = maxW;
                    mmi.ptMaxSize.y = maxH;
                    mmi.ptMaxTrackSize.x = Math.Max(maxW, mmi.ptMinTrackSize.x);
                    mmi.ptMaxTrackSize.y = Math.Max(maxH, mmi.ptMinTrackSize.y);
                }
            }

            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_APPCOMMAND)
        {
            int cmd = (int)((long)lParam >> 16) & ~0xF000;
            switch (cmd)
            {
                case APPCOMMAND_MEDIA_PLAY_PAUSE:
                    _viewModel.TogglePlayPauseCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_PLAY:
                    _viewModel.Controller.Play();
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_PAUSE:
                    _viewModel.Controller.Pause();
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_NEXTTRACK:
                    _viewModel.NextTrackCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_PREVIOUSTRACK:
                    _viewModel.PreviousTrackCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_STOP:
                    _viewModel.Controller.Stop();
                    handled = true;
                    break;
                case APPCOMMAND_VOLUME_MUTE:
                    _viewModel.ToggleMuteCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_VOLUME_UP:
                    _viewModel.Volume = Math.Min(1.0f, _viewModel.Volume + 0.05f);
                    handled = true;
                    break;
                case APPCOMMAND_VOLUME_DOWN:
                    _viewModel.Volume = Math.Max(0.0f, _viewModel.Volume - 0.05f);
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }
}
