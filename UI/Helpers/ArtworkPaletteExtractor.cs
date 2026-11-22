using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AuraAudio.UI.Helpers;

/// <summary>
/// Named tuning parameters controlling backdrop OKLCH lightness/chroma bounds,
/// post-sRGB linear luminance ceiling, and black scrim opacities.
/// SafeDefault enforces the mathematically verified Phase 1 contrast guarantees
/// (Y_blob <= 0.110 + >= 42% scrim => worst-case Y_post <= 0.0370).
/// </summary>
public readonly record struct BackdropTuningPreset(
    string Name,
    double MaxLinearLuminance,
    double BaseMinL,
    double BaseMaxL,
    double BaseMinC,
    double BaseMaxC,
    double Blob1MinL,
    double Blob1MaxL,
    double Blob1MinC,
    double Blob1MaxC,
    double Blob2MinL,
    double Blob2MaxL,
    double Blob2MinC,
    double Blob2MaxC,
    double Blob3MinL,
    double Blob3MaxL,
    double Blob3MinC,
    double Blob3MaxC,
    double FullScreenScrimTopOpacity,
    double FullScreenScrimMidOpacity,
    double FullScreenScrimBottomOpacity,
    double MiniCompactScrimOpacity,
    double MiniTrayScrimOpacity,
    double LightnessLift = 0.0,
    double ChromaGain = 1.0,
    bool UseSpatialScrim = false,
    double SpatialAmbientOpacity = 0.0,
    double SpatialTopBarOpacity = 0.0,
    double SpatialLyricsColumnOpacity = 0.0,
    double SpatialBottomControlsOpacity = 0.0,
    double SpatialMiniLeftGlowOpacity = 0.0,
    double SpatialMiniRightControlsOpacity = 0.0);

/// <summary>
/// Immutable 4-color artwork-derived palette (1 deep base + 3 atmospheric radial blobs)
/// guaranteed by construction to stay beneath the preset's linear luminance ceiling.
/// </summary>
public readonly record struct BackdropPalette(
    Color BaseColor,
    Color Blob1Color,
    Color Blob2Color,
    Color Blob3Color,
    bool IsMonochrome,
    double MaxRoleRelativeLuminance,
    double TotalLightnessSpread,
    double BlobLightnessSpread)
{
    public static BackdropPalette NeutralFallback { get; } = CreateNeutralStudioPalette(
        baseL: 0.14,
        blob2L: 0.22,
        blob1L: 0.30,
        blob3L: 0.38,
        maxLinearY: ArtworkPaletteExtractor.SafeMaxLinearLuminance);

    internal static BackdropPalette CreateNeutralStudioPalette(
        double baseL,
        double blob2L,
        double blob1L,
        double blob3L,
        double maxLinearY)
    {
        Color b0 = ArtworkPaletteExtractor.OklchToClampedSrgb(baseL, 0.0, 0.0, maxLinearY, forceNeutral: true);
        Color b1 = ArtworkPaletteExtractor.OklchToClampedSrgb(blob1L, 0.0, 0.0, maxLinearY, forceNeutral: true);
        Color b2 = ArtworkPaletteExtractor.OklchToClampedSrgb(blob2L, 0.0, 0.0, maxLinearY, forceNeutral: true);
        Color b3 = ArtworkPaletteExtractor.OklchToClampedSrgb(blob3L, 0.0, 0.0, maxLinearY, forceNeutral: true);

        double maxY = Math.Max(
            Math.Max(ArtworkPaletteExtractor.ComputeRelativeLuminance(b0), ArtworkPaletteExtractor.ComputeRelativeLuminance(b1)),
            Math.Max(ArtworkPaletteExtractor.ComputeRelativeLuminance(b2), ArtworkPaletteExtractor.ComputeRelativeLuminance(b3)));

        double minBlobL = Math.Min(blob1L, Math.Min(blob2L, blob3L));
        double maxBlobL = Math.Max(blob1L, Math.Max(blob2L, blob3L));

        return new BackdropPalette(
            BaseColor: b0,
            Blob1Color: b1,
            Blob2Color: b2,
            Blob3Color: b3,
            IsMonochrome: true,
            MaxRoleRelativeLuminance: maxY,
            TotalLightnessSpread: maxBlobL - baseL,
            BlobLightnessSpread: maxBlobL - minBlobL);
    }
}

/// <summary>
/// Extracts a perceptual OKLab/OKLCH 4-color backdrop palette from downscaled (32x32)
/// track artwork on a background thread with a bounded 64-entry LRU cache.
/// Exempted in check_color_purity.py strictly for runtime artwork palette synthesis.
/// </summary>
public static class ArtworkPaletteExtractor
{
    // SAFE default constants (Phase 1 Section 4.1 & 4.2 mathematical readability guarantee)
    public const double SafeMaxLinearLuminance = 0.110;
    public const double SafeFullScreenScrimTopOpacity = 0.42;
    public const double SafeFullScreenScrimMidOpacity = 0.45;
    public const double SafeFullScreenScrimBottomOpacity = 0.54;
    public const double SafeMiniCompactScrimOpacity = 0.48;
    public const double SafeMiniTrayScrimOpacity = 0.55;

    public const double SafeBaseMinL = 0.16;
    public const double SafeBaseMaxL = 0.24;
    public const double SafeBaseMinC = 0.02;
    public const double SafeBaseMaxC = 0.06;

    public const double SafeBlob1MinL = 0.34;
    public const double SafeBlob1MaxL = 0.46;
    public const double SafeBlob1MinC = 0.06;
    public const double SafeBlob1MaxC = 0.14;

    public const double SafeBlob2MinL = 0.28;
    public const double SafeBlob2MaxL = 0.40;
    public const double SafeBlob2MinC = 0.05;
    public const double SafeBlob2MaxC = 0.13;

    public const double SafeBlob3MinL = 0.38;
    public const double SafeBlob3MaxL = 0.48;
    public const double SafeBlob3MinC = 0.06;
    public const double SafeBlob3MaxC = 0.14;

    public const double MinTotalLightnessSpread = 0.14;
    public const double MinBlobLightnessSpread = 0.08;
    public const double MinDistinctClusterDeltaE = 0.10;
    public const double GrayscaleChromaP95Threshold = 0.030;
    public const int SampleDimension = 32;
    public const int LruCacheCapacity = 64;

    public static BackdropTuningPreset SafeDefault { get; } = new(
        Name: "SafeDefault",
        MaxLinearLuminance: SafeMaxLinearLuminance,
        BaseMinL: SafeBaseMinL,
        BaseMaxL: SafeBaseMaxL,
        BaseMinC: SafeBaseMinC,
        BaseMaxC: SafeBaseMaxC,
        Blob1MinL: SafeBlob1MinL,
        Blob1MaxL: SafeBlob1MaxL,
        Blob1MinC: SafeBlob1MinC,
        Blob1MaxC: SafeBlob1MaxC,
        Blob2MinL: SafeBlob2MinL,
        Blob2MaxL: SafeBlob2MaxL,
        Blob2MinC: SafeBlob2MinC,
        Blob2MaxC: SafeBlob2MaxC,
        Blob3MinL: SafeBlob3MinL,
        Blob3MaxL: SafeBlob3MaxL,
        Blob3MinC: SafeBlob3MinC,
        Blob3MaxC: SafeBlob3MaxC,
        FullScreenScrimTopOpacity: SafeFullScreenScrimTopOpacity,
        FullScreenScrimMidOpacity: SafeFullScreenScrimMidOpacity,
        FullScreenScrimBottomOpacity: SafeFullScreenScrimBottomOpacity,
        MiniCompactScrimOpacity: SafeMiniCompactScrimOpacity,
        MiniTrayScrimOpacity: SafeMiniTrayScrimOpacity);

    // Preset 2 (Phase 4 Visual Tuning Candidate): BalancedVivid
    // Uniform vertical scrim with richer OKLCH chroma and slightly higher luminance ceiling; clears all readability gates.
    public const double BalancedMaxLinearLuminance = 0.140;
    public const double BalancedFullScreenScrimTopOpacity = 0.45;
    public const double BalancedFullScreenScrimMidOpacity = 0.47;
    public const double BalancedFullScreenScrimBottomOpacity = 0.54;
    public const double BalancedMiniCompactScrimOpacity = 0.49;
    public const double BalancedMiniTrayScrimOpacity = 0.55;
    public const double BalancedLightnessLift = 0.05;
    public const double BalancedChromaGain = 1.25;

    public const double BalancedBaseMinL = 0.18;
    public const double BalancedBaseMaxL = 0.27;
    public const double BalancedBaseMinC = 0.04;
    public const double BalancedBaseMaxC = 0.10;

    public const double BalancedBlob1MinL = 0.38;
    public const double BalancedBlob1MaxL = 0.51;
    public const double BalancedBlob1MinC = 0.09;
    public const double BalancedBlob1MaxC = 0.21;

    public const double BalancedBlob2MinL = 0.32;
    public const double BalancedBlob2MaxL = 0.45;
    public const double BalancedBlob2MinC = 0.08;
    public const double BalancedBlob2MaxC = 0.19;

    public const double BalancedBlob3MinL = 0.41;
    public const double BalancedBlob3MaxL = 0.53;
    public const double BalancedBlob3MinC = 0.09;
    public const double BalancedBlob3MaxC = 0.21;

    public static BackdropTuningPreset BalancedVivid { get; } = new(
        Name: "BalancedVivid",
        MaxLinearLuminance: BalancedMaxLinearLuminance,
        BaseMinL: BalancedBaseMinL,
        BaseMaxL: BalancedBaseMaxL,
        BaseMinC: BalancedBaseMinC,
        BaseMaxC: BalancedBaseMaxC,
        Blob1MinL: BalancedBlob1MinL,
        Blob1MaxL: BalancedBlob1MaxL,
        Blob1MinC: BalancedBlob1MinC,
        Blob1MaxC: BalancedBlob1MaxC,
        Blob2MinL: BalancedBlob2MinL,
        Blob2MaxL: BalancedBlob2MaxL,
        Blob2MinC: BalancedBlob2MinC,
        Blob2MaxC: BalancedBlob2MaxC,
        Blob3MinL: BalancedBlob3MinL,
        Blob3MaxL: BalancedBlob3MaxL,
        Blob3MinC: BalancedBlob3MinC,
        Blob3MaxC: BalancedBlob3MaxC,
        FullScreenScrimTopOpacity: BalancedFullScreenScrimTopOpacity,
        FullScreenScrimMidOpacity: BalancedFullScreenScrimMidOpacity,
        FullScreenScrimBottomOpacity: BalancedFullScreenScrimBottomOpacity,
        MiniCompactScrimOpacity: BalancedMiniCompactScrimOpacity,
        MiniTrayScrimOpacity: BalancedMiniTrayScrimOpacity,
        LightnessLift: BalancedLightnessLift,
        ChromaGain: BalancedChromaGain);

    // Preset 3 (Phase 4 Visual Tuning Candidate): AppleMusicSpatial
    // Luminous glow around cover artwork paired with a spatially varying pure-black scrim
    // (lighter in open ambient areas, deeper behind the lyrics column, top bar, and lower controls).
    public const double SpatialMaxLinearLuminance = 0.220;
    public const double SpatialAmbientScrimOpacity = 0.18;
    public const double SpatialTopBarScrimOpacity = 0.50;
    public const double SpatialLyricsColumnScrimOpacity = 0.50;
    public const double SpatialBottomControlsScrimOpacity = 0.54;
    public const double SpatialMiniLeftGlowScrimOpacity = 0.22;
    public const double SpatialMiniRightControlsScrimOpacity = 0.52;
    public const double SpatialMiniTrayScrimOpacity = 0.60;
    public const double SpatialLightnessLift = 0.13;
    public const double SpatialChromaGain = 1.35;

    public const double SpatialBaseMinL = 0.20;
    public const double SpatialBaseMaxL = 0.30;
    public const double SpatialBaseMinC = 0.05;
    public const double SpatialBaseMaxC = 0.12;

    public const double SpatialBlob1MinL = 0.44;
    public const double SpatialBlob1MaxL = 0.60;
    public const double SpatialBlob1MinC = 0.11;
    public const double SpatialBlob1MaxC = 0.24;

    public const double SpatialBlob2MinL = 0.36;
    public const double SpatialBlob2MaxL = 0.52;
    public const double SpatialBlob2MinC = 0.10;
    public const double SpatialBlob2MaxC = 0.22;

    public const double SpatialBlob3MinL = 0.48;
    public const double SpatialBlob3MaxL = 0.62;
    public const double SpatialBlob3MinC = 0.11;
    public const double SpatialBlob3MaxC = 0.24;

    public static BackdropTuningPreset AppleMusicSpatial { get; } = new(
        Name: "AppleMusicSpatial",
        MaxLinearLuminance: SpatialMaxLinearLuminance,
        BaseMinL: SpatialBaseMinL,
        BaseMaxL: SpatialBaseMaxL,
        BaseMinC: SpatialBaseMinC,
        BaseMaxC: SpatialBaseMaxC,
        Blob1MinL: SpatialBlob1MinL,
        Blob1MaxL: SpatialBlob1MaxL,
        Blob1MinC: SpatialBlob1MinC,
        Blob1MaxC: SpatialBlob1MaxC,
        Blob2MinL: SpatialBlob2MinL,
        Blob2MaxL: SpatialBlob2MaxL,
        Blob2MinC: SpatialBlob2MinC,
        Blob2MaxC: SpatialBlob2MaxC,
        Blob3MinL: SpatialBlob3MinL,
        Blob3MaxL: SpatialBlob3MaxL,
        Blob3MinC: SpatialBlob3MinC,
        Blob3MaxC: SpatialBlob3MaxC,
        FullScreenScrimTopOpacity: SpatialTopBarScrimOpacity,
        FullScreenScrimMidOpacity: SpatialAmbientScrimOpacity,
        FullScreenScrimBottomOpacity: SpatialBottomControlsScrimOpacity,
        MiniCompactScrimOpacity: SpatialMiniLeftGlowScrimOpacity,
        MiniTrayScrimOpacity: SpatialMiniTrayScrimOpacity,
        LightnessLift: SpatialLightnessLift,
        ChromaGain: SpatialChromaGain,
        UseSpatialScrim: true,
        SpatialAmbientOpacity: SpatialAmbientScrimOpacity,
        SpatialTopBarOpacity: SpatialTopBarScrimOpacity,
        SpatialLyricsColumnOpacity: SpatialLyricsColumnScrimOpacity,
        SpatialBottomControlsOpacity: SpatialBottomControlsScrimOpacity,
        SpatialMiniLeftGlowOpacity: SpatialMiniLeftGlowScrimOpacity,
        SpatialMiniRightControlsOpacity: SpatialMiniRightControlsScrimOpacity);

    /// <summary>
    /// Active default backdrop preset selected for v2.1.0.
    /// SafeDefault and BalancedVivid remain exposed as named presets/constants.
    /// </summary>
    public const string ActiveDefaultPresetName = "AppleMusicSpatial";
    public static BackdropTuningPreset ActiveDefault => AppleMusicSpatial;

    private readonly struct OkLabSample
    {
        public readonly double L;
        public readonly double A;
        public readonly double B;
        public readonly double C;
        public readonly double Weight;

        public OkLabSample(double l, double a, double b, double c, double weight)
        {
            L = l;
            A = a;
            B = b;
            C = c;
            Weight = weight;
        }
    }

    private struct ClusterCenter
    {
        public double L;
        public double A;
        public double B;
        public double TotalWeight;

        public double Chroma => Math.Sqrt((A * A) + (B * B));
        public double HueRadians => Math.Atan2(B, A);
    }

    private readonly struct CacheEntry
    {
        public readonly string Key;
        public readonly BackdropPalette Palette;

        public CacheEntry(string key, BackdropPalette palette)
        {
            Key = key;
            Palette = palette;
        }
    }

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> LruLookup = new(LruCacheCapacity + 4, StringComparer.Ordinal);
    private static readonly LinkedList<CacheEntry> LruOrder = new();

    internal static int CacheCount
    {
        get
        {
            lock (CacheLock) return LruLookup.Count;
        }
    }

    internal static void ClearCacheForTest()
    {
        lock (CacheLock)
        {
            LruLookup.Clear();
            LruOrder.Clear();
        }
    }

    internal static bool TryGetCached(string cacheKey, out BackdropPalette palette)
    {
        lock (CacheLock)
        {
            if (LruLookup.TryGetValue(cacheKey, out var node))
            {
                LruOrder.Remove(node);
                LruOrder.AddFirst(node);
                palette = node.Value.Palette;
                return true;
            }
        }
        palette = default;
        return false;
    }

    private static void PutCache(string cacheKey, BackdropPalette palette)
    {
        lock (CacheLock)
        {
            if (LruLookup.TryGetValue(cacheKey, out var existing))
            {
                LruOrder.Remove(existing);
                var updated = new LinkedListNode<CacheEntry>(new CacheEntry(cacheKey, palette));
                LruOrder.AddFirst(updated);
                LruLookup[cacheKey] = updated;
                return;
            }

            if (LruLookup.Count >= LruCacheCapacity && LruOrder.Last != null)
            {
                var oldest = LruOrder.Last;
                LruOrder.RemoveLast();
                LruLookup.Remove(oldest.Value.Key);
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(cacheKey, palette));
            LruOrder.AddFirst(node);
            LruLookup[cacheKey] = node;
        }
    }

    /// <summary>
    /// Extracts (or retrieves from the 64-entry LRU cache) the backdrop palette for a track's CoverArtCacheKey.
    /// Runs extraction on a background thread pool worker and never blocks the UI thread.
    /// </summary>
    public static Task<BackdropPalette> ExtractForCoverKeyAsync(
        string? coverCacheKey,
        BackdropTuningPreset? presetOverride = null,
        CancellationToken cancellationToken = default)
    {
        BackdropTuningPreset preset = presetOverride ?? ActiveDefault;
        if (string.IsNullOrWhiteSpace(coverCacheKey))
        {
            return Task.FromResult(BackdropPalette.NeutralFallback);
        }

        string compositeKey = $"{coverCacheKey}|{preset.Name}";
        if (TryGetCached(compositeKey, out var cached))
        {
            return Task.FromResult(cached);
        }

        // Capture any already-loaded frozen memory bitmap or L2 disk path cleanly before Task.Run
        BitmapSource? memBitmap = CoverArtLoader.TryGetCachedMemoryImage(coverCacheKey);
        string? diskPath = memBitmap == null ? CoverArtLoader.TryGetCachedThumbnailFilePath(coverCacheKey) : null;

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetCached(compositeKey, out var raceHit))
            {
                return raceHit;
            }

            BackdropPalette extracted = BackdropPalette.NeutralFallback;
            try
            {
                byte[]? bgra = null;
                int w = 0, h = 0;

                if (memBitmap != null)
                {
                    bgra = SampleBitmapToBgra32(memBitmap, out w, out h);
                }
                else if (!string.IsNullOrWhiteSpace(diskPath) && File.Exists(diskPath))
                {
                    bgra = DecodeDiskImageToSampleBgra32(diskPath, out w, out h);
                }
                else
                {
                    // Trigger on-demand recovery from CoverArtLoader if L2 thumbnail was not generated yet
                    if (CoverArtLoader.GetImage(coverCacheKey, AuraAudio.Scanner.ThumbnailSize.Mini) is BitmapSource recovered)
                    {
                        bgra = SampleBitmapToBgra32(recovered, out w, out h);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (bgra != null && w > 0 && h > 0)
                {
                    extracted = ExtractFromBgra32Pixels(bgra, w, h, preset);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                extracted = BackdropPalette.NeutralFallback;
            }

            PutCache(compositeKey, extracted);
            return extracted;
        }, cancellationToken);
    }

    public static BackdropPalette ExtractFromBitmapSource(BitmapSource? source, BackdropTuningPreset? presetOverride = null)
    {
        BackdropTuningPreset preset = presetOverride ?? ActiveDefault;
        if (source == null) return BackdropPalette.NeutralFallback;
        byte[]? bgra = SampleBitmapToBgra32(source, out int w, out int h);
        if (bgra == null || w <= 0 || h <= 0) return BackdropPalette.NeutralFallback;
        return ExtractFromBgra32Pixels(bgra, w, h, preset);
    }

    internal static byte[]? DecodeDiskImageToSampleBgra32(string filePath, out int width, out int height)
    {
        width = 0;
        height = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(filePath, UriKind.Absolute);
        bmp.DecodePixelWidth = SampleDimension;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.EndInit();
        bmp.Freeze();
        return SampleBitmapToBgra32(bmp, out width, out height);
    }

    internal static byte[]? SampleBitmapToBgra32(BitmapSource source, out int width, out int height)
    {
        width = 0;
        height = 0;
        int srcW = source.PixelWidth;
        int srcH = source.PixelHeight;
        if (srcW <= 0 || srcH <= 0) return null;

        BitmapSource formatted = source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0.0);

        int srcStride = srcW * 4;
        byte[] raw = new byte[srcStride * srcH];
        formatted.CopyPixels(raw, srcStride, 0);

        if (srcW <= SampleDimension && srcH <= SampleDimension)
        {
            width = srcW;
            height = srcH;
            return raw;
        }

        // Fast area/nearest downsample to 32x32 on background thread (no WPF TransformedBitmap threading surprises)
        width = SampleDimension;
        height = SampleDimension;
        byte[] sampled = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int sy = Math.Clamp((int)((y + 0.5) * srcH / height), 0, srcH - 1);
            int srcRow = sy * srcStride;
            int dstRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int sx = Math.Clamp((int)((x + 0.5) * srcW / width), 0, srcW - 1);
                int si = srcRow + (sx * 4);
                int di = dstRow + (x * 4);
                sampled[di] = raw[si];
                sampled[di + 1] = raw[si + 1];
                sampled[di + 2] = raw[si + 2];
                sampled[di + 3] = raw[si + 3];
            }
        }
        return sampled;
    }

    /// <summary>
    /// Core deterministic OKLab/OKLCH palette extractor from BGRA32 pixel data.
    /// Enforces grayscale neutrality, perceptual cluster separation, lightness spread,
    /// and strict post-sRGB linear relative luminance ceiling (<= preset.MaxLinearLuminance).
    /// </summary>
    public static BackdropPalette ExtractFromBgra32Pixels(
        byte[] bgraPixels,
        int width,
        int height,
        BackdropTuningPreset? presetOverride = null)
    {
        BackdropTuningPreset preset = presetOverride ?? ActiveDefault;
        int pixelCount = Math.Min(width * height, bgraPixels.Length / 4);
        if (pixelCount <= 0)
        {
            return BackdropPalette.NeutralFallback;
        }

        var samples = new OkLabSample[pixelCount];
        var chromas = new double[pixelCount];
        double lSum = 0.0;
        double lMinObserved = 1.0;
        double lMaxObserved = 0.0;

        for (int i = 0; i < pixelCount; i++)
        {
            int offset = i * 4;
            byte b = bgraPixels[offset];
            byte g = bgraPixels[offset + 1];
            byte r = bgraPixels[offset + 2];

            SrgbToOkLab(r, g, b, out double l, out double a, out double ob);
            double c = Math.Sqrt((a * a) + (ob * ob));
            chromas[i] = c;
            lSum += l;
            if (l < lMinObserved) lMinObserved = l;
            if (l > lMaxObserved) lMaxObserved = l;

            // Emphasize chromatic artwork pixels and gently downweight stark white/black outer borders
            double weight = 1.0 + (2.5 * c);
            if (l > 0.94 || l < 0.06)
            {
                weight *= 0.35;
            }
            samples[i] = new OkLabSample(l, a, ob, c, weight);
        }

        Array.Sort(chromas);
        double chromaP95 = chromas[(int)Math.Clamp(Math.Floor(0.95 * (pixelCount - 1)), 0, pixelCount - 1)];

        // Grayscale / pure white / pure black covers: strict neutral monochrome studio palette (R == G == B)
        if (chromaP95 < GrayscaleChromaP95Threshold)
        {
            double meanL = lSum / pixelCount;
            double baseL = Math.Clamp(0.11 + (0.07 * meanL), 0.11, 0.18);
            double b2L = Math.Clamp(baseL + 0.08, 0.19, 0.28);
            double b1L = Math.Clamp(baseL + 0.15, 0.26, 0.35);
            double b3L = Math.Clamp(baseL + 0.22, 0.32, 0.42);
            return BackdropPalette.CreateNeutralStudioPalette(baseL, b2L, b1L, b3L, preset.MaxLinearLuminance);
        }

        // Deterministic weighted k-means++ (k = 5, 10 iterations) in OKLab space
        const int k = 5;
        var centers = RunDeterministicWeightedKMeans(samples, k, iterations: 10);

        // Sort clusters by chromatic prominence (weight * (0.4 + 2.5 * chroma))
        Array.Sort(centers, (x, y) =>
        {
            double sx = x.TotalWeight * (0.4 + (2.5 * x.Chroma));
            double sy = y.TotalWeight * (0.4 + (2.5 * y.Chroma));
            return sy.CompareTo(sx);
        });

        // Role 1: Blob 1 (Primary Atmospheric) = strongest chromatic cluster
        ClusterCenter primary = centers[0];

        // Role 0: Base = darkest significant cluster (or primary darkened)
        ClusterCenter baseCluster = primary;
        double darkestScore = double.MaxValue;
        for (int i = 0; i < centers.Length; i++)
        {
            if (centers[i].TotalWeight <= 0.0) continue;
            if (centers[i].L < darkestScore)
            {
                darkestScore = centers[i].L;
                baseCluster = centers[i];
            }
        }

        // Role 2: Blob 2 (Secondary Depth) = first distinct cluster (deltaE >= 0.10 from Blob 1), else analogous -26 deg
        bool foundBlob2 = false;
        ClusterCenter blob2Cluster = default;
        for (int i = 1; i < centers.Length; i++)
        {
            if (centers[i].TotalWeight <= 0.0) continue;
            if (OkLabDistance(primary, centers[i]) >= MinDistinctClusterDeltaE)
            {
                blob2Cluster = centers[i];
                foundBlob2 = true;
                break;
            }
        }

        // Role 3: Blob 3 (Highlight Accent) = first distinct cluster from Blob 1 & Blob 2, else analogous +28 deg
        bool foundBlob3 = false;
        ClusterCenter blob3Cluster = default;
        for (int i = 1; i < centers.Length; i++)
        {
            if (centers[i].TotalWeight <= 0.0) continue;
            if (OkLabDistance(primary, centers[i]) >= MinDistinctClusterDeltaE &&
                (!foundBlob2 || OkLabDistance(blob2Cluster, centers[i]) >= MinDistinctClusterDeltaE))
            {
                blob3Cluster = centers[i];
                foundBlob3 = true;
                break;
            }
        }

        double h1 = primary.HueRadians;
        double hBase = baseCluster.Chroma >= 0.02 ? baseCluster.HueRadians : h1;
        double h2 = foundBlob2 ? blob2Cluster.HueRadians : h1 - (26.0 * Math.PI / 180.0);
        double h3 = foundBlob3 ? blob3Cluster.HueRadians : h1 + (28.0 * Math.PI / 180.0);

        double lBase = Math.Clamp((baseCluster.L * 0.45) + (preset.LightnessLift * 0.35), preset.BaseMinL, preset.BaseMaxL);
        double cBase = Math.Clamp(baseCluster.Chroma * 0.55 * preset.ChromaGain, preset.BaseMinC, preset.BaseMaxC);

        double l1 = Math.Clamp(0.24 + (primary.L * 0.30) + preset.LightnessLift, preset.Blob1MinL, preset.Blob1MaxL);
        double c1 = Math.Clamp(primary.Chroma * preset.ChromaGain, preset.Blob1MinC, preset.Blob1MaxC);

        double l2Raw = foundBlob2 ? 0.20 + (blob2Cluster.L * 0.28) + (preset.LightnessLift * 0.85) : l1 - 0.06;
        double l2 = Math.Clamp(l2Raw, preset.Blob2MinL, preset.Blob2MaxL);
        double c2 = Math.Clamp((foundBlob2 ? blob2Cluster.Chroma * preset.ChromaGain : c1 * 0.85), preset.Blob2MinC, preset.Blob2MaxC);

        double l3Raw = foundBlob3 ? 0.26 + (blob3Cluster.L * 0.30) + preset.LightnessLift : l1 + 0.05;
        double l3 = Math.Clamp(l3Raw, preset.Blob3MinL, preset.Blob3MaxL);
        double c3 = Math.Clamp((foundBlob3 ? blob3Cluster.Chroma * preset.ChromaGain : c1 * 0.92), preset.Blob3MinC, preset.Blob3MaxC);

        // Enforce lightness variety (Section 4.1: max(L_blobs) - L_base >= 0.14 and max(L_blobs) - min(L_blobs) >= 0.08)
        EnforceLightnessVariety(
            ref lBase, ref l1, ref l2, ref l3,
            preset.BaseMinL, preset.BaseMaxL,
            preset.Blob2MinL, preset.Blob3MaxL);

        Color baseColor = OklchToClampedSrgb(lBase, cBase, hBase, preset.MaxLinearLuminance, forceNeutral: false);
        Color blob1Color = OklchToClampedSrgb(l1, c1, h1, preset.MaxLinearLuminance, forceNeutral: false);
        Color blob2Color = OklchToClampedSrgb(l2, c2, h2, preset.MaxLinearLuminance, forceNeutral: false);
        Color blob3Color = OklchToClampedSrgb(l3, c3, h3, preset.MaxLinearLuminance, forceNeutral: false);

        double maxY = Math.Max(
            Math.Max(ComputeRelativeLuminance(baseColor), ComputeRelativeLuminance(blob1Color)),
            Math.Max(ComputeRelativeLuminance(blob2Color), ComputeRelativeLuminance(blob3Color)));

        double minBlobL = Math.Min(l1, Math.Min(l2, l3));
        double maxBlobL = Math.Max(l1, Math.Max(l2, l3));

        return new BackdropPalette(
            BaseColor: baseColor,
            Blob1Color: blob1Color,
            Blob2Color: blob2Color,
            Blob3Color: blob3Color,
            IsMonochrome: false,
            MaxRoleRelativeLuminance: maxY,
            TotalLightnessSpread: maxBlobL - lBase,
            BlobLightnessSpread: maxBlobL - minBlobL);
    }

    private static void EnforceLightnessVariety(
        ref double lBase,
        ref double l1,
        ref double l2,
        ref double l3,
        double baseMinL,
        double baseMaxL,
        double blobMinFloor,
        double blobMaxCeil)
    {
        double minB = Math.Min(l1, Math.Min(l2, l3));
        double maxB = Math.Max(l1, Math.Max(l2, l3));

        if (maxB - minB < MinBlobLightnessSpread)
        {
            double mid = (minB + maxB) * 0.5;
            double half = MinBlobLightnessSpread * 0.55;
            l2 = Math.Clamp(mid - half, blobMinFloor, blobMaxCeil - MinBlobLightnessSpread);
            l3 = Math.Clamp(l2 + (MinBlobLightnessSpread + 0.01), blobMinFloor + MinBlobLightnessSpread, blobMaxCeil);
            l1 = Math.Clamp((l2 + l3) * 0.5, blobMinFloor, blobMaxCeil);
            maxB = Math.Max(l1, Math.Max(l2, l3));
        }

        if (maxB - lBase < MinTotalLightnessSpread)
        {
            lBase = Math.Clamp(maxB - MinTotalLightnessSpread - 0.01, baseMinL, baseMaxL);
            if (maxB - lBase < MinTotalLightnessSpread)
            {
                l3 = Math.Clamp(lBase + MinTotalLightnessSpread + 0.01, blobMinFloor, blobMaxCeil);
            }
        }
    }

    private static ClusterCenter[] RunDeterministicWeightedKMeans(OkLabSample[] samples, int k, int iterations)
    {
        int n = samples.Length;
        var centers = new ClusterCenter[k];

        // Deterministic k-means++ seeding: pick highest-weight sample first, then farthest weighted distance
        int firstIdx = 0;
        double bestW = -1.0;
        for (int i = 0; i < n; i++)
        {
            double score = samples[i].Weight * (1.0 + samples[i].C);
            if (score > bestW)
            {
                bestW = score;
                firstIdx = i;
            }
        }
        centers[0] = new ClusterCenter { L = samples[firstIdx].L, A = samples[firstIdx].A, B = samples[firstIdx].B };

        var minDistSq = new double[n];
        Array.Fill(minDistSq, double.MaxValue);

        for (int c = 1; c < k; c++)
        {
            int nextIdx = 0;
            double maxWeightedDist = -1.0;
            var prev = centers[c - 1];
            for (int i = 0; i < n; i++)
            {
                double dl = samples[i].L - prev.L;
                double da = samples[i].A - prev.A;
                double db = samples[i].B - prev.B;
                double d2 = (dl * dl) + (da * da) + (db * db);
                if (d2 < minDistSq[i]) minDistSq[i] = d2;

                double wd = minDistSq[i] * samples[i].Weight;
                if (wd > maxWeightedDist)
                {
                    maxWeightedDist = wd;
                    nextIdx = i;
                }
            }
            centers[c] = new ClusterCenter { L = samples[nextIdx].L, A = samples[nextIdx].A, B = samples[nextIdx].B };
        }

        var sumL = new double[k];
        var sumA = new double[k];
        var sumB = new double[k];
        var sumW = new double[k];

        for (int iter = 0; iter < iterations; iter++)
        {
            Array.Clear(sumL, 0, k);
            Array.Clear(sumA, 0, k);
            Array.Clear(sumB, 0, k);
            Array.Clear(sumW, 0, k);

            for (int i = 0; i < n; i++)
            {
                var s = samples[i];
                int bestC = 0;
                double bestD2 = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    double dl = s.L - centers[c].L;
                    double da = s.A - centers[c].A;
                    double db = s.B - centers[c].B;
                    double d2 = (dl * dl) + (da * da) + (db * db);
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        bestC = c;
                    }
                }
                sumL[bestC] += s.L * s.Weight;
                sumA[bestC] += s.A * s.Weight;
                sumB[bestC] += s.B * s.Weight;
                sumW[bestC] += s.Weight;
            }

            for (int c = 0; c < k; c++)
            {
                if (sumW[c] > 1e-6)
                {
                    centers[c].L = sumL[c] / sumW[c];
                    centers[c].A = sumA[c] / sumW[c];
                    centers[c].B = sumB[c] / sumW[c];
                    centers[c].TotalWeight = sumW[c];
                }
                else
                {
                    centers[c].TotalWeight = 0.0;
                }
            }
        }

        return centers;
    }

    private static double OkLabDistance(in ClusterCenter x, in ClusterCenter y)
    {
        double dl = x.L - y.L;
        double da = x.A - y.A;
        double db = x.B - y.B;
        return Math.Sqrt((dl * dl) + (da * da) + (db * db));
    }

    public static double SrgbByteToLinear(byte value)
    {
        double c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    public static byte LinearToSrgbByte(double linear)
    {
        double clamped = Math.Clamp(linear, 0.0, 1.0);
        double srgb = clamped <= 0.0031308
            ? 12.92 * clamped
            : (1.055 * Math.Pow(clamped, 1.0 / 2.4)) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(srgb * 255.0), 0, 255);
    }

    public static double ComputeRelativeLuminance(byte r, byte g, byte b) =>
        (0.2126 * SrgbByteToLinear(r)) +
        (0.7152 * SrgbByteToLinear(g)) +
        (0.0722 * SrgbByteToLinear(b));

    public static double ComputeRelativeLuminance(Color c) =>
        ComputeRelativeLuminance(c.R, c.G, c.B);

    public static double ComputeContrastRatio(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        double y1 = ComputeRelativeLuminance(r1, g1, b1);
        double y2 = ComputeRelativeLuminance(r2, g2, b2);
        double lighter = Math.Max(y1, y2);
        double darker = Math.Min(y1, y2);
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double ComputeContrastRatio(Color c1, Color c2) =>
        ComputeContrastRatio(c1.R, c1.G, c1.B, c2.R, c2.G, c2.B);

    public static double ComputeAlphaOverContrastRatio(Color fg, double alpha, byte bgR, byte bgG, byte bgB)
    {
        double a = Math.Clamp(alpha, 0.0, 1.0);
        byte r = (byte)Math.Clamp((int)Math.Round((fg.R * a) + (bgR * (1.0 - a))), 0, 255);
        byte g = (byte)Math.Clamp((int)Math.Round((fg.G * a) + (bgG * (1.0 - a))), 0, 255);
        byte b = (byte)Math.Clamp((int)Math.Round((fg.B * a) + (bgB * (1.0 - a))), 0, 255);
        return ComputeContrastRatio(r, g, b, bgR, bgG, bgB);
    }

    public static void SrgbToOkLab(byte rByte, byte gByte, byte bByte, out double L, out double a, out double b)
    {
        double r = SrgbByteToLinear(rByte);
        double g = SrgbByteToLinear(gByte);
        double bl = SrgbByteToLinear(bByte);

        double l = (0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * bl);
        double m = (0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * bl);
        double s = (0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * bl);

        double l_ = Math.Cbrt(Math.Max(0.0, l));
        double m_ = Math.Cbrt(Math.Max(0.0, m));
        double s_ = Math.Cbrt(Math.Max(0.0, s));

        L = (0.2104542553 * l_) + (0.7936177850 * m_) - (0.0040720468 * s_);
        a = (1.9779984951 * l_) - (2.4285922050 * m_) + (0.4505937099 * s_);
        b = (0.0259040371 * l_) + (0.7827717662 * m_) - (0.8086757660 * s_);
    }

    private static void OkLabToLinearSrgb(double L, double a, double b, out double r, out double g, out double bl)
    {
        double l_ = L + (0.3963377774 * a) + (0.2158037573 * b);
        double m_ = L - (0.1055613458 * a) - (0.0638541728 * b);
        double s_ = L - (0.0894841775 * a) - (1.2914855480 * b);

        double l = l_ * l_ * l_;
        double m = m_ * m_ * m_;
        double s = s_ * s_ * s_;

        r = (+4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s);
        g = (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s);
        bl = (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s);
    }

    /// <summary>
    /// Converts OKLCH to gamut-mapped sRGB with a strict post-sRGB WCAG relative luminance ceiling.
    /// Guarantees ComputeRelativeLuminance(result) <= maxLinearLuminance even after 8-bit rounding.
    /// </summary>
    public static Color OklchToClampedSrgb(
        double L,
        double C,
        double hueRadians,
        double maxLinearLuminance,
        bool forceNeutral)
    {
        double clampedL = Math.Clamp(L, 0.0, 1.0);
        if (forceNeutral || C <= 1e-6)
        {
            OkLabToLinearSrgb(clampedL, 0.0, 0.0, out double r0, out double g0, out double b0);
            double grayLin = Math.Clamp((r0 + g0 + b0) / 3.0, 0.0, maxLinearLuminance);
            byte gByte = LinearToSrgbByte(grayLin);
            while (gByte > 0 && ComputeRelativeLuminance(Color.FromRgb(gByte, gByte, gByte)) > maxLinearLuminance)
            {
                gByte--;
            }
            return Color.FromRgb(gByte, gByte, gByte);
        }

        double cosH = Math.Cos(hueRadians);
        double sinH = Math.Sin(hueRadians);

        // Gamut-map by binary-searching chroma inside sRGB [0, 1] so hue never shifts
        double loC = 0.0;
        double hiC = Math.Max(0.0, C);
        OkLabToLinearSrgb(clampedL, hiC * cosH, hiC * sinH, out double r, out double g, out double b);
        if (r < 0.0 || r > 1.0 || g < 0.0 || g > 1.0 || b < 0.0 || b > 1.0)
        {
            for (int iter = 0; iter < 12; iter++)
            {
                double midC = 0.5 * (loC + hiC);
                OkLabToLinearSrgb(clampedL, midC * cosH, midC * sinH, out double mr, out double mg, out double mb);
                if (mr >= 0.0 && mr <= 1.0 && mg >= 0.0 && mg <= 1.0 && mb >= 0.0 && mb <= 1.0)
                {
                    loC = midC;
                }
                else
                {
                    hiC = midC;
                }
            }
            OkLabToLinearSrgb(clampedL, loC * cosH, loC * sinH, out r, out g, out b);
        }

        r = Math.Clamp(r, 0.0, 1.0);
        g = Math.Clamp(g, 0.0, 1.0);
        b = Math.Clamp(b, 0.0, 1.0);

        // Hard post-sRGB relative luminance ceiling (convex combination bound for all overlapping blobs)
        double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        if (y > maxLinearLuminance && y > 1e-9)
        {
            double scale = maxLinearLuminance / y;
            r *= scale;
            g *= scale;
            b *= scale;
        }

        byte rb = LinearToSrgbByte(r);
        byte gb = LinearToSrgbByte(g);
        byte bb = LinearToSrgbByte(b);

        // Guarantee strict <= maxLinearLuminance even after 8-bit rounding
        while (ComputeRelativeLuminance(Color.FromRgb(rb, gb, bb)) > maxLinearLuminance)
        {
            if (gb >= rb && gb >= bb && gb > 0) gb--;
            else if (rb >= bb && rb > 0) rb--;
            else if (bb > 0) bb--;
            else break;
        }

        return Color.FromRgb(rb, gb, bb);
    }
}
