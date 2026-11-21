using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Scanner;
using Xunit;

namespace AuraAudio.Tests;

public sealed class LibraryScannerRescanTests
{
    private static string WriteTaggedWav(string dir, string fileName, string title, string artist, string album)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, fileName);

        int sampleRate = 44_100;
        int channels = 2;
        int bitsPerSample = 16;
        int blockAlign = channels * (bitsPerSample / 8);
        int frames = 4410; // 0.1 s
        int dataBytes = frames * blockAlign;

        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write("RIFF"u8);
            bw.Write(36 + dataBytes);
            bw.Write("WAVE"u8);
            bw.Write("fmt "u8);
            bw.Write(16);
            bw.Write((short)1);
            bw.Write((short)channels);
            bw.Write(sampleRate);
            bw.Write(sampleRate * blockAlign);
            bw.Write((short)blockAlign);
            bw.Write((short)bitsPerSample);
            bw.Write("data"u8);
            bw.Write(dataBytes);
            bw.Write(new byte[dataBytes]);
        }

        using (var tagFile = TagLib.File.Create(path))
        {
            tagFile.Tag.Title = title;
            tagFile.Tag.Performers = new[] { artist };
            tagFile.Tag.AlbumArtists = new[] { artist };
            tagFile.Tag.Album = album;
            tagFile.Save();
        }

        return path;
    }

    [Fact]
    public async Task RescanAfterPhysicalFileDeleted_PrunesTrackAlbumArtistAndUpdatesPlaylistCount()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"aura_rescan_{Guid.NewGuid():N}");
        string musicDir = Path.Combine(tempRoot, "Music");
        string dbPath = Path.Combine(tempRoot, "library.db");
        Directory.CreateDirectory(musicDir);

        try
        {
            string fileA = WriteTaggedWav(musicDir, "song_a.wav", "Song Alpha", "Artist Alpha", "Album Alpha");
            string fileB = WriteTaggedWav(musicDir, "song_b.wav", "Song Beta", "Artist Beta", "Album Beta");

            using var db = new DatabaseManager(dbPath);
            db.Initialize();
            db.AddLibraryFolder(musicDir);

            var scanner = new LibraryScanner(db);
            var initial = await scanner.ScanDirectoriesAsync(new[] { musicDir });

            Assert.Equal(2, initial.Added);
            Assert.Equal(0, initial.Removed);
            Assert.Equal(2, db.GetTrackCount());
            Assert.Equal(2, db.GetAlbums().Count);
            Assert.Equal(2, db.GetArtists().Count);

            var tracks = db.GetSortedTracks(TrackSortOption.TitleAscending, limit: 10);
            var trackA = tracks.Single(t => t.Title == "Song Alpha");
            var trackB = tracks.Single(t => t.Title == "Song Beta");

            // Add both to a playlist and record play history on surviving Track A.
            long playlistId = db.CreatePlaylist("MyMix", new[] { trackA.Id, trackB.Id });
            db.RecordPlayHistory(trackA.Id);
            db.RecordPlayHistory(trackB.Id);

            // Physically delete Song Beta from disk, then rescan.
            File.Delete(fileB);
            Assert.False(File.Exists(fileB));

            var rescan = await scanner.ScanDirectoriesAsync(new[] { musicDir });

            Assert.Equal(1, rescan.Removed);
            Assert.Equal(0, rescan.Added);
            Assert.Equal(1, rescan.Skipped);
            Assert.Equal(1, db.GetTrackCount());

            var remainingTracks = db.GetSortedTracks(TrackSortOption.TitleAscending, limit: 10);
            Assert.Single(remainingTracks);
            Assert.Equal("Song Alpha", remainingTracks[0].Title);
            Assert.Equal(trackA.DateAdded, remainingTracks[0].DateAdded);

            // Ghost album and ghost artist must be gone.
            var remainingAlbums = db.GetAlbums();
            Assert.Single(remainingAlbums);
            Assert.Equal("Album Alpha", remainingAlbums[0].Title);

            var remainingArtists = db.GetArtists();
            Assert.Single(remainingArtists);
            Assert.Equal("Artist Alpha", remainingArtists[0].Name);

            // Playlist track count & membership updated; surviving track play history preserved.
            var updatedPlaylist = db.GetPlaylists().Single(p => p.Id == playlistId);
            Assert.Equal(1, updatedPlaylist.TrackCount);
            var playlistTracks = db.GetTracksForPlaylist(playlistId);
            Assert.Single(playlistTracks);
            Assert.Equal(trackA.Id, playlistTracks[0].Id);

            var history = db.GetRecentPlayHistory(10);
            Assert.Single(history);
            Assert.Equal(trackA.Id, history[0].Id);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RescanSingleDirectory_DoesNotPruneValidTracksInSiblingDirectory()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"aura_rescan_multi_{Guid.NewGuid():N}");
        string dirA = Path.Combine(tempRoot, "Music");
        string dirB = Path.Combine(tempRoot, "Music2"); // Prefix edge case: "Music" vs "Music2"
        string dbPath = Path.Combine(tempRoot, "library.db");

        try
        {
            string fileA1 = WriteTaggedWav(dirA, "a1.wav", "A1", "Artist A", "Album A");
            string fileA2 = WriteTaggedWav(dirA, "a2.wav", "A2", "Artist A", "Album A");
            string fileB1 = WriteTaggedWav(dirB, "b1.wav", "B1", "Artist B", "Album B");

            using var db = new DatabaseManager(dbPath);
            db.Initialize();
            var scanner = new LibraryScanner(db);

            await scanner.ScanDirectoriesAsync(new[] { dirA, dirB });
            Assert.Equal(3, db.GetTrackCount());

            // Delete a2.wav inside Music, then scan ONLY Music (not Music2).
            File.Delete(fileA2);
            var singleRescan = await scanner.ScanDirectoryAsync(dirA);

            Assert.Equal(1, singleRescan.Removed);
            Assert.Equal(2, db.GetTrackCount());
            var remaining = db.GetSortedTracks(TrackSortOption.TitleAscending, limit: 10).Select(t => t.FilePath).ToList();
            Assert.Contains(fileA1, remaining, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(fileB1, remaining, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(fileA2, remaining, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RescanAfterAllFilesDeletedOrSubfolderExcluded_RemovesDeletedAndExcludedTracks()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"aura_rescan_all_{Guid.NewGuid():N}");
        string rootDir = Path.Combine(tempRoot, "Library");
        string excludedDir = Path.Combine(rootDir, "Samples");
        string dbPath = Path.Combine(tempRoot, "library.db");

        try
        {
            string rootFile = WriteTaggedWav(rootDir, "main.wav", "Main Track", "Main Artist", "Main Album");
            string excludedFile = WriteTaggedWav(excludedDir, "sample.wav", "Sample Track", "Sample Artist", "Sample Album");

            using var db = new DatabaseManager(dbPath);
            db.Initialize();
            var scanner = new LibraryScanner(db);

            await scanner.ScanDirectoriesAsync(new[] { rootDir });
            Assert.Equal(2, db.GetTrackCount());

            // Exclude Samples subfolder and rescan -> sample.wav pruned, main.wav remains.
            db.AddExcludedFolder(excludedDir);
            var afterExclude = await scanner.ScanDirectoriesAsync(new[] { rootDir });
            Assert.Equal(1, afterExclude.Removed);
            Assert.Equal(1, db.GetTrackCount());

            // Delete last remaining file and rescan -> 0 tracks, 0 albums, 0 artists.
            File.Delete(rootFile);
            var afterDeleteAll = await scanner.ScanDirectoriesAsync(new[] { rootDir });
            Assert.Equal(1, afterDeleteAll.Removed);
            Assert.Equal(0, db.GetTrackCount());
            Assert.Empty(db.GetAlbums());
            Assert.Empty(db.GetArtists());
            Assert.True(File.Exists(excludedFile)); // Never deletes physical files on disk
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
