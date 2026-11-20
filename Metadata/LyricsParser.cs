using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TagLib;

namespace AuraAudio.Metadata;

public enum LyricsSourceKind
{
    None,
    SidecarLrc,
    EmbeddedLrc,
    Id3v2Sylt,
    PlainText
}

public sealed record ParsedLyricsLine(TimeSpan Timestamp, string Text, bool IsInstrumentalGap);

public sealed record ParsedLyricsResult(
    LyricsSourceKind Source,
    bool IsSynced,
    IReadOnlyList<ParsedLyricsLine> Lines,
    string PlainText,
    string? Writers)
{
    public static ParsedLyricsResult Empty { get; } = new(
        LyricsSourceKind.None,
        IsSynced: false,
        Lines: Array.Empty<ParsedLyricsLine>(),
        PlainText: string.Empty,
        Writers: null);

    public bool HasLyrics => IsSynced ? Lines.Count > 0 : !string.IsNullOrWhiteSpace(PlainText);
}

/// <summary>
/// v2.0.0 Lyrics resolver and parser (03_design_spec.md Part D.8):
/// Resolution priority:
///   1. Sidecar .lrc file matching the audio file path (UTF-8 / auto-BOM)
///   2. Embedded LRC timestamps inside tag.Lyrics
///   3. TagLib ID3v2 SYLT (SynchronisedLyricsFrame)
///   4. Plain-text fallback from tag.Lyrics (unsynced, no fake timestamps)
/// Supports multi-timestamp LRC lines, [offset:±ms] header tags, and >= 5.0s instrumental break markers.
/// </summary>
public static class LyricsParser
{
    public const string InstrumentalBreakGlyph = "• • •";
    public static readonly TimeSpan InstrumentalGapThreshold = TimeSpan.FromSeconds(5.0);

    private static readonly Regex TimestampRegex = new(
        @"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]",
        RegexOptions.Compiled);

    private static readonly Regex OffsetTagRegex = new(
        @"^\[offset:\s*([+-]?\d+)\s*\]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MetadataTagRegex = new(
        @"^\[([a-zA-Z]{2,8}):\s*(.*?)\s*\]$",
        RegexOptions.Compiled);

    /// <summary>
    /// Returns true if raw text contains at least one valid LRC timestamp tag such as [00:12.40].
    /// </summary>
    public static bool HasLrcTimestamps(string? content) =>
        !string.IsNullOrWhiteSpace(content) && TimestampRegex.IsMatch(content);

    /// <summary>
    /// Imports an external .lrc or plain-text .txt lyrics file as a sidecar .lrc next to the audio file
    /// (preserving the original audio file untouched) and immediately parses the imported lyrics.
    /// </summary>
    public static ParsedLyricsResult ImportSidecarLyrics(string audioFilePath, string sourceLyricsFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLyricsFilePath);

        if (!System.IO.File.Exists(sourceLyricsFilePath))
        {
            throw new FileNotFoundException("Source lyrics file not found.", sourceLyricsFilePath);
        }

        string? targetDir = Path.GetDirectoryName(audioFilePath);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            throw new DirectoryNotFoundException($"Track directory does not exist: {targetDir}");
        }

        string content = ReadTextAutoEncoding(sourceLyricsFilePath);
        string sidecarPath = Path.ChangeExtension(audioFilePath, ".lrc");
        System.IO.File.WriteAllText(sidecarPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return LoadForFile(audioFilePath);
    }

    /// <summary>
    /// Resolves lyrics for an audio file path on disk following the strict 4-tier priority order.
    /// </summary>
    public static ParsedLyricsResult LoadForFile(string? audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            return ParsedLyricsResult.Empty;
        }

        string? sidecarPlainFallback = null;
        string? sidecarWriters = null;

        // Priority 1: Sidecar .lrc in the same directory
        try
        {
            string lrcPath = Path.ChangeExtension(audioFilePath, ".lrc");
            if (!string.IsNullOrWhiteSpace(lrcPath) && System.IO.File.Exists(lrcPath))
            {
                string rawLrc = ReadTextAutoEncoding(lrcPath);
                if (HasLrcTimestamps(rawLrc))
                {
                    var parsed = ParseLrcContent(rawLrc, LyricsSourceKind.SidecarLrc);
                    if (parsed.IsSynced && parsed.Lines.Count > 0)
                    {
                        string? tagWriters = TryReadComposerFromAudioFile(audioFilePath);
                        string? finalWriters = !string.IsNullOrWhiteSpace(tagWriters) ? tagWriters : parsed.Writers;
                        return parsed with { Writers = finalWriters };
                    }
                }
                else if (!string.IsNullOrWhiteSpace(rawLrc))
                {
                    sidecarPlainFallback = NormalizePlainText(rawLrc);
                }
            }
        }
        catch
        {
            // Continue to embedded tag resolution if sidecar read fails
        }

        // Priority 2, 3, 4: Embedded tags via TagLib#
        if (!System.IO.File.Exists(audioFilePath))
        {
            if (!string.IsNullOrWhiteSpace(sidecarPlainFallback))
            {
                return new ParsedLyricsResult(
                    LyricsSourceKind.PlainText,
                    IsSynced: false,
                    Lines: Array.Empty<ParsedLyricsLine>(),
                    PlainText: sidecarPlainFallback,
                    Writers: sidecarWriters);
            }
            return ParsedLyricsResult.Empty;
        }

        try
        {
            using var tagFile = TagLib.File.Create(audioFilePath);
            var tag = tagFile.Tag;

            string? composer = !string.IsNullOrWhiteSpace(tag.JoinedComposers)
                ? tag.JoinedComposers.Trim()
                : null;

            string? rawTagLyrics = tag.Lyrics;

            // Priority 2: Embedded LRC timestamps inside tag.Lyrics
            if (HasLrcTimestamps(rawTagLyrics))
            {
                var embeddedLrc = ParseLrcContent(rawTagLyrics!, LyricsSourceKind.EmbeddedLrc, composer);
                if (embeddedLrc.IsSynced && embeddedLrc.Lines.Count > 0)
                {
                    return embeddedLrc;
                }
            }

            // Priority 3: TagLib ID3v2 SYLT (SynchronisedLyricsFrame)
            var syltResult = TryParseId3v2Sylt(tagFile, composer);
            if (syltResult != null && syltResult.IsSynced && syltResult.Lines.Count > 0)
            {
                return syltResult;
            }

            // Priority 4: Plain-text fallback from tag.Lyrics (or untimestamped sidecar)
            if (!string.IsNullOrWhiteSpace(rawTagLyrics))
            {
                string plain = NormalizePlainText(rawTagLyrics);
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    return new ParsedLyricsResult(
                        LyricsSourceKind.PlainText,
                        IsSynced: false,
                        Lines: Array.Empty<ParsedLyricsLine>(),
                        PlainText: plain,
                        Writers: composer);
                }
            }

            if (!string.IsNullOrWhiteSpace(sidecarPlainFallback))
            {
                return new ParsedLyricsResult(
                    LyricsSourceKind.PlainText,
                    IsSynced: false,
                    Lines: Array.Empty<ParsedLyricsLine>(),
                    PlainText: sidecarPlainFallback,
                    Writers: composer);
            }
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(sidecarPlainFallback))
            {
                return new ParsedLyricsResult(
                    LyricsSourceKind.PlainText,
                    IsSynced: false,
                    Lines: Array.Empty<ParsedLyricsLine>(),
                    PlainText: sidecarPlainFallback,
                    Writers: sidecarWriters);
            }
        }

        return ParsedLyricsResult.Empty;
    }

    /// <summary>
    /// Parses arbitrary raw lyrics text: returns synced lines if LRC timestamps are present,
    /// otherwise falls back cleanly to unsynced plain text.
    /// </summary>
    public static ParsedLyricsResult ParseText(
        string? rawContent,
        LyricsSourceKind syncedSource = LyricsSourceKind.EmbeddedLrc,
        string? writersOverride = null)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return ParsedLyricsResult.Empty;
        }

        if (HasLrcTimestamps(rawContent))
        {
            var lrc = ParseLrcContent(rawContent, syncedSource, writersOverride);
            if (lrc.IsSynced && lrc.Lines.Count > 0)
            {
                return lrc;
            }
        }

        string plain = NormalizePlainText(rawContent);
        if (string.IsNullOrWhiteSpace(plain))
        {
            return ParsedLyricsResult.Empty;
        }

        return new ParsedLyricsResult(
            LyricsSourceKind.PlainText,
            IsSynced: false,
            Lines: Array.Empty<ParsedLyricsLine>(),
            PlainText: plain,
            Writers: string.IsNullOrWhiteSpace(writersOverride) ? null : writersOverride.Trim());
    }

    /// <summary>
    /// Parses LRC formatted text including multi-timestamp lines ([00:12.40][01:08.15]Chorus),
    /// [offset:±ms] header adjustments (positive shifts timestamps earlier, negative shifts later,
    /// clamped at TimeSpan.Zero), and >= 5.0s instrumental break markers.
    /// </summary>
    public static ParsedLyricsResult ParseLrcContent(
        string content,
        LyricsSourceKind source = LyricsSourceKind.SidecarLrc,
        string? writersOverride = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return ParsedLyricsResult.Empty;
        }

        long offsetMs = 0;
        string? lrcAuthor = null;
        var rawEntries = new List<(TimeSpan Timestamp, string Text, int FileOrder)>();

        string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int order = 0;

        foreach (string rawLine in lines)
        {
            string trimmed = rawLine.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Check [offset:±ms] header tag
            var offsetMatch = OffsetTagRegex.Match(trimmed);
            if (offsetMatch.Success &&
                long.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedOffset))
            {
                offsetMs = parsedOffset;
                continue;
            }

            var timestampMatches = TimestampRegex.Matches(trimmed);
            if (timestampMatches.Count == 0)
            {
                // Check metadata tags like [au:Composer Name]
                var metaMatch = MetadataTagRegex.Match(trimmed);
                if (metaMatch.Success)
                {
                    string tagKey = metaMatch.Groups[1].Value;
                    string tagVal = metaMatch.Groups[2].Value.Trim();
                    if (tagKey.Equals("au", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(tagVal))
                    {
                        lrcAuthor = tagVal;
                    }
                }
                continue;
            }

            string lyricText = TimestampRegex.Replace(trimmed, string.Empty).Trim();

            foreach (Match m in timestampMatches)
            {
                if (TryParseTimestampMatch(m, out TimeSpan ts))
                {
                    rawEntries.Add((ts, lyricText, order++));
                }
            }
        }

        if (rawEntries.Count == 0)
        {
            return ParsedLyricsResult.Empty;
        }

        // Standard LRC offset convention: positive [offset:+500] speeds lyrics up (timestamp - 500ms);
        // negative [offset:-500] delays lyrics (timestamp + 500ms). Clamped at TimeSpan.Zero.
        TimeSpan offsetShift = TimeSpan.FromMilliseconds(offsetMs);
        var adjusted = new List<(TimeSpan Timestamp, string Text, int FileOrder)>(rawEntries.Count);
        foreach (var entry in rawEntries)
        {
            TimeSpan shifted = entry.Timestamp - offsetShift;
            if (shifted < TimeSpan.Zero)
            {
                shifted = TimeSpan.Zero;
            }
            adjusted.Add((shifted, entry.Text, entry.FileOrder));
        }

        // Sort chronologically, preserving file order for identical timestamps
        adjusted.Sort((a, b) =>
        {
            int cmp = a.Timestamp.CompareTo(b.Timestamp);
            return cmp != 0 ? cmp : a.FileOrder.CompareTo(b.FileOrder);
        });

        var builtLines = BuildLinesWithInstrumentalGaps(adjusted);
        if (builtLines.Count == 0)
        {
            return ParsedLyricsResult.Empty;
        }

        string? finalWriters = !string.IsNullOrWhiteSpace(writersOverride)
            ? writersOverride.Trim()
            : lrcAuthor;

        string plainFallback = string.Join(
            "\n",
            builtLines.Where(l => !l.IsInstrumentalGap).Select(l => l.Text));

        return new ParsedLyricsResult(
            Source: source,
            IsSynced: true,
            Lines: builtLines,
            PlainText: plainFallback,
            Writers: finalWriters);
    }

    private static ParsedLyricsResult? TryParseId3v2Sylt(TagLib.File tagFile, string? composer)
    {
        if (tagFile.GetTag(TagLib.TagTypes.Id3v2, create: false) is not TagLib.Id3v2.Tag id3v2)
        {
            return null;
        }

        var frames = id3v2.GetFrames<TagLib.Id3v2.SynchronisedLyricsFrame>().ToList();
        if (frames.Count == 0)
        {
            return null;
        }

        var selected = frames.FirstOrDefault(f =>
                           f.Type == TagLib.Id3v2.SynchedTextType.Lyrics &&
                           f.Format == TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds &&
                           f.Text is { Length: > 0 })
                       ?? frames.FirstOrDefault(f =>
                           f.Format == TagLib.Id3v2.TimestampFormat.AbsoluteMilliseconds &&
                           f.Text is { Length: > 0 });

        if (selected == null || selected.Text == null || selected.Text.Length == 0)
        {
            return null;
        }

        var entries = new List<(TimeSpan Timestamp, string Text, int FileOrder)>(selected.Text.Length);
        int order = 0;
        foreach (var item in selected.Text)
        {
            TimeSpan ts = TimeSpan.FromMilliseconds(Math.Max(0, item.Time));
            string cleaned = (item.Text ?? string.Empty).Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
            entries.Add((ts, cleaned, order++));
        }

        entries.Sort((a, b) =>
        {
            int cmp = a.Timestamp.CompareTo(b.Timestamp);
            return cmp != 0 ? cmp : a.FileOrder.CompareTo(b.FileOrder);
        });

        var lines = BuildLinesWithInstrumentalGaps(entries);
        if (lines.Count == 0)
        {
            return null;
        }

        string plainFallback = string.Join(
            "\n",
            lines.Where(l => !l.IsInstrumentalGap).Select(l => l.Text));

        return new ParsedLyricsResult(
            Source: LyricsSourceKind.Id3v2Sylt,
            IsSynced: true,
            Lines: lines,
            PlainText: plainFallback,
            Writers: composer);
    }

    private static List<ParsedLyricsLine> BuildLinesWithInstrumentalGaps(
        List<(TimeSpan Timestamp, string Text, int FileOrder)> sortedEntries)
    {
        var result = new List<ParsedLyricsLine>(sortedEntries.Count + 4);
        TimeSpan? pendingExplicitBreakTimestamp = null;
        TimeSpan? lastLyricTimestamp = null;

        for (int i = 0; i < sortedEntries.Count; i++)
        {
            var entry = sortedEntries[i];

            if (string.IsNullOrWhiteSpace(entry.Text))
            {
                // Remember explicit blank timestamp lines (used by LRC authors to mark instrumental breaks)
                if (lastLyricTimestamp.HasValue && !pendingExplicitBreakTimestamp.HasValue)
                {
                    pendingExplicitBreakTimestamp = entry.Timestamp;
                }
                continue;
            }

            if (lastLyricTimestamp.HasValue)
            {
                TimeSpan gap = entry.Timestamp - lastLyricTimestamp.Value;
                if (gap >= InstrumentalGapThreshold)
                {
                    TimeSpan breakTs;
                    if (pendingExplicitBreakTimestamp.HasValue &&
                        pendingExplicitBreakTimestamp.Value > lastLyricTimestamp.Value &&
                        pendingExplicitBreakTimestamp.Value < entry.Timestamp)
                    {
                        breakTs = pendingExplicitBreakTimestamp.Value;
                    }
                    else
                    {
                        double offsetSec = Math.Min(3.0, gap.TotalSeconds * 0.5);
                        breakTs = lastLyricTimestamp.Value + TimeSpan.FromSeconds(offsetSec);
                    }

                    result.Add(new ParsedLyricsLine(breakTs, InstrumentalBreakGlyph, IsInstrumentalGap: true));
                }
            }

            pendingExplicitBreakTimestamp = null;
            result.Add(new ParsedLyricsLine(entry.Timestamp, entry.Text, IsInstrumentalGap: false));
            lastLyricTimestamp = entry.Timestamp;
        }

        return result;
    }

    private static bool TryParseTimestampMatch(Match match, out TimeSpan timestamp)
    {
        timestamp = TimeSpan.Zero;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
        {
            return false;
        }

        int milliseconds = 0;
        string frac = match.Groups[3].Value;
        if (!string.IsNullOrEmpty(frac) &&
            int.TryParse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fracVal))
        {
            milliseconds = frac.Length switch
            {
                1 => fracVal * 100,
                2 => fracVal * 10,
                _ => fracVal
            };
        }

        timestamp = new TimeSpan(0, 0, minutes, seconds, milliseconds);
        return true;
    }

    private static string ReadTextAutoEncoding(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? TryReadComposerFromAudioFile(string audioFilePath)
    {
        try
        {
            if (!System.IO.File.Exists(audioFilePath)) return null;
            using var tagFile = TagLib.File.Create(audioFilePath);
            string? c = tagFile.Tag.JoinedComposers;
            return string.IsNullOrWhiteSpace(c) ? null : c.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizePlainText(string raw)
    {
        return raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }
}
