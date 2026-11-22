using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuraAudio.Core;
using AuraAudio.Scanner;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Pemuat cover art ultra-cepat dengan Two-Tier Caching (L1 RAM ImageSource + L2 Disk JPEG).
/// Menghindari decoding berulang pada UI thread sehingga scrolling list/grid tetap 120 FPS.
/// </summary>
public static class CoverArtLoader
{
    private static readonly ConcurrentDictionary<string, ImageSource> _memoryImageCache = new();
    private static readonly ConcurrentDictionary<string, BitmapSource> _syntheticTestCovers = new();
    private static string CacheDir => AppDataPaths.EnsureThumbnailCacheDirectory();

    private static AuraAudio.Database.DatabaseManager? _database;

    public static void SetDatabase(AuraAudio.Database.DatabaseManager db)
    {
        _database = db;
    }

    internal static void RegisterSyntheticCoverForTest(string coverCacheKey, BitmapSource bitmap)
    {
        if (string.IsNullOrWhiteSpace(coverCacheKey) || bitmap == null) return;
        _syntheticTestCovers[coverCacheKey] = bitmap;
    }

    internal static void UnregisterSyntheticCoverForTest(string coverCacheKey)
    {
        if (string.IsNullOrWhiteSpace(coverCacheKey)) return;
        _syntheticTestCovers.TryRemove(coverCacheKey, out _);
    }

    internal static BitmapSource? TryGetCachedMemoryImage(string coverCacheKey)
    {
        if (string.IsNullOrWhiteSpace(coverCacheKey)) return null;
        if (_syntheticTestCovers.TryGetValue(coverCacheKey, out var synth)) return synth;
        if (_memoryImageCache.TryGetValue($"{coverCacheKey}_{(int)ThumbnailSize.Mini}", out var mini) && mini is BitmapSource mb) return mb;
        if (_memoryImageCache.TryGetValue($"{coverCacheKey}_{(int)ThumbnailSize.Grid}", out var grid) && grid is BitmapSource gb) return gb;
        if (_memoryImageCache.TryGetValue($"{coverCacheKey}_{(int)ThumbnailSize.Large}", out var large) && large is BitmapSource lb) return lb;
        return null;
    }

    internal static string? TryGetCachedThumbnailFilePath(string coverCacheKey)
    {
        if (string.IsNullOrWhiteSpace(coverCacheKey)) return null;
        string miniPath = Path.Combine(CacheDir, $"{coverCacheKey}_{(int)ThumbnailSize.Mini}.jpg");
        if (File.Exists(miniPath)) return miniPath;
        string gridPath = Path.Combine(CacheDir, $"{coverCacheKey}_{(int)ThumbnailSize.Grid}.jpg");
        if (File.Exists(gridPath)) return gridPath;
        string largePath = Path.Combine(CacheDir, $"{coverCacheKey}_{(int)ThumbnailSize.Large}.jpg");
        if (File.Exists(largePath)) return largePath;
        return null;
    }

    /// <summary>
    /// Mengambil ImageSource untuk ditampilkan di UI WPF.
    /// Menggunakan DecodePixelWidth agar konsumsi RAM sangat minim (~50KB per gambar 300x300).
    /// </summary>
    public static ImageSource? GetImage(string? coverCacheKey, ThumbnailSize size = ThumbnailSize.Grid)
    {
        if (string.IsNullOrWhiteSpace(coverCacheKey)) return null;

        if (_syntheticTestCovers.TryGetValue(coverCacheKey, out var syntheticImg))
        {
            return syntheticImg;
        }

        string cacheKey = $"{coverCacheKey}_{(int)size}";

        // 1. Cek L1 Memory Cache (RAM)
        if (_memoryImageCache.TryGetValue(cacheKey, out var cachedImg))
        {
            return cachedImg;
        }

        // 2. Cek L2 Disk Cache
        string filePath = Path.Combine(CacheDir, $"{coverCacheKey}_{(int)size}.jpg");
        if (File.Exists(filePath))
        {
            var loaded = LoadBitmapFromFile(filePath, size);
            if (loaded != null)
            {
                _memoryImageCache[cacheKey] = loaded;
                return loaded;
            }
        }

        // 3. On-Demand Recovery jika berkas thumbnail belum dibuat di disk
        if (_database != null)
        {
            try
            {
                string? audioFilePath = _database.GetFilePathByCoverKey(coverCacheKey);
                if (!string.IsNullOrEmpty(audioFilePath) && File.Exists(audioFilePath))
                {
                    byte[]? rawBytes = null;
                    try
                    {
                        using var tagFile = TagLib.File.Create(audioFilePath);
                        if (tagFile.Tag.Pictures.Length > 0)
                        {
                            rawBytes = tagFile.Tag.Pictures[0].Data.Data;
                        }
                    }
                    catch { }

                    if (rawBytes == null)
                    {
                        // Fallback cek gambar folder
                        string? dir = Path.GetDirectoryName(audioFilePath);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            string[] candidates = ["cover.jpg", "folder.jpg", "front.jpg", "album.jpg", "cover.png", "folder.png", "front.png", "album.png"];
                            foreach (var name in candidates)
                            {
                                string cPath = Path.Combine(dir, name);
                                if (File.Exists(cPath))
                                {
                                    rawBytes = File.ReadAllBytes(cPath);
                                    break;
                                }
                            }
                        }
                    }

                    if (rawBytes != null && rawBytes.Length > 0)
                    {
                        var thumbMgr = new ThumbnailCacheManager(CacheDir);
                        thumbMgr.GetOrCreateThumbnail(rawBytes, coverCacheKey, size);
                        if (size != ThumbnailSize.Grid)
                        {
                            thumbMgr.GetOrCreateThumbnail(rawBytes, coverCacheKey, ThumbnailSize.Grid);
                        }
                        if (size != ThumbnailSize.Mini)
                        {
                            thumbMgr.GetOrCreateThumbnail(rawBytes, coverCacheKey, ThumbnailSize.Mini);
                        }

                        if (File.Exists(filePath))
                        {
                            var loaded = LoadBitmapFromFile(filePath, size);
                            if (loaded != null)
                            {
                                _memoryImageCache[cacheKey] = loaded;
                                return loaded;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // 4. Fallback ke ukuran Grid (300px) jika ukuran Large (600px) belum dapat dibuat dari file asli
        if (size == ThumbnailSize.Large)
        {
            string fallbackGridPath = Path.Combine(CacheDir, $"{coverCacheKey}_{(int)ThumbnailSize.Grid}.jpg");
            if (File.Exists(fallbackGridPath))
            {
                var fallbackLoaded = LoadBitmapFromFile(fallbackGridPath, ThumbnailSize.Grid);
                if (fallbackLoaded != null)
                {
                    _memoryImageCache[cacheKey] = fallbackLoaded;
                    return fallbackLoaded;
                }
            }
        }

        return null;
    }

    private static BitmapImage? LoadBitmapFromFile(string path, ThumbnailSize size)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.DecodePixelWidth = (int)size;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Converter XAML untuk mengubah coverArtCacheKey menjadi ImageSource WPF secara otomatis.
/// Mendukung ConverterParameter: "Mini" (64px), "Grid" (300px), dan "Large" / "Hero" (600px).
/// </summary>
public class CoverArtConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && !string.IsNullOrWhiteSpace(key))
        {
            var size = ThumbnailSize.Grid;
            if (parameter is string paramStr)
            {
                if (paramStr.Equals("Mini", StringComparison.OrdinalIgnoreCase))
                {
                    size = ThumbnailSize.Mini;
                }
                else if (paramStr.Equals("Large", StringComparison.OrdinalIgnoreCase) ||
                         paramStr.Equals("Hero", StringComparison.OrdinalIgnoreCase))
                {
                    size = ThumbnailSize.Large;
                }
            }
            return CoverArtLoader.GetImage(key, size);
        }
        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null!;
}
