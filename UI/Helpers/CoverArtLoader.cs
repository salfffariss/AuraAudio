using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuraAudio.Scanner;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Pemuat cover art ultra-cepat dengan Two-Tier Caching (L1 RAM ImageSource + L2 Disk JPEG).
/// Menghindari decoding berulang pada UI thread sehingga scrolling list/grid tetap 120 FPS.
/// </summary>
public static class CoverArtLoader
{
    private static readonly ConcurrentDictionary<string, ImageSource> _memoryImageCache = new();
    private static readonly string CacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cache", "Thumbnails");

    static CoverArtLoader()
    {
        Directory.CreateDirectory(CacheDir);
    }

    private static AuraAudio.Database.DatabaseManager? _database;

    public static void SetDatabase(AuraAudio.Database.DatabaseManager db)
    {
        _database = db;
    }

    /// <summary>
    /// Mengambil ImageSource untuk ditampilkan di UI WPF.
    /// Menggunakan DecodePixelWidth agar konsumsi RAM sangat minim (~50KB per gambar 300x300).
    /// </summary>
    public static ImageSource? GetImage(string? coverCacheKey, ThumbnailSize size = ThumbnailSize.Grid)
    {
        if (string.IsNullOrWhiteSpace(coverCacheKey)) return null;

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
                        thumbMgr.GetOrCreateThumbnail(rawBytes, coverCacheKey, ThumbnailSize.Grid);
                        thumbMgr.GetOrCreateThumbnail(rawBytes, coverCacheKey, ThumbnailSize.Mini);

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
/// </summary>
public class CoverArtConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && !string.IsNullOrWhiteSpace(key))
        {
            var size = ThumbnailSize.Grid;
            if (parameter is string paramStr && paramStr.Equals("Mini", StringComparison.OrdinalIgnoreCase))
            {
                size = ThumbnailSize.Mini;
            }
            return CoverArtLoader.GetImage(key, size);
        }
        return null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null!;
}
