using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AuraAudio.Core.Models;
using TagLib;

namespace AuraAudio.Metadata;

/// <summary>
/// Hasil pembacaan metadata lengkap dari file audio tunggal.
/// </summary>
public record MetadataResult
{
    public Track Track { get; init; }
    public byte[]? CoverArtBytes { get; init; }
    public string? CoverArtMimeType { get; init; }

    public MetadataResult(Track track, byte[]? coverArtBytes = null, string? coverArtMimeType = null)
    {
        Track = track ?? throw new ArgumentNullException(nameof(track));
        CoverArtBytes = coverArtBytes;
        CoverArtMimeType = coverArtMimeType;
    }
}

/// <summary>
/// Pembaca metadata audio tunggal berkinerja tinggi.
/// Menggunakan TagLib# untuk mengekstrak tag ID3, Vorbis Comments, DSD headers,
/// serta properti audio fisik (Sample Rate, Bit Depth, Codec) secara akurat.
/// </summary>
public class AudioMetadataReader
{
    private static readonly Regex TrackNumberPrefixRegex = new(
        @"^(\d{1,3})\s*[-._\s]\s*(.+)$",
        RegexOptions.Compiled
    );

    private static readonly ConcurrentDictionary<string, TrackExtendedDetails> ExtendedDetailsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mengekstrak metadata mendalam (Composer, Conductor, Genre, Copyright, Bitrate, Lyrics)
    /// secara on-demand untuk halaman Now Playing Music Detail Stage dengan L1 RAM Cache.
    /// </summary>
    public static TrackExtendedDetails ReadExtendedDetails(Track? track)
    {
        if (track == null) return TrackExtendedDetails.Empty;

        if (ExtendedDetailsCache.TryGetValue(track.FilePath, out var cached))
        {
            return cached;
        }

        var fallback = TrackExtendedDetails.FromTrackFallback(track);
        if (!System.IO.File.Exists(track.FilePath))
        {
            return fallback;
        }

        try
        {
            using var tagFile = TagLib.File.Create(track.FilePath);
            var tag = tagFile.Tag;
            var props = tagFile.Properties;

            string performers = !string.IsNullOrWhiteSpace(tag.JoinedPerformers)
                ? tag.JoinedPerformers.Trim()
                : fallback.Performers;

            string albumArtist = !string.IsNullOrWhiteSpace(tag.JoinedAlbumArtists)
                ? tag.JoinedAlbumArtists.Trim()
                : fallback.AlbumArtist;

            string composer = !string.IsNullOrWhiteSpace(tag.JoinedComposers)
                ? tag.JoinedComposers.Trim()
                : "—";

            string conductor = !string.IsNullOrWhiteSpace(tag.Conductor)
                ? tag.Conductor.Trim()
                : "—";

            string genre = !string.IsNullOrWhiteSpace(tag.JoinedGenres)
                ? tag.JoinedGenres.Trim()
                : "—";

            string copyright = !string.IsNullOrWhiteSpace(tag.Copyright)
                ? tag.Copyright.Trim()
                : (!string.IsNullOrWhiteSpace(tag.Publisher)
                    ? tag.Publisher.Trim()
                    : (!string.IsNullOrWhiteSpace(tag.Comment) ? tag.Comment.Trim() : "—"));

            int yearVal = tag.Year > 0 ? (int)tag.Year : (track.Year ?? 0);
            string yearText = yearVal > 0 ? yearVal.ToString(CultureInfo.InvariantCulture) : "—";

            int trackNo = tag.Track > 0 ? (int)tag.Track : Math.Max(1, track.TrackNumber);
            int discNo = tag.Disc > 0 ? (int)tag.Disc : Math.Max(1, track.DiscNumber);
            string trackDiscText = tag.TrackCount > 0
                ? $"Track {trackNo:D2}/{tag.TrackCount:D2} • Disc {discNo}"
                : $"Track {trackNo:D2} • Disc {discNo}";

            uint kbps = track.StreamSpec.BitrateKbps;
            if (kbps == 0 && props != null && props.AudioBitrate > 0)
            {
                kbps = (uint)props.AudioBitrate;
            }
            if (kbps == 0 && track.Duration.TotalSeconds > 0.5 && track.FileSizeBytes > 0)
            {
                kbps = (uint)Math.Round((track.FileSizeBytes * 8.0) / (track.Duration.TotalSeconds * 1000.0));
            }
            string bitrateText = kbps > 0 ? $"{kbps:N0} kbps" : fallback.BitrateText;

            string? lyrics = !string.IsNullOrWhiteSpace(tag.Lyrics) ? tag.Lyrics.Trim() : null;

            var details = fallback with
            {
                Performers = performers,
                AlbumArtist = albumArtist,
                Composer = composer,
                Conductor = conductor,
                Genre = genre,
                YearText = yearText,
                TrackDiscText = trackDiscText,
                Copyright = copyright,
                BitrateText = bitrateText,
                Lyrics = lyrics
            };

            if (ExtendedDetailsCache.Count > 512)
            {
                ExtendedDetailsCache.Clear();
            }
            ExtendedDetailsCache[track.FilePath] = details;
            return details;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Membaca file audio dari harddisk dan mengonversinya menjadi entitas Track lengkap.
    /// Dilengkapi proteksi fallback jika metadata tag kosong/rusak (membaca struktur nama file).
    /// </summary>
    public MetadataResult ReadFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!System.IO.File.Exists(filePath))
        {
            throw new FileNotFoundException("File audio tidak ditemukan di harddisk.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        long fileSizeBytes = fileInfo.Length;
        DateTime dateAdded = DateTime.UtcNow;

        string extension = fileInfo.Extension.ToLowerInvariant();

        // Buka file menggunakan TagLib# terlebih dahulu agar codec sebenarnya (misal AAC vs ALAC dalam .m4a) terbaca akurat
        using var tagFile = TagLib.File.Create(filePath);
        var properties = tagFile.Properties;
        var tag = tagFile.Tag;

        AudioCodec codec = DetectCodec(extension, properties);

        // 1. Ekstraksi Spesifikasi Fisik Audio
        uint sampleRateHz = (uint)Math.Max(properties.AudioSampleRate, 44_100);
        ushort channels = (ushort)Math.Max(properties.AudioChannels, 2);
        uint bitrateKbps = (uint)Math.Max(properties.AudioBitrate, 0);
        BitDepth bitDepth = DetectBitDepth(properties, codec);
        TimeSpan duration = properties.Duration > TimeSpan.Zero ? properties.Duration : TimeSpan.FromSeconds(1);

        var streamSpec = new AudioStreamSpec(codec, sampleRateHz, bitDepth, channels, bitrateKbps);

        // 2. Ekstraksi Tag Teks & Fallback Cerdas
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        string fallbackTitle = fileNameWithoutExt;
        int parsedTrackNumber = (int)tag.Track;

        // Jika nomor track di tag kosong (0), coba tebak dari awalan nama file (misal: "01 - Song.flac")
        var match = TrackNumberPrefixRegex.Match(fileNameWithoutExt);
        if (match.Success)
        {
            if (parsedTrackNumber <= 0 && int.TryParse(match.Groups[1].Value, out int trackNo))
            {
                parsedTrackNumber = trackNo;
            }
            fallbackTitle = match.Groups[2].Value.Trim();
        }

        string title = string.IsNullOrWhiteSpace(tag.Title) ? fallbackTitle : tag.Title.Trim();
        string artist = string.IsNullOrWhiteSpace(tag.FirstPerformer) ? "Unknown Artist" : tag.FirstPerformer.Trim();
        string album = string.IsNullOrWhiteSpace(tag.Album) ? GetParentFolderName(filePath) : tag.Album.Trim();
        string albumArtist = string.IsNullOrWhiteSpace(tag.FirstAlbumArtist) ? artist : tag.FirstAlbumArtist.Trim();
        int? year = tag.Year > 0 ? (int)tag.Year : null;
        int discNumber = tag.Disc > 0 ? (int)tag.Disc : 1;

        // 3. Deteksi Cover Art Tertanam (Embedded Album Cover) atau Folder Image Fallback
        bool hasCover = tag.Pictures.Length > 0;
        byte[]? coverBytes = null;
        string? coverMime = null;
        string? coverCacheKey = null;

        if (hasCover)
        {
            var pic = tag.Pictures[0];
            coverBytes = pic.Data.Data;
            coverMime = pic.MimeType;

            // Buat kunci hash unik untuk cache agar cover tidak diekstrak berulang-ulang
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(coverBytes);
            coverCacheKey = Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
        else
        {
            // Cek berkas gambar di dalam folder direktori lagu jika tidak ada cover tertanam
            try
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string[] candidates = ["cover.jpg", "folder.jpg", "front.jpg", "album.jpg", "cover.png", "folder.png", "front.png", "album.png"];
                    foreach (var name in candidates)
                    {
                        string candidatePath = Path.Combine(dir, name);
                        if (System.IO.File.Exists(candidatePath))
                        {
                            coverBytes = System.IO.File.ReadAllBytes(candidatePath);
                            coverMime = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                            hasCover = true;
                            using var sha256 = SHA256.Create();
                            byte[] hash = sha256.ComputeHash(coverBytes);
                            coverCacheKey = Convert.ToHexString(hash)[..16].ToLowerInvariant();
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        // 4. Bangun Entitas Track Baku
        var track = new Track(
            id: 0,
            filePath: filePath,
            title: title,
            artist: artist,
            sortArtist: Track.NormalizeSortArtist(artist),
            album: album,
            albumArtist: albumArtist,
            year: year,
            trackNumber: parsedTrackNumber > 0 ? parsedTrackNumber : 1,
            discNumber: discNumber,
            duration: duration,
            streamSpec: streamSpec,
            fileSizeBytes: fileSizeBytes,
            dateAdded: dateAdded,
            hasEmbeddedCover: hasCover,
            coverArtCacheKey: coverCacheKey
        );

        return new MetadataResult(track, coverBytes, coverMime);
    }

    /// <summary>
    /// Mendeteksi AudioCodec sebenarnya dari ekstensi dan deskripsi header stream TagLib#
    /// agar berkas .m4a AAC (lossy) tidak salah dikira ALAC (lossless).
    /// </summary>
    internal static AudioCodec DetectCodec(string extension, Properties? properties)
    {
        string codecInfo = BuildCodecDescription(properties);

        return extension switch
        {
            ".flac"            => AudioCodec.FLAC,
            ".wav"             => AudioCodec.WAV,
            ".dsf"             => AudioCodec.DSD_DSF,
            ".dff"             => AudioCodec.DSD_DFF,
            ".alac"            => AudioCodec.ALAC,
            ".aac"             => AudioCodec.AAC,
            ".aiff" or ".aif"  => AudioCodec.AIFF,
            ".mp3"             => AudioCodec.MP3,
            ".opus"            => AudioCodec.OPUS,
            ".ogg"             => codecInfo.Contains("opus", StringComparison.OrdinalIgnoreCase)
                                    ? AudioCodec.OPUS
                                    : AudioCodec.OGG_VORBIS,
            ".m4a" or ".mp4"   => (codecInfo.Contains("alac", StringComparison.OrdinalIgnoreCase) ||
                                   codecInfo.Contains("apple lossless", StringComparison.OrdinalIgnoreCase))
                                    ? AudioCodec.ALAC
                                    : AudioCodec.AAC,
            _                  => DetectCodecFromDescription(codecInfo)
        };
    }

    private static string BuildCodecDescription(Properties? properties)
    {
        if (properties == null) return string.Empty;
        string desc = properties.Description ?? string.Empty;
        if (properties.Codecs == null) return desc;

        var parts = new System.Text.StringBuilder(desc);
        foreach (var c in properties.Codecs)
        {
            if (c?.Description != null)
            {
                parts.Append(' ').Append(c.Description);
            }
        }
        return parts.ToString();
    }

    private static AudioCodec DetectCodecFromDescription(string codecInfo)
    {
        if (codecInfo.Contains("alac", StringComparison.OrdinalIgnoreCase) ||
            codecInfo.Contains("apple lossless", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.ALAC;
        if (codecInfo.Contains("flac", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.FLAC;
        if (codecInfo.Contains("pcm", StringComparison.OrdinalIgnoreCase) ||
            codecInfo.Contains("wav", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.WAV;
        if (codecInfo.Contains("aiff", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.AIFF;
        if (codecInfo.Contains("opus", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.OPUS;
        if (codecInfo.Contains("vorbis", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.OGG_VORBIS;
        if (codecInfo.Contains("mp3", StringComparison.OrdinalIgnoreCase) ||
            codecInfo.Contains("mpeg", StringComparison.OrdinalIgnoreCase))
            return AudioCodec.MP3;
        return AudioCodec.AAC;
    }

    /// <summary>
    /// Mendeteksi bit depth audio secara akurat dari properti TagLib.
    /// </summary>
    private static BitDepth DetectBitDepth(Properties properties, AudioCodec codec)
    {
        if (codec is AudioCodec.DSD_DSF or AudioCodec.DSD_DFF)
        {
            return BitDepth.DSD1Bit;
        }

        int bits = properties.BitsPerSample;
        return bits switch
        {
            >= 32 => BitDepth.Bit32Float,
            >= 24 => BitDepth.Bit24,
            _     => BitDepth.Bit16
        };
    }

    /// <summary>
    /// Mengambil nama folder induk sebagai fallback nama album jika tag album kosong.
    /// </summary>
    private static string GetParentFolderName(string filePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            return string.IsNullOrWhiteSpace(dir) ? "Unknown Album" : new DirectoryInfo(dir).Name;
        }
        catch
        {
            return "Unknown Album";
        }
    }
}
