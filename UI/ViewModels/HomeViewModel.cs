using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.UI.Helpers;

namespace AuraAudio.UI.ViewModels;

/// <summary>
/// Child ViewModel for the v2.0.0 Home / Landing screen (03_design_spec.md B.1 & D.6).
/// Populates horizontal shelves exclusively from real local SQLite data.
/// </summary>
public class HomeViewModel : ViewModelBase
{
    private readonly DatabaseManager _db;
    private readonly MainViewModel _parent;
    private bool _isEmptyLibrary;

    public BulkObservableCollection<Album> RecentlyAddedAlbums { get; } = new();
    public BulkObservableCollection<Track> RecentlyPlayedTracks { get; } = new();
    public BulkObservableCollection<Album> HiResAlbums { get; } = new();
    public BulkObservableCollection<Artist> FeaturedArtists { get; } = new();

    public bool HasRecentlyPlayed => RecentlyPlayedTracks.Count > 0;
    public bool HasHiResAlbums => HiResAlbums.Count > 0;

    public bool IsEmptyLibrary
    {
        get => _isEmptyLibrary;
        private set => SetProperty(ref _isEmptyLibrary, value);
    }

    public ICommand PlayAlbumCommand { get; }
    public ICommand OpenAlbumCommand { get; }
    public ICommand OpenArtistCommand { get; }
    public ICommand PlayTrackCommand { get; }
    public ICommand NavigateToSectionCommand { get; }

    public HomeViewModel(DatabaseManager db, MainViewModel parent)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));

        PlayAlbumCommand = new RelayCommand(p => _parent.PlayAlbumCommand.Execute(p));
        OpenAlbumCommand = new RelayCommand(p => _parent.OpenAlbumDetailCommand.Execute(p));
        OpenArtistCommand = new RelayCommand(p => _parent.NavigateToArtistLinkCommand.Execute(p));
        PlayTrackCommand = new RelayCommand(p => _parent.PlaySelectedTrackCommand.Execute(p));
        NavigateToSectionCommand = new RelayCommand(p => _parent.NavigateCommand.Execute(p));
    }

    public async Task RefreshAsync()
    {
        var (recentAlbums, recentHistory, hiRes, artists, trackCount) = await Task.Run(() =>
        {
            var ra = _db.GetRecentlyAddedAlbums(12);
            var rh = _db.GetRecentPlayHistory(8);
            var hr = _db.GetHiResAlbums(12);
            var ar = _db.GetArtists().OrderByDescending(a => a.TrackCount).Take(12).ToList();
            long tc = _db.GetTrackCount();
            return (ra, rh, hr, ar, tc);
        });

        RecentlyAddedAlbums.ReplaceAll(recentAlbums);
        RecentlyPlayedTracks.ReplaceAll(recentHistory);
        HiResAlbums.ReplaceAll(hiRes);
        FeaturedArtists.ReplaceAll(artists);
        IsEmptyLibrary = trackCount == 0;

        OnPropertyChanged(nameof(HasRecentlyPlayed));
        OnPropertyChanged(nameof(HasHiResAlbums));
    }
}
