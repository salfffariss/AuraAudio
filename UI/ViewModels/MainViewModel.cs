using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Audio.Controller;
using AuraAudio.Audio.Playlist;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Metadata;
using AuraAudio.UI.Helpers;
using Microsoft.Win32;

namespace AuraAudio.UI.ViewModels;

public sealed record PlaylistOverviewItem(Playlist Playlist, string? CoverArtCacheKey);

/// <summary>
/// ViewModel Utama yang mengorkestrasi state antarmuka pengguna (120 FPS UI Thread)
/// dengan mesin pemutar audio kelas audiophile (AudiophilePlaybackController).
/// Mendukung virtualisasi 100.000+ lagu, grid album berkecepatan tinggi, dan sorting dinamis.
/// </summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly AudiophilePlaybackController _controller;
    private readonly DatabaseManager _db;
    private readonly CancellationTokenSource _cts = new();

    private sealed record NavigationEntry(
        string Route,
        Album? Album = null,
        Artist? Artist = null,
        Playlist? Playlist = null);

    private string _currentView = "Home";
    private readonly Stack<NavigationEntry> _backStack = new();
    private readonly Stack<NavigationEntry> _forwardStack = new();
    private bool _isHistoryNavigating;
    private long _lastRecordedHistoryTrackId = -1;
    private DateTime _lastRecordedHistoryUtc = DateTime.MinValue;
    private string _searchQuery = string.Empty;
    private long _totalTracksCount = 0;
    private string _libraryStatsText = "Memuat pustaka...";

    // Sorting state
    private TrackSortOption _currentSortOption = TrackSortOption.TitleAscending;
    private string _activeSortColumn = "Title";
    private bool _isSortAscending = true;

    // Playback state
    private Track? _currentTrack;
    private long? _activePlayingTrackId;
    private bool _isPlaying = false;
    private TimeSpan _currentTime = TimeSpan.Zero;
    private TimeSpan _totalTime = TimeSpan.Zero;
    private double _seekSliderValue = 0;
    private double _seekSliderMaximum = 100;
    private bool _isUserDraggingSlider = false;

    private string _signalPathBadge = "BIT-PERFECT DIRECT";
    private SignalPathSummary? _currentSignalPath;
    private AudioIntegrityReport? _currentIntegrityReport;
    private string _streamSpecBadge = "--";
    private bool _isShuffleEnabled = false;
    private PlaybackRepeatMode _repeatMode = PlaybackRepeatMode.Off;
    private float _volume = 1.0f;
    private string _outputDeviceName = "Default Endpoint";
    private string _sidebarDriverTitle = "WASAPI Exclusive";
    private string _sidebarDriverSubtitle = "Jalur Bit-Perfect Langsung";
    private bool _isExclusiveDriverActive = true;
    private AuraAudio.UI.Helpers.WindowsMediaControlsManager? _mediaControlsManager;
    private PlaybackStatus _lastKnownStatus = PlaybackStatus.Stopped;
    private Artist? _selectedArtist;
    private bool _isArtistDetailView;
    private string _artistStatsSummary = string.Empty;
    private Playlist? _selectedPlaylist;
    private string _queueSummaryText = "Antrean kosong";
    private int _currentQueueIndex = -1;
    private bool _isQueueDrawerOpen = false;
    private string _analysisErrorMessage = string.Empty;
    private string _playbackErrorMessage = string.Empty;
    private CancellationTokenSource? _searchDebounceCts;

    // Child ViewModels (v2.0.0 architecture, 03_design_spec.md Part D.6)
    public PlayerPresentationViewModel Presentation { get; }
    public HomeViewModel Home { get; }
    public SearchViewModel Search { get; }
    public LyricsViewModel Lyrics { get; }
    public SettingsViewModel Settings { get; }

    // Now Playing Music Detail Stage State
    private TrackExtendedDetails _currentTrackDetails = TrackExtendedDetails.Empty;
    private string _currentTrackArtistName = "—";
    private string _currentTrackArtistSummary = string.Empty;
    private string _currentTrackArtistInitials = "♪";
    private string? _currentTrackArtistCoverKey;

    public BulkObservableCollection<Track> DisplayedTracks { get; } = new();
    public BulkObservableCollection<Album> DisplayedAlbums { get; } = new();
    public BulkObservableCollection<Artist> DisplayedArtists { get; } = new();
    public BulkObservableCollection<Album> ArtistAlbums { get; } = new();
    public BulkObservableCollection<Track> ArtistTracks { get; } = new();
    public BulkObservableCollection<ArtistAlbumGroup> ArtistAlbumGroups { get; } = new();
    public BulkObservableCollection<Track> ArtistFeaturedTracks { get; } = new();
    public BulkObservableCollection<QueueTrackItem> QueueTracks { get; } = new();
    public BulkObservableCollection<Track> PlayHistoryTracks { get; } = new();
    public BulkObservableCollection<Playlist> SavedPlaylists { get; } = new();
    public BulkObservableCollection<PlaylistOverviewItem> SavedPlaylistCards { get; } = new();
    public BulkObservableCollection<Track> SelectedPlaylistTracks { get; } = new();
    public BulkObservableCollection<Track> SelectedAlbumDetailTracks { get; } = new();
    public BulkObservableCollection<Track> CurrentTrackArtistTopTracks { get; } = new();

    // Songs Column Browser (Format/Codec | Artist | Album)
    private bool _isColumnBrowserVisible;
    private string _selectedBrowserFormat = "All Formats";
    private string _selectedBrowserArtist = "All Artists";
    private string _selectedBrowserAlbum = "All Albums";
    private IReadOnlyList<Track> _allLoadedTracksCache = Array.Empty<Track>();

    public BulkObservableCollection<string> ColumnBrowserFormats { get; } = new() { "All Formats" };
    public BulkObservableCollection<string> ColumnBrowserArtists { get; } = new() { "All Artists" };
    public BulkObservableCollection<string> ColumnBrowserAlbums { get; } = new() { "All Albums" };

    public bool IsColumnBrowserVisible
    {
        get => _isColumnBrowserVisible;
        set => SetProperty(ref _isColumnBrowserVisible, value);
    }

    public string SelectedBrowserFormat
    {
        get => _selectedBrowserFormat;
        set
        {
            string v = string.IsNullOrWhiteSpace(value) ? "All Formats" : value;
            if (SetProperty(ref _selectedBrowserFormat, v))
            {
                RebuildColumnBrowserArtistsAndAlbums();
                ApplyColumnBrowserFilter();
            }
        }
    }

    public string SelectedBrowserArtist
    {
        get => _selectedBrowserArtist;
        set
        {
            string v = string.IsNullOrWhiteSpace(value) ? "All Artists" : value;
            if (SetProperty(ref _selectedBrowserArtist, v))
            {
                RebuildColumnBrowserAlbums();
                ApplyColumnBrowserFilter();
            }
        }
    }

    public string SelectedBrowserAlbum
    {
        get => _selectedBrowserAlbum;
        set
        {
            string v = string.IsNullOrWhiteSpace(value) ? "All Albums" : value;
            if (SetProperty(ref _selectedBrowserAlbum, v))
            {
                ApplyColumnBrowserFilter();
            }
        }
    }

    // Albums Sort & Quick Filter
    private string _albumSortMode = "Title";
    private string _albumFilterQuery = string.Empty;
    public string AlbumSortMode
    {
        get => _albumSortMode;
        set
        {
            if (SetProperty(ref _albumSortMode, value))
            {
                LoadAlbums();
            }
        }
    }

    public string AlbumFilterQuery
    {
        get => _albumFilterQuery;
        set
        {
            if (SetProperty(ref _albumFilterQuery, value))
            {
                LoadAlbums();
            }
        }
    }

    // Artists Master-Detail Quick Filter
    private string _artistFilterQuery = string.Empty;
    public string ArtistFilterQuery
    {
        get => _artistFilterQuery;
        set
        {
            if (SetProperty(ref _artistFilterQuery, value))
            {
                LoadArtists(_artistFilterQuery);
            }
        }
    }

    private string? _selectedPlaylistCoverKey;
    public string? SelectedPlaylistCoverKey
    {
        get => _selectedPlaylistCoverKey;
        private set => SetProperty(ref _selectedPlaylistCoverKey, value);
    }

    public TrackExtendedDetails CurrentTrackDetails
    {
        get => _currentTrackDetails;
        private set => SetProperty(ref _currentTrackDetails, value);
    }

    public string CurrentTrackArtistName
    {
        get => _currentTrackArtistName;
        private set => SetProperty(ref _currentTrackArtistName, value);
    }

    public string CurrentTrackArtistSummary
    {
        get => _currentTrackArtistSummary;
        private set => SetProperty(ref _currentTrackArtistSummary, value);
    }

    public string CurrentTrackArtistInitials
    {
        get => _currentTrackArtistInitials;
        private set => SetProperty(ref _currentTrackArtistInitials, value);
    }

    public string? CurrentTrackArtistCoverKey
    {
        get => _currentTrackArtistCoverKey;
        private set => SetProperty(ref _currentTrackArtistCoverKey, value);
    }

    public bool IsNowPlayingView => _currentView == "NowPlaying";
    public bool HasCurrentTrack => _currentTrack != null;

    private Album? _selectedAlbumDetail;
    public Album? SelectedAlbumDetail
    {
        get => _selectedAlbumDetail;
        set
        {
            if (SetProperty(ref _selectedAlbumDetail, value))
            {
                NotifyBreadcrumbChanged();
            }
        }
    }

    private string _albumDetailSummaryText = string.Empty;
    public string AlbumDetailSummaryText
    {
        get => _albumDetailSummaryText;
        set => SetProperty(ref _albumDetailSummaryText, value);
    }

    private string _albumDetailAudioBadge = string.Empty;
    public string AlbumDetailAudioBadge
    {
        get => _albumDetailAudioBadge;
        private set => SetProperty(ref _albumDetailAudioBadge, value);
    }

    private string _albumDetailFooterText = string.Empty;
    public string AlbumDetailFooterText
    {
        get => _albumDetailFooterText;
        private set => SetProperty(ref _albumDetailFooterText, value);
    }

    private Album? _artistLatestRelease;
    public Album? ArtistLatestRelease
    {
        get => _artistLatestRelease;
        private set => SetProperty(ref _artistLatestRelease, value);
    }

    private string _artistFormatsSummary = "--";
    public string ArtistFormatsSummary
    {
        get => _artistFormatsSummary;
        private set => SetProperty(ref _artistFormatsSummary, value);
    }

    private string _artistTotalRuntimeText = "--";
    public string ArtistTotalRuntimeText
    {
        get => _artistTotalRuntimeText;
        private set => SetProperty(ref _artistTotalRuntimeText, value);
    }

    private string _artistHiResSummaryText = "--";
    public string ArtistHiResSummaryText
    {
        get => _artistHiResSummaryText;
        private set => SetProperty(ref _artistHiResSummaryText, value);
    }

    private string _previousView = "Albums";
    public string PreviousView
    {
        get => _previousView;
        set => SetProperty(ref _previousView, value);
    }

    private string _selectedPlaylistSummaryText = string.Empty;
    public string SelectedPlaylistSummaryText
    {
        get => _selectedPlaylistSummaryText;
        set => SetProperty(ref _selectedPlaylistSummaryText, value);
    }

    public Playlist? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            if (SetProperty(ref _selectedPlaylist, value))
            {
                LoadSelectedPlaylistTracks();
                NotifyBreadcrumbChanged();
            }
        }
    }

    public string QueueSummaryText
    {
        get => _queueSummaryText;
        set => SetProperty(ref _queueSummaryText, value);
    }

    public int CurrentQueueIndex
    {
        get => _currentQueueIndex;
        set => SetProperty(ref _currentQueueIndex, value);
    }

    public bool IsQueueDrawerOpen
    {
        get => _isQueueDrawerOpen;
        set
        {
            if (SetProperty(ref _isQueueDrawerOpen, value))
            {
                if (value && Presentation != null && Presentation.ActiveRightPane != RightPaneMode.Queue)
                {
                    Presentation.ActiveRightPane = RightPaneMode.Queue;
                }
                else if (!value && Presentation != null && Presentation.ActiveRightPane == RightPaneMode.Queue)
                {
                    Presentation.ActiveRightPane = RightPaneMode.None;
                }
            }
        }
    }

    public int QueueCount => QueueTracks.Count;

    public Artist? SelectedArtist
    {
        get => _selectedArtist;
        set
        {
            if (SetProperty(ref _selectedArtist, value))
            {
                NotifyBreadcrumbChanged();
            }
        }
    }

    public bool IsArtistDetailView
    {
        get => _isArtistDetailView;
        set
        {
            if (SetProperty(ref _isArtistDetailView, value))
            {
                NotifyBreadcrumbChanged();
            }
        }
    }

    public string ArtistStatsSummary
    {
        get => _artistStatsSummary;
        set => SetProperty(ref _artistStatsSummary, value);
    }

    // Properti Akses UI
    public string CurrentView
    {
        get => _currentView;
        set
        {
            string normalized = string.Equals(value, "Library", StringComparison.OrdinalIgnoreCase)
                ? "Songs"
                : (string.IsNullOrWhiteSpace(value) ? "Home" : value);

            if (string.Equals(_currentView, normalized, StringComparison.Ordinal))
            {
                return;
            }

            string previous = _currentView;
            if (!_isHistoryNavigating && !string.IsNullOrWhiteSpace(previous))
            {
                _backStack.Push(CaptureNavigationEntry(previous));
                _forwardStack.Clear();
            }

            if (SetProperty(ref _currentView, normalized))
            {
                OnPropertyChanged(nameof(IsNowPlayingView));
                if (normalized != "Artists" && normalized != "ArtistDetail")
                {
                    IsArtistDetailView = false;
                }

                if (normalized == "Home")
                {
                    _ = Home?.RefreshAsync();
                }
                else if (normalized == "Albums" && DisplayedAlbums.Count == 0)
                {
                    LoadAlbums();
                }
                else if (normalized == "Artists")
                {
                    if (DisplayedArtists.Count == 0)
                    {
                        LoadArtists(_artistFilterQuery);
                    }
                    if (SelectedArtist == null && DisplayedArtists.Count > 0)
                    {
                        ViewArtistDetail(DisplayedArtists[0]);
                    }
                }
                else if (normalized == "Playlists" || normalized == "PlaylistDetail")
                {
                    if (SavedPlaylists.Count == 0)
                    {
                        LoadPlaylists();
                    }
                }
                else if (normalized == "Queue")
                {
                    RefreshQueue();
                    if (SavedPlaylists.Count == 0)
                    {
                        LoadPlaylists();
                    }
                }
                else if (normalized == "NowPlaying")
                {
                    RefreshCurrentTrackExtendedContext();
                }

                NotifyBreadcrumbChanged();
            }
        }
    }

    public string CurrentBreadcrumbRoot
    {
        get
        {
            bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
            return CurrentView switch
            {
                "Home" => isEn ? "Home" : "Beranda",
                "Songs" => isEn ? "Songs" : "Lagu",
                "Albums" => isEn ? "Albums" : "Album",
                "AlbumDetail" => PreviousView == "Artists"
                    ? (isEn ? "Artists" : "Artis")
                    : (isEn ? "Albums" : "Album"),
                "Artists" => isEn ? "Artists" : "Artis",
                "ArtistDetail" => isEn ? "Artists" : "Artis",
                "Playlists" => isEn ? "Playlists" : "Playlist",
                "PlaylistDetail" => isEn ? "Playlists" : "Playlist",
                "Search" => isEn ? "Search" : "Cari",
                "Inspector" => isEn ? "Audio Inspector" : "Inspektor Audio",
                "Settings" => isEn ? "Settings" : "Pengaturan",
                "NowPlaying" => isEn ? "Now Playing" : "Sedang Diputar",
                _ => isEn ? "Songs" : "Lagu"
            };
        }
    }

    public string CurrentBreadcrumbDetail
    {
        get
        {
            if (CurrentView == "AlbumDetail" && SelectedAlbumDetail != null)
                return SelectedAlbumDetail.Title;
            if ((CurrentView == "Artists" || CurrentView == "ArtistDetail") && SelectedArtist != null)
                return SelectedArtist.Name;
            if (CurrentView == "PlaylistDetail" && SelectedPlaylist != null)
                return SelectedPlaylist.Name;
            if (CurrentView == "Search" && Search != null && Search.HasQuery)
                return Search.Query;
            if (CurrentView == "NowPlaying" && CurrentTrack != null)
                return CurrentTrack.Title;
            return string.Empty;
        }
    }

    public bool HasBreadcrumbDetail => !string.IsNullOrEmpty(CurrentBreadcrumbDetail);

    public bool CanNavigateBack =>
        _backStack.Count > 0 ||
        CurrentView == "AlbumDetail" ||
        CurrentView == "ArtistDetail" ||
        CurrentView == "PlaylistDetail" ||
        CurrentView == "NowPlaying";

    public bool CanNavigateForward => _forwardStack.Count > 0;

    private void NotifyBreadcrumbChanged()
    {
        OnPropertyChanged(nameof(CurrentBreadcrumbRoot));
        OnPropertyChanged(nameof(CurrentBreadcrumbDetail));
        OnPropertyChanged(nameof(HasBreadcrumbDetail));
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ScheduleFilterData();
            }
        }
    }

    public string LibraryStatsText
    {
        get => _libraryStatsText;
        set => SetProperty(ref _libraryStatsText, value);
    }

    public string ActiveSortColumn
    {
        get => _activeSortColumn;
        set => SetProperty(ref _activeSortColumn, value);
    }

    public bool IsSortAscending
    {
        get => _isSortAscending;
        set => SetProperty(ref _isSortAscending, value);
    }

    public Track? CurrentTrack
    {
        get => _currentTrack;
        set
        {
            if (SetProperty(ref _currentTrack, value))
            {
                StreamSpecBadge = value?.StreamSpec.FormattedBadge ?? "--";
                OnPropertyChanged(nameof(LcdSpecBadge));
                OnPropertyChanged(nameof(HasCurrentTrack));
                OnPropertyChanged(nameof(IsInfoTargetCurrentTrack));
                ActivePlayingTrackId = value?.Id;
                RefreshCurrentTrackExtendedContext();
                if (CurrentView == "NowPlaying")
                {
                    NotifyBreadcrumbChanged();
                }
            }
        }
    }

    /// <summary>
    /// Badge spesifikasi ringkas untuk kapsul LCD bar atas (misal "24/48 FLAC" atau "DSD64")
    /// agar judul lagu tetap leluasa pada lebar jendela minimal (980px).
    /// </summary>
    public string LcdSpecBadge => FormatLcdSpecBadge(_currentTrack?.StreamSpec);

    private static string FormatLcdSpecBadge(AudioStreamSpec? spec)
    {
        if (spec == null) return "--";
        if (spec.IsDsd)
        {
            return spec.FormattedBadge.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        }

        double khz = Math.Round(spec.SampleRateHz / 1000.0, 1);
        string khzText = khz.ToString("0.#", CultureInfo.InvariantCulture);
        return $"{(int)spec.BitDepth}/{khzText} {spec.Codec}";
    }

    public long? ActivePlayingTrackId
    {
        get => _activePlayingTrackId;
        set => SetProperty(ref _activePlayingTrackId, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => SetProperty(ref _isPlaying, value);
    }

    public TimeSpan CurrentTime
    {
        get => _currentTime;
        set => SetProperty(ref _currentTime, value);
    }

    public TimeSpan TotalTime
    {
        get => _totalTime;
        set => SetProperty(ref _totalTime, value);
    }

    public double SeekSliderValue
    {
        get => _seekSliderValue;
        set => SetProperty(ref _seekSliderValue, value);
    }

    public double SeekSliderMaximum
    {
        get => _seekSliderMaximum;
        set => SetProperty(ref _seekSliderMaximum, value);
    }

    public string SignalPathBadge
    {
        get => _signalPathBadge;
        set => SetProperty(ref _signalPathBadge, value);
    }

    public SignalPathSummary? CurrentSignalPath
    {
        get => _currentSignalPath;
        set => SetProperty(ref _currentSignalPath, value);
    }

    public AudioIntegrityReport? CurrentIntegrityReport
    {
        get => _currentIntegrityReport;
        set
        {
            if (SetProperty(ref _currentIntegrityReport, value))
            {
                OnPropertyChanged(nameof(HasActiveIntegrityReport));
                OnPropertyChanged(nameof(IsInspectorEmpty));
            }
        }
    }

    public string StreamSpecBadge
    {
        get => _streamSpecBadge;
        set => SetProperty(ref _streamSpecBadge, value);
    }

    public string SidebarDriverTitle
    {
        get => _sidebarDriverTitle;
        set => SetProperty(ref _sidebarDriverTitle, value);
    }

    public string SidebarDriverSubtitle
    {
        get => _sidebarDriverSubtitle;
        set => SetProperty(ref _sidebarDriverSubtitle, value);
    }

    public bool IsExclusiveDriverActive
    {
        get => _isExclusiveDriverActive;
        set => SetProperty(ref _isExclusiveDriverActive, value);
    }

    public bool IsLibraryEmpty => DisplayedTracks.Count == 0;
    public bool IsLibraryZeroTracks => _totalTracksCount == 0;

    public string EmptyLibraryTitle =>
        IsLibraryZeroTracks
            ? (AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en"
                ? "No music in your library yet"
                : "Belum ada lagu di pustaka musik")
            : (AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en"
                ? "No matching tracks found"
                : "Tidak ada lagu yang cocok");

    public string EmptyLibraryDesc =>
        IsLibraryZeroTracks
            ? (AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en"
                ? "Add your music folder to index FLAC, WAV, ALAC, DSD, or MP3 files."
                : "Tambahkan folder musik Anda untuk memindai berkas FLAC, WAV, ALAC, DSD, atau MP3.")
            : (AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en"
                ? "Try clearing the search query or using a different keyword."
                : "Coba hapus kata kunci pencarian atau gunakan kata kunci lain.");

    public bool IsShuffleEnabled
    {
        get => _isShuffleEnabled;
        set
        {
            if (SetProperty(ref _isShuffleEnabled, value))
            {
                OnPropertyChanged(nameof(ShuffleToolTip));
            }
        }
    }

    public string ShuffleToolTip => IsShuffleEnabled
        ? (AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en" ? "Shuffle Mode Active (Fisher-Yates)" : "Mode Acak Aktif (Fisher-Yates)")
        : (AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en" ? "Shuffle Mode Off" : "Mode Acak Nonaktif");

    public PlaybackRepeatMode RepeatMode
    {
        get => _repeatMode;
        set
        {
            if (SetProperty(ref _repeatMode, value))
            {
                OnPropertyChanged(nameof(IsRepeatOne));
                OnPropertyChanged(nameof(IsRepeatActive));
                OnPropertyChanged(nameof(RepeatToolTip));
            }
        }
    }

    public bool IsRepeatOne => RepeatMode == PlaybackRepeatMode.RepeatOne;
    public bool IsRepeatActive => RepeatMode != PlaybackRepeatMode.Off;
    public string RepeatToolTip => RepeatMode switch
    {
        PlaybackRepeatMode.RepeatAll => AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en" ? "Repeat: All Tracks" : "Pengulangan: Semua Lagu",
        PlaybackRepeatMode.RepeatOne => AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en" ? "Repeat: Current Track" : "Pengulangan: Satu Lagu Ini",
        _ => AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en" ? "Repeat: Off" : "Pengulangan: Nonaktif"
    };

    private float _lastNonZeroVolume = 1.0f;
    private bool _isMuted = false;

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(IsSoundMuted));
            }
        }
    }

    public bool IsSoundMuted => IsMuted || Volume <= 0;

    public float Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
            {
                _controller.Volume = value;
                if (value > 0 && IsMuted) IsMuted = false;
                else if (value <= 0 && !IsMuted) IsMuted = true;
                OnPropertyChanged(nameof(IsSoundMuted));
            }
        }
    }

    public string OutputDeviceName
    {
        get => _outputDeviceName;
        set => SetProperty(ref _outputDeviceName, value);
    }

    // Track Selection State
    private Track? _selectedTrack;
    public Track? SelectedTrack
    {
        get => _selectedTrack;
        set => SetProperty(ref _selectedTrack, value);
    }

    // Authenticity Inspector State
    private Track? _inspectedTrack;
    private bool _isAnalyzing;
    private CancellationTokenSource? _analyzeCts;

    public Track? InspectedTrack
    {
        get => _inspectedTrack ?? CurrentTrack;
        set => SetProperty(ref _inspectedTrack, value);
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(HasAnalysisError));
                OnPropertyChanged(nameof(HasActiveIntegrityReport));
                OnPropertyChanged(nameof(IsInspectorEmpty));
            }
        }
    }

    public string AnalysisErrorMessage
    {
        get => _analysisErrorMessage;
        set
        {
            if (SetProperty(ref _analysisErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasAnalysisError));
                OnPropertyChanged(nameof(HasActiveIntegrityReport));
                OnPropertyChanged(nameof(IsInspectorEmpty));
            }
        }
    }

    public string PlaybackErrorMessage
    {
        get => _playbackErrorMessage;
        private set => SetProperty(ref _playbackErrorMessage, value);
    }

    public bool HasAnalysisError => !IsAnalyzing && !string.IsNullOrEmpty(_analysisErrorMessage);
    public bool HasActiveIntegrityReport => !IsAnalyzing && CurrentIntegrityReport != null && string.IsNullOrEmpty(_analysisErrorMessage);
    public bool IsInspectorEmpty => !IsAnalyzing && CurrentIntegrityReport == null && string.IsNullOrEmpty(_analysisErrorMessage);

    // Perintah Tombol (ICommand)
    public ICommand TogglePlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand NextTrackCommand { get; }
    public ICommand PreviousTrackCommand { get; }
    public ICommand ToggleShuffleCommand { get; }
    public ICommand ToggleRepeatCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand PlaySelectedTrackCommand { get; }
    public ICommand PlayAlbumCommand { get; }
    public ICommand ShuffleAlbumCommand { get; }
    public ICommand SortByCommand { get; }
    public ICommand PlayNextCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand OpenFileLocationCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public AudiophilePlaybackController Controller => _controller;
    public DatabaseManager Database => _db;
    public event Action? RequestOpenHardwareConfig;
    public event Action? RequestOpenLibraryManager;
    public event Action<Track>? RequestOpenAddToPlaylist;
    public event Action<string, string, Action<string>>? RequestOpenRenamePlaylist;

    public ICommand InspectTrackCommand { get; }
    public ICommand AnalyzeTrackCommand { get; }
    public ICommand OpenInspectFileDialogCommand { get; }
    public ICommand SelectArtistCommand { get; }
    public ICommand OpenHardwareConfigCommand { get; }
    public ICommand OpenLibraryManagerCommand { get; }
    public ICommand OpenArtistDetailCommand { get; }
    public ICommand BackToArtistsCommand { get; }
    public ICommand PlayArtistAllCommand { get; }
    public ICommand ShuffleArtistAllCommand { get; }

    public ICommand PlayQueueItemCommand { get; }
    public ICommand RemoveQueueItemCommand { get; }
    public ICommand ClearQueueCommand { get; }
    public ICommand MoveQueueItemUpCommand { get; }
    public ICommand MoveQueueItemDownCommand { get; }
    public ICommand SaveQueueAsPlaylistCommand { get; }
    public ICommand ExportQueueToM3uCommand { get; }
    public ICommand ImportM3uPlaylistCommand { get; }
    public ICommand PlaySavedPlaylistCommand { get; }
    public ICommand DeleteSavedPlaylistCommand { get; }
    public ICommand AddToPlaylistCommand { get; }
    public ICommand CreatePlaylistCommand { get; }
    public ICommand RenamePlaylistCommand { get; }
    public ICommand PlaySelectedPlaylistTrackCommand { get; }
    public ICommand ShuffleSavedPlaylistCommand { get; }
    public ICommand RemoveTrackFromCurrentPlaylistCommand { get; }
    public ICommand PlayAllTracksCommand { get; }
    public ICommand ShuffleAllTracksCommand { get; }

    public ICommand OpenAlbumDetailCommand { get; }
    public ICommand BackFromAlbumDetailCommand { get; }
    public ICommand PlayAlbumDetailTrackCommand { get; }
    public ICommand PlayCurrentAlbumDetailCommand { get; }
    public ICommand ShuffleCurrentAlbumDetailCommand { get; }

    public string CurrentLanguage => AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage.ToUpperInvariant();
    public ICommand ToggleLanguageCommand { get; }
    public ICommand ToggleQueueDrawerCommand { get; }
    public ICommand CloseQueueDrawerCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand NavigateBackCommand { get; }
    public ICommand NavigateForwardCommand { get; }
    public ICommand OpenPlaylistCommand { get; }
    public ICommand ToggleColumnBrowserCommand { get; }
    public ICommand OpenFullArtistPageCommand { get; }
    public ICommand PlayAlbumNextCommand { get; }
    public ICommand AddAlbumToQueueCommand { get; }
    public ICommand PlayArtistAlbumGroupCommand { get; }
    public ICommand PlayTrackInArtistContextCommand { get; }
    public ICommand RenameSelectedPlaylistDirectCommand { get; }
    public ICommand ExportSelectedPlaylistToM3uCommand { get; }
    public ICommand ToggleNowPlayingDetailCommand { get; }
    public ICommand OpenTrackDetailCommand { get; }
    public ICommand NavigateToArtistLinkCommand { get; }
    public ICommand NavigateToAlbumLinkCommand { get; }
    public ICommand PlayArtistTopTrackCommand { get; }
    public ICommand OpenInfoForTrackCommand { get; }
    public ICommand AddTargetTrackToPlaylistCommand { get; }
    public ICommand CreatePlaylistAndAddTargetTrackCommand { get; }
    public ICommand CloseAddToPlaylistSheetCommand { get; }
    public ICommand ConfirmRenameSheetCommand { get; }
    public ICommand CancelRenameSheetCommand { get; }
    public ICommand ClearPlayHistoryCommand { get; }
    public ICommand ImportLyricsFileCommand { get; }

    // Stage 5 Modal Sheets State (Get Info, Add to Playlist, Rename / New Playlist)
    private Track? _infoTargetTrack;
    private TrackExtendedDetails _infoTargetDetails = TrackExtendedDetails.Empty;
    private string _infoTargetLyricsSource = "No Lyrics";
    private string _infoTargetLyricsText = string.Empty;
    private string _infoTargetLyricsWriters = string.Empty;
    private Track? _addToPlaylistTargetTrack;
    private bool _isAddToPlaylistSheetOpen;
    private bool _isRenamePlaylistSheetOpen;
    private string _renameSheetTitle = "Rename Playlist";
    private string _renameSheetInputText = string.Empty;
    private Action<string>? _renameSheetCallback;

    public Track? InfoTargetTrack
    {
        get => _infoTargetTrack ?? CurrentTrack;
        private set
        {
            if (SetProperty(ref _infoTargetTrack, value))
            {
                OnPropertyChanged(nameof(IsInfoTargetCurrentTrack));
            }
        }
    }

    public bool IsInfoTargetCurrentTrack =>
        InfoTargetTrack != null &&
        CurrentTrack != null &&
        string.Equals(InfoTargetTrack.FilePath, CurrentTrack.FilePath, StringComparison.OrdinalIgnoreCase);

    public TrackExtendedDetails InfoTargetDetails
    {
        get => _infoTargetDetails;
        private set => SetProperty(ref _infoTargetDetails, value);
    }

    public string InfoTargetLyricsSource
    {
        get => _infoTargetLyricsSource;
        private set => SetProperty(ref _infoTargetLyricsSource, value);
    }

    public string InfoTargetLyricsText
    {
        get => _infoTargetLyricsText;
        private set
        {
            if (SetProperty(ref _infoTargetLyricsText, value))
            {
                OnPropertyChanged(nameof(InfoTargetHasLyrics));
            }
        }
    }

    public bool InfoTargetHasLyrics => !string.IsNullOrWhiteSpace(_infoTargetLyricsText);

    public string InfoTargetLyricsWriters
    {
        get => _infoTargetLyricsWriters;
        private set
        {
            if (SetProperty(ref _infoTargetLyricsWriters, value))
            {
                OnPropertyChanged(nameof(InfoTargetHasLyricsWriters));
            }
        }
    }

    public bool InfoTargetHasLyricsWriters => !string.IsNullOrWhiteSpace(_infoTargetLyricsWriters);

    public Track? AddToPlaylistTargetTrack
    {
        get => _addToPlaylistTargetTrack;
        private set => SetProperty(ref _addToPlaylistTargetTrack, value);
    }

    public bool IsAddToPlaylistSheetOpen
    {
        get => _isAddToPlaylistSheetOpen;
        set => SetProperty(ref _isAddToPlaylistSheetOpen, value);
    }

    public bool IsRenamePlaylistSheetOpen
    {
        get => _isRenamePlaylistSheetOpen;
        set => SetProperty(ref _isRenamePlaylistSheetOpen, value);
    }

    public string RenameSheetTitle
    {
        get => _renameSheetTitle;
        private set => SetProperty(ref _renameSheetTitle, value);
    }

    public string RenameSheetInputText
    {
        get => _renameSheetInputText;
        set => SetProperty(ref _renameSheetInputText, value);
    }

    public MainViewModel(AudiophilePlaybackController? controller = null, DatabaseManager? db = null)
    {
        _controller = controller ?? new AudiophilePlaybackController();

        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraAudio");
        Directory.CreateDirectory(appData);
        string dbPath = Path.Combine(appData, "library.db");

        _db = db ?? new DatabaseManager(dbPath);
        _db.Initialize();
        AuraAudio.UI.Helpers.CoverArtLoader.SetDatabase(_db);

        // Initialize v2.0.0 child ViewModels (03_design_spec.md Part D.6)
        Presentation = new PlayerPresentationViewModel();
        Home = new HomeViewModel(_db, this);
        Search = new SearchViewModel(_db, this);
        Lyrics = new LyricsViewModel(sec => OnSliderDragCompleted(sec));
        Settings = new SettingsViewModel(_controller, _db, RefreshLibraryAfterSettingsMutation, RefreshAudioAfterSettingsMutation);

        Presentation.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(PlayerPresentationViewModel.ActiveRightPane))
            {
                bool queueOpen = Presentation.IsQueuePaneActive;
                if (_isQueueDrawerOpen != queueOpen)
                {
                    _isQueueDrawerOpen = queueOpen;
                    OnPropertyChanged(nameof(IsQueueDrawerOpen));
                }
                if (queueOpen)
                {
                    RefreshQueue();
                    RefreshPlayHistory();
                }
            }
            else if (args.PropertyName == nameof(PlayerPresentationViewModel.MiniPlayerTrayMode) &&
                     Presentation.IsMiniPlayerQueueActive)
            {
                RefreshQueue();
                RefreshPlayHistory();
            }
            else if (args.PropertyName == nameof(PlayerPresentationViewModel.IsInfoSheetOpen))
            {
                if (Presentation.IsInfoSheetOpen)
                {
                    PrepareInfoSheetForTrack(_infoTargetTrack ?? CurrentTrack ?? SelectedTrack);
                }
                else
                {
                    _infoTargetTrack = null;
                }
            }
        };

        // Pulihkan preferensi bahasa aplikasi dari database
        var savedLang = _db.GetSetting("app_language");
        if (!string.IsNullOrEmpty(savedLang))
        {
            AuraAudio.UI.Helpers.LocalizationManager.SetLanguage(savedLang);
        }

        AuraAudio.UI.Helpers.LocalizationManager.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(CurrentLanguage));
            OnPropertyChanged(nameof(ShuffleToolTip));
            OnPropertyChanged(nameof(RepeatToolTip));
            OnPropertyChanged(nameof(EmptyLibraryTitle));
            OnPropertyChanged(nameof(EmptyLibraryDesc));
            OnPropertyChanged(nameof(CurrentIntegrityReport));
            RefreshSidebarDriverStatus();
            UpdateLibraryStatsText();
            RefreshQueue();
            LoadSelectedPlaylistTracks();
            RefreshCurrentTrackExtendedContext();
            NotifyBreadcrumbChanged();
            if (DisplayedAlbums.Count > 0) LoadAlbums();
            if (DisplayedArtists.Count > 0) LoadArtists(SearchQuery);
        };

        ToggleLanguageCommand = new RelayCommand(() =>
        {
            var next = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "id" ? "en" : "id";
            AuraAudio.UI.Helpers.LocalizationManager.SetLanguage(next);
            _db.SetSetting("app_language", next);
            OnPropertyChanged(nameof(CurrentLanguage));
        });

        ToggleQueueDrawerCommand = new RelayCommand(() =>
        {
            Presentation.ToggleQueuePaneCommand.Execute(null);
        });

        CloseQueueDrawerCommand = new RelayCommand(() =>
        {
            Presentation.CloseRightPaneCommand.Execute(null);
        });

        ClearSearchCommand = new RelayCommand(() =>
        {
            SearchQuery = string.Empty;
            Search.ClearSearchCommand.Execute(null);
        });

        NavigateBackCommand = new RelayCommand(() =>
        {
            if (_backStack.Count > 0)
            {
                var target = _backStack.Pop();
                _forwardStack.Push(CaptureNavigationEntry(_currentView));
                RestoreNavigationEntry(target);
                return;
            }

            if (CurrentView == "NowPlaying")
            {
                CurrentView = string.IsNullOrWhiteSpace(PreviousView) || PreviousView == "NowPlaying" ? "Home" : PreviousView;
            }
            else if (CurrentView == "AlbumDetail")
            {
                CurrentView = string.IsNullOrWhiteSpace(PreviousView) || PreviousView == "AlbumDetail" ? "Albums" : PreviousView;
            }
            else if (CurrentView == "ArtistDetail")
            {
                CurrentView = "Artists";
            }
            else if (CurrentView == "PlaylistDetail")
            {
                CurrentView = "Playlists";
            }
        });

        NavigateForwardCommand = new RelayCommand(() =>
        {
            if (_forwardStack.Count == 0) return;

            var target = _forwardStack.Pop();
            _backStack.Push(CaptureNavigationEntry(_currentView));
            RestoreNavigationEntry(target);
        });

        OpenPlaylistCommand = new RelayCommand(param =>
        {
            var playlist = (param as PlaylistOverviewItem)?.Playlist ?? param as Playlist;
            if (playlist != null)
            {
                if (!(_currentView == "PlaylistDetail" && SelectedPlaylist?.Id == playlist.Id))
                {
                    _backStack.Push(CaptureNavigationEntry(_currentView));
                    _forwardStack.Clear();
                }
                _isHistoryNavigating = true;
                try
                {
                    SelectedPlaylist = playlist;
                    CurrentView = "PlaylistDetail";
                }
                finally
                {
                    _isHistoryNavigating = false;
                }
                NotifyBreadcrumbChanged();
            }
        });

        ToggleColumnBrowserCommand = new RelayCommand(() =>
        {
            IsColumnBrowserVisible = !IsColumnBrowserVisible;
            if (!IsColumnBrowserVisible)
            {
                _selectedBrowserFormat = "All Formats";
                _selectedBrowserArtist = "All Artists";
                _selectedBrowserAlbum = "All Albums";
                OnPropertyChanged(nameof(SelectedBrowserFormat));
                OnPropertyChanged(nameof(SelectedBrowserArtist));
                OnPropertyChanged(nameof(SelectedBrowserAlbum));
                ApplyColumnBrowserFilter();
            }
        });

        OpenFullArtistPageCommand = new RelayCommand(param =>
        {
            var artist = param as Artist ?? SelectedArtist;
            if (artist != null)
            {
                OpenArtistDetailPage(artist);
            }
        });

        PlayAlbumNextCommand = new RelayCommand(param =>
        {
            if (param is Album album)
            {
                var albumTracks = _db.GetTracksByAlbum(album.Title, album.Artist);
                for (int i = albumTracks.Count - 1; i >= 0; i--)
                {
                    _controller.PlayNext(albumTracks[i]);
                }
                RefreshQueue();
            }
        });

        AddAlbumToQueueCommand = new RelayCommand(param =>
        {
            if (param is Album album)
            {
                var albumTracks = _db.GetTracksByAlbum(album.Title, album.Artist);
                foreach (var t in albumTracks)
                {
                    _controller.Enqueue(t);
                }
                RefreshQueue();
            }
        });

        PlayArtistAlbumGroupCommand = new RelayCommand(async param =>
        {
            if (param is ArtistAlbumGroup group && group.Tracks.Count > 0)
            {
                await _controller.PlayQueueAsync(group.Tracks, 0);
                RefreshQueue();
            }
        });

        PlayTrackInArtistContextCommand = new RelayCommand(async param =>
        {
            if (param is Track track)
            {
                int idx = ArtistTracks.IndexOf(track);
                if (idx >= 0 && ArtistTracks.Count > 0)
                {
                    await _controller.PlayQueueAsync(ArtistTracks, idx);
                }
                else
                {
                    await _controller.PlayQueueAsync(new[] { track }, 0);
                }
                RefreshQueue();
            }
        });

        RenameSelectedPlaylistDirectCommand = new RelayCommand(param =>
        {
            if (param is string newName && !string.IsNullOrWhiteSpace(newName) && SelectedPlaylist != null)
            {
                long id = SelectedPlaylist.Id;
                _db.RenamePlaylist(id, newName.Trim());
                LoadPlaylists();
                SelectedPlaylist = SavedPlaylists.FirstOrDefault(p => p.Id == id);
            }
        });

        ExportSelectedPlaylistToM3uCommand = new RelayCommand(() =>
        {
            if (SelectedPlaylist == null || SelectedPlaylistTracks.Count == 0) return;

            var dialog = new SaveFileDialog
            {
                Title = "Export Playlist to M3U8",
                Filter = "M3U8 Playlist (*.m3u8)|*.m3u8|M3U Playlist (*.m3u)|*.m3u",
                FileName = $"{SelectedPlaylist.Name}.m3u8"
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    M3uPlaylistService.ExportToM3u(dialog.FileName, SelectedPlaylistTracks);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ExportSelectedPlaylistToM3u Warning] {ex.Message}");
                }
            }
        });

        ToggleNowPlayingDetailCommand = new RelayCommand(() =>
        {
            if (CurrentView == "NowPlaying")
            {
                CurrentView = string.IsNullOrWhiteSpace(PreviousView) || PreviousView == "NowPlaying" ? "Library" : PreviousView;
            }
            else
            {
                if (CurrentTrack == null && DisplayedTracks.Count > 0)
                {
                    CurrentTrack = DisplayedTracks[0];
                }
                PreviousView = CurrentView;
                CurrentView = "NowPlaying";
            }
        });

        OpenTrackDetailCommand = new RelayCommand(param =>
        {
            if (param is Track target)
            {
                SelectedTrack = target;
                if (CurrentTrack == null)
                {
                    CurrentTrack = target;
                }
            }
            if (CurrentView != "NowPlaying")
            {
                PreviousView = CurrentView;
                CurrentView = "NowPlaying";
            }
            else
            {
                RefreshCurrentTrackExtendedContext();
            }
        });

        NavigateToArtistLinkCommand = new RelayCommand(param =>
        {
            if (param is Artist artistObj)
            {
                OpenArtistDetailPage(artistObj);
                return;
            }

            string? rawArtist = param switch
            {
                Track t => t.Artist,
                string s => s,
                _ => CurrentTrack?.Artist
            };

            if (string.IsNullOrWhiteSpace(rawArtist)) return;

            var parsed = ArtistParser.ParseArtists(rawArtist);
            string primaryName = parsed.Count > 0 ? parsed[0] : rawArtist.Trim();
            var matches = _db.GetArtists(primaryName);
            var exact = matches.FirstOrDefault(a => string.Equals(a.Name, primaryName, StringComparison.OrdinalIgnoreCase))
                        ?? matches.FirstOrDefault();

            if (exact == null)
            {
                var artistTracks = _db.GetTracksByArtist(primaryName);
                int albCount = artistTracks.Select(x => x.Album).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                string? coverKey = artistTracks.FirstOrDefault(x => !string.IsNullOrEmpty(x.CoverArtCacheKey))?.CoverArtCacheKey;
                exact = new Artist(primaryName, artistTracks.Count, Math.Max(1, albCount), coverKey);
            }

            OpenArtistDetailPage(exact);
        });

        NavigateToAlbumLinkCommand = new RelayCommand(param =>
        {
            if (param is Album albObj)
            {
                OpenAlbumDetail(albObj);
                return;
            }

            string? albumTitle = null;
            string? artistHint = null;
            if (param is Track t)
            {
                albumTitle = t.Album;
                artistHint = string.IsNullOrWhiteSpace(t.AlbumArtist) ? t.Artist : t.AlbumArtist;
            }
            else if (param is string s)
            {
                albumTitle = s;
                artistHint = CurrentTrack?.AlbumArtist ?? CurrentTrack?.Artist;
            }
            else if (CurrentTrack != null)
            {
                albumTitle = CurrentTrack.Album;
                artistHint = string.IsNullOrWhiteSpace(CurrentTrack.AlbumArtist) ? CurrentTrack.Artist : CurrentTrack.AlbumArtist;
            }

            if (string.IsNullOrWhiteSpace(albumTitle)) return;

            var album = _db.GetAlbumByTitle(albumTitle, artistHint);
            if (album != null)
            {
                OpenAlbumDetail(album);
            }
        });

        PlayArtistTopTrackCommand = new RelayCommand(async param =>
        {
            if (param is Track track)
            {
                int idx = CurrentTrackArtistTopTracks.IndexOf(track);
                if (idx >= 0 && CurrentTrackArtistTopTracks.Count > 0)
                {
                    await _controller.PlayQueueAsync(CurrentTrackArtistTopTracks, idx);
                }
                else
                {
                    await _controller.PlayQueueAsync(new[] { track }, 0);
                }
                RefreshQueue();
            }
        });

        // Pulihkan konfigurasi hardware dari sesi sebelumnya
        RestoreHardwareSettings();

        // Inisialisasi Perintah UI
        TogglePlayPauseCommand = new RelayCommand(async () =>
        {
            if (_controller.CurrentTrack == null)
            {
                if (DisplayedTracks.Count > 0)
                {
                    await _controller.PlayQueueAsync(DisplayedTracks, 0);
                }
            }
            else
            {
                _controller.TogglePlayPause();
            }
        });
        StopCommand = new RelayCommand(() => _controller.Stop());
        NextTrackCommand = new RelayCommand(async () => await _controller.NextAsync());
        PreviousTrackCommand = new RelayCommand(async () => await _controller.PreviousAsync());
        ToggleShuffleCommand = new RelayCommand(() =>
        {
            _controller.ToggleShuffle();
            RefreshQueue();
        });
        ToggleRepeatCommand = new RelayCommand(CycleRepeatMode);
        OpenHardwareConfigCommand = new RelayCommand(() =>
        {
            if (RequestOpenHardwareConfig != null)
            {
                RequestOpenHardwareConfig.Invoke();
            }
            else
            {
                Settings.ActiveCategory = "Playback";
                CurrentView = "Settings";
            }
        });
        OpenLibraryManagerCommand = new RelayCommand(() =>
        {
            if (RequestOpenLibraryManager != null)
            {
                RequestOpenLibraryManager.Invoke();
            }
            else
            {
                Settings.ActiveCategory = "Library";
                CurrentView = "Settings";
            }
        });
        NavigateCommand = new RelayCommand(param =>
        {
            if (param is string viewName)
            {
                if (viewName == "Hardware")
                {
                    OpenHardwareConfigCommand.Execute(null);
                    return;
                }
                if (viewName == "Queue")
                {
                    ToggleQueueDrawerCommand.Execute(null);
                    return;
                }
                if (viewName == "NowPlaying")
                {
                    if (CurrentTrack == null && DisplayedTracks.Count > 0)
                    {
                        CurrentTrack = DisplayedTracks[0];
                    }
                    if (CurrentView != "NowPlaying")
                    {
                        PreviousView = CurrentView;
                    }
                }
                CurrentView = viewName;
                if (viewName == "Inspector" && CurrentIntegrityReport == null && (InspectedTrack != null || CurrentTrack != null))
                {
                    _ = AnalyzeTrackAsync(InspectedTrack ?? CurrentTrack);
                }
            }
        });
        PlaySelectedTrackCommand = new RelayCommand(async param =>
        {
            var track = param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                int idx = DisplayedTracks.IndexOf(track);
                if (idx >= 0)
                    await _controller.PlayQueueAsync(DisplayedTracks, idx);
                else
                    await _controller.PlayQueueAsync(new[] { track }, 0);
                RefreshQueue();
            }
        });
        PlayAllTracksCommand = new RelayCommand(async () =>
        {
            if (DisplayedTracks.Count > 0)
            {
                await _controller.PlayQueueAsync(DisplayedTracks, 0);
                RefreshQueue();
            }
        });
        ShuffleAllTracksCommand = new RelayCommand(async () =>
        {
            if (DisplayedTracks.Count > 0)
            {
                _controller.SetShuffle(true);
                await _controller.PlayQueueAsync(DisplayedTracks, 0);
                RefreshQueue();
            }
        });
        PlayAlbumCommand = new RelayCommand(async param =>
        {
            if (param is Album album)
            {
                var albumTracks = _db.GetTracksByAlbum(album.Title, album.Artist);
                if (albumTracks.Count > 0)
                {
                    await _controller.PlayQueueAsync(albumTracks, 0);
                    RefreshQueue();
                }
            }
        });
        ShuffleAlbumCommand = new RelayCommand(async param =>
        {
            if (param is Album album)
            {
                var albumTracks = _db.GetTracksByAlbum(album.Title, album.Artist);
                if (albumTracks.Count > 0)
                {
                    _controller.SetShuffle(true);
                    await _controller.PlayQueueAsync(albumTracks, 0);
                    RefreshQueue();
                }
            }
        });
        SortByCommand = new RelayCommand(param =>
        {
            if (param is string colName) ApplySort(colName);
        });
        PlayNextCommand = new RelayCommand(param =>
        {
            var track = param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                _controller.PlayNext(track);
                RefreshQueue();
            }
        });
        AddToQueueCommand = new RelayCommand(param =>
        {
            var track = param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                _controller.Enqueue(track);
                RefreshQueue();
            }
        });
        OpenFileLocationCommand = new RelayCommand(param =>
        {
            var track = param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null && File.Exists(track.FilePath))
            {
                Process.Start("explorer.exe", $"/select,\"{track.FilePath}\"");
            }
        });
        ToggleMuteCommand = new RelayCommand(() =>
        {
            if (IsMuted)
            {
                Volume = _lastNonZeroVolume > 0 ? _lastNonZeroVolume : 1.0f;
                IsMuted = false;
            }
            else
            {
                _lastNonZeroVolume = Volume > 0 ? Volume : 1.0f;
                Volume = 0f;
                IsMuted = true;
            }
        });

        InspectTrackCommand = new RelayCommand(async param =>
        {
            var target = param as Track ?? SelectedTrack ?? CurrentTrack;
            if (target != null)
            {
                InspectedTrack = target;
                CurrentView = "Inspector";
                await AnalyzeTrackAsync(target);
            }
        });

        AnalyzeTrackCommand = new RelayCommand(async () =>
        {
            await AnalyzeTrackAsync(InspectedTrack ?? CurrentTrack);
        });

        OpenInspectFileDialogCommand = new RelayCommand(async () =>
        {
            bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
            var dlg = new OpenFileDialog
            {
                Title = isEn ? "Select Audio File to Inspect" : "Pilih Berkas Audio untuk Diperiksa",
                Filter = isEn
                    ? "All Audio Formats (*.flac;*.wav;*.alac;*.m4a;*.dsf;*.dff;*.mp3;*.aac;*.ogg;*.opus)|*.flac;*.wav;*.alac;*.m4a;*.dsf;*.dff;*.mp3;*.aac;*.ogg;*.opus|FLAC Lossless (*.flac)|*.flac|WAV / AIFF (*.wav;*.aiff)|*.wav;*.aiff|DSD Stream (*.dsf;*.dff)|*.dsf;*.dff|Lossy Audio (*.mp3;*.m4a;*.aac;*.ogg;*.opus)|*.mp3;*.m4a;*.aac;*.ogg;*.opus|All Files (*.*)|*.*"
                    : "Semua Format Audio (*.flac;*.wav;*.alac;*.m4a;*.dsf;*.dff;*.mp3;*.aac;*.ogg;*.opus)|*.flac;*.wav;*.alac;*.m4a;*.dsf;*.dff;*.mp3;*.aac;*.ogg;*.opus|FLAC Lossless (*.flac)|*.flac|WAV / AIFF (*.wav;*.aiff)|*.wav;*.aiff|DSD Stream (*.dsf;*.dff)|*.dsf;*.dff|Audio Lossy (*.mp3;*.m4a;*.aac;*.ogg;*.opus)|*.mp3;*.m4a;*.aac;*.ogg;*.opus|Semua Berkas (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var scanned = new AudioMetadataReader().ReadFile(dlg.FileName);
                    InspectedTrack = scanned.Track;
                    CurrentView = "Inspector";
                    await AnalyzeTrackAsync(scanned.Track);
                }
                catch (Exception ex)
                {
                    CurrentIntegrityReport = null;
                    CurrentView = "Inspector";
                    AnalysisErrorMessage = isEn
                        ? $"Failed to read audio file metadata: {ex.Message}"
                        : $"Gagal membaca metadata berkas audio: {ex.Message}";
                    Debug.WriteLine($"Gagal membaca metadata: {ex.Message}");
                }
            }
        });

        SelectArtistCommand = new RelayCommand(param =>
        {
            if (param is Artist artist)
            {
                ViewArtistDetail(artist);
            }
        });

        OpenArtistDetailCommand = new RelayCommand(param =>
        {
            if (param is Artist artist)
            {
                ViewArtistDetail(artist);
            }
        });

        BackToArtistsCommand = new RelayCommand(() =>
        {
            IsArtistDetailView = false;
            SelectedArtist = null;
        });

        PlayArtistAllCommand = new RelayCommand(async () =>
        {
            if (ArtistTracks.Count > 0)
            {
                await _controller.PlayQueueAsync(ArtistTracks, 0);
            }
        });

        ShuffleArtistAllCommand = new RelayCommand(async () =>
        {
            if (ArtistTracks.Count > 0)
            {
                _controller.SetShuffle(true);
                await _controller.PlayQueueAsync(ArtistTracks, 0);
            }
        });

        PlayQueueItemCommand = new RelayCommand(async param =>
        {
            var track = (param as QueueTrackItem)?.Track ?? param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                var playlist = _controller.Playlist;
                int idx = FindPlaylistTrackIndex(track);
                if (idx >= 0)
                {
                    await _controller.PlayQueueAsync(playlist, idx);
                    RefreshQueue();
                }
            }
        });

        RemoveQueueItemCommand = new RelayCommand(param =>
        {
            var track = (param as QueueTrackItem)?.Track ?? param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                int idx = FindPlaylistTrackIndex(track);
                if (idx >= 0)
                {
                    _controller.RemoveFromQueue(idx);
                    RefreshQueue();
                }
            }
        });

        ClearQueueCommand = new RelayCommand(() =>
        {
            _controller.Stop();
            _controller.QueueManager.Clear();
            RefreshQueue();
        });

        MoveQueueItemUpCommand = new RelayCommand(param =>
        {
            var track = (param as QueueTrackItem)?.Track ?? param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                int idx = FindPlaylistTrackIndex(track);
                if (idx > 0)
                {
                    _controller.MoveQueueItem(idx, idx - 1);
                    RefreshQueue();
                }
            }
        });

        MoveQueueItemDownCommand = new RelayCommand(param =>
        {
            var track = (param as QueueTrackItem)?.Track ?? param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                int idx = FindPlaylistTrackIndex(track);
                if (idx >= 0 && idx < _controller.Playlist.Count - 1)
                {
                    _controller.MoveQueueItem(idx, idx + 1);
                    RefreshQueue();
                }
            }
        });

        SaveQueueAsPlaylistCommand = new RelayCommand(() =>
        {
            if (_controller.Playlist.Count == 0)
            {
                MessageBox.Show("Antrean masih kosong. Tambahkan lagu terlebih dahulu.", "Simpan Playlist", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string defaultName = $"Playlist {DateTime.Now:dd MMM yyyy HH:mm}";
            try
            {
                var trackIds = _controller.Playlist.Select(t => t.Id);
                _db.CreatePlaylist(defaultName, trackIds);
                LoadPlaylists();
                MessageBox.Show($"Antrean berhasil disimpan sebagai playlist:\n\"{defaultName}\" ({_controller.Playlist.Count} lagu)", "Playlist Tersimpan", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Gagal menyimpan playlist: {ex.Message}", "Kesalahan", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        ExportQueueToM3uCommand = new RelayCommand(() =>
        {
            if (_controller.Playlist.Count == 0)
            {
                MessageBox.Show("Antrean kosong. Tidak ada lagu untuk diekspor.", "Ekspor M3U8", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Ekspor Antrean ke Playlist M3U8",
                Filter = "M3U8 Playlist (*.m3u8)|*.m3u8|M3U Playlist (*.m3u)|*.m3u",
                FileName = $"AuraAudio_Queue_{DateTime.Now:yyyyMMdd_HHmmss}.m3u8"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    M3uPlaylistService.ExportToM3u(dialog.FileName, _controller.Playlist);
                    MessageBox.Show($"Antrean berhasil diekspor ke:\n{dialog.FileName}", "Ekspor Selesai", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Gagal mengekspor playlist: {ex.Message}", "Kesalahan Ekspor", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        });

        OpenAlbumDetailCommand = new RelayCommand(param =>
        {
            if (param is Album album)
            {
                OpenAlbumDetail(album);
            }
        });

        BackFromAlbumDetailCommand = new RelayCommand(() =>
        {
            CurrentView = string.IsNullOrWhiteSpace(PreviousView) ? "Albums" : PreviousView;
        });

        PlayAlbumDetailTrackCommand = new RelayCommand(async param =>
        {
            var track = param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                int idx = SelectedAlbumDetailTracks.IndexOf(track);
                await _controller.PlayQueueAsync(SelectedAlbumDetailTracks, idx >= 0 ? idx : 0);
                RefreshQueue();
            }
        });

        PlayCurrentAlbumDetailCommand = new RelayCommand(async () =>
        {
            if (SelectedAlbumDetailTracks.Count > 0)
            {
                await _controller.PlayQueueAsync(SelectedAlbumDetailTracks, 0);
                RefreshQueue();
            }
        });

        ShuffleCurrentAlbumDetailCommand = new RelayCommand(async () =>
        {
            if (SelectedAlbumDetailTracks.Count > 0)
            {
                _controller.SetShuffle(true);
                await _controller.PlayQueueAsync(SelectedAlbumDetailTracks, 0);
                RefreshQueue();
            }
        });

        ImportM3uPlaylistCommand = new RelayCommand(async () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Pilih Berkas Playlist M3U atau M3U8",
                Filter = "Playlist Files (*.m3u;*.m3u8)|*.m3u;*.m3u8|All Files (*.*)|*.*",
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var importedTracks = M3uPlaylistService.ImportFromM3u(dialog.FileName, _db);
                    if (importedTracks.Count > 0)
                    {
                        await _controller.PlayQueueAsync(importedTracks, 0);
                        RefreshQueue();
                        MessageBox.Show($"Berhasil mengimpor {importedTracks.Count} lagu dari playlist:\n{Path.GetFileName(dialog.FileName)}", "Impor Berhasil", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("Tidak ada lagu yang dapat ditemukan atau diputar dari berkas playlist tersebut.", "Playlist Kosong", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Gagal mengimpor playlist: {ex.Message}", "Kesalahan Impor", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        });

        PlaySavedPlaylistCommand = new RelayCommand(async param =>
        {
            var playlist = (param as PlaylistOverviewItem)?.Playlist ?? param as Playlist ?? SelectedPlaylist;
            if (playlist != null)
            {
                var tracks = _db.GetTracksForPlaylist(playlist.Id);
                if (tracks.Count > 0)
                {
                    await _controller.PlayQueueAsync(tracks, 0);
                    RefreshQueue();
                }
            }
        });

        DeleteSavedPlaylistCommand = new RelayCommand(param =>
        {
            var playlist = (param as PlaylistOverviewItem)?.Playlist ?? param as Playlist ?? SelectedPlaylist;
            if (playlist != null)
            {
                var result = MessageBox.Show($"Delete playlist \"{playlist.Name}\"?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _db.DeletePlaylist(playlist.Id);
                    if (SelectedPlaylist?.Id == playlist.Id)
                    {
                        SelectedPlaylist = null;
                        if (CurrentView == "PlaylistDetail")
                        {
                            CurrentView = "Playlists";
                        }
                    }
                    LoadPlaylists();
                }
            }
        });

        OpenInfoForTrackCommand = new RelayCommand(param =>
        {
            // Explicit row/context-menu Track wins; otherwise globally inspect the currently playing track first!
            var target = (param as QueueTrackItem)?.Track ?? param as Track ?? CurrentTrack ?? SelectedTrack;
            PrepareInfoSheetForTrack(target);
            Presentation.ActiveInfoTab = "Details";
            Presentation.IsInfoSheetOpen = true;
        });

        AddToPlaylistCommand = new RelayCommand(param =>
        {
            var track = (param as QueueTrackItem)?.Track ?? param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                if (RequestOpenAddToPlaylist != null)
                {
                    RequestOpenAddToPlaylist.Invoke(track);
                }
                else
                {
                    LoadPlaylists();
                    AddToPlaylistTargetTrack = track;
                    IsAddToPlaylistSheetOpen = true;
                }
            }
        });

        AddTargetTrackToPlaylistCommand = new RelayCommand(param =>
        {
            var playlist = (param as PlaylistOverviewItem)?.Playlist ?? param as Playlist;
            if (playlist != null && AddToPlaylistTargetTrack != null)
            {
                AddTrackToPlaylistById(playlist.Id, AddToPlaylistTargetTrack);
                IsAddToPlaylistSheetOpen = false;
                AddToPlaylistTargetTrack = null;
            }
        });

        CreatePlaylistAndAddTargetTrackCommand = new RelayCommand(param =>
        {
            string name = (param as string)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) || AddToPlaylistTargetTrack == null) return;
            try
            {
                long newId = _db.CreatePlaylist(name, new[] { AddToPlaylistTargetTrack.Id });
                LoadPlaylists();
                SelectedPlaylist = SavedPlaylists.FirstOrDefault(p => p.Id == newId);
                IsAddToPlaylistSheetOpen = false;
                AddToPlaylistTargetTrack = null;
            }
            catch { }
        });

        CloseAddToPlaylistSheetCommand = new RelayCommand(() =>
        {
            IsAddToPlaylistSheetOpen = false;
            AddToPlaylistTargetTrack = null;
        });

        ConfirmRenameSheetCommand = new RelayCommand(() =>
        {
            string committed = RenameSheetInputText?.Trim() ?? string.Empty;
            var cb = _renameSheetCallback;
            IsRenamePlaylistSheetOpen = false;
            _renameSheetCallback = null;
            if (!string.IsNullOrWhiteSpace(committed))
            {
                cb?.Invoke(committed);
            }
        });

        CancelRenameSheetCommand = new RelayCommand(() =>
        {
            IsRenamePlaylistSheetOpen = false;
            _renameSheetCallback = null;
        });

        ClearPlayHistoryCommand = new RelayCommand(() =>
        {
            try
            {
                _db.ClearPlayHistory();
                _lastRecordedHistoryTrackId = -1;
                _lastRecordedHistoryUtc = DateTime.MinValue;
                PlayHistoryTracks.ReplaceAll(Array.Empty<Track>());
                _ = Home.RefreshAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ClearPlayHistory Warning] {ex.Message}");
            }
        });

        ImportLyricsFileCommand = new RelayCommand(async param =>
        {
            var target = (param as QueueTrackItem)?.Track
                         ?? param as Track
                         ?? (Presentation.IsInfoSheetOpen ? InfoTargetTrack : CurrentTrack)
                         ?? CurrentTrack
                         ?? SelectedTrack;
            if (target == null) return;

            bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
            var dlg = new OpenFileDialog
            {
                Title = isEn
                    ? $"Import Lyrics for \"{target.Title}\""
                    : $"Impor Lirik untuk \"{target.Title}\"",
                Filter = isEn
                    ? "Lyrics Files (*.lrc;*.txt)|*.lrc;*.txt|Synced LRC (*.lrc)|*.lrc|Plain Text (*.txt)|*.txt|All Files (*.*)|*.*"
                    : "Berkas Lirik (*.lrc;*.txt)|*.lrc;*.txt|Lirik Tersinkron (*.lrc)|*.lrc|Teks Biasa (*.txt)|*.txt|Semua Berkas (*.*)|*.*"
            };

            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                try
                {
                    await ImportLyricsFromPathAsync(target, dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        isEn ? $"Failed to import lyrics: {ex.Message}" : $"Gagal mengimpor lirik: {ex.Message}",
                        isEn ? "Import Lyrics Error" : "Kesalahan Impor Lirik",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
        });

        CreatePlaylistCommand = new RelayCommand(param =>
        {
            string name = (param as string)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                string sheetTitle = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en"
                    ? "New Playlist"
                    : "Buat Playlist Baru";
                OpenRenameModalSheet("New Playlist", sheetTitle, newName =>
                {
                    if (!string.IsNullOrWhiteSpace(newName))
                    {
                        CreatePlaylistInternal(newName);
                    }
                });
                return;
            }

            CreatePlaylistInternal(name);
        });

        RenamePlaylistCommand = new RelayCommand(param =>
        {
            var playlist = (param as PlaylistOverviewItem)?.Playlist ?? param as Playlist ?? SelectedPlaylist;
            if (playlist == null) return;

            string sheetTitle = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en"
                ? "Rename Playlist"
                : "Ubah Nama Playlist";
            OpenRenameModalSheet(playlist.Name, sheetTitle, newName =>
            {
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    _db.RenamePlaylist(playlist.Id, newName);
                    long currentId = playlist.Id;
                    LoadPlaylists();
                    SelectedPlaylist = SavedPlaylists.FirstOrDefault(p => p.Id == currentId);
                }
            });
        });

        PlaySelectedPlaylistTrackCommand = new RelayCommand(async param =>
        {
            if (param is Track track)
            {
                int idx = SelectedPlaylistTracks.IndexOf(track);
                if (idx >= 0)
                {
                    await _controller.PlayQueueAsync(SelectedPlaylistTracks, idx);
                    RefreshQueue();
                }
            }
        });

        ShuffleSavedPlaylistCommand = new RelayCommand(async param =>
        {
            var playlist = (param as PlaylistOverviewItem)?.Playlist ?? param as Playlist ?? SelectedPlaylist;
            if (playlist != null)
            {
                var tracks = _db.GetTracksForPlaylist(playlist.Id);
                if (tracks.Count > 0)
                {
                    _controller.SetShuffle(true);
                    await _controller.PlayQueueAsync(tracks, 0);
                    RefreshQueue();
                }
            }
        });

        RemoveTrackFromCurrentPlaylistCommand = new RelayCommand(param =>
        {
            if (param is Track track && SelectedPlaylist != null)
            {
                _db.RemoveTrackFromPlaylist(SelectedPlaylist.Id, track.Id);
                long currentId = SelectedPlaylist.Id;
                LoadSelectedPlaylistTracks();
                LoadPlaylists();
                SelectedPlaylist = SavedPlaylists.FirstOrDefault(p => p.Id == currentId);
            }
        });

        OutputDeviceName = _controller.CurrentDevice?.Name ?? "WASAPI Exclusive DAC";
        RefreshSidebarDriverStatus();
        _controller.PlaybackFailed += OnControllerPlaybackFailed;

        // Mulai consumer loop Channel State Snapshot (120 FPS tanpa alokasi berat)
        Task.Run(ConsumeStateChannelAsync);

        // Muat pustaka dari database
        LoadLibrary();
        LoadAlbums();
        LoadArtists();
        LoadPlaylists();
        RefreshPlayHistory();
        Search.RefreshBrowseBricks();
        _ = Home.RefreshAsync();

        // Pulihkan sesi pemutaran & antrean sebelumnya (Auto-restore)
        RestoreSession();
    }

    public void PrepareInfoSheetForTrack(Track? track)
    {
        var target = track ?? CurrentTrack;
        var previousInspectedPath = _inspectedTrack?.FilePath;

        if (target != null && System.IO.File.Exists(target.FilePath))
        {
            var resolved = AuraAudio.Audio.AudioSourceFormatResolver.ResolveFromFile(
                target.FilePath,
                decoderDuration: target.Duration > TimeSpan.Zero ? target.Duration : null);
            if (resolved.Spec != target.StreamSpec)
            {
                target = target with { StreamSpec = resolved.Spec };
                AudioMetadataReader.InvalidateExtendedDetailsCache(target.FilePath);
                if (target.Id > 0)
                {
                    try { _db.UpdateTrackStreamSpec(target.Id, resolved.Spec); } catch { }
                }
            }
        }

        InfoTargetTrack = target;
        OnPropertyChanged(nameof(IsInfoTargetCurrentTrack));
        InfoTargetDetails = target != null ? AudioMetadataReader.ReadExtendedDetails(target) : TrackExtendedDetails.Empty;
        if (target != null)
        {
            if (!string.Equals(previousInspectedPath, target.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                CurrentIntegrityReport = null;
                AnalysisErrorMessage = string.Empty;
            }
            InspectedTrack = target;

            var parsed = LyricsParser.LoadForFile(target.FilePath);
            ApplyInfoLyricsResult(parsed);
        }
        else
        {
            ApplyInfoLyricsResult(ParsedLyricsResult.Empty);
        }
    }

    public async Task<ParsedLyricsResult> ImportLyricsFromPathAsync(Track target, string sourceLyricsPath)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLyricsPath);

        var parsed = await Task.Run(() => LyricsParser.ImportSidecarLyrics(target.FilePath, sourceLyricsPath));

        if (InfoTargetTrack != null &&
            string.Equals(InfoTargetTrack.FilePath, target.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            ApplyInfoLyricsResult(parsed);
        }

        if (CurrentTrack != null &&
            string.Equals(CurrentTrack.FilePath, target.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            Lyrics.ApplyParsedResult(parsed);
            if (parsed.IsSynced)
            {
                Lyrics.UpdatePlaybackPosition(CurrentTime);
            }
        }

        return parsed;
    }

    private void ApplyInfoLyricsResult(ParsedLyricsResult parsed)
    {
        if (parsed != null && parsed.HasLyrics)
        {
            InfoTargetLyricsSource = parsed.Source switch
            {
                LyricsSourceKind.SidecarLrc => "Synced · Sidecar .lrc",
                LyricsSourceKind.EmbeddedLrc => "Synced · Embedded LRC",
                LyricsSourceKind.Id3v2Sylt => "Synced · ID3v2 SYLT",
                _ => "Unsynced Plain Text"
            };
            InfoTargetLyricsText = parsed.PlainText;
            InfoTargetLyricsWriters = !string.IsNullOrWhiteSpace(parsed.Writers) && parsed.Writers.Trim() != "—"
                ? $"Written by: {parsed.Writers.Trim()}"
                : string.Empty;
        }
        else
        {
            InfoTargetLyricsSource = "No Lyrics";
            InfoTargetLyricsText = string.Empty;
            InfoTargetLyricsWriters = string.Empty;
        }
    }

    private void OpenRenameModalSheet(string initialText, string title, Action<string> onConfirm)
    {
        if (RequestOpenRenamePlaylist != null)
        {
            RequestOpenRenamePlaylist.Invoke(initialText, title, onConfirm);
            return;
        }

        RenameSheetTitle = title;
        RenameSheetInputText = initialText;
        _renameSheetCallback = onConfirm;
        IsRenamePlaylistSheetOpen = true;
    }

    private void RefreshLibraryAfterSettingsMutation()
    {
        LoadLibrary();
        LoadAlbums();
        LoadArtists();
        LoadPlaylists();
        RefreshPlayHistory();
        Search.RefreshBrowseBricks();
        _ = Home.RefreshAsync();
    }

    private void RefreshAudioAfterSettingsMutation()
    {
        OutputDeviceName = _controller.CurrentDevice?.Name ?? "Default Endpoint";
        RefreshSidebarDriverStatus();
    }

    private void CycleRepeatMode()
    {
        var next = _repeatMode switch
        {
            PlaybackRepeatMode.Off => PlaybackRepeatMode.RepeatAll,
            PlaybackRepeatMode.RepeatAll => PlaybackRepeatMode.RepeatOne,
            _ => PlaybackRepeatMode.Off
        };
        _controller.SetRepeatMode(next);
    }

    public void OnSliderDragStarted() => _isUserDraggingSlider = true;

    public void OnSliderDragCompleted(double targetSeconds)
    {
        _isUserDraggingSlider = false;
        _controller.Seek(TimeSpan.FromSeconds(targetSeconds));
    }

    private async Task ConsumeStateChannelAsync()
    {
        var reader = _controller.StateReader;
        var token = _cts.Token;

        try
        {
            while (!token.IsCancellationRequested && await reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var snapshot))
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        ApplySnapshot(snapshot);
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void ApplySnapshot(PlaybackStateSnapshot s)
    {
        bool trackChanged = _currentTrack?.FilePath != s.CurrentTrack?.FilePath;
        CurrentTrack = s.CurrentTrack;
        IsPlaying = s.Status == PlaybackStatus.Playing;
        if (IsPlaying && !string.IsNullOrEmpty(_playbackErrorMessage))
        {
            PlaybackErrorMessage = string.Empty;
        }
        CurrentTime = s.CurrentTime;
        TotalTime = s.TotalTime;
        ActivePlayingTrackId = s.CurrentTrack?.Id;

        if (!_isUserDraggingSlider)
        {
            SeekSliderMaximum = Math.Max(1, s.TotalTime.TotalSeconds);
            SeekSliderValue = s.CurrentTime.TotalSeconds;
        }

        if (s.SignalPath != null)
        {
            SignalPathBadge = s.SignalPath.BadgeText;
            CurrentSignalPath = s.SignalPath;
        }

        if (s.IntegrityReport != null &&
            (InspectedTrack == null || string.Equals(InspectedTrack.FilePath, s.CurrentTrack?.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            CurrentIntegrityReport = s.IntegrityReport;
        }

        RefreshSidebarDriverStatus();

        if (CurrentIntegrityReport != null && InspectedTrack != null)
        {
            var (hwSpec, hasActivePath, isExclusive, driverType) = _controller.GetHardwareAuditContext(InspectedTrack.FilePath);
            var targetHwSpec = hwSpec ?? CurrentIntegrityReport.SourceSpec;
            if (CurrentIntegrityReport.HasActiveHardwarePath != hasActivePath ||
                CurrentIntegrityReport.IsHardwareExclusive != isExclusive ||
                CurrentIntegrityReport.ConfiguredDriverType != driverType ||
                (hasActivePath && CurrentIntegrityReport.HardwareSpec != targetHwSpec))
            {
                CurrentIntegrityReport = CurrentIntegrityReport with
                {
                    HardwareSpec = targetHwSpec,
                    HasActiveHardwarePath = hasActivePath,
                    IsHardwareExclusive = isExclusive,
                    ConfiguredDriverType = driverType
                };
            }
        }

        IsShuffleEnabled = s.IsShuffleEnabled;
        RepeatMode = s.RepeatMode;
        if (s.ActiveDevice != null)
        {
            OutputDeviceName = s.ActiveDevice.Name;
        }

        if (trackChanged && CurrentView == "Inspector" && s.CurrentTrack != null)
        {
            InspectedTrack = s.CurrentTrack;
            _ = AnalyzeTrackAsync(s.CurrentTrack);
        }

        if (trackChanged || QueueTracks.Count != _controller.Playlist.Count)
        {
            RefreshQueue();
        }

        // Record play history when active playback begins on a new track
        if (s.CurrentTrack != null && s.Status == PlaybackStatus.Playing)
        {
            var nowUtc = DateTime.UtcNow;
            if (s.CurrentTrack.Id != _lastRecordedHistoryTrackId || (nowUtc - _lastRecordedHistoryUtc).TotalSeconds >= 30.0)
            {
                _lastRecordedHistoryTrackId = s.CurrentTrack.Id;
                _lastRecordedHistoryUtc = nowUtc;
                long playedTrackId = s.CurrentTrack.Id;
                _ = Task.Run(() =>
                {
                    try
                    {
                        _db.RecordPlayHistory(playedTrackId);
                        Application.Current?.Dispatcher.BeginInvoke(RefreshPlayHistory);
                    }
                    catch { }
                });
            }
        }

        if (trackChanged)
        {
            _ = Lyrics.LoadForTrackAsync(s.CurrentTrack);
        }
        Lyrics.UpdatePlaybackPosition(s.CurrentTime);

        // Sinkronisasi status dan metadata ke Windows 11 SMTC
        if (trackChanged || _lastKnownStatus != s.Status)
        {
            _lastKnownStatus = s.Status;
            _mediaControlsManager?.UpdateTrackMetadata(s.CurrentTrack, s.Status);
        }
    }

    /// <summary>
    /// Menghubungkan Windows 11 System Media Transport Controls (SMTC) ke ViewModel.
    /// </summary>
    public void SetMediaControlsManager(AuraAudio.UI.Helpers.WindowsMediaControlsManager manager)
    {
        _mediaControlsManager = manager;
        if (_currentTrack != null)
        {
            _mediaControlsManager.UpdateTrackMetadata(_currentTrack, _isPlaying ? PlaybackStatus.Playing : PlaybackStatus.Paused);
        }
    }

    public void ApplySort(string column)
    {
        if (ActiveSortColumn == column)
        {
            IsSortAscending = !IsSortAscending;
        }
        else
        {
            ActiveSortColumn = column;
            IsSortAscending = true;
        }

        _currentSortOption = column switch
        {
            "Title" => IsSortAscending ? TrackSortOption.TitleAscending : TrackSortOption.TitleDescending,
            "Artist" => TrackSortOption.ArtistDiscography,
            "Album" => IsSortAscending ? TrackSortOption.AlbumAscending : TrackSortOption.AlbumDescending,
            "Duration" => IsSortAscending ? TrackSortOption.DurationAscending : TrackSortOption.DurationDescending,
            "Format" => IsSortAscending ? TrackSortOption.HiResFirst : TrackSortOption.FormatAscending,
            _ => TrackSortOption.TitleAscending
        };

        LoadLibrary();
    }

    public void LoadLibrary()
    {
        var tracks = _db.GetSortedTracks(_currentSortOption, limit: 10000, offset: 0, filter: _searchQuery);
        _allLoadedTracksCache = tracks;
        RebuildColumnBrowserOptions();
        ApplyColumnBrowserFilter();

        _totalTracksCount = _db.GetTrackCount();
        OnPropertyChanged(nameof(IsLibraryEmpty));
        OnPropertyChanged(nameof(IsLibraryZeroTracks));
        OnPropertyChanged(nameof(EmptyLibraryTitle));
        OnPropertyChanged(nameof(EmptyLibraryDesc));
        UpdateLibraryStatsText();
    }

    private void RebuildColumnBrowserOptions()
    {
        var formats = new List<string> { "All Formats" };
        if (_allLoadedTracksCache.Any(t => t.StreamSpec.IsHiRes))
        {
            formats.Add("Hi-Res Lossless");
        }
        formats.AddRange(
            _allLoadedTracksCache
                .Select(t => t.StreamSpec.Codec.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        ColumnBrowserFormats.ReplaceAll(formats);
        if (!formats.Any(x => string.Equals(x, _selectedBrowserFormat, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedBrowserFormat = "All Formats";
            OnPropertyChanged(nameof(SelectedBrowserFormat));
        }
        RebuildColumnBrowserArtistsAndAlbums();
    }

    private IEnumerable<Track> FilterTracksByFormat(IEnumerable<Track> source)
    {
        if (string.Equals(_selectedBrowserFormat, "All Formats", StringComparison.OrdinalIgnoreCase))
            return source;
        if (string.Equals(_selectedBrowserFormat, "Hi-Res Lossless", StringComparison.OrdinalIgnoreCase))
            return source.Where(t => t.StreamSpec.IsHiRes);
        return source.Where(t => string.Equals(t.StreamSpec.Codec.ToString(), _selectedBrowserFormat, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildColumnBrowserArtistsAndAlbums()
    {
        var byFormat = FilterTracksByFormat(_allLoadedTracksCache).ToList();
        var artists = new List<string> { "All Artists" };
        artists.AddRange(
            byFormat
                .Select(t => t.Artist)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
        ColumnBrowserArtists.ReplaceAll(artists);
        if (!artists.Any(x => string.Equals(x, _selectedBrowserArtist, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedBrowserArtist = "All Artists";
            OnPropertyChanged(nameof(SelectedBrowserArtist));
        }
        RebuildColumnBrowserAlbums();
    }

    private void RebuildColumnBrowserAlbums()
    {
        var query = FilterTracksByFormat(_allLoadedTracksCache);
        if (!string.Equals(_selectedBrowserArtist, "All Artists", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(t => string.Equals(t.Artist, _selectedBrowserArtist, StringComparison.OrdinalIgnoreCase));
        }

        var albums = new List<string> { "All Albums" };
        albums.AddRange(
            query
                .Select(t => t.Album)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
        ColumnBrowserAlbums.ReplaceAll(albums);
        if (!albums.Any(x => string.Equals(x, _selectedBrowserAlbum, StringComparison.OrdinalIgnoreCase)))
        {
            _selectedBrowserAlbum = "All Albums";
            OnPropertyChanged(nameof(SelectedBrowserAlbum));
        }
    }

    private void ApplyColumnBrowserFilter()
    {
        IEnumerable<Track> filtered = FilterTracksByFormat(_allLoadedTracksCache);
        if (!string.Equals(_selectedBrowserArtist, "All Artists", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(t => string.Equals(t.Artist, _selectedBrowserArtist, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.Equals(_selectedBrowserAlbum, "All Albums", StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(t => string.Equals(t.Album, _selectedBrowserAlbum, StringComparison.OrdinalIgnoreCase));
        }

        DisplayedTracks.ReplaceAll(filtered.ToList());
        OnPropertyChanged(nameof(IsLibraryEmpty));
    }

    /// <summary>
    /// Memperbarui metadata mendalam (Credits, Mastering Specs, Lyrics) serta kartu profil artis
    /// untuk panggung Now Playing Music Detail Stage (Apple Music Monochrome InfoPanelControl).
    /// </summary>
    public void RefreshCurrentTrackExtendedContext()
    {
        var track = _currentTrack;
        if (track == null)
        {
            CurrentTrackDetails = TrackExtendedDetails.Empty;
            CurrentTrackArtistName = "—";
            CurrentTrackArtistSummary = string.Empty;
            CurrentTrackArtistInitials = "♪";
            CurrentTrackArtistCoverKey = null;
            if (CurrentTrackArtistTopTracks.Count > 0)
            {
                CurrentTrackArtistTopTracks.ReplaceAll(Array.Empty<Track>());
            }
            return;
        }

        CurrentTrackDetails = AudioMetadataReader.ReadExtendedDetails(track);

        var parsedArtists = ArtistParser.ParseArtists(track.Artist);
        string primaryArtist = parsedArtists.Count > 0 ? parsedArtists[0] : track.Artist;
        CurrentTrackArtistName = primaryArtist;

        var parts = primaryArtist.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        CurrentTrackArtistInitials = parts.Length >= 2
            ? $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[1][0])}"
            : (primaryArtist.Length > 0 ? char.ToUpperInvariant(primaryArtist[0]).ToString() : "♪");

        try
        {
            var artistTracks = _db.GetTracksByArtist(primaryArtist);
            int albumCount = artistTracks.Select(t => t.Album).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            bool hasHiRes = artistTracks.Any(t => t.StreamSpec.IsHiRes);
            CurrentTrackArtistCoverKey = artistTracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.CoverArtCacheKey))?.CoverArtCacheKey
                                         ?? track.CoverArtCacheKey;

            bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
            string hiResTag = hasHiRes ? (isEn ? " • Hi-Res Lossless" : " • Hi-Res Lossless") : string.Empty;
            CurrentTrackArtistSummary = isEn
                ? $"{Math.Max(1, albumCount)} {(albumCount == 1 ? "Album" : "Albums")} • {Math.Max(1, artistTracks.Count)} {(artistTracks.Count == 1 ? "Track" : "Tracks")} in Library{hiResTag}"
                : $"{Math.Max(1, albumCount)} Album • {Math.Max(1, artistTracks.Count)} Lagu di Pustaka{hiResTag}";

            var topTracks = artistTracks.Take(6).ToList();
            CurrentTrackArtistTopTracks.ReplaceAll(topTracks);
        }
        catch
        {
            CurrentTrackArtistCoverKey = track.CoverArtCacheKey;
            CurrentTrackArtistSummary = track.Album;
            CurrentTrackArtistTopTracks.ReplaceAll(new[] { track });
        }
    }

    public void RefreshSidebarDriverStatus()
    {
        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
        bool isShared = _controller.DriverType == AudioDriverType.WASAPI_Shared ||
                        (_controller.Status != PlaybackStatus.Stopped && !_controller.IsExclusiveOutputActive);

        if (isShared)
        {
            SidebarDriverTitle = "WASAPI Shared";
            SidebarDriverSubtitle = isEn ? "Windows Audio Mixer" : "Lewat Mixer Windows";
            IsExclusiveDriverActive = false;
        }
        else
        {
            SidebarDriverTitle = "WASAPI Exclusive";
            SidebarDriverSubtitle = isEn ? "Bit-Perfect Direct Out" : "Jalur Bit-Perfect Langsung";
            IsExclusiveDriverActive = true;
        }
    }

    public void RefreshPlaylistsAfterMutation()
    {
        LoadPlaylists();
        if (SelectedPlaylist != null)
        {
            LoadSelectedPlaylistTracks();
        }
    }

    private void CreatePlaylistInternal(string name)
    {
        try
        {
            long newId = _db.CreatePlaylist(name, Array.Empty<long>());
            LoadPlaylists();
            SelectedPlaylist = SavedPlaylists.FirstOrDefault(p => p.Id == newId);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Gagal membuat playlist: {ex.Message}", "Kesalahan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateLibraryStatsText()
    {
        string suffix = AuraAudio.UI.Helpers.LocalizationManager.GetString("Str_Top_TracksIndexedSuffix", "Lagu Terindeks • WASAPI Exclusive Siap");
        LibraryStatsText = $"{_totalTracksCount} {suffix}";
    }

    public void LoadAlbums()
    {
        IEnumerable<Album> albums = _albumSortMode == "RecentlyAdded"
            ? _db.GetRecentlyAddedAlbums(500)
            : _db.GetAlbums(string.IsNullOrWhiteSpace(_albumFilterQuery) ? null : _albumFilterQuery);

        if (_albumSortMode == "RecentlyAdded" && !string.IsNullOrWhiteSpace(_albumFilterQuery))
        {
            string q = _albumFilterQuery.Trim();
            albums = albums.Where(a =>
                a.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Artist.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        albums = _albumSortMode switch
        {
            "Artist" => albums.OrderBy(a => a.Artist, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase),
            "Year" => albums.OrderByDescending(a => a.Year ?? 0).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase),
            "HiResFirst" => albums.OrderByDescending(a => a.IsHiRes).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase),
            "RecentlyAdded" => albums,
            _ => albums.OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
        };

        DisplayedAlbums.ReplaceAll(albums.ToList());
    }

    public void LoadArtists(string? filter = null)
    {
        var artists = _db.GetArtists(filter);
        DisplayedArtists.ReplaceAll(artists);
    }

    /// <summary>
    /// Memuat diskografi lengkap artis (seluruh album dan lagu dari artis tersebut).
    /// Mengelompokkan trek berdasarkan album untuk tampilan Apple Music style.
    /// </summary>
    public void ViewArtistDetail(Artist artist)
    {
        ArgumentNullException.ThrowIfNull(artist);

        SelectedArtist = artist;
        IsArtistDetailView = true;

        var albums = _db.GetAlbumsByArtist(artist.Name);
        ArtistAlbums.ReplaceAll(albums);

        var tracks = _db.GetTracksByArtist(artist.Name);
        ArtistTracks.ReplaceAll(tracks);
        long totalDurMs = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            totalDurMs += (long)tracks[i].Duration.TotalMilliseconds;
        }

        // Kelompokkan trek per album untuk gaya Apple Music
        var groups = new List<ArtistAlbumGroup>();
        var albumTrackMap = new Dictionary<string, List<Track>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            string albKey = string.IsNullOrWhiteSpace(t.Album) ? "Single & Lainnya" : t.Album;
            if (!albumTrackMap.TryGetValue(albKey, out var list))
            {
                list = new List<Track>();
                albumTrackMap[albKey] = list;
            }
            list.Add(t);
        }

        var matchedAlbumTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alb in albums)
        {
            if (albumTrackMap.TryGetValue(alb.Title, out var matchedTracks) && matchedTracks.Count > 0)
            {
                matchedAlbumTitles.Add(alb.Title);
                groups.Add(new ArtistAlbumGroup(alb, matchedTracks));
            }
            else
            {
                var dbAlbumTracks = _db.GetTracksByAlbum(alb.Title, alb.Artist);
                if (dbAlbumTracks.Count > 0)
                {
                    matchedAlbumTitles.Add(alb.Title);
                    groups.Add(new ArtistAlbumGroup(alb, dbAlbumTracks));
                }
            }
        }

        foreach (var kvp in albumTrackMap)
        {
            if (!matchedAlbumTitles.Contains(kvp.Key))
            {
                var sampleTrack = kvp.Value.First();
                var fallbackAlbum = new Album(
                    id: sampleTrack.Id,
                    title: kvp.Key,
                    artist: artist.Name,
                    year: kvp.Value.FirstOrDefault(x => x.Year.HasValue && x.Year.Value > 0)?.Year,
                    trackCount: kvp.Value.Count,
                    totalDuration: TimeSpan.FromMilliseconds(kvp.Value.Sum(x => (long)x.Duration.TotalMilliseconds)),
                    isHiRes: kvp.Value.Any(x => x.StreamSpec.IsHiRes),
                    coverArtCacheKey: kvp.Value.FirstOrDefault(x => !string.IsNullOrEmpty(x.CoverArtCacheKey))?.CoverArtCacheKey
                );
                groups.Add(new ArtistAlbumGroup(fallbackAlbum, kvp.Value));
            }
        }
        ArtistAlbumGroups.ReplaceAll(groups);
        ArtistFeaturedTracks.ReplaceAll(tracks.Take(8).ToList());
        ArtistLatestRelease = albums
            .OrderByDescending(a => a.Year ?? 0)
            .ThenByDescending(a => a.TrackCount)
            .FirstOrDefault() ?? groups.FirstOrDefault()?.Album;

        var totalSpan = TimeSpan.FromMilliseconds(totalDurMs);
        ArtistTotalRuntimeText = totalSpan.TotalHours >= 1
            ? $"{(int)totalSpan.TotalHours} hr {totalSpan.Minutes} min"
            : $"{Math.Max(1, (int)Math.Round(totalSpan.TotalMinutes))} min";

        var distinctCodecs = tracks
            .Select(t => t.StreamSpec.Codec.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ArtistFormatsSummary = distinctCodecs.Count > 0 ? string.Join(", ", distinctCodecs) : "--";

        int hiResCount = tracks.Count(t => t.StreamSpec.IsHiRes);
        ArtistHiResSummaryText = hiResCount > 0
            ? $"{hiResCount} of {tracks.Count} tracks Hi-Res"
            : "Standard Lossless / PCM";

        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
        ArtistStatsSummary = isEn
            ? $"{albums.Count} {(albums.Count == 1 ? "Album" : "Albums")} • {tracks.Count} {(tracks.Count == 1 ? "Track" : "Tracks")} • {(int)totalSpan.TotalMinutes} min"
            : $"{albums.Count} Album • {tracks.Count} Lagu • {(int)totalSpan.TotalMinutes} Menit";
    }

    private NavigationEntry CaptureNavigationEntry(string route) =>
        new(route, SelectedAlbumDetail, SelectedArtist, SelectedPlaylist);

    private void RestoreNavigationEntry(NavigationEntry entry)
    {
        _isHistoryNavigating = true;
        try
        {
            if (entry.Route == "AlbumDetail" && entry.Album != null)
            {
                LoadAlbumDetailData(entry.Album);
            }
            else if ((entry.Route == "ArtistDetail" || entry.Route == "Artists") && entry.Artist != null)
            {
                ViewArtistDetail(entry.Artist);
            }
            else if (entry.Route == "PlaylistDetail" && entry.Playlist != null)
            {
                SelectedPlaylist = entry.Playlist;
            }

            CurrentView = entry.Route;
        }
        finally
        {
            _isHistoryNavigating = false;
        }
        NotifyBreadcrumbChanged();
    }

    public void OpenArtistDetailPage(Artist artist)
    {
        ArgumentNullException.ThrowIfNull(artist);

        if (!(_currentView == "ArtistDetail" && SelectedArtist != null &&
              string.Equals(SelectedArtist.Name, artist.Name, StringComparison.OrdinalIgnoreCase)))
        {
            _backStack.Push(CaptureNavigationEntry(_currentView));
            _forwardStack.Clear();
        }

        _isHistoryNavigating = true;
        try
        {
            if (CurrentView != "ArtistDetail")
            {
                PreviousView = CurrentView;
            }
            ViewArtistDetail(artist);
            CurrentView = "ArtistDetail";
        }
        finally
        {
            _isHistoryNavigating = false;
        }
        NotifyBreadcrumbChanged();
    }

    private void LoadAlbumDetailData(Album album)
    {
        SelectedAlbumDetail = album;

        var tracks = _db.GetTracksByAlbum(album.Title, album.Artist);
        SelectedAlbumDetailTracks.ReplaceAll(tracks);
        long totalDurMs = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            totalDurMs += (long)tracks[i].Duration.TotalMilliseconds;
        }

        var totalSpan = TimeSpan.FromMilliseconds(totalDurMs);
        string yearText = album.Year.HasValue && album.Year.Value > 0 ? $"{album.Year.Value} • " : string.Empty;
        string runtimeText = totalSpan.TotalHours >= 1
            ? $"{(int)totalSpan.TotalHours} hr {totalSpan.Minutes} min"
            : $"{Math.Max(1, (int)Math.Round(totalSpan.TotalMinutes))} min";

        var primarySpec = tracks
            .OrderByDescending(t => t.StreamSpec.SampleRateHz)
            .ThenByDescending(t => (int)t.StreamSpec.BitDepth)
            .FirstOrDefault()?.StreamSpec;

        AlbumDetailAudioBadge = primarySpec?.FormattedBadge ?? string.Empty;
        AlbumDetailSummaryText = $"{yearText}{tracks.Count} {(tracks.Count == 1 ? "song" : "songs")}, {runtimeText}";
        AlbumDetailFooterText = !string.IsNullOrEmpty(AlbumDetailAudioBadge)
            ? $"{tracks.Count} {(tracks.Count == 1 ? "song" : "songs")}, {runtimeText} • {AlbumDetailAudioBadge}"
            : $"{tracks.Count} {(tracks.Count == 1 ? "song" : "songs")}, {runtimeText}";
    }

    /// <summary>
    /// Membuka tampilan detail album khusus yang menyajikan daftar lagu individual dari album terpilih.
    /// </summary>
    public void OpenAlbumDetail(Album album)
    {
        ArgumentNullException.ThrowIfNull(album);

        if (!(_currentView == "AlbumDetail" && SelectedAlbumDetail != null &&
              string.Equals(SelectedAlbumDetail.Title, album.Title, StringComparison.OrdinalIgnoreCase) &&
              string.Equals(SelectedAlbumDetail.Artist, album.Artist, StringComparison.OrdinalIgnoreCase)))
        {
            _backStack.Push(CaptureNavigationEntry(_currentView));
            _forwardStack.Clear();
        }

        _isHistoryNavigating = true;
        try
        {
            if (CurrentView != "AlbumDetail")
            {
                PreviousView = CurrentView;
            }
            LoadAlbumDetailData(album);
            CurrentView = "AlbumDetail";
        }
        finally
        {
            _isHistoryNavigating = false;
        }
        NotifyBreadcrumbChanged();
    }

    private void RestoreHardwareSettings()
    {
        try
        {
            var savedDevId = _db.GetSetting("audio_device_id");
            if (!string.IsNullOrEmpty(savedDevId))
            {
                var match = _controller.DeviceEnumerator.GetActiveRenderDevices().FirstOrDefault(d => d.Id == savedDevId);
                if (match != null) _controller.SetDevice(match);
            }

            var savedDriver = _db.GetSetting("audio_driver_type");
            if (!string.IsNullOrEmpty(savedDriver) && Enum.TryParse<AudioDriverType>(savedDriver, out var driverType))
            {
                _controller.SetDriverType(driverType);
            }

            var savedLatency = _db.GetSetting("latency_ms");
            if (!string.IsNullOrEmpty(savedLatency) && int.TryParse(savedLatency, out var latencyMs))
            {
                _controller.SetLatency(latencyMs);
            }

            var savedDsd = _db.GetSetting("dsd_mode");
            if (!string.IsNullOrEmpty(savedDsd) && Enum.TryParse<DsdPlaybackMode>(savedDsd, out var dsdMode))
            {
                _controller.SetDsdMode(dsdMode);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Hardware Settings Restore Warning] {ex.Message}");
        }
    }

    private void ScheduleFilterData()
    {
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = null;

        // Eksekusi langsung jika kata kunci dikosongkan atau berjalan di lingkungan unit test tanpa Dispatcher aktif
        if (string.IsNullOrWhiteSpace(_searchQuery) || Application.Current == null)
        {
            FilterData();
            return;
        }

        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(65, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        FilterData();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, token);
    }

    private void FilterData()
    {
        if (CurrentView == "Albums")
        {
            var albums = _db.GetAlbums(SearchQuery);
            DisplayedAlbums.ReplaceAll(albums);
        }
        else if (CurrentView == "Artists")
        {
            LoadArtists(SearchQuery);
        }
        else
        {
            var filtered = _db.GetSortedTracks(_currentSortOption, limit: 1000, offset: 0, filter: SearchQuery);
            DisplayedTracks.ReplaceAll(filtered);
            OnPropertyChanged(nameof(IsLibraryEmpty));
            OnPropertyChanged(nameof(IsLibraryZeroTracks));
            OnPropertyChanged(nameof(EmptyLibraryTitle));
            OnPropertyChanged(nameof(EmptyLibraryDesc));
        }
    }

    public async Task AnalyzeTrackAsync(Track? track)
    {
        var target = track ?? InspectedTrack ?? CurrentTrack;
        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
        if (target == null)
        {
            AnalysisErrorMessage = string.Empty;
            return;
        }
        if (!File.Exists(target.FilePath))
        {
            CurrentIntegrityReport = null;
            AnalysisErrorMessage = isEn
                ? $"Audio file could not be found: {target.FilePath}"
                : $"Berkas audio tidak ditemukan di penyimpanan: {target.FilePath}";
            return;
        }

        // Batalkan analisis sebelumnya jika pengguna berpindah lagu dengan cepat
        _analyzeCts?.Cancel();
        _analyzeCts?.Dispose();
        _analyzeCts = new CancellationTokenSource();
        var ct = _analyzeCts.Token;

        InspectedTrack = target;
        AnalysisErrorMessage = string.Empty;
        IsAnalyzing = true;
        try
        {
            var (hwSpec, hasActivePath, isExclusive, driverType) = _controller.GetHardwareAuditContext(target.FilePath);
            var report = await NyquistSpectrumAnalyzer.AnalyzeAsync(
                target.FilePath,
                hwSpec,
                hasActivePath,
                isExclusive,
                driverType,
                cancellationToken: ct);
            if (!ct.IsCancellationRequested)
            {
                if (report.SourceSpec != target.StreamSpec)
                {
                    var updated = target with { StreamSpec = report.SourceSpec };
                    InspectedTrack = updated;
                    AudioMetadataReader.InvalidateExtendedDetailsCache(updated.FilePath);
                    if (updated.Id > 0)
                    {
                        try { _db.UpdateTrackStreamSpec(updated.Id, report.SourceSpec); } catch { }
                    }
                }
                CurrentIntegrityReport = report;
            }
        }
        catch (OperationCanceledException)
        {
            // Abaikan pembatalan normal saat berganti lagu
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                CurrentIntegrityReport = null;
                AnalysisErrorMessage = isEn
                    ? $"Unable to analyze this audio file: {ex.Message}"
                    : $"Gagal menganalisis berkas audio ini: {ex.Message}";
            }
            Debug.WriteLine($"Kesalahan analisis Nyquist FFT: {ex.Message}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsAnalyzing = false;
            }
        }
    }

    /// <summary>
    /// Mencari indeks trek di dalam antrean aktif pemutar audio berdasarkan ID atau referensi objek.
    /// </summary>
    public int FindPlaylistTrackIndex(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var playlist = _controller.Playlist;
        for (int i = 0; i < playlist.Count; i++)
        {
            if (playlist[i].Id == track.Id || ReferenceEquals(playlist[i], track))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Memperbarui koleksi QueueTracks agar sinkron dengan antrean pemutar audio saat ini.
    /// </summary>
    public void RefreshQueue()
    {
        var items = _controller.Playlist;
        var built = new List<QueueTrackItem>(items.Count);
        long totalDurMs = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var t = items[i];
            bool isCurrent = _controller.CurrentTrack != null
                ? string.Equals(t.FilePath, _controller.CurrentTrack.FilePath, StringComparison.OrdinalIgnoreCase)
                : (i == _controller.CurrentIndex);
            built.Add(new QueueTrackItem(i + 1, t, isCurrent));
            totalDurMs += (long)t.Duration.TotalMilliseconds;
        }
        QueueTracks.ReplaceAll(built);

        CurrentQueueIndex = _controller.CurrentIndex;
        OnPropertyChanged(nameof(QueueCount));
        var span = TimeSpan.FromMilliseconds(totalDurMs);
        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
        QueueSummaryText = items.Count > 0
            ? (isEn
                ? $"{items.Count} {(items.Count == 1 ? "Track" : "Tracks")} • {(int)span.TotalMinutes} min"
                : $"{items.Count} Lagu • {(int)span.TotalMinutes} Menit")
            : (isEn
                ? "Queue is empty"
                : "Antrean kosong");
    }

    /// <summary>
    /// Refreshes recently played history from SQLite play_history table.
    /// </summary>
    public void RefreshPlayHistory()
    {
        try
        {
            var recent = _db.GetRecentPlayHistory(50);
            PlayHistoryTracks.ReplaceAll(recent);
        }
        catch { }
    }

    /// <summary>
    /// Adds a track directly to a target playlist (used by sidebar playlist drop targets).
    /// </summary>
    public void AddTrackToPlaylistById(long playlistId, Track track)
    {
        if (playlistId <= 0 || track == null || track.Id <= 0) return;
        try
        {
            _db.AddTrackToPlaylist(playlistId, track.Id);
            RefreshPlaylistsAfterMutation();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AddTrackToPlaylistById Warning] {ex.Message}");
        }
    }

    /// <summary>
    /// Adds multiple tracks directly to a target playlist (multi-select drag-drop).
    /// </summary>
    public void AddTracksToPlaylistById(long playlistId, IEnumerable<Track> tracks)
    {
        if (playlistId <= 0 || tracks == null) return;
        try
        {
            bool changed = false;
            foreach (var track in tracks)
            {
                if (track != null && track.Id > 0)
                {
                    _db.AddTrackToPlaylist(playlistId, track.Id);
                    changed = true;
                }
            }
            if (changed) RefreshPlaylistsAfterMutation();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AddTracksToPlaylistById Warning] {ex.Message}");
        }
    }

    /// <summary>
    /// Adds all tracks of an album directly to a target playlist (album tile drag-drop).
    /// </summary>
    public void AddAlbumToPlaylistById(long playlistId, Album album)
    {
        if (playlistId <= 0 || album == null) return;
        var albumTracks = _db.GetTracksByAlbum(album.Title, album.Artist);
        AddTracksToPlaylistById(playlistId, albumTracks);
    }

    /// <summary>
    /// Memuat seluruh playlist tersimpan dari database beserta cover art asli untuk overview grid.
    /// </summary>
    public void LoadPlaylists()
    {
        var list = _db.GetPlaylists();
        SavedPlaylists.ReplaceAll(list);

        var cards = new List<PlaylistOverviewItem>(list.Count);
        foreach (var pl in list)
        {
            string? coverKey = null;
            try
            {
                var plTracks = _db.GetTracksForPlaylist(pl.Id);
                coverKey = plTracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.CoverArtCacheKey))?.CoverArtCacheKey;
            }
            catch { }
            cards.Add(new PlaylistOverviewItem(pl, coverKey));
        }
        SavedPlaylistCards.ReplaceAll(cards);
    }

    /// <summary>
    /// Memuat trek yang tersimpan di dalam playlist terpilih saat ini.
    /// </summary>
    public void LoadSelectedPlaylistTracks()
    {
        if (_selectedPlaylist == null)
        {
            SelectedPlaylistTracks.ReplaceAll(Array.Empty<Track>());
            SelectedPlaylistSummaryText = string.Empty;
            SelectedPlaylistCoverKey = null;
            return;
        }

        var tracks = _db.GetTracksForPlaylist(_selectedPlaylist.Id);
        SelectedPlaylistTracks.ReplaceAll(tracks);
        SelectedPlaylistCoverKey = tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.CoverArtCacheKey))?.CoverArtCacheKey;
        long totalDurMs = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            totalDurMs += (long)tracks[i].Duration.TotalMilliseconds;
        }

        var span = TimeSpan.FromMilliseconds(totalDurMs);
        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
        SelectedPlaylistSummaryText = isEn
            ? $"{tracks.Count} {(tracks.Count == 1 ? "Track" : "Tracks")} • {(int)span.TotalMinutes} min"
            : $"{tracks.Count} Lagu • {(int)span.TotalMinutes} Menit";
    }

    /// <summary>
    /// Menyimpan sesi pemutaran audio (lagu, posisi, antrean, volume) ke SQLite.
    /// </summary>
    public void SaveSession()
    {
        try
        {
            _controller.SaveSession(_db);
            _db.SetSetting("volume", Volume.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SaveSession Warning] {ex.Message}");
        }
    }

    /// <summary>
    /// Memulihkan sesi pemutaran terakhir dari SQLite (antrean, posisi, volume).
    /// </summary>
    private void RestoreSession()
    {
        try
        {
            var savedVol = _db.GetSetting("volume");
            if (!string.IsNullOrEmpty(savedVol) && float.TryParse(savedVol, CultureInfo.InvariantCulture, out float v))
            {
                Volume = v;
                _controller.Volume = v;
            }

            _ = Task.Run(async () =>
            {
                bool restored = await _controller.RestoreSessionAsync(_db, autoPlay: false);
                if (restored)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        RefreshQueue();
                        if (_controller.CurrentTrack != null)
                        {
                            CurrentTrack = _controller.CurrentTrack;
                            ActivePlayingTrackId = _controller.CurrentTrack.Id;
                            _ = Lyrics.LoadForTrackAsync(_controller.CurrentTrack);
                        }
                    });
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RestoreSession Warning] {ex.Message}");
        }
    }

    private void OnControllerPlaybackFailed(object? sender, PlaybackErrorEventArgs e)
    {
        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
        string trackName = string.IsNullOrWhiteSpace(e.Track.Title)
            ? Path.GetFileName(e.Track.FilePath)
            : e.Track.Title;
        string message = isEn
            ? $"Unable to play \"{trackName}\": {e.Message}"
            : $"Tidak dapat memutar \"{trackName}\": {e.Message}";

        void ApplyOnUi()
        {
            PlaybackErrorMessage = message;
            if (CurrentView == "Inspector" &&
                (InspectedTrack == null || string.Equals(InspectedTrack.FilePath, e.Track.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                CurrentIntegrityReport = null;
                AnalysisErrorMessage = message;
            }

            if (Application.Current?.MainWindow is { IsVisible: true } owner &&
                !string.Equals(Environment.GetEnvironmentVariable("AURA_SMOKE_MODE"), "1", StringComparison.Ordinal))
            {
                MessageBox.Show(
                    owner,
                    message,
                    isEn ? "Playback Error" : "Kesalahan Pemutaran",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        if (Application.Current?.Dispatcher != null)
        {
            Application.Current.Dispatcher.BeginInvoke(ApplyOnUi);
        }
        else
        {
            PlaybackErrorMessage = message;
        }
    }

    private bool _isDisposed;

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _controller.PlaybackFailed -= OnControllerPlaybackFailed;
        SaveSession();
        Settings.HardwareConfig.Cleanup();
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _cts.Cancel();
        _cts.Dispose();
        _controller.Dispose();
        _db.Dispose();
    }
}
