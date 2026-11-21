using System;
using System.Collections.Generic;
using System.IO;
using AuraAudio.Core.Models;
using Microsoft.Data.Sqlite;

namespace AuraAudio.Database;

/// <summary>
/// Pengelola koneksi dan operasi database SQLite lokal untuk AuraAudio.
/// Didesain thread-safe dengan Write-Ahead Logging (WAL), Compound B-Tree Indexing,
/// dan Batch Transactions untuk mengolah ribuan lagu per detik.
/// </summary>
public class DatabaseManager : IDisposable
{
    private const string HiResSqlCondition =
        "codec IN ('FLAC', 'ALAC', 'WAV', 'AIFF', 'DSD_DSF', 'DSD_DFF') AND (sample_rate_hz > 48000 OR bit_depth > 16 OR codec LIKE 'DSD%')";

    private const string HiResSqlConditionAliasedT =
        "t.codec IN ('FLAC', 'ALAC', 'WAV', 'AIFF', 'DSD_DSF', 'DSD_DFF') AND (t.sample_rate_hz > 48000 OR t.bit_depth > 16 OR t.codec LIKE 'DSD%')";

    private readonly string _connectionString;
    private bool _isDisposed;

    public string DatabasePath { get; }

    public DatabaseManager(string? customDbPath = null)
    {
        DatabasePath = customDbPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aura_library.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    /// <summary>
    /// Membuka koneksi baru ke SQLite.
    /// </summary>
    public SqliteConnection CreateConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Menginisialisasi database: menerapkan PRAGMA tuning, membuat tabel, dan membangun Compound B-Tree Index.
    /// </summary>
    public void Initialize()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        // 1. Terapkan PRAGMA Performance Tuning
        command.CommandText = DatabaseSchema.PragmaTuning;
        command.ExecuteNonQuery();

        // 2. Buat seluruh tabel inti jika belum ada
        command.CommandText = DatabaseSchema.CreateTablesSql;
        command.ExecuteNonQuery();

        // 3. Bangun Compound B-Tree Indexing untuk Sorting Instan
        command.CommandText = DatabaseSchema.CreateIndexesSql;
        command.ExecuteNonQuery();

        // 4. Migrasi / Isi Relasi Multi-Artis jika belum ada
        EnsureTrackArtistsPopulated(connection);

        // 5. Bersihkan baris play_history yatim (defensive orphan prune jika DB lama diubah di luar FK)
        using var pruneCmd = connection.CreateCommand();
        pruneCmd.CommandText = "DELETE FROM play_history WHERE track_id NOT IN (SELECT id FROM tracks);";
        pruneCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Menyimpan atau memperbarui data lagu tunggal ke dalam tabel 'tracks'.
    /// </summary>
    public long InsertOrUpdateTrack(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        PrepareInsertCommand(command, track);

        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Menyimpan kumpulan lagu dalam satu Transaksi Batch (Atomic Batch Transaction).
    /// Kunci utama kecepatan: Mampu memasukkan 10.000+ lagu dalam hitungan ratusan milidetik.
    /// </summary>
    public int InsertTracksBatch(IEnumerable<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO tracks (
                file_path, title, artist, sort_artist, album, album_artist,
                year, track_number, disc_number, duration_ms,
                codec, sample_rate_hz, bit_depth, channels, bitrate_kbps,
                file_size_bytes, date_added_ticks, has_embedded_cover, cover_art_cache_key
            ) VALUES (
                @file_path, @title, @artist, @sort_artist, @album, @album_artist,
                @year, @track_number, @disc_number, @duration_ms,
                @codec, @sample_rate_hz, @bit_depth, @channels, @bitrate_kbps,
                @file_size_bytes, @date_added_ticks, @has_embedded_cover, @cover_art_cache_key
            )
            ON CONFLICT(file_path) DO UPDATE SET
                title = excluded.title,
                artist = excluded.artist,
                sort_artist = excluded.sort_artist,
                album = excluded.album,
                album_artist = excluded.album_artist,
                year = excluded.year,
                track_number = excluded.track_number,
                disc_number = excluded.disc_number,
                duration_ms = excluded.duration_ms,
                codec = excluded.codec,
                sample_rate_hz = excluded.sample_rate_hz,
                bit_depth = excluded.bit_depth,
                channels = excluded.channels,
                bitrate_kbps = excluded.bitrate_kbps,
                file_size_bytes = excluded.file_size_bytes,
                has_embedded_cover = excluded.has_embedded_cover,
                cover_art_cache_key = excluded.cover_art_cache_key
            RETURNING id;
        """;

        // Tambahkan parameter kosong satu kali untuk dipakai ulang
        var pPath = command.Parameters.Add("@file_path", SqliteType.Text);
        var pTitle = command.Parameters.Add("@title", SqliteType.Text);
        var pArtist = command.Parameters.Add("@artist", SqliteType.Text);
        var pSortArtist = command.Parameters.Add("@sort_artist", SqliteType.Text);
        var pAlbum = command.Parameters.Add("@album", SqliteType.Text);
        var pAlbumArtist = command.Parameters.Add("@album_artist", SqliteType.Text);
        var pYear = command.Parameters.Add("@year", SqliteType.Integer);
        var pTrackNo = command.Parameters.Add("@track_number", SqliteType.Integer);
        var pDiscNo = command.Parameters.Add("@disc_number", SqliteType.Integer);
        var pDuration = command.Parameters.Add("@duration_ms", SqliteType.Integer);
        var pCodec = command.Parameters.Add("@codec", SqliteType.Text);
        var pSampleRate = command.Parameters.Add("@sample_rate_hz", SqliteType.Integer);
        var pBitDepth = command.Parameters.Add("@bit_depth", SqliteType.Integer);
        var pChannels = command.Parameters.Add("@channels", SqliteType.Integer);
        var pBitrate = command.Parameters.Add("@bitrate_kbps", SqliteType.Integer);
        var pFileSize = command.Parameters.Add("@file_size_bytes", SqliteType.Integer);
        var pDateAdded = command.Parameters.Add("@date_added_ticks", SqliteType.Integer);
        var pHasCover = command.Parameters.Add("@has_embedded_cover", SqliteType.Integer);
        var pCoverKey = command.Parameters.Add("@cover_art_cache_key", SqliteType.Text);

        using var deleteArtistsCmd = connection.CreateCommand();
        deleteArtistsCmd.Transaction = transaction;
        deleteArtistsCmd.CommandText = "DELETE FROM track_artists WHERE track_id = @track_id;";
        var pDelTrackId = deleteArtistsCmd.Parameters.Add("@track_id", SqliteType.Integer);

        using var insertArtistCmd = connection.CreateCommand();
        insertArtistCmd.Transaction = transaction;
        insertArtistCmd.CommandText = "INSERT OR IGNORE INTO track_artists (track_id, artist_name, is_primary) VALUES (@track_id, @name, @is_primary);";
        var pArtTrackId = insertArtistCmd.Parameters.Add("@track_id", SqliteType.Integer);
        var pArtName = insertArtistCmd.Parameters.Add("@name", SqliteType.Text);
        var pArtPrimary = insertArtistCmd.Parameters.Add("@is_primary", SqliteType.Integer);

        int count = 0;
        foreach (var track in tracks)
        {
            pPath.Value = track.FilePath;
            pTitle.Value = track.Title;
            pArtist.Value = track.Artist;
            pSortArtist.Value = track.SortArtist;
            pAlbum.Value = track.Album;
            pAlbumArtist.Value = track.AlbumArtist;
            pYear.Value = (object?)track.Year ?? DBNull.Value;
            pTrackNo.Value = track.TrackNumber;
            pDiscNo.Value = track.DiscNumber;
            pDuration.Value = (long)track.Duration.TotalMilliseconds;
            pCodec.Value = track.StreamSpec.Codec.ToString();
            pSampleRate.Value = (long)track.StreamSpec.SampleRateHz;
            pBitDepth.Value = (int)track.StreamSpec.BitDepth;
            pChannels.Value = (int)track.StreamSpec.Channels;
            pBitrate.Value = (long)track.StreamSpec.BitrateKbps;
            pFileSize.Value = track.FileSizeBytes;
            pDateAdded.Value = track.DateAdded.Ticks;
            pHasCover.Value = track.HasEmbeddedCover ? 1 : 0;
            pCoverKey.Value = (object?)track.CoverArtCacheKey ?? DBNull.Value;

            object? scalarResult = command.ExecuteScalar();
            long trackId = scalarResult != null ? Convert.ToInt64(scalarResult) : 0;
            count++;

            if (trackId > 0)
            {
                pDelTrackId.Value = trackId;
                deleteArtistsCmd.ExecuteNonQuery();

                var parsedArtists = AuraAudio.Metadata.ArtistParser.ParseArtists(track.Artist);
                for (int i = 0; i < parsedArtists.Count; i++)
                {
                    pArtTrackId.Value = trackId;
                    pArtName.Value = parsedArtists[i];
                    pArtPrimary.Value = i == 0 ? 1 : 0;
                    insertArtistCmd.ExecuteNonQuery();
                }

                if (!string.IsNullOrWhiteSpace(track.AlbumArtist))
                {
                    var albumArtists = AuraAudio.Metadata.ArtistParser.ParseArtists(track.AlbumArtist);
                    foreach (var aa in albumArtists)
                    {
                        if (!parsedArtists.Contains(aa, StringComparer.OrdinalIgnoreCase))
                        {
                            pArtTrackId.Value = trackId;
                            pArtName.Value = aa;
                            pArtPrimary.Value = 1;
                            insertArtistCmd.ExecuteNonQuery();
                        }
                    }
                }
            }
        }

        transaction.Commit();
        return count;
    }

    /// <summary>
    /// Mengambil kamus (Dictionary) path file dan ukuran fisik yang sudah ada di database.
    /// Digunakan oleh scanner untuk melewati file yang tidak berubah (Fast Re-scan Optimization).
    /// </summary>
    public Dictionary<string, long> GetExistingFileSignatures()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path, file_size_bytes FROM tracks;";

        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.GetString(0);
            long size = reader.GetInt64(1);
            result[path] = size;
        }

        return result;
    }

    /// <summary>
    /// Mengambil path file fisik audio berdasarkan cover_art_cache_key untuk on-demand thumbnail generation.
    /// </summary>
    public string? GetFilePathByCoverKey(string coverKey)
    {
        if (string.IsNullOrWhiteSpace(coverKey)) return null;
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path FROM tracks WHERE cover_art_cache_key = @key LIMIT 1;";
        command.Parameters.AddWithValue("@key", coverKey);
        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// Mengambil total jumlah lagu di database.
    /// </summary>
    public long GetTrackCount()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tracks;";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Mengambil data satu lagu berdasarkan Id.
    /// </summary>
    public Track? GetTrackById(long id)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tracks WHERE id = @id LIMIT 1;";
        command.Parameters.AddWithValue("@id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return MapTrackFromReader(reader);
    }

    /// <summary>
    /// Mengambil daftar lagu yang sudah terurut secara instan menggunakan Compound B-Tree Index,
    /// dilengkapi filter pencarian langsung di level SQLite jika kata kunci diberikan.
    /// </summary>
    public IReadOnlyList<Track> GetSortedTracks(TrackSortOption sortOption, int limit = 50, int offset = 0, string? filter = null)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        string orderByClause = GetOrderByClause(sortOption);
        if (string.IsNullOrWhiteSpace(filter))
        {
            command.CommandText = $"SELECT * FROM tracks ORDER BY {orderByClause} LIMIT @limit OFFSET @offset;";
        }
        else
        {
            command.CommandText = $"""
                SELECT * FROM tracks
                WHERE title LIKE @filter OR artist LIKE @filter OR album LIKE @filter OR album_artist LIKE @filter
                ORDER BY {orderByClause}
                LIMIT @limit OFFSET @offset;
            """;
            command.Parameters.AddWithValue("@filter", $"%{filter.Trim()}%");
        }
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);

        var list = new List<Track>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapTrackFromReader(reader));
        }

        return list;
    }

    /// <summary>
    /// Mengambil satu entitas Album teragregasi berdasarkan judul album (dan opsional artis)
    /// untuk navigasi cross-link instan dari baris lagu atau kapsul Now Playing.
    /// </summary>
    public Album? GetAlbumByTitle(string albumTitle, string? artistHint = null)
    {
        if (string.IsNullOrWhiteSpace(albumTitle)) return null;

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT 
                MIN(id) AS id,
                album,
                album_artist,
                MAX(year) AS year,
                COUNT(*) AS track_count,
                SUM(duration_ms) AS total_duration_ms,
                MAX(CASE WHEN {HiResSqlCondition} THEN 1 ELSE 0 END) AS is_hires,
                MAX(cover_art_cache_key) AS cover_art_cache_key
            FROM tracks
            WHERE album = @album COLLATE NOCASE
            GROUP BY album, album_artist
            ORDER BY CASE WHEN album_artist = @artist COLLATE NOCASE THEN 0 ELSE 1 END, track_count DESC
            LIMIT 1;
        """;
        command.Parameters.AddWithValue("@album", albumTitle.Trim());
        command.Parameters.AddWithValue("@artist", artistHint?.Trim() ?? string.Empty);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        long id = reader.GetInt64(0);
        string albumName = reader.GetString(1);
        string artistName = reader.GetString(2);
        int? year = reader.IsDBNull(3) ? null : reader.GetInt32(3);
        int trackCount = reader.GetInt32(4);
        long durMs = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
        bool isHiRes = reader.GetInt32(6) == 1;
        string? coverKey = reader.IsDBNull(7) ? null : reader.GetString(7);

        return new Album(id, albumName, artistName, year, trackCount, TimeSpan.FromMilliseconds(durMs), isHiRes, coverKey);
    }

    /// <summary>
    /// Mengambil seluruh album musik yang teragregasi secara instan menggunakan indeks compound.
    /// </summary>
    public IReadOnlyList<Album> GetAlbums(string? filter = null)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        string sql = $"""
            SELECT 
                MIN(id) AS id,
                album,
                album_artist,
                MAX(year) AS year,
                COUNT(*) AS track_count,
                SUM(duration_ms) AS total_duration_ms,
                MAX(CASE WHEN {HiResSqlCondition} THEN 1 ELSE 0 END) AS is_hires,
                MAX(cover_art_cache_key) AS cover_art_cache_key
            FROM tracks
        """;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            sql += " WHERE album LIKE @filter OR album_artist LIKE @filter ";
            command.Parameters.AddWithValue("@filter", $"%{filter}%");
        }

        sql += " GROUP BY album, album_artist ORDER BY album COLLATE NOCASE ASC;";
        command.CommandText = sql;

        var albums = new List<Album>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string albumName = reader.GetString(1);
            string artistName = reader.GetString(2);
            int? year = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            int trackCount = reader.GetInt32(4);
            long durMs = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
            bool isHiRes = reader.GetInt32(6) == 1;
            string? coverKey = reader.IsDBNull(7) ? null : reader.GetString(7);

            albums.Add(new Album(id, albumName, artistName, year, trackCount, TimeSpan.FromMilliseconds(durMs), isHiRes, coverKey));
        }

        return albums;
    }

    /// <summary>
    /// Memastikan tabel track_artists terisi dari data tracks yang ada (auto-migration untuk artis kolaborasi).
    /// </summary>
    public void EnsureTrackArtistsPopulated(SqliteConnection? existingConn = null)
    {
        try
        {
            var connection = existingConn ?? CreateConnection();
            bool disposeConn = existingConn == null;
            try
            {
                using var initTableCmd = connection.CreateCommand();
                initTableCmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS track_artists (
                        track_id            INTEGER NOT NULL,
                        artist_name         TEXT    NOT NULL,
                        is_primary          INTEGER NOT NULL DEFAULT 1,
                        PRIMARY KEY (track_id, artist_name),
                        FOREIGN KEY (track_id) REFERENCES tracks(id) ON DELETE CASCADE
                    );
                """;
                initTableCmd.ExecuteNonQuery();

                using var tracksCmd = connection.CreateCommand();
                tracksCmd.CommandText = """
                    SELECT t.id, t.artist, t.album_artist 
                    FROM tracks t
                    LEFT JOIN track_artists ta ON t.id = ta.track_id
                    WHERE ta.track_id IS NULL;
                """;
                using var reader = tracksCmd.ExecuteReader();

                var records = new List<(long TrackId, string Artist, string AlbumArtist)>();
                while (reader.Read())
                {
                    records.Add((
                        reader.GetInt64(0),
                        reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        reader.IsDBNull(2) ? string.Empty : reader.GetString(2)
                    ));
                }
                reader.Close();

                if (records.Count == 0) return;

                using var transaction = connection.BeginTransaction();
                using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = "INSERT OR IGNORE INTO track_artists (track_id, artist_name, is_primary) VALUES (@track_id, @name, @is_primary);";
                var pId = insertCmd.Parameters.Add("@track_id", SqliteType.Integer);
                var pName = insertCmd.Parameters.Add("@name", SqliteType.Text);
                var pPrimary = insertCmd.Parameters.Add("@is_primary", SqliteType.Integer);

                foreach (var r in records)
                {
                    var artists = AuraAudio.Metadata.ArtistParser.ParseArtists(r.Artist);
                    for (int i = 0; i < artists.Count; i++)
                    {
                        pId.Value = r.TrackId;
                        pName.Value = artists[i];
                        pPrimary.Value = i == 0 ? 1 : 0;
                        insertCmd.ExecuteNonQuery();
                    }

                    if (!string.IsNullOrWhiteSpace(r.AlbumArtist))
                    {
                        var albumArtists = AuraAudio.Metadata.ArtistParser.ParseArtists(r.AlbumArtist);
                        foreach (var aa in albumArtists)
                        {
                            if (!artists.Contains(aa, StringComparer.OrdinalIgnoreCase))
                            {
                                pId.Value = r.TrackId;
                                pName.Value = aa;
                                pPrimary.Value = 1;
                                insertCmd.ExecuteNonQuery();
                            }
                        }
                    }
                }

                transaction.Commit();
            }
            finally
            {
                if (disposeConn) connection.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EnsureTrackArtistsPopulated Warning] {ex.Message}");
        }
    }

    /// <summary>
    /// Mengambil seluruh artis musik yang teragregasi secara cerdas (Multi-Artist Splitting) dari basis data SQLite.
    /// Artis yang berkolaborasi ("A & B") akan dipecah sehingga A dan B memiliki profil artisnya masing-masing.
    /// </summary>
    public IReadOnlyList<Artist> GetArtists(string? filter = null)
    {
        EnsureTrackArtistsPopulated();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        string sql = """
            SELECT 
                ta.artist_name AS artist,
                COUNT(DISTINCT ta.track_id) AS track_count,
                COUNT(DISTINCT t.album) AS album_count,
                MAX(t.cover_art_cache_key) AS cover_key
            FROM track_artists ta
            JOIN tracks t ON ta.track_id = t.id
        """;

        if (!string.IsNullOrWhiteSpace(filter))
        {
            sql += " WHERE ta.artist_name LIKE @filter ";
            command.Parameters.AddWithValue("@filter", $"%{filter}%");
        }

        sql += """
            GROUP BY ta.artist_name COLLATE NOCASE
            ORDER BY ta.artist_name COLLATE NOCASE ASC;
        """;
        command.CommandText = sql;

        var artists = new List<Artist>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(0);
            int trackCount = reader.GetInt32(1);
            int albumCount = reader.GetInt32(2);
            string? coverKey = reader.IsDBNull(3) ? null : reader.GetString(3);
            artists.Add(new Artist(name, trackCount, albumCount, coverKey));
        }

        return artists;
    }

    /// <summary>
    /// Mengambil seluruh lagu di dalam satu album tertentu yang terurut berdasarkan disc dan nomor track.
    /// </summary>
    public IReadOnlyList<Track> GetTracksByAlbum(string album, string albumArtist)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT * FROM tracks 
            WHERE album = @album AND album_artist = @albumArtist 
            ORDER BY disc_number ASC, track_number ASC, title ASC;
        """;
        command.Parameters.AddWithValue("@album", album);
        command.Parameters.AddWithValue("@albumArtist", albumArtist ?? string.Empty);

        var list = new List<Track>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                list.Add(MapTrackFromReader(reader));
            }
        }

        if (list.Count == 0)
        {
            using var fallbackCmd = connection.CreateCommand();
            fallbackCmd.CommandText = """
                SELECT * FROM tracks 
                WHERE album = @album 
                ORDER BY disc_number ASC, track_number ASC, title ASC;
            """;
            fallbackCmd.Parameters.AddWithValue("@album", album);
            using var fallbackReader = fallbackCmd.ExecuteReader();
            while (fallbackReader.Read())
            {
                list.Add(MapTrackFromReader(fallbackReader));
            }
        }

        return list;
    }

    /// <summary>
    /// Mengambil seluruh album dari satu artis tertentu (termasuk album kolaborasi / split album).
    /// </summary>
    public IReadOnlyList<Album> GetAlbumsByArtist(string artistName)
    {
        EnsureTrackArtistsPopulated();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT 
                MIN(t.id) AS id,
                t.album,
                t.album_artist,
                MAX(t.year) AS year,
                COUNT(DISTINCT t.id) AS track_count,
                SUM(t.duration_ms) AS total_duration_ms,
                MAX(CASE WHEN {HiResSqlConditionAliasedT} THEN 1 ELSE 0 END) AS is_hires,
                MAX(t.cover_art_cache_key) AS cover_art_cache_key
            FROM tracks t
            JOIN track_artists ta ON t.id = ta.track_id
            WHERE ta.artist_name = @artist COLLATE NOCASE
            GROUP BY t.album, t.album_artist
            ORDER BY year DESC, t.album COLLATE NOCASE ASC;
        """;
        command.Parameters.AddWithValue("@artist", artistName);

        var list = new List<Album>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string albumName = reader.GetString(1);
            string artist = reader.GetString(2);
            int? year = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            int trackCount = reader.GetInt32(4);
            long durMs = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
            bool isHiRes = reader.GetInt32(6) == 1;
            string? coverKey = reader.IsDBNull(7) ? null : reader.GetString(7);

            list.Add(new Album(id, albumName, artist, year, trackCount, TimeSpan.FromMilliseconds(durMs), isHiRes, coverKey));
        }

        return list;
    }

    /// <summary>
    /// Mengambil seluruh lagu dari satu artis tertentu (baik lagu solo maupun lagu kolaborasi/featuring).
    /// </summary>
    public IReadOnlyList<Track> GetTracksByArtist(string artistName)
    {
        EnsureTrackArtistsPopulated();

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT DISTINCT t.* FROM tracks t
            JOIN track_artists ta ON t.id = ta.track_id
            WHERE ta.artist_name = @artist COLLATE NOCASE
            ORDER BY t.album COLLATE NOCASE ASC, t.disc_number ASC, t.track_number ASC, t.title ASC;
        """;
        command.Parameters.AddWithValue("@artist", artistName);

        var list = new List<Track>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapTrackFromReader(reader));
        }

        return list;
    }

    /// <summary>
    /// Memeriksa Query Plan SQLite untuk membuktikan penggunaan B-Tree Index.
    /// </summary>
    public string ExplainSortQueryPlan(TrackSortOption sortOption)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        string orderByClause = GetOrderByClause(sortOption);
        command.CommandText = $"EXPLAIN QUERY PLAN SELECT * FROM tracks ORDER BY {orderByClause} LIMIT 50 OFFSET 0;";

        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            string detail = reader.GetString(reader.GetOrdinal("detail"));
            lines.Add(detail);
        }

        return string.Join(" -> ", lines);
    }

    private static void PrepareInsertCommand(SqliteCommand command, Track track)
    {
        command.CommandText = """
            INSERT INTO tracks (
                file_path, title, artist, sort_artist, album, album_artist,
                year, track_number, disc_number, duration_ms,
                codec, sample_rate_hz, bit_depth, channels, bitrate_kbps,
                file_size_bytes, date_added_ticks, has_embedded_cover, cover_art_cache_key
            ) VALUES (
                @file_path, @title, @artist, @sort_artist, @album, @album_artist,
                @year, @track_number, @disc_number, @duration_ms,
                @codec, @sample_rate_hz, @bit_depth, @channels, @bitrate_kbps,
                @file_size_bytes, @date_added_ticks, @has_embedded_cover, @cover_art_cache_key
            )
            ON CONFLICT(file_path) DO UPDATE SET
                title = excluded.title,
                artist = excluded.artist,
                sort_artist = excluded.sort_artist,
                album = excluded.album,
                album_artist = excluded.album_artist,
                year = excluded.year,
                track_number = excluded.track_number,
                disc_number = excluded.disc_number,
                duration_ms = excluded.duration_ms,
                codec = excluded.codec,
                sample_rate_hz = excluded.sample_rate_hz,
                bit_depth = excluded.bit_depth,
                channels = excluded.channels,
                bitrate_kbps = excluded.bitrate_kbps,
                file_size_bytes = excluded.file_size_bytes,
                has_embedded_cover = excluded.has_embedded_cover,
                cover_art_cache_key = excluded.cover_art_cache_key;

            SELECT last_insert_rowid();
        """;

        command.Parameters.AddWithValue("@file_path", track.FilePath);
        command.Parameters.AddWithValue("@title", track.Title);
        command.Parameters.AddWithValue("@artist", track.Artist);
        command.Parameters.AddWithValue("@sort_artist", track.SortArtist);
        command.Parameters.AddWithValue("@album", track.Album);
        command.Parameters.AddWithValue("@album_artist", track.AlbumArtist);
        command.Parameters.AddWithValue("@year", (object?)track.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("@track_number", track.TrackNumber);
        command.Parameters.AddWithValue("@disc_number", track.DiscNumber);
        command.Parameters.AddWithValue("@duration_ms", (long)track.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("@codec", track.StreamSpec.Codec.ToString());
        command.Parameters.AddWithValue("@sample_rate_hz", (long)track.StreamSpec.SampleRateHz);
        command.Parameters.AddWithValue("@bit_depth", (int)track.StreamSpec.BitDepth);
        command.Parameters.AddWithValue("@channels", (int)track.StreamSpec.Channels);
        command.Parameters.AddWithValue("@bitrate_kbps", (long)track.StreamSpec.BitrateKbps);
        command.Parameters.AddWithValue("@file_size_bytes", track.FileSizeBytes);
        command.Parameters.AddWithValue("@date_added_ticks", track.DateAdded.Ticks);
        command.Parameters.AddWithValue("@has_embedded_cover", track.HasEmbeddedCover ? 1 : 0);
        command.Parameters.AddWithValue("@cover_art_cache_key", (object?)track.CoverArtCacheKey ?? DBNull.Value);
    }

    private static string GetOrderByClause(TrackSortOption sortOption) => sortOption switch
    {
        TrackSortOption.ArtistDiscography =>
            "sort_artist COLLATE NOCASE ASC, year ASC, album COLLATE NOCASE ASC, disc_number ASC, track_number ASC",

        TrackSortOption.TitleAscending =>
            "title COLLATE NOCASE ASC, sort_artist COLLATE NOCASE ASC",

        TrackSortOption.TitleDescending =>
            "title COLLATE NOCASE DESC, sort_artist COLLATE NOCASE ASC",

        TrackSortOption.YearNewestFirst =>
            "year DESC, album COLLATE NOCASE ASC, track_number ASC",

        TrackSortOption.YearOldestFirst =>
            "year ASC, album COLLATE NOCASE ASC, track_number ASC",

        TrackSortOption.RecentlyAdded =>
            "date_added_ticks DESC",

        TrackSortOption.HiResFirst =>
            "sample_rate_hz DESC, bit_depth DESC",

        TrackSortOption.AlbumAscending =>
            "album COLLATE NOCASE ASC, disc_number ASC, track_number ASC, title COLLATE NOCASE ASC",

        TrackSortOption.AlbumDescending =>
            "album COLLATE NOCASE DESC, disc_number ASC, track_number ASC, title COLLATE NOCASE ASC",

        TrackSortOption.FormatAscending =>
            "sample_rate_hz ASC, bit_depth ASC",

        TrackSortOption.DurationAscending =>
            "duration_ms ASC",

        TrackSortOption.DurationDescending =>
            "duration_ms DESC",

        _ => "id ASC"
    };

    private static Track MapTrackFromReader(SqliteDataReader reader)
    {
        long id = reader.GetInt64(reader.GetOrdinal("id"));
        string filePath = reader.GetString(reader.GetOrdinal("file_path"));
        string title = reader.GetString(reader.GetOrdinal("title"));
        string artist = reader.GetString(reader.GetOrdinal("artist"));
        string sortArtist = reader.GetString(reader.GetOrdinal("sort_artist"));
        string album = reader.GetString(reader.GetOrdinal("album"));
        string albumArtist = reader.GetString(reader.GetOrdinal("album_artist"));
        int? year = reader.IsDBNull(reader.GetOrdinal("year")) ? null : reader.GetInt32(reader.GetOrdinal("year"));
        int trackNumber = reader.GetInt32(reader.GetOrdinal("track_number"));
        int discNumber = reader.GetInt32(reader.GetOrdinal("disc_number"));
        long durationMs = reader.GetInt64(reader.GetOrdinal("duration_ms"));

        string codecStr = reader.GetString(reader.GetOrdinal("codec"));
        _ = Enum.TryParse<AudioCodec>(codecStr, out var codec);

        uint sampleRate = (uint)reader.GetInt64(reader.GetOrdinal("sample_rate_hz"));
        BitDepth bitDepth = (BitDepth)reader.GetInt32(reader.GetOrdinal("bit_depth"));
        ushort channels = (ushort)reader.GetInt32(reader.GetOrdinal("channels"));
        uint bitrate = (uint)reader.GetInt64(reader.GetOrdinal("bitrate_kbps"));

        long fileSizeBytes = reader.GetInt64(reader.GetOrdinal("file_size_bytes"));
        long ticks = reader.GetInt64(reader.GetOrdinal("date_added_ticks"));
        bool hasCover = reader.GetInt32(reader.GetOrdinal("has_embedded_cover")) == 1;
        string? coverKey = reader.IsDBNull(reader.GetOrdinal("cover_art_cache_key")) ? null : reader.GetString(reader.GetOrdinal("cover_art_cache_key"));

        var streamSpec = new AudioStreamSpec(codec, sampleRate, bitDepth, channels, bitrate);

        return new Track(
            id, filePath, title, artist, sortArtist, album, albumArtist,
            year, trackNumber, discNumber, TimeSpan.FromMilliseconds(durationMs),
            streamSpec, fileSizeBytes, new DateTime(ticks, DateTimeKind.Utc),
            hasCover, coverKey
        );
    }

    /// <summary>
    /// Menyimpan sesi pemutaran audio terakhir (lagu aktif, posisi milidetik, shuffle, repeat, dan antrean).
    /// </summary>
    public void SavePlaybackSession(long? currentTrackId, int currentTimeMs, PlaybackRepeatMode repeatMode, bool isShuffle, IEnumerable<long> queueTrackIds)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        string queueStr = string.Join(",", queueTrackIds);

        command.CommandText = """
            INSERT INTO playback_session (id, current_track_id, current_time_ms, repeat_mode, is_shuffle_enabled, queue_json, updated_at_ticks)
            VALUES (1, $trackId, $timeMs, $repeat, $shuffle, $queue, $ticks)
            ON CONFLICT(id) DO UPDATE SET
                current_track_id = excluded.current_track_id,
                current_time_ms = excluded.current_time_ms,
                repeat_mode = excluded.repeat_mode,
                is_shuffle_enabled = excluded.is_shuffle_enabled,
                queue_json = excluded.queue_json,
                updated_at_ticks = excluded.updated_at_ticks;
        """;

        command.Parameters.AddWithValue("$trackId", (object?)currentTrackId ?? DBNull.Value);
        command.Parameters.AddWithValue("$timeMs", currentTimeMs);
        command.Parameters.AddWithValue("$repeat", repeatMode.ToString());
        command.Parameters.AddWithValue("$shuffle", isShuffle ? 1 : 0);
        command.Parameters.AddWithValue("$queue", queueStr);
        command.Parameters.AddWithValue("$ticks", DateTime.UtcNow.Ticks);

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Membaca sesi pemutaran terakhir yang tersimpan di SQLite.
    /// </summary>
    public (long? CurrentTrackId, int CurrentTimeMs, PlaybackRepeatMode RepeatMode, bool IsShuffle, List<long> QueueTrackIds)? LoadPlaybackSession()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT current_track_id, current_time_ms, repeat_mode, is_shuffle_enabled, queue_json FROM playback_session WHERE id = 1;";
        using var reader = command.ExecuteReader();

        if (!reader.Read()) return null;

        long? trackId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
        int timeMs = reader.GetInt32(1);
        string repeatStr = reader.GetString(2);
        _ = Enum.TryParse<PlaybackRepeatMode>(repeatStr, out var repeatMode);
        bool isShuffle = reader.GetInt32(3) == 1;
        string queueStr = reader.GetString(4);

        var queueIds = new List<long>();
        if (!string.IsNullOrWhiteSpace(queueStr))
        {
            var parts = queueStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (long.TryParse(p.Trim(), out long qId)) queueIds.Add(qId);
            }
        }

        return (trackId, timeMs, repeatMode, isShuffle, queueIds);
    }

    /// <summary>
    /// Menyimpan satu nilai konfigurasi aplikasi ke tabel 'app_settings'.
    /// </summary>
    public void SetSetting(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings (key, value)
            VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
        """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Mengambil nilai konfigurasi dari tabel 'app_settings' berdasarkan kuncinya.
    /// </summary>
    public string? GetSetting(string key, string? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = @key LIMIT 1;";
        command.Parameters.AddWithValue("@key", key);
        var result = command.ExecuteScalar();
        return result != null ? Convert.ToString(result) : defaultValue;
    }

    /// <summary>
    /// Mencari data lagu berdasarkan path berkas absolut.
    /// </summary>
    public Track? FindTrackByPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tracks WHERE file_path = @file_path LIMIT 1;";
        command.Parameters.AddWithValue("@file_path", filePath);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return MapTrackFromReader(reader);
        }
        return null;
    }

    /// <summary>
    /// Membuat playlist baru beserta daftar id lagu di dalamnya.
    /// </summary>
    public long CreatePlaylist(string name, IEnumerable<long> trackIds)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(trackIds);

        var idList = trackIds.ToList();

        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            INSERT INTO playlists (name, created_at_ticks, track_count)
            VALUES (@name, @ticks, @count);
            SELECT last_insert_rowid();
        """;
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
        command.Parameters.AddWithValue("@count", idList.Count);

        long playlistId = (long)(command.ExecuteScalar() ?? 0L);

        for (int i = 0; i < idList.Count; i++)
        {
            using var itemCmd = connection.CreateCommand();
            itemCmd.Transaction = transaction;
            itemCmd.CommandText = """
                INSERT INTO playlist_tracks (playlist_id, track_id, position)
                VALUES (@pId, @tId, @pos);
            """;
            itemCmd.Parameters.AddWithValue("@pId", playlistId);
            itemCmd.Parameters.AddWithValue("@tId", idList[i]);
            itemCmd.Parameters.AddWithValue("@pos", i);
            itemCmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return playlistId;
    }

    /// <summary>
    /// Mengambil seluruh playlist yang tersimpan di database.
    /// </summary>
    public List<Playlist> GetPlaylists()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, track_count, created_at_ticks FROM playlists ORDER BY name COLLATE NOCASE ASC;";

        var list = new List<Playlist>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            string name = reader.GetString(1);
            int count = reader.GetInt32(2);
            long ticks = reader.GetInt64(3);
            list.Add(new Playlist(id, name, count, new DateTime(ticks, DateTimeKind.Utc)));
        }
        return list;
    }

    /// <summary>
    /// Mengambil seluruh lagu di dalam playlist tertentu secara berurutan.
    /// </summary>
    public List<Track> GetTracksForPlaylist(long playlistId)
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.* 
            FROM tracks t
            INNER JOIN playlist_tracks pt ON t.id = pt.track_id
            WHERE pt.playlist_id = @pId
            ORDER BY pt.position ASC;
        """;
        command.Parameters.AddWithValue("@pId", playlistId);

        var list = new List<Track>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapTrackFromReader(reader));
        }
        return list;
    }

    /// <summary>
    /// Menghapus playlist dari database beserta relasi lagunya.
    /// </summary>
    public bool DeletePlaylist(long playlistId)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        using var cmdTracks = connection.CreateCommand();
        cmdTracks.Transaction = transaction;
        cmdTracks.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id = @pId;";
        cmdTracks.Parameters.AddWithValue("@pId", playlistId);
        cmdTracks.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM playlists WHERE id = @pId;";
        command.Parameters.AddWithValue("@pId", playlistId);
        bool result = command.ExecuteNonQuery() > 0;

        transaction.Commit();
        return result;
    }

    /// <summary>
    /// Menambahkan satu lagu ke dalam playlist.
    /// </summary>
    public bool AddTrackToPlaylist(long playlistId, long trackId)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        // Ambil posisi maksimum saat ini
        using var maxCmd = connection.CreateCommand();
        maxCmd.Transaction = transaction;
        maxCmd.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM playlist_tracks WHERE playlist_id = @pId;";
        maxCmd.Parameters.AddWithValue("@pId", playlistId);
        long nextPos = Convert.ToInt64(maxCmd.ExecuteScalar() ?? 0);

        using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = transaction;
        insertCmd.CommandText = "INSERT INTO playlist_tracks (playlist_id, track_id, position) VALUES (@pId, @tId, @pos);";
        insertCmd.Parameters.AddWithValue("@pId", playlistId);
        insertCmd.Parameters.AddWithValue("@tId", trackId);
        insertCmd.Parameters.AddWithValue("@pos", nextPos);
        insertCmd.ExecuteNonQuery();

        // Update jumlah lagu di tabel playlists
        using var countCmd = connection.CreateCommand();
        countCmd.Transaction = transaction;
        countCmd.CommandText = "UPDATE playlists SET track_count = (SELECT COUNT(*) FROM playlist_tracks WHERE playlist_id = @pId) WHERE id = @pId;";
        countCmd.Parameters.AddWithValue("@pId", playlistId);
        countCmd.ExecuteNonQuery();

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Menghapus satu lagu dari dalam playlist.
    /// </summary>
    public bool RemoveTrackFromPlaylist(long playlistId, long trackId)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        using var delCmd = connection.CreateCommand();
        delCmd.Transaction = transaction;
        delCmd.CommandText = "DELETE FROM playlist_tracks WHERE playlist_id = @pId AND track_id = @tId;";
        delCmd.Parameters.AddWithValue("@pId", playlistId);
        delCmd.Parameters.AddWithValue("@tId", trackId);
        int rows = delCmd.ExecuteNonQuery();

        if (rows > 0)
        {
            using var countCmd = connection.CreateCommand();
            countCmd.Transaction = transaction;
            countCmd.CommandText = "UPDATE playlists SET track_count = (SELECT COUNT(*) FROM playlist_tracks WHERE playlist_id = @pId) WHERE id = @pId;";
            countCmd.Parameters.AddWithValue("@pId", playlistId);
            countCmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return rows > 0;
    }

    /// <summary>
    /// Mengubah nama playlist.
    /// </summary>
    public bool RenamePlaylist(long playlistId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playlists SET name = @name WHERE id = @pId;";
        command.Parameters.AddWithValue("@name", newName.Trim());
        command.Parameters.AddWithValue("@pId", playlistId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Mengambil seluruh direktori folder musik yang dipantau oleh aplikasi (ala MusicBee).
    /// </summary>
    public List<string> GetLibraryFolders()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM library_folders WHERE is_active = 1 ORDER BY path ASC;";

        var list = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    /// <summary>
    /// Menambahkan folder musik baru ke dalam daftar pantauan pustaka.
    /// </summary>
    public bool AddLibraryFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        string normalized = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library_folders (path, added_at_ticks, is_active)
            VALUES (@path, @ticks, 1)
            ON CONFLICT(path) DO UPDATE SET is_active = 1;
        """;
        command.Parameters.AddWithValue("@path", normalized);
        command.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Menghapus folder musik dari daftar pantauan pustaka.
    /// </summary>
    public bool RemoveLibraryFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        string normalized = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM library_folders WHERE path = @path;";
        command.Parameters.AddWithValue("@path", normalized);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Mengambil seluruh direktori folder yang dikecualikan dari pemindaian.
    /// </summary>
    public List<string> GetExcludedFolders()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM excluded_folders ORDER BY path ASC;";

        var list = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }
        return list;
    }

    /// <summary>
    /// Menambahkan folder yang ingin dikecualikan / diabaikan dari pemindaian.
    /// </summary>
    public bool AddExcludedFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        string normalized = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO excluded_folders (path, added_at_ticks)
            VALUES (@path, @ticks)
            ON CONFLICT(path) DO NOTHING;
        """;
        command.Parameters.AddWithValue("@path", normalized);
        command.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Menghapus folder dari daftar pengecualian.
    /// </summary>
    public bool RemoveExcludedFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        string normalized = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM excluded_folders WHERE path = @path;";
        command.Parameters.AddWithValue("@path", normalized);

        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Mengosongkan seluruh isi perpustakaan lagu, album, artis, dan antrean (Purge & Clean Slate).
    /// Sangat berguna jika pengguna secara tidak sengaja memindai folder salah (misal: aset suara game).
    /// </summary>
    public void ClearLibrary()
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            DELETE FROM play_history;
            DELETE FROM track_artists;
            DELETE FROM playlist_tracks;
            DELETE FROM playlists;
            DELETE FROM tracks;
            DELETE FROM albums;
            DELETE FROM artists;
            DELETE FROM playback_session;
        """;
        command.ExecuteNonQuery();
        transaction.Commit();

        try
        {
            using var vacuumCmd = connection.CreateCommand();
            vacuumCmd.CommandText = "VACUUM;";
            vacuumCmd.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>
    /// Removes tracks whose physical files no longer exist (or were excluded) in a single SQLite
    /// transaction. Foreign-key cascades automatically clean playlist_tracks, track_artists, and
    /// play_history; playlist track counts and stale playback_session references are synchronized.
    /// </summary>
    public int DeleteTracksByFilePaths(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        var distinctPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in filePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                distinctPaths.Add(path);
            }
        }

        if (distinctPaths.Count == 0)
        {
            return 0;
        }

        int removedCount = 0;
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        using var deleteCmd = connection.CreateCommand();
        deleteCmd.Transaction = transaction;
        deleteCmd.CommandText = "DELETE FROM tracks WHERE file_path = @path;";
        var pPath = deleteCmd.Parameters.Add("@path", Microsoft.Data.Sqlite.SqliteType.Text);
        deleteCmd.Prepare();

        foreach (var path in distinctPaths)
        {
            pPath.Value = path;
            removedCount += deleteCmd.ExecuteNonQuery();
        }

        if (removedCount > 0)
        {
            using var syncCmd = connection.CreateCommand();
            syncCmd.Transaction = transaction;
            syncCmd.CommandText = """
                UPDATE playlists
                SET track_count = (
                    SELECT COUNT(*) FROM playlist_tracks pt WHERE pt.playlist_id = playlists.id
                );
                UPDATE playback_session
                SET current_track_id = NULL
                WHERE current_track_id IS NOT NULL
                  AND current_track_id NOT IN (SELECT id FROM tracks);
            """;
            syncCmd.ExecuteNonQuery();
        }

        transaction.Commit();

        foreach (var path in distinctPaths)
        {
            AuraAudio.Metadata.AudioMetadataReader.InvalidateExtendedDetailsCache(path);
        }

        return removedCount;
    }

    /// <summary>
    /// Mengambil path direktori ekspor audio yang dikonfigurasi pengguna.
    /// </summary>
    public string GetExportDirectory()
    {
        string? val = GetSetting("export_directory");
        if (string.IsNullOrWhiteSpace(val))
        {
            val = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "AuraAudio_Exports");
        }
        return val;
    }

    /// <summary>
    /// Menyimpan path direktori ekspor audio kustom pengguna.
    /// </summary>
    public void SetExportDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        SetSetting("export_directory", directoryPath);
    }

    /// <summary>
    /// Memeriksa apakah pemindaian otomatis saat startup aktif.
    /// </summary>
    public bool GetAutoScanOnStartup()
    {
        string? val = GetSetting("auto_scan_on_startup");
        return val == null || val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mengatur opsi pemindaian otomatis saat startup.
    /// </summary>
    public void SetAutoScanOnStartup(bool enabled)
    {
        SetSetting("auto_scan_on_startup", enabled ? "1" : "0");
    }

    /// <summary>
    /// Records a playback event into play_history, deduplicating rapid repeats (<= 30s)
    /// and pruning entries beyond the newest 500 records.
    /// </summary>
    public void RecordPlayHistory(long trackId)
    {
        if (trackId <= 0) return;

        using var connection = CreateConnection();
        long nowTicks = DateTime.UtcNow.Ticks;

        using (var checkCmd = connection.CreateCommand())
        {
            checkCmd.CommandText = "SELECT track_id, played_at_ticks FROM play_history ORDER BY played_at_ticks DESC LIMIT 1;";
            using var reader = checkCmd.ExecuteReader();
            if (reader.Read())
            {
                long lastTrackId = reader.GetInt64(0);
                long lastTicks = reader.GetInt64(1);
                if (lastTrackId == trackId && TimeSpan.FromTicks(nowTicks - lastTicks).TotalSeconds < 30.0)
                {
                    return;
                }
            }
        }

        using var tx = connection.BeginTransaction();
        using (var insertCmd = connection.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = "INSERT INTO play_history (track_id, played_at_ticks) VALUES (@trackId, @ticks);";
            insertCmd.Parameters.AddWithValue("@trackId", trackId);
            insertCmd.Parameters.AddWithValue("@ticks", nowTicks);
            insertCmd.ExecuteNonQuery();
        }

        using (var pruneCmd = connection.CreateCommand())
        {
            pruneCmd.Transaction = tx;
            pruneCmd.CommandText = """
                DELETE FROM play_history
                WHERE id NOT IN (
                    SELECT id FROM play_history ORDER BY played_at_ticks DESC LIMIT 500
                );
            """;
            pruneCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// Returns the most recently played tracks in reverse chronological order.
    /// </summary>
    public IReadOnlyList<Track> GetRecentPlayHistory(int limit = 50)
    {
        var results = new List<Track>();
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.*
            FROM play_history h
            INNER JOIN tracks t ON t.id = h.track_id
            ORDER BY h.played_at_ticks DESC
            LIMIT @limit;
        """;
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(MapTrackFromReader(reader));
        }
        return results;
    }

    /// <summary>
    /// Returns recently added albums grouped by (album, album_artist) using tracks.date_added_ticks DESC.
    /// </summary>
    public IReadOnlyList<Album> GetRecentlyAddedAlbums(int limit = 12)
    {
        var results = new List<Album>();
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                MIN(id) AS id,
                album,
                album_artist,
                MAX(year) AS year,
                COUNT(*) AS track_count,
                SUM(duration_ms) AS total_duration_ms,
                MAX(CASE WHEN {HiResSqlCondition} THEN 1 ELSE 0 END) AS is_hi_res,
                MAX(cover_art_cache_key) AS cover_key
            FROM tracks
            GROUP BY album COLLATE NOCASE, album_artist COLLATE NOCASE
            ORDER BY MAX(date_added_ticks) DESC
            LIMIT @limit;
        """;
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Album(
                id: reader.GetInt64(0),
                title: reader.GetString(1),
                artist: reader.GetString(2),
                year: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                trackCount: reader.GetInt32(4),
                totalDuration: TimeSpan.FromMilliseconds(reader.GetInt64(5)),
                isHiRes: reader.GetInt32(6) == 1,
                coverArtCacheKey: reader.IsDBNull(7) ? null : reader.GetString(7)
            ));
        }
        return results;
    }

    /// <summary>
    /// Returns albums containing Hi-Res lossless or DSD tracks ordered by highest sample rate / bit depth.
    /// </summary>
    public IReadOnlyList<Album> GetHiResAlbums(int limit = 12)
    {
        var results = new List<Album>();
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT
                MIN(id) AS id,
                album,
                album_artist,
                MAX(year) AS year,
                COUNT(*) AS track_count,
                SUM(duration_ms) AS total_duration_ms,
                1 AS is_hi_res,
                MAX(cover_art_cache_key) AS cover_key
            FROM tracks
            GROUP BY album COLLATE NOCASE, album_artist COLLATE NOCASE
            HAVING MAX(CASE WHEN {HiResSqlCondition} THEN 1 ELSE 0 END) = 1
            ORDER BY MAX(sample_rate_hz) DESC, MAX(bit_depth) DESC, album COLLATE NOCASE ASC
            LIMIT @limit;
        """;
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new Album(
                id: reader.GetInt64(0),
                title: reader.GetString(1),
                artist: reader.GetString(2),
                year: reader.IsDBNull(3) ? null : reader.GetInt32(3),
                trackCount: reader.GetInt32(4),
                totalDuration: TimeSpan.FromMilliseconds(reader.GetInt64(5)),
                isHiRes: true,
                coverArtCacheKey: reader.IsDBNull(7) ? null : reader.GetString(7)
            ));
        }
        return results;
    }

    /// <summary>
    /// Updates stored technical audio format columns for an existing track row in place
    /// when authoritative format resolution corrects a previously scanned spec (e.g. 24-bit ALAC).
    /// </summary>
    public void UpdateTrackStreamSpec(long trackId, AudioStreamSpec spec)
    {
        if (trackId <= 0 || spec == null) return;

        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE tracks
            SET codec = @codec,
                sample_rate_hz = @sample_rate_hz,
                bit_depth = @bit_depth,
                channels = @channels,
                bitrate_kbps = @bitrate_kbps
            WHERE id = @id;
        """;
        command.Parameters.AddWithValue("@id", trackId);
        command.Parameters.AddWithValue("@codec", spec.Codec.ToString());
        command.Parameters.AddWithValue("@sample_rate_hz", (long)spec.SampleRateHz);
        command.Parameters.AddWithValue("@bit_depth", (int)spec.BitDepth);
        command.Parameters.AddWithValue("@channels", (int)spec.Channels);
        command.Parameters.AddWithValue("@bitrate_kbps", (long)spec.BitrateKbps);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Clears all recorded play events from the play_history table.
    /// </summary>
    public void ClearPlayHistory()
    {
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM play_history;";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
