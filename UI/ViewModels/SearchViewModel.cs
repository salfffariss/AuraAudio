using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.UI.Helpers;

namespace AuraAudio.UI.ViewModels;

public sealed record SearchBrowseBrick(string Key, string Title, string Subtitle, string CountBadge);

public sealed record SearchHintItem(
    string Kind,
    string Title,
    string Subtitle,
    string? CoverArtCacheKey,
    object Payload);

/// <summary>
/// Child ViewModel for the v2.0.0 Search page and top-bar SearchHints popup (03_design_spec.md Part B.8 & D.6).
/// Uses real SQLite library queries and real local codec/Hi-Res/recency browse bricks (zero fake data).
/// </summary>
public class SearchViewModel : ViewModelBase
{
    private readonly DatabaseManager _db;
    private readonly MainViewModel _parent;
    private string _query = string.Empty;
    private string _selectedScope = "All";
    private bool _isHintsOpen;
    private Track? _topResult;
    private CancellationTokenSource? _debounceCts;

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                OnPropertyChanged(nameof(HasQuery));
                OnPropertyChanged(nameof(ShowBrowseLanding));
                OnPropertyChanged(nameof(ShowNoResults));
                if (string.IsNullOrWhiteSpace(value))
                {
                    IsHintsOpen = false;
                }
                ScheduleSearch();
            }
        }
    }

    public bool HasQuery => !string.IsNullOrWhiteSpace(_query);
    public bool ShowBrowseLanding => !HasQuery;

    public string SelectedScope
    {
        get => _selectedScope;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "All" : value;
            if (SetProperty(ref _selectedScope, normalized))
            {
                NotifyScopeVisibilityChanged();
            }
        }
    }

    public bool IsScopeAll => SelectedScope.Equals("All", StringComparison.OrdinalIgnoreCase);
    public bool IsScopeSongs => SelectedScope.Equals("Songs", StringComparison.OrdinalIgnoreCase);
    public bool IsScopeAlbums => SelectedScope.Equals("Albums", StringComparison.OrdinalIgnoreCase);
    public bool IsScopeArtists => SelectedScope.Equals("Artists", StringComparison.OrdinalIgnoreCase);
    public bool IsScopePlaylists => SelectedScope.Equals("Playlists", StringComparison.OrdinalIgnoreCase);

    public bool IsHintsOpen
    {
        get => _isHintsOpen;
        set => SetProperty(ref _isHintsOpen, value);
    }

    public Track? TopResult
    {
        get => _topResult;
        private set
        {
            if (SetProperty(ref _topResult, value))
            {
                OnPropertyChanged(nameof(ShowTopResult));
            }
        }
    }

    public BulkObservableCollection<SearchHintItem> QuickHints { get; } = new();
    public BulkObservableCollection<SearchBrowseBrick> BrowseBricks { get; } = new();
    public BulkObservableCollection<Track> MatchingTracks { get; } = new();
    public BulkObservableCollection<Album> MatchingAlbums { get; } = new();
    public BulkObservableCollection<Artist> MatchingArtists { get; } = new();
    public BulkObservableCollection<Playlist> MatchingPlaylists { get; } = new();

    public bool HasQuickHints => QuickHints.Count > 0;
    public bool HasResults =>
        MatchingTracks.Count > 0 || MatchingAlbums.Count > 0 || MatchingArtists.Count > 0 || MatchingPlaylists.Count > 0;

    public bool ShowTopResult => HasQuery && IsScopeAll && TopResult != null;
    public bool ShowSongsSection => HasQuery && (IsScopeAll || IsScopeSongs) && MatchingTracks.Count > 0;
    public bool ShowAlbumsSection => HasQuery && (IsScopeAll || IsScopeAlbums) && MatchingAlbums.Count > 0;
    public bool ShowArtistsSection => HasQuery && (IsScopeAll || IsScopeArtists) && MatchingArtists.Count > 0;
    public bool ShowPlaylistsSection => HasQuery && (IsScopeAll || IsScopePlaylists) && MatchingPlaylists.Count > 0;

    public bool ShowNoResults =>
        HasQuery &&
        !ShowSongsSection &&
        !ShowAlbumsSection &&
        !ShowArtistsSection &&
        !ShowPlaylistsSection;

    public ICommand CommitSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand SelectScopeCommand { get; }
    public ICommand SelectHintCommand { get; }
    public ICommand OpenBrowseBrickCommand { get; }

    public SearchViewModel(DatabaseManager db, MainViewModel parent)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));

        CommitSearchCommand = new RelayCommand(() =>
        {
            IsHintsOpen = false;
            _parent.NavigateCommand.Execute("Search");
            _ = ExecuteSearchAsync(openHintsOnResult: false, ct: CancellationToken.None);
        });

        ClearSearchCommand = new RelayCommand(() =>
        {
            Query = string.Empty;
            IsHintsOpen = false;
        });

        SelectScopeCommand = new RelayCommand(p =>
        {
            if (p is string scope && !string.IsNullOrWhiteSpace(scope))
            {
                SelectedScope = scope;
            }
        });

        SelectHintCommand = new RelayCommand(p =>
        {
            IsHintsOpen = false;
            if (p is SearchHintItem hint)
            {
                switch (hint.Payload)
                {
                    case Track t:
                        _parent.PlaySelectedTrackCommand.Execute(t);
                        break;
                    case Album a:
                        _parent.OpenAlbumDetailCommand.Execute(a);
                        break;
                    case Artist ar:
                        _parent.OpenArtistDetailPage(ar);
                        break;
                    case Playlist pl:
                        _parent.OpenPlaylistCommand.Execute(pl);
                        break;
                }
            }
            else if (p is Track directTrack)
            {
                _parent.PlaySelectedTrackCommand.Execute(directTrack);
            }
        });

        OpenBrowseBrickCommand = new RelayCommand(p =>
        {
            if (p is not SearchBrowseBrick brick) return;
            IsHintsOpen = false;

            switch (brick.Key)
            {
                case "HiRes":
                    _parent.IsColumnBrowserVisible = true;
                    _parent.SelectedBrowserFormat = "Hi-Res Lossless";
                    _parent.NavigateCommand.Execute("Songs");
                    break;
                case "FLAC":
                    _parent.IsColumnBrowserVisible = true;
                    _parent.SelectedBrowserFormat = "FLAC";
                    _parent.NavigateCommand.Execute("Songs");
                    break;
                case "LosslessPcm":
                    _parent.IsColumnBrowserVisible = true;
                    _parent.SelectedBrowserFormat = "All Formats";
                    _parent.NavigateCommand.Execute("Songs");
                    break;
                case "RecentlyAdded":
                    _parent.AlbumSortMode = "RecentlyAdded";
                    _parent.NavigateCommand.Execute("Albums");
                    break;
                case "Artists":
                    _parent.NavigateCommand.Execute("Artists");
                    break;
                case "Playlists":
                    _parent.NavigateCommand.Execute("Playlists");
                    break;
                default:
                    _parent.NavigateCommand.Execute("Songs");
                    break;
            }
        });
    }

    public void RefreshBrowseBricks()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var allTracks = _db.GetSortedTracks(TrackSortOption.TitleAscending, limit: 10000, offset: 0);
                var albums = _db.GetAlbums();
                var artists = _db.GetArtists();
                var playlists = _db.GetPlaylists();

                int hiResCount = allTracks.Count(t => t.StreamSpec.IsHiRes);
                int flacCount = allTracks.Count(t => t.StreamSpec.Codec == AudioCodec.FLAC);
                int pcmOtherCount = allTracks.Count(t => t.StreamSpec.Codec is AudioCodec.ALAC or AudioCodec.WAV or AudioCodec.AIFF or AudioCodec.DSD_DSF or AudioCodec.DSD_DFF);

                var bricks = new List<SearchBrowseBrick>
                {
                    new("HiRes", "Hi-Res Masters", "24-bit, >48 kHz & DSD streams", $"{hiResCount} tracks"),
                    new("FLAC", "FLAC Collection", "Free Lossless Audio Codec library", $"{flacCount} tracks"),
                    new("LosslessPcm", "ALAC, WAV & DSD", "Studio PCM & native DSD formats", $"{pcmOtherCount} tracks"),
                    new("RecentlyAdded", "Recently Added", "Newest albums indexed in SQLite", $"{albums.Count} albums"),
                    new("Artists", "Your Artists", "Master-detail discographies", $"{artists.Count} artists"),
                    new("Playlists", "Saved Playlists", "Custom & imported M3U8 lists", $"{playlists.Count} playlists")
                };

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(() => BrowseBricks.ReplaceAll(bricks));
                }
                else
                {
                    BrowseBricks.ReplaceAll(bricks);
                }
            }
            catch { }
        });
    }

    private void ScheduleSearch()
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(180, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    await ExecuteSearchAsync(openHintsOnResult: true, ct: token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public async Task ExecuteSearchAsync(CancellationToken ct = default)
        => await ExecuteSearchAsync(openHintsOnResult: false, ct: ct).ConfigureAwait(false);

    public async Task ExecuteSearchAsync(bool openHintsOnResult, CancellationToken ct)
    {
        string q = _query?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(q))
        {
            void ClearAll()
            {
                QuickHints.ReplaceAll(Array.Empty<SearchHintItem>());
                MatchingTracks.ReplaceAll(Array.Empty<Track>());
                MatchingAlbums.ReplaceAll(Array.Empty<Album>());
                MatchingArtists.ReplaceAll(Array.Empty<Artist>());
                MatchingPlaylists.ReplaceAll(Array.Empty<Playlist>());
                TopResult = null;
                IsHintsOpen = false;
                NotifyResultPropertiesChanged();
            }

            DispatchUi(ClearAll);
            return;
        }

        var (tracks, albums, artists, playlists, hints) = await Task.Run(() =>
        {
            var t = _db.GetSortedTracks(TrackSortOption.TitleAscending, limit: 100, offset: 0, filter: q);
            var al = _db.GetAlbums(q);
            var ar = _db.GetArtists(q);
            var pl = _db.GetPlaylists()
                .Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var builtHints = new List<SearchHintItem>(8);
            foreach (var artist in ar.Take(2))
            {
                builtHints.Add(new SearchHintItem("Artist", artist.Name, artist.SummaryText, artist.SampleCoverKey, artist));
            }
            foreach (var album in al.Take(2))
            {
                builtHints.Add(new SearchHintItem("Album", album.Title, album.Artist, album.CoverArtCacheKey, album));
            }
            foreach (var track in t.Take(Math.Max(0, 8 - builtHints.Count)))
            {
                builtHints.Add(new SearchHintItem("Song", track.Title, $"{track.Artist} · {track.Album}", track.CoverArtCacheKey, track));
            }

            return (t, al, ar, pl, builtHints);
        }, ct).ConfigureAwait(false);

        if (ct.IsCancellationRequested) return;

        DispatchUi(() =>
        {
            if (ct.IsCancellationRequested) return;
            QuickHints.ReplaceAll(hints);
            MatchingTracks.ReplaceAll(tracks);
            MatchingAlbums.ReplaceAll(albums);
            MatchingArtists.ReplaceAll(artists);
            MatchingPlaylists.ReplaceAll(playlists);
            TopResult = tracks.FirstOrDefault();
            if (openHintsOnResult && hints.Count > 0 && _parent.CurrentView != "Search")
            {
                IsHintsOpen = true;
            }
            NotifyResultPropertiesChanged();
        });
    }

    private void NotifyScopeVisibilityChanged()
    {
        OnPropertyChanged(nameof(IsScopeAll));
        OnPropertyChanged(nameof(IsScopeSongs));
        OnPropertyChanged(nameof(IsScopeAlbums));
        OnPropertyChanged(nameof(IsScopeArtists));
        OnPropertyChanged(nameof(IsScopePlaylists));
        OnPropertyChanged(nameof(ShowTopResult));
        OnPropertyChanged(nameof(ShowSongsSection));
        OnPropertyChanged(nameof(ShowAlbumsSection));
        OnPropertyChanged(nameof(ShowArtistsSection));
        OnPropertyChanged(nameof(ShowPlaylistsSection));
        OnPropertyChanged(nameof(ShowNoResults));
    }

    private void NotifyResultPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasQuickHints));
        OnPropertyChanged(nameof(HasResults));
        NotifyScopeVisibilityChanged();
    }

    private static void DispatchUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }
}
