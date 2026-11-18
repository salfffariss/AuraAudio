using System.Collections.Generic;

namespace AuraAudio.Core.Models;

/// <summary>
/// Kelompok album beserta seluruh lagunya untuk tampilan diskografi artis gaya Apple Music.
/// </summary>
public class ArtistAlbumGroup
{
    public Album Album { get; set; }
    public IReadOnlyList<Track> Tracks { get; set; }

    public int TrackCount => Tracks.Count;

    public string SummaryText
    {
        get
        {
            bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
            string yearStr = Album.Year.HasValue && Album.Year.Value > 0 ? $"{Album.Year.Value} • " : "";
            string trackWord = isEn ? (Tracks.Count == 1 ? "track" : "tracks") : "lagu";
            return $"{yearStr}{Tracks.Count} {trackWord}";
        }
    }

    public ArtistAlbumGroup(Album album, IReadOnlyList<Track> tracks)
    {
        Album = album;
        Tracks = tracks;
    }
}
