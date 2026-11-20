namespace AuraAudio.Database;

/// <summary>
/// Definisi DDL (Data Definition Language), Compound B-Tree Indexing,
/// dan tuning performa SQLite untuk AuraAudio.
/// Disesuaikan untuk menampung 100.000+ lagu dengan latensi kueri di bawah 2 milidetik.
/// </summary>
public static class DatabaseSchema
{
    /// <summary>
    /// Konfigurasi PRAGMA SQLite untuk performa baca/tulis masif (Audiophile Grade Engine).
    /// </summary>
    public const string PragmaTuning = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;
        PRAGMA cache_size = -64000;
        PRAGMA foreign_keys = ON;
    """;

    /// <summary>
    /// Skema tabel utama untuk lagu, album, dan artis.
    /// </summary>
    public const string CreateTablesSql = """
        -- 1. TABEL TRACKS (Lagu)
        CREATE TABLE IF NOT EXISTS tracks (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            file_path           TEXT    NOT NULL UNIQUE,
            title               TEXT    NOT NULL,
            artist              TEXT    NOT NULL,
            sort_artist         TEXT    NOT NULL,
            album               TEXT    NOT NULL,
            album_artist        TEXT    NOT NULL,
            year                INTEGER,
            track_number        INTEGER NOT NULL DEFAULT 0,
            disc_number         INTEGER NOT NULL DEFAULT 1,
            duration_ms         INTEGER NOT NULL,
            codec               TEXT    NOT NULL,
            sample_rate_hz      INTEGER NOT NULL,
            bit_depth           INTEGER NOT NULL,
            channels            INTEGER NOT NULL DEFAULT 2,
            bitrate_kbps        INTEGER NOT NULL DEFAULT 0,
            file_size_bytes     INTEGER NOT NULL DEFAULT 0,
            date_added_ticks    INTEGER NOT NULL,
            has_embedded_cover  INTEGER NOT NULL DEFAULT 0,
            cover_art_cache_key TEXT
        );

        -- 2. TABEL ALBUMS (Ringkasan Rilis untuk Grid UI)
        CREATE TABLE IF NOT EXISTS albums (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            title               TEXT    NOT NULL,
            artist              TEXT    NOT NULL,
            year                INTEGER,
            track_count         INTEGER NOT NULL DEFAULT 0,
            total_duration_ms   INTEGER NOT NULL DEFAULT 0,
            is_hi_res           INTEGER NOT NULL DEFAULT 0,
            cover_art_cache_key TEXT,
            UNIQUE(title, artist)
        );

        -- 3. TABEL ARTISTS (Daftar Musisi untuk Sidebar Navigasi)
        CREATE TABLE IF NOT EXISTS artists (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            name                TEXT    NOT NULL UNIQUE,
            sort_name           TEXT    NOT NULL,
            album_count         INTEGER NOT NULL DEFAULT 0,
            track_count         INTEGER NOT NULL DEFAULT 0
        );

        -- 4. TABEL PLAYBACK_SESSION (Persistensi Sesi Pemutaran & Antrean Lagu)
        CREATE TABLE IF NOT EXISTS playback_session (
            id                  INTEGER PRIMARY KEY CHECK (id = 1),
            current_track_id    INTEGER,
            current_time_ms     INTEGER NOT NULL DEFAULT 0,
            repeat_mode         TEXT    NOT NULL DEFAULT 'Off',
            is_shuffle_enabled  INTEGER NOT NULL DEFAULT 0,
            queue_json          TEXT    NOT NULL DEFAULT '[]',
            updated_at_ticks    INTEGER NOT NULL
        );

        -- 5. TABEL APP_SETTINGS (Konfigurasi Hardware DAC, Latensi, dan Driver)
        CREATE TABLE IF NOT EXISTS app_settings (
            key                 TEXT    PRIMARY KEY,
            value               TEXT    NOT NULL
        );

        -- 6. TABEL PLAYLISTS (Daftar Putar Kustom Pengguna)
        CREATE TABLE IF NOT EXISTS playlists (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            name                TEXT    NOT NULL UNIQUE,
            created_at_ticks    INTEGER NOT NULL,
            track_count         INTEGER NOT NULL DEFAULT 0
        );

        -- 7. TABEL PLAYLIST_TRACKS (Relasi Lagu ke Playlist dengan Urutan Posisi)
        CREATE TABLE IF NOT EXISTS playlist_tracks (
            playlist_id         INTEGER NOT NULL,
            track_id            INTEGER NOT NULL,
            position            INTEGER NOT NULL,
            PRIMARY KEY (playlist_id, position),
            FOREIGN KEY (playlist_id) REFERENCES playlists(id) ON DELETE CASCADE,
            FOREIGN KEY (track_id) REFERENCES tracks(id) ON DELETE CASCADE
        );

        -- 8. TABEL LIBRARY_FOLDERS (Direktori Musik Monitored ala MusicBee)
        CREATE TABLE IF NOT EXISTS library_folders (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            path                TEXT    NOT NULL UNIQUE,
            added_at_ticks      INTEGER NOT NULL,
            is_active           INTEGER NOT NULL DEFAULT 1
        );

        -- 9. TABEL EXCLUDED_FOLDERS (Folder yang Dikecualikan dari Pemindaian ala MusicBee)
        CREATE TABLE IF NOT EXISTS excluded_folders (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            path                TEXT    NOT NULL UNIQUE,
            added_at_ticks      INTEGER NOT NULL
        );

        -- 10. TABEL TRACK_ARTISTS (Relasi Cerdas Multi-Artis Kolaborasi ala Spotify/MusicBee)
        CREATE TABLE IF NOT EXISTS track_artists (
            track_id            INTEGER NOT NULL,
            artist_name         TEXT    NOT NULL,
            is_primary          INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (track_id, artist_name),
            FOREIGN KEY (track_id) REFERENCES tracks(id) ON DELETE CASCADE
        );

        -- 11. TABEL PLAY_HISTORY (Riwayat Pemutaran untuk Home Jump Back In & Queue History)
        CREATE TABLE IF NOT EXISTS play_history (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            track_id            INTEGER NOT NULL,
            played_at_ticks     INTEGER NOT NULL,
            FOREIGN KEY (track_id) REFERENCES tracks(id) ON DELETE CASCADE
        );
    """;

    /// <summary>
    /// Kumpulan Compound B-Tree Index untuk pengurutan instan multi-level.
    /// Didesain agar kueri ORDER BY tidak memerlukan komputasi ulang (Zero Temp B-Tree overhead).
    /// </summary>
    public const string CreateIndexesSql = """
        -- 1. Indeks Multi-Level Diskografi Artis (Artis A-Z -> Tahun -> Album -> Track)
        CREATE INDEX IF NOT EXISTS idx_tracks_artist_discography ON tracks (
            sort_artist COLLATE NOCASE ASC,
            year ASC,
            album COLLATE NOCASE ASC,
            disc_number ASC,
            track_number ASC
        );

        -- 2. Indeks Judul Lagu A-Z
        CREATE INDEX IF NOT EXISTS idx_tracks_title_asc ON tracks (
            title COLLATE NOCASE ASC,
            sort_artist COLLATE NOCASE ASC
        );

        -- 3. Indeks Judul Lagu Z-A
        CREATE INDEX IF NOT EXISTS idx_tracks_title_desc ON tracks (
            title COLLATE NOCASE DESC,
            sort_artist COLLATE NOCASE ASC
        );

        -- 4. Indeks Tahun Rilis Terbaru (Newest First)
        CREATE INDEX IF NOT EXISTS idx_tracks_year_desc ON tracks (
            year DESC,
            album COLLATE NOCASE ASC,
            track_number ASC
        );

        -- 5. Indeks Tahun Rilis Terlama (Oldest First)
        CREATE INDEX IF NOT EXISTS idx_tracks_year_asc ON tracks (
            year ASC,
            album COLLATE NOCASE ASC,
            track_number ASC
        );

        -- 6. Indeks Baru Ditambahkan (Recently Added)
        CREATE INDEX IF NOT EXISTS idx_tracks_date_added ON tracks (
            date_added_ticks DESC
        );

        -- 7. Indeks Format Resolusi Tertinggi (Hi-Res First: Sample Rate & Bit Depth)
        CREATE INDEX IF NOT EXISTS idx_tracks_hires ON tracks (
            sample_rate_hz DESC,
            bit_depth DESC
        );

        -- 8. Indeks Multi-Artis Kolaborasi
        CREATE INDEX IF NOT EXISTS idx_track_artists_name ON track_artists (
            artist_name COLLATE NOCASE ASC
        );
        CREATE INDEX IF NOT EXISTS idx_track_artists_track ON track_artists (
            track_id ASC
        );

        -- 9. Indeks Riwayat Pemutaran Terbaru (Play History)
        CREATE INDEX IF NOT EXISTS idx_play_history_time ON play_history (
            played_at_ticks DESC
        );
    """;
}
