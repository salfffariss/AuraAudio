using System;

namespace AuraAudio.Core.Models;

/// <summary>
/// Entitas agregat Artis musik untuk tampilan daftar artis.
/// </summary>
public record Artist
{
    public string Name { get; init; }
    public int TrackCount { get; init; }
    public int AlbumCount { get; init; }
    public string? SampleCoverKey { get; init; }

    public Artist(string name, int trackCount, int albumCount, string? sampleCoverKey = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Unknown Artist" : name;
        TrackCount = trackCount;
        AlbumCount = albumCount;
        SampleCoverKey = sampleCoverKey;
    }

    /// <summary>
    /// Keterangan ringkas jumlah album dan lagu dari artis.
    /// </summary>
    public string SummaryText
    {
        get
        {
            bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
            return isEn
                ? $"{AlbumCount} {(AlbumCount == 1 ? "album" : "albums")} • {TrackCount} {(TrackCount == 1 ? "track" : "tracks")}"
                : $"{AlbumCount} album • {TrackCount} lagu";
        }
    }
}
