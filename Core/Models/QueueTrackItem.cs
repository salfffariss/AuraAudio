using System;

namespace AuraAudio.Core.Models;

/// <summary>
/// Item representasi baris antrean pemutaran dengan nomor urut deterministik (1, 2, 3... N).
/// Mencegah bug wrap-around AlternationIndex pada virtualized ListBox.
/// </summary>
public class QueueTrackItem
{
    public int DisplayNumber { get; set; }
    public Track Track { get; set; }
    public bool IsCurrentlyPlaying { get; set; }

    public QueueTrackItem(int displayNumber, Track track, bool isCurrentlyPlaying = false)
    {
        DisplayNumber = displayNumber;
        Track = track ?? throw new ArgumentNullException(nameof(track));
        IsCurrentlyPlaying = isCurrentlyPlaying;
    }

    public long Id => Track.Id;
    public string Title => Track.Title;
    public string Artist => Track.Artist;
    public string Album => Track.Album;
    public TimeSpan Duration => Track.Duration;
    public AudioStreamSpec StreamSpec => Track.StreamSpec;
    public string? CoverArtCacheKey => Track.CoverArtCacheKey;
    public string FilePath => Track.FilePath;
}
