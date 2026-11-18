namespace AuraAudio.Core.Models;

/// <summary>
/// Entitas agregat Album yang merepresentasikan satu rilis album di dalam grid UI.
/// Dirancang untuk tampilan grid modern seperti di Apple Music & Spotify.
/// </summary>
public record Album
{
    public long Id { get; init; }
    public string Title { get; init; }
    public string Artist { get; init; }
    public int? Year { get; init; }
    public int TrackCount { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public bool IsHiRes { get; init; }
    public string? CoverArtCacheKey { get; init; }

    public Album(
        long id,
        string title,
        string artist,
        int? year,
        int trackCount,
        TimeSpan totalDuration,
        bool isHiRes = false,
        string? coverArtCacheKey = null)
    {
        Id = id;
        Title = string.IsNullOrWhiteSpace(title) ? "Unknown Album" : title;
        Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;
        Year = year;
        TrackCount = trackCount;
        TotalDuration = totalDuration;
        IsHiRes = isHiRes;
        CoverArtCacheKey = coverArtCacheKey;
    }

    /// <summary>
    /// Keterangan ringkas durasi album, misal "12 tracks • 48 mins".
    /// </summary>
    public string SummaryText
    {
        get
        {
            bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
            int mins = (int)TotalDuration.TotalMinutes;
            return isEn
                ? $"{TrackCount} {(TrackCount == 1 ? "track" : "tracks")} • {mins} min"
                : $"{TrackCount} lagu • {mins} mnt";
        }
    }

    /// <summary>
    /// Subjudul kartu album tanpa klaim palsu bila tahun rilis tidak tersedia di tag.
    /// </summary>
    public string GridSubtitleText
    {
        get
        {
            bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
            string trackWord = isEn ? (TrackCount == 1 ? "track" : "tracks") : "lagu";
            return Year.HasValue && Year.Value > 0
                ? $"{TrackCount} {trackWord} • {Year.Value}"
                : $"{TrackCount} {trackWord}";
        }
    }
}
