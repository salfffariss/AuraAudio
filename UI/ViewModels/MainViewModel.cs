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
using Microsoft.Win32;

namespace AuraAudio.UI.ViewModels;

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

    private string _currentView = "Library";
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
    private string _signalPathColorHex = "#E5A23C";
    private SignalPathSummary? _currentSignalPath;
    private AudioIntegrityReport? _currentIntegrityReport;
    private string _streamSpecBadge = "--";
    private bool _isShuffleEnabled = false;
    private PlaybackRepeatMode _repeatMode = PlaybackRepeatMode.Off;
    private float _volume = 1.0f;
    private string _outputDeviceName = "Default Endpoint";
    private string _sidebarDriverTitle = "WASAPI Exclusive";
    private string _sidebarDriverSubtitle = "Jalur Bit-Perfect Langsung";
    private string _sidebarDriverColorHex = "#34D399";
    private AuraAudio.UI.Helpers.WindowsMediaControlsManager? _mediaControlsManager;
    private PlaybackStatus _lastKnownStatus = PlaybackStatus.Stopped;
    private Artist? _selectedArtist;
    private bool _isArtistDetailView;
    private string _artistStatsSummary = string.Empty;
    private Playlist? _selectedPlaylist;
    private string _queueSummaryText = "Antrean kosong";
    private int _currentQueueIndex = -1;
    private string _analysisErrorMessage = string.Empty;

    public ObservableCollection<Track> DisplayedTracks { get; } = new();
    public ObservableCollection<Album> DisplayedAlbums { get; } = new();
    public ObservableCollection<Artist> DisplayedArtists { get; } = new();
    public ObservableCollection<Album> ArtistAlbums { get; } = new();
    public ObservableCollection<Track> ArtistTracks { get; } = new();
    public ObservableCollection<ArtistAlbumGroup> ArtistAlbumGroups { get; } = new();
    public ObservableCollection<QueueTrackItem> QueueTracks { get; } = new();
    public ObservableCollection<Playlist> SavedPlaylists { get; } = new();
    public ObservableCollection<Track> SelectedPlaylistTracks { get; } = new();
    public ObservableCollection<Track> SelectedAlbumDetailTracks { get; } = new();

    private Album? _selectedAlbumDetail;
    public Album? SelectedAlbumDetail
    {
        get => _selectedAlbumDetail;
        set => SetProperty(ref _selectedAlbumDetail, value);
    }

    private string _albumDetailSummaryText = string.Empty;
    public string AlbumDetailSummaryText
    {
        get => _albumDetailSummaryText;
        set => SetProperty(ref _albumDetailSummaryText, value);
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

    public Artist? SelectedArtist
    {
        get => _selectedArtist;
        set => SetProperty(ref _selectedArtist, value);
    }

    public bool IsArtistDetailView
    {
        get => _isArtistDetailView;
        set => SetProperty(ref _isArtistDetailView, value);
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
            if (SetProperty(ref _currentView, value))
            {
                if (value != "Artists")
                {
                    IsArtistDetailView = false;
                }

                if (value == "Albums" && DisplayedAlbums.Count == 0)
                {
                    LoadAlbums();
                }
                else if (value == "Artists" && DisplayedArtists.Count == 0)
                {
                    LoadArtists();
                }
                else if (value == "Queue")
                {
                    RefreshQueue();
                    if (SavedPlaylists.Count == 0)
                    {
                        LoadPlaylists();
                    }
                }
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                FilterData();
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
                ActivePlayingTrackId = value?.Id;
            }
        }
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

    public string SignalPathColorHex
    {
        get => _signalPathColorHex;
        set => SetProperty(ref _signalPathColorHex, value);
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

    public string SidebarDriverColorHex
    {
        get => _sidebarDriverColorHex;
        set => SetProperty(ref _sidebarDriverColorHex, value);
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
                OnPropertyChanged(nameof(ShuffleColorHex));
                OnPropertyChanged(nameof(ShuffleToolTip));
            }
        }
    }

    public string ShuffleColorHex => IsShuffleEnabled ? "#E5A23C" : "#949087";
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
                OnPropertyChanged(nameof(RepeatColorHex));
                OnPropertyChanged(nameof(RepeatToolTip));
            }
        }
    }

    public bool IsRepeatOne => RepeatMode == PlaybackRepeatMode.RepeatOne;
    public string RepeatColorHex => RepeatMode != PlaybackRepeatMode.Off ? "#E5A23C" : "#949087";
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

    public MainViewModel(AudiophilePlaybackController? controller = null, DatabaseManager? db = null)
    {
        _controller = controller ?? new AudiophilePlaybackController();

        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AuraAudio");
        Directory.CreateDirectory(appData);
        string dbPath = Path.Combine(appData, "library.db");

        _db = db ?? new DatabaseManager(dbPath);
        _db.Initialize();
        AuraAudio.UI.Helpers.CoverArtLoader.SetDatabase(_db);

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
        ToggleShuffleCommand = new RelayCommand(() => _controller.ToggleShuffle());
        ToggleRepeatCommand = new RelayCommand(CycleRepeatMode);
        OpenHardwareConfigCommand = new RelayCommand(() => RequestOpenHardwareConfig?.Invoke());
        OpenLibraryManagerCommand = new RelayCommand(() => RequestOpenLibraryManager?.Invoke());
        NavigateCommand = new RelayCommand(param =>
        {
            if (param is string viewName)
            {
                if (viewName == "Hardware")
                {
                    OpenHardwareConfigCommand.Execute(null);
                    return;
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
                await _controller.PlayQueueAsync(DisplayedTracks, idx >= 0 ? idx : 0);
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
            if (track != null) _controller.PlayNext(track);
        });
        AddToQueueCommand = new RelayCommand(param =>
        {
            var track = param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null) _controller.Enqueue(track);
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
            var playlist = param as Playlist ?? SelectedPlaylist;
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
            var playlist = param as Playlist ?? SelectedPlaylist;
            if (playlist != null)
            {
                var result = MessageBox.Show($"Hapus playlist \"{playlist.Name}\"?", "Konfirmasi Hapus", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    _db.DeletePlaylist(playlist.Id);
                    if (SelectedPlaylist?.Id == playlist.Id)
                    {
                        SelectedPlaylist = null;
                    }
                    LoadPlaylists();
                }
            }
        });

        AddToPlaylistCommand = new RelayCommand(param =>
        {
            var track = param as Track ?? SelectedTrack ?? CurrentTrack;
            if (track != null)
            {
                var dlg = new AuraAudio.UI.Views.AddToPlaylistDialog(_db, track)
                {
                    Owner = Application.Current?.MainWindow
                };
                if (dlg.ShowDialog() == true)
                {
                    LoadPlaylists();
                    if (SelectedPlaylist != null)
                    {
                        LoadSelectedPlaylistTracks();
                    }
                }
            }
        });

        CreatePlaylistCommand = new RelayCommand(param =>
        {
            string name = (param as string)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                var dlg = new AuraAudio.UI.Views.RenamePlaylistDialog("Playlist Baru")
                {
                    Title = "Buat Playlist Baru - AuraAudio",
                    Owner = Application.Current?.MainWindow
                };
                if (dlg.ShowDialog() == true)
                {
                    name = dlg.PlaylistName;
                }
                else return;
            }

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
        });

        RenamePlaylistCommand = new RelayCommand(param =>
        {
            var playlist = param as Playlist ?? SelectedPlaylist;
            if (playlist == null) return;

            var dlg = new AuraAudio.UI.Views.RenamePlaylistDialog(playlist.Name)
            {
                Owner = Application.Current?.MainWindow
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.PlaylistName))
            {
                _db.RenamePlaylist(playlist.Id, dlg.PlaylistName);
                long currentId = playlist.Id;
                LoadPlaylists();
                SelectedPlaylist = SavedPlaylists.FirstOrDefault(p => p.Id == currentId);
            }
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
            var playlist = param as Playlist ?? SelectedPlaylist;
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

        // Mulai consumer loop Channel State Snapshot (120 FPS tanpa alokasi berat)
        Task.Run(ConsumeStateChannelAsync);

        // Muat pustaka dari database
        LoadLibrary();
        LoadAlbums();
        LoadArtists();
        LoadPlaylists();

        // Pulihkan sesi pemutaran & antrean sebelumnya (Auto-restore)
        RestoreSession();
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
            SignalPathColorHex = s.SignalPath.BadgeColorHex;
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
            "Album" => TrackSortOption.ArtistDiscography,
            "Duration" => TrackSortOption.TitleAscending,
            "Format" => TrackSortOption.HiResFirst,
            _ => TrackSortOption.TitleAscending
        };

        LoadLibrary();
    }

    public void LoadLibrary()
    {
        var tracks = _db.GetSortedTracks(_currentSortOption, limit: 1000);
        DisplayedTracks.Clear();

        foreach (var t in tracks)
        {
            DisplayedTracks.Add(t);
        }

        _totalTracksCount = _db.GetTrackCount();
        OnPropertyChanged(nameof(IsLibraryEmpty));
        OnPropertyChanged(nameof(IsLibraryZeroTracks));
        OnPropertyChanged(nameof(EmptyLibraryTitle));
        OnPropertyChanged(nameof(EmptyLibraryDesc));
        UpdateLibraryStatsText();
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
            SidebarDriverColorHex = "#FBBF24";
        }
        else
        {
            SidebarDriverTitle = "WASAPI Exclusive";
            SidebarDriverSubtitle = isEn ? "Bit-Perfect Direct Out" : "Jalur Bit-Perfect Langsung";
            SidebarDriverColorHex = "#34D399";
        }
    }

    private void UpdateLibraryStatsText()
    {
        string suffix = AuraAudio.UI.Helpers.LocalizationManager.GetString("Str_Top_TracksIndexedSuffix", "Lagu Terindeks • WASAPI Exclusive Siap");
        LibraryStatsText = $"{_totalTracksCount} {suffix}";
    }

    public void LoadAlbums()
    {
        var albums = _db.GetAlbums();
        DisplayedAlbums.Clear();

        foreach (var a in albums)
        {
            DisplayedAlbums.Add(a);
        }
    }

    public void LoadArtists(string? filter = null)
    {
        var artists = _db.GetArtists(filter);
        DisplayedArtists.Clear();

        foreach (var a in artists)
        {
            DisplayedArtists.Add(a);
        }
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
        ArtistAlbums.Clear();
        foreach (var a in albums)
        {
            ArtistAlbums.Add(a);
        }

        var tracks = _db.GetTracksByArtist(artist.Name);
        ArtistTracks.Clear();
        long totalDurMs = 0;
        foreach (var t in tracks)
        {
            ArtistTracks.Add(t);
            totalDurMs += (long)t.Duration.TotalMilliseconds;
        }

        // Kelompokkan trek per album untuk gaya Apple Music
        ArtistAlbumGroups.Clear();
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
                ArtistAlbumGroups.Add(new ArtistAlbumGroup(alb, matchedTracks));
            }
            else
            {
                var dbAlbumTracks = _db.GetTracksByAlbum(alb.Title, alb.Artist);
                if (dbAlbumTracks.Count > 0)
                {
                    matchedAlbumTitles.Add(alb.Title);
                    ArtistAlbumGroups.Add(new ArtistAlbumGroup(alb, dbAlbumTracks));
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
                ArtistAlbumGroups.Add(new ArtistAlbumGroup(fallbackAlbum, kvp.Value));
            }
        }

        var totalSpan = TimeSpan.FromMilliseconds(totalDurMs);
        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
        ArtistStatsSummary = isEn
            ? $"{albums.Count} {(albums.Count == 1 ? "Album" : "Albums")} • {tracks.Count} {(tracks.Count == 1 ? "Track" : "Tracks")} • {(int)totalSpan.TotalMinutes} min"
            : $"{albums.Count} Album • {tracks.Count} Lagu • {(int)totalSpan.TotalMinutes} Menit";
    }

    /// <summary>
    /// Membuka tampilan detail album khusus yang menyajikan daftar lagu individual dari album terpilih.
    /// </summary>
    public void OpenAlbumDetail(Album album)
    {
        ArgumentNullException.ThrowIfNull(album);

        if (CurrentView != "AlbumDetail")
        {
            PreviousView = CurrentView;
        }

        SelectedAlbumDetail = album;

        var tracks = _db.GetTracksByAlbum(album.Title, album.Artist);
        SelectedAlbumDetailTracks.Clear();
        long totalDurMs = 0;
        foreach (var t in tracks)
        {
            SelectedAlbumDetailTracks.Add(t);
            totalDurMs += (long)t.Duration.TotalMilliseconds;
        }

        var totalSpan = TimeSpan.FromMilliseconds(totalDurMs);
        string yearText = album.Year.HasValue && album.Year.Value > 0 ? $"{album.Year.Value} • " : string.Empty;
        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
        AlbumDetailSummaryText = isEn
            ? $"{yearText}{tracks.Count} {(tracks.Count == 1 ? "Track" : "Tracks")} • {(int)totalSpan.TotalMinutes} min"
            : $"{yearText}{tracks.Count} Lagu • {(int)totalSpan.TotalMinutes} Menit";

        CurrentView = "AlbumDetail";
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

    private void FilterData()
    {
        if (CurrentView == "Albums")
        {
            var albums = _db.GetAlbums(SearchQuery);
            DisplayedAlbums.Clear();
            foreach (var a in albums) DisplayedAlbums.Add(a);
        }
        else if (CurrentView == "Artists")
        {
            LoadArtists(SearchQuery);
        }
        else
        {
            var all = _db.GetSortedTracks(_currentSortOption, limit: 1000);
            DisplayedTracks.Clear();

            var filtered = string.IsNullOrWhiteSpace(SearchQuery)
                ? all
                : all.Where(t => t.Title.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                                 t.Artist.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                                 t.Album.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

            foreach (var t in filtered)
            {
                DisplayedTracks.Add(t);
            }
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
        QueueTracks.Clear();
        long totalDurMs = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var t = items[i];
            bool isCurrent = (i == _controller.CurrentIndex);
            QueueTracks.Add(new QueueTrackItem(i + 1, t, isCurrent));
            totalDurMs += (long)t.Duration.TotalMilliseconds;
        }

        CurrentQueueIndex = _controller.CurrentIndex;
        var span = TimeSpan.FromMilliseconds(totalDurMs);
        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
        QueueSummaryText = items.Count > 0
            ? (isEn
                ? $"{items.Count} {(items.Count == 1 ? "Track" : "Tracks")} • {(int)span.TotalMinutes} min • Active Queue"
                : $"{items.Count} Lagu • {(int)span.TotalMinutes} Menit • Antrean Aktif")
            : (isEn
                ? "Queue is empty • Pick a track to start listening"
                : "Antrean kosong • Pilih lagu untuk mulai mendengarkan");
    }

    /// <summary>
    /// Memuat seluruh playlist tersimpan dari database.
    /// </summary>
    public void LoadPlaylists()
    {
        var list = _db.GetPlaylists();
        SavedPlaylists.Clear();
        foreach (var p in list)
        {
            SavedPlaylists.Add(p);
        }
    }

    /// <summary>
    /// Memuat trek yang tersimpan di dalam playlist terpilih saat ini.
    /// </summary>
    public void LoadSelectedPlaylistTracks()
    {
        SelectedPlaylistTracks.Clear();
        if (_selectedPlaylist == null)
        {
            SelectedPlaylistSummaryText = string.Empty;
            return;
        }

        var tracks = _db.GetTracksForPlaylist(_selectedPlaylist.Id);
        long totalDurMs = 0;
        foreach (var t in tracks)
        {
            SelectedPlaylistTracks.Add(t);
            totalDurMs += (long)t.Duration.TotalMilliseconds;
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

    public void Dispose()
    {
        SaveSession();
        _cts.Cancel();
        _cts.Dispose();
        _controller.Dispose();
        _db.Dispose();
    }
}
