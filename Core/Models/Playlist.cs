using System;

namespace AuraAudio.Core.Models;

/// <summary>
/// Representasi Playlist musik kustom lokal yang tersimpan di SQLite atau hasil impor M3U/M3U8.
/// </summary>
public record Playlist(
    long Id,
    string Name,
    int TrackCount,
    DateTime CreatedAt
)
{
    public string SummaryText
    {
        get
        {
            bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";
            return isEn
                ? $"{TrackCount} {(TrackCount == 1 ? "track" : "tracks")} • {CreatedAt:dd MMM yyyy}"
                : $"{TrackCount} lagu • {CreatedAt:dd MMM yyyy}";
        }
    }
}
