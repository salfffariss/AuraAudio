using System;
using System.Windows.Input;

namespace AuraAudio.UI.ViewModels;

public enum RightPaneMode
{
    None,
    Queue,
    Lyrics
}

public enum QueueTabMode
{
    PlayingNext,
    History
}

/// <summary>
/// Manages v2.0.0 shell presentation state: responsive left rail collapse,
/// adaptive right split pane (Queue | Lyrics, Inline vs Overlay), Full-Screen Stage,
/// Floating Mini-Player, Signal Path flyout, and Get Info modal sheet.
/// </summary>
public class PlayerPresentationViewModel : ViewModelBase
{
    private bool _isFullScreenOpen;
    private bool _isFullScreenLyricsOpen;
    private bool _isMiniPlayerOpen;
    private bool _isMiniPlayerAlbumView;
    private bool _isMiniPlayerPaneExpanded;
    private RightPaneMode _miniPlayerTrayMode = RightPaneMode.None;
    private RightPaneMode _activeRightPane = RightPaneMode.None;
    private QueueTabMode _activeQueueTab = QueueTabMode.PlayingNext;
    private bool _isSignalPathFlyoutOpen;
    private bool _isInfoSheetOpen;
    private string _activeInfoTab = "Details";
    private bool _isSidebarCollapsed;
    private bool _userManualSidebarOverride;
    private bool _isRightPaneOverlay;
    private double _lastWindowWidth = 1280;

    public bool IsFullScreenOpen
    {
        get => _isFullScreenOpen;
        set
        {
            if (SetProperty(ref _isFullScreenOpen, value) && value)
            {
                IsSignalPathFlyoutOpen = false;
                if (_activeRightPane == RightPaneMode.Lyrics)
                {
                    IsFullScreenLyricsOpen = true;
                }
            }
        }
    }

    public bool IsFullScreenLyricsOpen
    {
        get => _isFullScreenLyricsOpen;
        set => SetProperty(ref _isFullScreenLyricsOpen, value);
    }

    public bool IsMiniPlayerOpen
    {
        get => _isMiniPlayerOpen;
        set
        {
            if (SetProperty(ref _isMiniPlayerOpen, value) && value)
            {
                IsFullScreenOpen = false;
                IsSignalPathFlyoutOpen = false;
            }
        }
    }

    public bool IsMiniPlayerAlbumView
    {
        get => _isMiniPlayerAlbumView;
        set => SetProperty(ref _isMiniPlayerAlbumView, value);
    }

    public bool IsMiniPlayerPaneExpanded
    {
        get => _isMiniPlayerPaneExpanded;
        set => SetProperty(ref _isMiniPlayerPaneExpanded, value);
    }

    public RightPaneMode MiniPlayerTrayMode
    {
        get => _miniPlayerTrayMode;
        set
        {
            if (SetProperty(ref _miniPlayerTrayMode, value))
            {
                IsMiniPlayerPaneExpanded = value != RightPaneMode.None;
                OnPropertyChanged(nameof(IsMiniPlayerQueueActive));
                OnPropertyChanged(nameof(IsMiniPlayerLyricsActive));
            }
        }
    }

    public bool IsMiniPlayerQueueActive
    {
        get => _miniPlayerTrayMode == RightPaneMode.Queue;
        set
        {
            if (value) MiniPlayerTrayMode = RightPaneMode.Queue;
            else if (_miniPlayerTrayMode == RightPaneMode.Queue) MiniPlayerTrayMode = RightPaneMode.None;
        }
    }

    public bool IsMiniPlayerLyricsActive
    {
        get => _miniPlayerTrayMode == RightPaneMode.Lyrics;
        set
        {
            if (value) MiniPlayerTrayMode = RightPaneMode.Lyrics;
            else if (_miniPlayerTrayMode == RightPaneMode.Lyrics) MiniPlayerTrayMode = RightPaneMode.None;
        }
    }

    public RightPaneMode ActiveRightPane
    {
        get => _activeRightPane;
        set
        {
            if (SetProperty(ref _activeRightPane, value))
            {
                OnPropertyChanged(nameof(IsRightPaneOpen));
                OnPropertyChanged(nameof(IsQueuePaneActive));
                OnPropertyChanged(nameof(IsLyricsPaneActive));
                UpdateResponsiveBreakpoints(_lastWindowWidth);
            }
        }
    }

    public bool IsRightPaneOpen => _activeRightPane != RightPaneMode.None;

    public bool IsQueuePaneActive
    {
        get => _activeRightPane == RightPaneMode.Queue;
        set
        {
            if (value)
                ActiveRightPane = RightPaneMode.Queue;
            else if (_activeRightPane == RightPaneMode.Queue)
                ActiveRightPane = RightPaneMode.None;
        }
    }

    public bool IsLyricsPaneActive
    {
        get => _activeRightPane == RightPaneMode.Lyrics;
        set
        {
            if (value)
                ActiveRightPane = RightPaneMode.Lyrics;
            else if (_activeRightPane == RightPaneMode.Lyrics)
                ActiveRightPane = RightPaneMode.None;
        }
    }

    public QueueTabMode ActiveQueueTab
    {
        get => _activeQueueTab;
        set
        {
            if (SetProperty(ref _activeQueueTab, value))
            {
                OnPropertyChanged(nameof(IsPlayingNextTab));
                OnPropertyChanged(nameof(IsHistoryTab));
            }
        }
    }

    public bool IsPlayingNextTab
    {
        get => _activeQueueTab == QueueTabMode.PlayingNext;
        set
        {
            if (value) ActiveQueueTab = QueueTabMode.PlayingNext;
        }
    }

    public bool IsHistoryTab
    {
        get => _activeQueueTab == QueueTabMode.History;
        set
        {
            if (value) ActiveQueueTab = QueueTabMode.History;
        }
    }

    public bool IsSignalPathFlyoutOpen
    {
        get => _isSignalPathFlyoutOpen;
        set => SetProperty(ref _isSignalPathFlyoutOpen, value);
    }

    public bool IsInfoSheetOpen
    {
        get => _isInfoSheetOpen;
        set => SetProperty(ref _isInfoSheetOpen, value);
    }

    public string ActiveInfoTab
    {
        get => _activeInfoTab;
        set => SetProperty(ref _activeInfoTab, value);
    }

    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        set
        {
            if (SetProperty(ref _isSidebarCollapsed, value))
            {
                OnPropertyChanged(nameof(IsSidebarExpanded));
                OnPropertyChanged(nameof(SidebarWidth));
            }
        }
    }

    public bool IsSidebarExpanded => !_isSidebarCollapsed;
    public double SidebarWidth => _isSidebarCollapsed ? 56.0 : 248.0;

    public bool IsRightPaneOverlay
    {
        get => _isRightPaneOverlay;
        set => SetProperty(ref _isRightPaneOverlay, value);
    }

    public ICommand ToggleFullScreenCommand { get; }
    public ICommand ExitFullScreenCommand { get; }
    public ICommand ToggleFullScreenLyricsCommand { get; }
    public ICommand ToggleMiniPlayerCommand { get; }
    public ICommand CloseMiniPlayerCommand { get; }
    public ICommand ToggleMiniPlayerViewModeCommand { get; }
    public ICommand ToggleMiniPlayerQueueCommand { get; }
    public ICommand ToggleMiniPlayerLyricsCommand { get; }
    public ICommand ToggleQueuePaneCommand { get; }
    public ICommand ToggleLyricsPaneCommand { get; }
    public ICommand CloseRightPaneCommand { get; }
    public ICommand SelectQueueTabCommand { get; }
    public ICommand ToggleSignalPathFlyoutCommand { get; }
    public ICommand OpenInfoSheetCommand { get; }
    public ICommand CloseInfoSheetCommand { get; }
    public ICommand ToggleSidebarCommand { get; }

    public PlayerPresentationViewModel()
    {
        ToggleFullScreenCommand = new RelayCommand(() => IsFullScreenOpen = !IsFullScreenOpen);
        ExitFullScreenCommand = new RelayCommand(() => IsFullScreenOpen = false);
        ToggleFullScreenLyricsCommand = new RelayCommand(() => IsFullScreenLyricsOpen = !IsFullScreenLyricsOpen);
        ToggleMiniPlayerCommand = new RelayCommand(() => IsMiniPlayerOpen = !IsMiniPlayerOpen);
        CloseMiniPlayerCommand = new RelayCommand(() => IsMiniPlayerOpen = false);
        ToggleMiniPlayerViewModeCommand = new RelayCommand(() => IsMiniPlayerAlbumView = !IsMiniPlayerAlbumView);
        ToggleMiniPlayerQueueCommand = new RelayCommand(() =>
            MiniPlayerTrayMode = MiniPlayerTrayMode == RightPaneMode.Queue ? RightPaneMode.None : RightPaneMode.Queue);
        ToggleMiniPlayerLyricsCommand = new RelayCommand(() =>
            MiniPlayerTrayMode = MiniPlayerTrayMode == RightPaneMode.Lyrics ? RightPaneMode.None : RightPaneMode.Lyrics);
        ToggleQueuePaneCommand = new RelayCommand(() =>
            ActiveRightPane = ActiveRightPane == RightPaneMode.Queue ? RightPaneMode.None : RightPaneMode.Queue);
        ToggleLyricsPaneCommand = new RelayCommand(() =>
            ActiveRightPane = ActiveRightPane == RightPaneMode.Lyrics ? RightPaneMode.None : RightPaneMode.Lyrics);
        CloseRightPaneCommand = new RelayCommand(() => ActiveRightPane = RightPaneMode.None);
        SelectQueueTabCommand = new RelayCommand(param =>
        {
            if (param is string tab && tab.Equals("History", StringComparison.OrdinalIgnoreCase))
                ActiveQueueTab = QueueTabMode.History;
            else
                ActiveQueueTab = QueueTabMode.PlayingNext;
        });
        ToggleSignalPathFlyoutCommand = new RelayCommand(() => IsSignalPathFlyoutOpen = !IsSignalPathFlyoutOpen);
        OpenInfoSheetCommand = new RelayCommand(param =>
        {
            if (param is string tab && !string.IsNullOrWhiteSpace(tab))
                ActiveInfoTab = tab;
            IsInfoSheetOpen = true;
        });
        CloseInfoSheetCommand = new RelayCommand(() => IsInfoSheetOpen = false);
        ToggleSidebarCommand = new RelayCommand(() =>
        {
            _userManualSidebarOverride = true;
            IsSidebarCollapsed = !IsSidebarCollapsed;
        });
    }

    /// <summary>
    /// Updates responsive shell breakpoints (03_design_spec.md C.1):
    /// >= 1680: Expanded rail + Inline right pane
    /// 1440..1679: Inline right pane; rail auto-compacts when right pane opens
    /// 1280..1439: Overlay right pane; rail expanded by default
    /// < 1280: Overlay right pane; rail auto-compacts to 56px
    /// </summary>
    public void UpdateWindowWidth(double width)
    {
        if (width <= 0) return;
        _lastWindowWidth = width;
        UpdateResponsiveBreakpoints(width);
    }

    private void UpdateResponsiveBreakpoints(double width)
    {
        IsRightPaneOverlay = width < 1440.0;

        if (_userManualSidebarOverride)
        {
            if (width < 1040.0)
            {
                IsSidebarCollapsed = true;
            }
            return;
        }

        if (width < 1280.0)
        {
            IsSidebarCollapsed = true;
        }
        else if (width < 1680.0 && IsRightPaneOpen && !IsRightPaneOverlay)
        {
            IsSidebarCollapsed = true;
        }
        else
        {
            IsSidebarCollapsed = false;
        }
    }
}
