using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AuraAudio.Metadata;

/// <summary>
/// Parser cerdas untuk memisahkan nama artis kolaborasi (Multi-Artist Splitting).
/// Mendukung delimiter standar: &, feat., ft., featuring, /, ;, ,, x, with.
/// </summary>
public static class ArtistParser
{
    private static readonly Regex SplitRegex = new(
        @"\s*(?:&|feat\.|ft\.|featuring|/|;|(?:\s+x\s+)|\bx\b|\bwith\b|,)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    /// <summary>
    /// Memecah string nama artis gabungan menjadi daftar nama musisi individual yang bersih.
    /// Contoh: "宝鐘マリン & Kobo Kanaeru" => ["宝鐘マリン", "Kobo Kanaeru"]
    /// </summary>
    public static List<string> ParseArtists(string? artistString)
    {
        if (string.IsNullOrWhiteSpace(artistString)) return new List<string>();

        var parts = SplitRegex.Split(artistString)
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return parts.Count > 0 ? parts : new List<string> { artistString.Trim() };
    }
}
