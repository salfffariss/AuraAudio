using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Metadata;

namespace AuraAudio.Audio.Playlist;

/// <summary>
/// Layanan Parser & Exporter Playlist M3U / M3U8 berstandar industri audiophile.
/// Fitur:
/// 1. Ekspor antrean musik ke format M3U8 (UTF-8) dengan tag info durasi, artis, dan judul (#EXTINF).
/// 2. Impor playlist M3U/M3U8 dengan resolusi path absolut maupun relatif.
/// 3. Pencocokan cerdas dengan database lokal (DatabaseManager) atau auto-read TagLib# jika belum terindeks.
/// </summary>
public static class M3uPlaylistService
{
    private static readonly AudioMetadataReader _metadataReader = new();

    /// <summary>
    /// Mengekspor daftar lagu ke berkas playlist M3U8 (UTF-8).
    /// </summary>
    public static void ExportToM3u(string targetFilePath, IEnumerable<Track> tracks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFilePath);
        ArgumentNullException.ThrowIfNull(tracks);

        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        foreach (var track in tracks)
        {
            int durationSec = (int)Math.Max(0, track.Duration.TotalSeconds);
            string artist = string.IsNullOrWhiteSpace(track.Artist) ? "Unknown Artist" : track.Artist.Trim();
            string title = string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileNameWithoutExtension(track.FilePath) : track.Title.Trim();

            sb.AppendLine($"#EXTINF:{durationSec},{artist} - {title}");
            sb.AppendLine(track.FilePath);
        }

        string? dir = Path.GetDirectoryName(targetFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(targetFilePath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Mengimpor berkas M3U / M3U8 dan mengembalikannya sebagai kumpulan Track yang valid.
    /// </summary>
    public static List<Track> ImportFromM3u(string m3uFilePath, DatabaseManager db)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(m3uFilePath);
        ArgumentNullException.ThrowIfNull(db);

        if (!File.Exists(m3uFilePath))
        {
            throw new FileNotFoundException("Berkas playlist M3U/M3U8 tidak ditemukan.", m3uFilePath);
        }

        string baseDir = Path.GetDirectoryName(m3uFilePath) ?? string.Empty;
        var lines = File.ReadAllLines(m3uFilePath, Encoding.UTF8);
        var resultTracks = new List<Track>();

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
            {
                continue;
            }

            // Resolusi path (baik path relatif dari lokasi berkas m3u maupun path absolut)
            string fullPath = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(baseDir, line));

            if (!File.Exists(fullPath))
            {
                continue; // Lewati jika file tidak ditemukan
            }

            // 1. Cek di database perpustakaan
            var existingTrack = db.FindTrackByPath(fullPath);
            if (existingTrack != null)
            {
                resultTracks.Add(existingTrack);
            }
            else
            {
                // 2. Baca metadata secara instan menggunakan AudioMetadataReader jika belum ada di database
                try
                {
                    var meta = _metadataReader.ReadFile(fullPath);
                    long insertedId = db.InsertOrUpdateTrack(meta.Track);
                    resultTracks.Add(meta.Track with { Id = insertedId });
                }
                catch
                {
                    // Abaikan jika berkas korup
                }
            }
        }

        return resultTracks;
    }
}
