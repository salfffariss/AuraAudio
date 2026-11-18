using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using AuraAudio.Core.Models;

namespace AuraAudio.Metadata;

/// <summary>
/// Mewakili satu entri trek di dalam file CUE sheet CD-Rip.
/// </summary>
public record CueTrackEntry
{
    public int TrackNumber { get; init; }
    public string Title { get; init; }
    public string Performer { get; init; }
    public TimeSpan StartTime { get; init; }
    public TimeSpan Duration { get; set; }

    public CueTrackEntry(int trackNumber, string title, string performer, TimeSpan startTime)
    {
        TrackNumber = trackNumber;
        Title = title;
        Performer = performer;
        StartTime = startTime;
        Duration = TimeSpan.Zero;
    }
}

/// <summary>
/// Parser berkemampuan tinggi untuk membaca file CUE Sheet (.cue).
/// Memecah file audio CD-rip masif menjadi daftar lagu individual virtual
/// dengan penanda waktu presisi frame Red Book (1 detik = 75 frame).
/// </summary>
public class CueSheetParser
{
    private static readonly Regex CommandRegex = new(
        @"^\s*([A-Z_]+)\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// Mem-parsing konten file CUE sheet dari path lokal.
    /// </summary>
    public IReadOnlyList<CueTrackEntry> Parse(string cueFilePath, TimeSpan totalAudioDuration = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cueFilePath);
        if (!File.Exists(cueFilePath))
        {
            throw new FileNotFoundException("File CUE sheet tidak ditemukan.", cueFilePath);
        }

        string text = File.ReadAllText(cueFilePath);
        return ParseText(text, totalAudioDuration);
    }

    /// <summary>
    /// Mem-parsing teks CUE sheet dengan toleransi seluruh variasi baris (\r\n, \n, \r).
    /// </summary>
    public IReadOnlyList<CueTrackEntry> ParseText(string cueText, TimeSpan totalAudioDuration = default)
    {
        var lines = new List<string>();
        using var reader = new StringReader(cueText);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            lines.Add(line);
        }

        return ParseLines(lines, totalAudioDuration);
    }

    /// <summary>
    /// Mem-parsing kumpulan baris CUE sheet.
    /// </summary>
    public IReadOnlyList<CueTrackEntry> ParseLines(IEnumerable<string> lines, TimeSpan totalAudioDuration = default)
    {
        var tracks = new List<CueTrackEntry>();

        string albumPerformer = "Unknown Artist";
        string albumTitle = "Unknown Album";
        string currentFile = string.Empty;

        int currentTrackNo = 0;
        string currentTrackTitle = string.Empty;
        string currentTrackPerformer = string.Empty;
        TimeSpan? currentStartTime = null;

        void CommitCurrentTrack()
        {
            if (currentTrackNo > 0 && currentStartTime.HasValue)
            {
                string title = string.IsNullOrWhiteSpace(currentTrackTitle) ? $"Track {currentTrackNo}" : currentTrackTitle;
                string performer = string.IsNullOrWhiteSpace(currentTrackPerformer) ? albumPerformer : currentTrackPerformer;

                tracks.Add(new CueTrackEntry(currentTrackNo, title, performer, currentStartTime.Value));
            }

            currentTrackTitle = string.Empty;
            currentTrackPerformer = string.Empty;
            currentStartTime = null;
        }

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = CommandRegex.Match(line);
            if (!match.Success) continue;

            string command = match.Groups[1].Value.ToUpperInvariant();
            string args = match.Groups[2].Value.Trim().Trim('"');

            switch (command)
            {
                case "PERFORMER":
                    if (currentTrackNo == 0)
                        albumPerformer = args;
                    else
                        currentTrackPerformer = args;
                    break;

                case "TITLE":
                    if (currentTrackNo == 0)
                        albumTitle = args;
                    else
                        currentTrackTitle = args;
                    break;

                case "FILE":
                    currentFile = args;
                    break;

                case "TRACK":
                    // Format: TRACK 01 AUDIO
                    CommitCurrentTrack();
                    var trackParts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (trackParts.Length > 0 && int.TryParse(trackParts[0], out int tNo))
                    {
                        currentTrackNo = tNo;
                    }
                    break;

                case "INDEX":
                    // Format: INDEX 01 mm:ss:ff (75 frames/sec)
                    var indexParts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (indexParts.Length >= 2 && indexParts[0] == "01")
                    {
                        currentStartTime = ParseTimeSpanFromIndex(indexParts[1]);
                    }
                    break;
            }
        }

        // Simpan track terakhir
        CommitCurrentTrack();

        // Hitung durasi masing-masing track berdasarkan selisih waktu StartTime ke track berikutnya
        for (int i = 0; i < tracks.Count; i++)
        {
            if (i < tracks.Count - 1)
            {
                tracks[i].Duration = tracks[i + 1].StartTime - tracks[i].StartTime;
            }
            else if (totalAudioDuration > tracks[i].StartTime)
            {
                tracks[i].Duration = totalAudioDuration - tracks[i].StartTime;
            }
            else
            {
                tracks[i].Duration = TimeSpan.FromMinutes(3); // Default fallback jika durasi total tidak diketahui
            }
        }

        return tracks;
    }

    /// <summary>
    /// Mengonversi format indeks Red Book "mm:ss:ff" ke TimeSpan.
    /// Di mana 1 frame = 1/75 detik (standar Compact Disc Audio).
    /// </summary>
    public static TimeSpan ParseTimeSpanFromIndex(string indexTime)
    {
        var parts = indexTime.Split(':');
        if (parts.Length != 3) return TimeSpan.Zero;

        if (int.TryParse(parts[0], out int minutes) &&
            int.TryParse(parts[1], out int seconds) &&
            int.TryParse(parts[2], out int frames))
        {
            double ms = (minutes * 60 + seconds) * 1000.0 + (frames * 1000.0 / 75.0);
            return TimeSpan.FromMilliseconds(ms);
        }

        return TimeSpan.Zero;
    }
}
