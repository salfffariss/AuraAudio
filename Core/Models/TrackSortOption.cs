namespace AuraAudio.Core.Models;

/// <summary>
/// Opsi pengurutan (Sorting) lagu yang didukung oleh AuraAudio.
/// Setiap opsi memiliki indeks B-Tree khusus di SQLite agar eksekusinya instan (&lt; 2 milidetik).
/// </summary>
public enum TrackSortOption
{
    /// <summary>
    /// Pengurutan Hierarkis Diskografi Artis:
    /// Nama Artis (A-Z) ──► Tahun Rilis (Tertua ke Terbaru) ──► Nama Album ──► Nomor Track.
    /// Memastikan seluruh album satu musisi tersusun rapi kronologis.
    /// </summary>
    ArtistDiscography,

    /// <summary>Judul lagu dari abjad A sampai Z</summary>
    TitleAscending,

    /// <summary>Judul lagu dari abjad Z sampai A</summary>
    TitleDescending,

    /// <summary>Tahun rilis album terbaru ke terlama (contoh: 2024 ──► 1960)</summary>
    YearNewestFirst,

    /// <summary>Tahun rilis album terlama ke terbaru (contoh: 1960 ──► 2024)</summary>
    YearOldestFirst,

    /// <summary>Lagu yang baru saja ditambahkan ke pustaka</summary>
    RecentlyAdded,

    /// <summary>Lagu dengan resolusi tertinggi lebih dahulu (DSD &amp; 192kHz di urutan atas)</summary>
    HiResFirst,

    /// <summary>Nama album dari abjad A sampai Z</summary>
    AlbumAscending,

    /// <summary>Nama album dari abjad Z sampai A</summary>
    AlbumDescending,

    /// <summary>Format resolusi standar lebih dahulu (sample rate &amp; bit depth menaik)</summary>
    FormatAscending,

    /// <summary>Durasi lagu terpendek ke terpanjang</summary>
    DurationAscending,

    /// <summary>Durasi lagu terpanjang ke terpendek</summary>
    DurationDescending
}
