using System;
using System.IO;

namespace AuraAudio.Core.Models;

/// <summary>
/// Entitas utama yang merepresentasikan satu trek lagu di pustaka musik lokal.
/// Didesain bersih, efisien dalam memori, dan mendukung pustaka berskala masif (100.000+ lagu).
/// </summary>
public record Track
{
    /// <summary>Kunci primer di database lokal (SQLite RowId)</summary>
    public long Id { get; init; }

    /// <summary>Lokasi absolut file di harddisk</summary>
    public string FilePath { get; init; }

    /// <summary>Judul lagu (Title tag)</summary>
    public string Title { get; init; }

    /// <summary>Nama artis penampil</summary>
    public string Artist { get; init; }

    /// <summary>
    /// Nama artis khusus sorting cerdas (misal: "Beatles, The" alih-alih "The Beatles").
    /// Memastikan pengurutan abjad di database selalu rapi.
    /// </summary>
    public string SortArtist { get; init; }

    /// <summary>Judul album</summary>
    public string Album { get; init; }

    /// <summary>Artis album (untuk album kompilasi / Various Artists)</summary>
    public string AlbumArtist { get; init; }

    /// <summary>Tahun rilis lagu/album</summary>
    public int? Year { get; init; }

    /// <summary>Nomor urut lagu di dalam album</summary>
    public int TrackNumber { get; init; }

    /// <summary>Nomor disc (untuk album yang terdiri dari beberapa CD/piringan)</summary>
    public int DiscNumber { get; init; }

    /// <summary>Durasi total lagu</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Spesifikasi teknis fisik audio (Sample Rate, Bit Depth, Codec)</summary>
    public AudioStreamSpec StreamSpec { get; init; }

    /// <summary>Ukuran file fisik di harddisk dalam satuan byte</summary>
    public long FileSizeBytes { get; init; }

    /// <summary>Waktu pertama kali file dimasukkan ke dalam database pustaka</summary>
    public DateTime DateAdded { get; init; }

    /// <summary>Menandakan apakah file ini memiliki gambar cover art yang tertanam di dalamnya</summary>
    public bool HasEmbeddedCover { get; init; }

    /// <summary>Kunci hash untuk mengambil cover art dari folder cache berkecepatan tinggi</summary>
    public string? CoverArtCacheKey { get; init; }

    /// <summary>
    /// Format durasi dalam bentuk teks keterbacaan tinggi (contoh: "04:35" atau "1:12:05").
    /// </summary>
    public string FormattedDuration =>
        Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"mm\:ss");

    /// <summary>
    /// Konstruktor lengkap untuk inisialisasi Track.
    /// </summary>
    public Track(
        long id,
        string filePath,
        string title,
        string artist,
        string sortArtist,
        string album,
        string albumArtist,
        int? year,
        int trackNumber,
        int discNumber,
        TimeSpan duration,
        AudioStreamSpec streamSpec,
        long fileSizeBytes,
        DateTime dateAdded,
        bool hasEmbeddedCover = false,
        string? coverArtCacheKey = null)
    {
        Id = id;
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(filePath) : title;
        Artist = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;
        SortArtist = string.IsNullOrWhiteSpace(sortArtist) ? NormalizeSortArtist(Artist) : sortArtist;
        Album = string.IsNullOrWhiteSpace(album) ? "Unknown Album" : album;
        AlbumArtist = string.IsNullOrWhiteSpace(albumArtist) ? Artist : albumArtist;
        Year = year;
        TrackNumber = trackNumber;
        DiscNumber = discNumber;
        Duration = duration;
        StreamSpec = streamSpec ?? throw new ArgumentNullException(nameof(streamSpec));
        FileSizeBytes = fileSizeBytes;
        DateAdded = dateAdded;
        HasEmbeddedCover = hasEmbeddedCover;
        CoverArtCacheKey = coverArtCacheKey;
    }

    /// <summary>
    /// Logika otomatis normalisasi nama artis untuk sorting:
    /// Menghapus kata sandang awalan seperti "The ", "A ", "An " agar terurut alami.
    /// </summary>
    public static string NormalizeSortArtist(string artistName)
    {
        if (string.IsNullOrWhiteSpace(artistName)) return "Unknown Artist";

        string trimmed = artistName.Trim();
        if (trimmed.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
        {
            return $"{trimmed[4..]}, The";
        }
        if (trimmed.StartsWith("A ", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 2)
        {
            return $"{trimmed[2..]}, A";
        }

        return trimmed;
    }
}
