using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Metadata;

namespace AuraAudio.Scanner;

/// <summary>
/// Mesin pemindai folder musik berbasis multithreading (Multithreaded Background Scanner).
/// Mampu memindai puluhan ribu file di background tanpa membuat UI macet,
/// dilengkapi optimasi Fast Re-scan dan Batch Transactions ke SQLite.
/// </summary>
public class LibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".wav", ".m4a", ".alac", ".aiff", ".aif", ".dsf", ".dff", ".ogg", ".opus"
    };

    private readonly DatabaseManager _databaseManager;
    private readonly AudioMetadataReader _metadataReader;
    private readonly ThumbnailCacheManager _thumbnailManager = new();
    private readonly int _batchSize;

    public LibraryScanner(DatabaseManager databaseManager, int batchSize = 250)
    {
        _databaseManager = databaseManager ?? throw new ArgumentNullException(nameof(databaseManager));
        _metadataReader = new AudioMetadataReader();
        _batchSize = Math.Max(50, batchSize);
    }

    /// <summary>
    /// Memindai satu folder direktori secara rekursif dan asinkron.
    /// Menggunakan alur kerja paralel untuk ekstraksi tag dan penyimpanan batch ke SQLite.
    /// </summary>
    public Task<ScanProgress> ScanDirectoryAsync(
        string directoryPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ScanDirectoryInternalAsync(directoryPath, progress, cancellationToken, persistSpecVersion: true);

    private async Task<ScanProgress> ScanDirectoryInternalAsync(
        string directoryPath,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        bool persistSpecVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Direktori musik tidak ditemukan: {directoryPath}");
        }

        var stopwatch = Stopwatch.StartNew();

        // 1. Dapatkan daftar tanda tangan file yang sudah ada di database untuk Fast Re-scan
        var existingSignatures = _databaseManager.GetExistingFileSignatures();
        var excludedFolders = _databaseManager.GetExcludedFolders();
        const string currentSpecVersion = "2";
        bool needsOneTimeSpecRefresh = !string.Equals(
            _databaseManager.GetSetting("metadata_spec_version"),
            currentSpecVersion,
            StringComparison.Ordinal);

        // 2. Temukan seluruh file audio yang didukung di dalam folder (Rekursif, melompati folder yang dikecualikan)
        var allAudioFiles = EnumerateAudioFiles(directoryPath, excludedFolders).ToList();
        int totalFound = allAudioFiles.Count;

        int processedCount = 0;
        int addedCount = 0;
        int skippedCount = 0;
        int failedCount = 0;

        // Pisahkan file yang baru/berubah vs file yang sudah ada (tidak berubah)
        var filesToProcess = new List<string>(totalFound);
        foreach (var file in allAudioFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fi = new FileInfo(file);
                if (!needsOneTimeSpecRefresh && existingSignatures.TryGetValue(file, out long existingSize) && existingSize == fi.Length)
                {
                    // File sudah ada dan ukurannya sama persis: Lewati pembacaan tag!
                    skippedCount++;
                    processedCount++;
                }
                else
                {
                    filesToProcess.Add(file);
                }
            }
            catch
            {
                filesToProcess.Add(file);
            }
        }

        // Lapor progres awal (jika banyak yang diskip)
        progress?.Report(new ScanProgress(totalFound, processedCount, addedCount, skippedCount, "Memulai pemrosesan...", stopwatch.Elapsed));

        // 3. Ekstraksi Metadata Secara Paralel Menggunakan Thread Pool
        if (filesToProcess.Count > 0)
        {
            int maxConcurrency = Math.Max(2, Environment.ProcessorCount - 1);
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            };

            var trackBatch = new ConcurrentBag<Track>();
            var lastReportTime = Stopwatch.StartNew();

            await Parallel.ForEachAsync(filesToProcess, parallelOptions, async (filePath, ct) =>
            {
                try
                {
                    var result = _metadataReader.ReadFile(filePath);
                    trackBatch.Add(result.Track);

                    if (result.CoverArtBytes != null && !string.IsNullOrEmpty(result.Track.CoverArtCacheKey))
                    {
                        try
                        {
                            _thumbnailManager.GetOrCreateThumbnail(result.CoverArtBytes, result.Track.CoverArtCacheKey, ThumbnailSize.Grid);
                            _thumbnailManager.GetOrCreateThumbnail(result.CoverArtBytes, result.Track.CoverArtCacheKey, ThumbnailSize.Mini);
                        }
                        catch { }
                    }

                    int currentProcessed = Interlocked.Increment(ref processedCount);
                    int currentAdded = Interlocked.Increment(ref addedCount);

                    // Simpan ke SQLite secara berkala jika batch sudah mencapai batas
                    if (trackBatch.Count >= _batchSize)
                    {
                        FlushBatch(trackBatch);
                    }

                    // Laporkan progres ke UI secara berkala (dibatasi tiap 50ms agar tidak membebani UI thread)
                    if (lastReportTime.ElapsedMilliseconds > 50 || currentProcessed == totalFound)
                    {
                        lastReportTime.Restart();
                        progress?.Report(new ScanProgress(
                            totalFound,
                            currentProcessed,
                            currentAdded,
                            Volatile.Read(ref skippedCount),
                            Path.GetFileName(filePath),
                            stopwatch.Elapsed,
                            Volatile.Read(ref failedCount)
                        ));
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref processedCount);
                    Interlocked.Increment(ref skippedCount);
                    Interlocked.Increment(ref failedCount);
                    Trace.TraceWarning($"[LibraryScanner] Skipped unreadable audio file '{filePath}': {ex.GetType().Name}: {ex.Message}");
                }

                await Task.CompletedTask;
            });

            // Simpan sisa track yang belum ter-flush
            FlushBatch(trackBatch);
        }

        if (needsOneTimeSpecRefresh && !cancellationToken.IsCancellationRequested && persistSpecVersion)
        {
            try { _databaseManager.SetSetting("metadata_spec_version", currentSpecVersion); } catch { }
        }

        stopwatch.Stop();
        var finalReport = new ScanProgress(totalFound, processedCount, addedCount, skippedCount, "Selesai", stopwatch.Elapsed, failedCount);
        progress?.Report(finalReport);

        return finalReport;
    }

    /// <summary>
    /// Mengambil file dalam batch dari ConcurrentBag lalu menyimpannya dalam 1 transaksi SQLite.
    /// </summary>
    private void FlushBatch(ConcurrentBag<Track> trackBatch)
    {
        var itemsToSave = new List<Track>();
        while (trackBatch.TryTake(out var track))
        {
            itemsToSave.Add(track);
        }

        if (itemsToSave.Count > 0)
        {
            _databaseManager.InsertTracksBatch(itemsToSave);
        }
    }

    /// <summary>
    /// Enumerator direktori yang aman dari error UnauthorizedAccessException.
    /// </summary>
    private static IEnumerable<string> EnumerateAudioFiles(string rootPath, IReadOnlyList<string>? excludedFolders = null)
    {
        var queue = new Queue<string>();
        queue.Enqueue(rootPath);

        while (queue.Count > 0)
        {
            string currentDir = queue.Dequeue();

            // Lewati jika currentDir merupakan atau berada di dalam folder yang dikecualikan
            if (excludedFolders != null && excludedFolders.Any(ex =>
                currentDir.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                currentDir.StartsWith(ex + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Jelajahi file di direktori saat ini
            string[] files;
            try
            {
                files = Directory.GetFiles(currentDir);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string file in files)
            {
                string ext = Path.GetExtension(file);
                if (!string.IsNullOrEmpty(ext) && SupportedExtensions.Contains(ext))
                {
                    yield return file;
                }
            }

            // Masukkan subdirektori ke dalam antrean
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string subDir in subDirs)
            {
                // Lewati folder tersembunyi sistem seperti $RECYCLE.BIN atau .git
                string dirName = Path.GetFileName(subDir);
                if (dirName.StartsWith('.') || dirName.StartsWith('$')) continue;

                // Lewati folder yang dikecualikan
                if (excludedFolders != null && excludedFolders.Any(ex =>
                    subDir.Equals(ex, StringComparison.OrdinalIgnoreCase) ||
                    subDir.StartsWith(ex + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                queue.Enqueue(subDir);
            }
        }
    }

    /// <summary>
    /// Memindai beberapa folder musik secara berurutan dan asinkron.
    /// </summary>
    public async Task<ScanProgress> ScanDirectoriesAsync(
        IEnumerable<string> directoryPaths,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directoryPaths);
        ScanProgress lastProgress = new ScanProgress(0, 0, 0, 0, "Selesai", TimeSpan.Zero);

        foreach (var dir in directoryPaths)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (Directory.Exists(dir))
            {
                lastProgress = await ScanDirectoryInternalAsync(dir, progress, cancellationToken, persistSpecVersion: false);
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            try { _databaseManager.SetSetting("metadata_spec_version", "2"); } catch { }
        }

        return lastProgress;
    }
}
