using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Metadata;
using AuraAudio.UI.Controls;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;
using AuraAudio.UI.Views;
using AuraAudio.UI.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class ImmersiveBackdropTests
{
    private readonly ITestOutputHelper _output;

    public ImmersiveBackdropTests(ITestOutputHelper output)
    {
        _output = output;
    }

    internal static readonly string[] SyntheticCoverNames =
    [
        "white",
        "black",
        "red",
        "pastel",
        "grayscale",
        "noisy",
        "missing"
    ];

    internal static WriteableBitmap? CreateSyntheticCover(string name, int size = 128)
    {
        if (string.Equals(name, "missing", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var wb = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        byte[] pixels = new byte[size * size * 4];
        var rng = new Random(42);

        for (int y = 0; y < size; y++)
        {
            double ny = (double)y / Math.Max(1, size - 1);
            for (int x = 0; x < size; x++)
            {
                double nx = (double)x / Math.Max(1, size - 1);
                byte r, g, b;

                switch (name)
                {
                    case "white":
                        r = g = b = 255;
                        break;
                    case "black":
                        r = g = b = 0;
                        break;
                    case "red":
                        r = 255;
                        g = 0;
                        b = 0;
                        break;
                    case "pastel":
                        // Multi-stop pastel RGB gradient: (255,209,220) -> (199,249,204) -> (189,224,254)
                        if (nx < 0.5)
                        {
                            double t = nx * 2.0;
                            r = LerpByte(255, 199, t);
                            g = LerpByte(209, 249, t);
                            b = LerpByte(220, 204, t);
                        }
                        else
                        {
                            double t = (nx - 0.5) * 2.0;
                            r = LerpByte(199, 189, t);
                            g = LerpByte(249, 224, t);
                            b = LerpByte(204, 254, t);
                        }
                        // Slight vertical modulation for realism
                        r = LerpByte(r, 245, ny * 0.15);
                        g = LerpByte(g, 245, ny * 0.15);
                        b = LerpByte(b, 252, ny * 0.15);
                        break;
                    case "grayscale":
                        byte lum = LerpByte(16, 232, (nx + ny) * 0.5);
                        r = g = b = lum;
                        break;
                    case "noisy":
                        r = (byte)rng.Next(0, 256);
                        g = (byte)rng.Next(0, 256);
                        b = (byte)rng.Next(0, 256);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown synthetic cover name.");
                }

                int idx = ((y * size) + x) * 4;
                pixels[idx] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 255;
            }
        }

        wb.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        wb.Freeze();
        return wb;
    }

    private static byte LerpByte(byte a, byte b, double t) =>
        (byte)Math.Clamp((int)Math.Round(a + ((b - a) * Math.Clamp(t, 0.0, 1.0))), 0, 255);

    private static (byte R, byte G, byte B, double MaxY, int MaxChromaDelta) RenderAndScanBackdrop(
        ImmersiveBackdropControl ctrl,
        int width,
        int height)
    {
        ctrl.Measure(new Size(width, height));
        ctrl.Arrange(new Rect(0, 0, width, height));
        ctrl.UpdateLayout();

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(ctrl);
        return ScanBrightestPixel(rtb, width, height);
    }

    private static (byte R, byte G, byte B, double MaxY, int MaxChromaDelta) RenderExistingVisualAndScan(FrameworkElement ctrl)
    {
        int width = Math.Max(1, (int)Math.Round(ctrl.ActualWidth));
        int height = Math.Max(1, (int)Math.Round(ctrl.ActualHeight));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(ctrl);
        return ScanBrightestPixel(rtb, width, height);
    }

    private static (byte R, byte G, byte B, double MaxY, int MaxChromaDelta) ScanBrightestPixel(
        RenderTargetBitmap rtb,
        int width,
        int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        rtb.CopyPixels(pixels, stride, 0);

        double maxY = -1.0;
        byte maxR = 0, maxG = 0, maxB = 0;
        int maxChromaDelta = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            double y = ArtworkPaletteExtractor.ComputeRelativeLuminance(r, g, b);
            if (y > maxY)
            {
                maxY = y;
                maxR = r;
                maxG = g;
                maxB = b;
            }
            int chromaDelta = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(g - b), Math.Abs(r - b)));
            if (chromaDelta > maxChromaDelta)
            {
                maxChromaDelta = chromaDelta;
            }
        }

        return (maxR, maxG, maxB, maxY, maxChromaDelta);
    }

    [Fact]
    public async Task PaletteExtractor_EnforcesLuminanceCeilingSpreadNeutralityAndLru_AcrossAll7SyntheticCovers()
    {
        ArtworkPaletteExtractor.ClearCacheForTest();

        foreach (string name in SyntheticCoverNames)
        {
            var bmp = CreateSyntheticCover(name);
            var palette = ArtworkPaletteExtractor.ExtractFromBitmapSource(bmp, ArtworkPaletteExtractor.SafeDefault);

            // 1. Hard post-sRGB relative luminance ceiling on every role
            Color[] roles = [palette.BaseColor, palette.Blob1Color, palette.Blob2Color, palette.Blob3Color];
            foreach (Color c in roles)
            {
                double y = ArtworkPaletteExtractor.ComputeRelativeLuminance(c);
                Assert.True(
                    y <= ArtworkPaletteExtractor.SafeMaxLinearLuminance + 1e-6,
                    $"Cover '{name}' exceeded SafeMaxLinearLuminance ({ArtworkPaletteExtractor.SafeMaxLinearLuminance}): RGB=({c.R},{c.G},{c.B}) Y={y:F5}");
            }
            Assert.True(palette.MaxRoleRelativeLuminance <= ArtworkPaletteExtractor.SafeMaxLinearLuminance + 1e-6);

            // 2. Enforced lightness variety (depth without flat slab)
            Assert.True(
                palette.TotalLightnessSpread >= ArtworkPaletteExtractor.MinTotalLightnessSpread - 1e-6,
                $"Cover '{name}' TotalLightnessSpread={palette.TotalLightnessSpread:F3} < {ArtworkPaletteExtractor.MinTotalLightnessSpread}");
            Assert.True(
                palette.BlobLightnessSpread >= ArtworkPaletteExtractor.MinBlobLightnessSpread - 1e-6,
                $"Cover '{name}' BlobLightnessSpread={palette.BlobLightnessSpread:F3} < {ArtworkPaletteExtractor.MinBlobLightnessSpread}");

            // 3. Neutral covers stay strictly pure monochrome (R == G == B); chromatic covers carry artwork hue
            bool expectNeutral = name is "white" or "black" or "grayscale" or "missing";
            if (expectNeutral)
            {
                Assert.True(palette.IsMonochrome, $"Cover '{name}' must produce monochrome palette.");
                foreach (Color c in roles)
                {
                    Assert.True(c.R == c.G && c.G == c.B, $"Cover '{name}' produced non-neutral RGB=({c.R},{c.G},{c.B})");
                }
            }
            else
            {
                Assert.False(palette.IsMonochrome, $"Chromatic cover '{name}' should not collapse to monochrome.");
                bool anyChroma = false;
                foreach (Color c in roles)
                {
                    if (Math.Max(Math.Abs(c.R - c.G), Math.Abs(c.G - c.B)) >= 4) anyChroma = true;
                }
                Assert.True(anyChroma, $"Chromatic cover '{name}' lost hue completely.");
            }
        }

        // 4. Deterministic repeatability on noisy cover
        var noisy1 = ArtworkPaletteExtractor.ExtractFromBitmapSource(CreateSyntheticCover("noisy"));
        var noisy2 = ArtworkPaletteExtractor.ExtractFromBitmapSource(CreateSyntheticCover("noisy"));
        Assert.Equal(noisy1, noisy2);

        // 5. Async background extraction & bounded 64-entry LRU cache
        ArtworkPaletteExtractor.ClearCacheForTest();
        var redBmp = CreateSyntheticCover("red")!;
        try
        {
            for (int i = 0; i < 66; i++)
            {
                string key = $"synth_lru_test_{i}";
                CoverArtLoader.RegisterSyntheticCoverForTest(key, redBmp);
                var p = await ArtworkPaletteExtractor.ExtractForCoverKeyAsync(key);
                Assert.False(p.IsMonochrome);
            }

            Assert.Equal(ArtworkPaletteExtractor.LruCacheCapacity, ArtworkPaletteExtractor.CacheCount);
            Assert.False(ArtworkPaletteExtractor.TryGetCached("synth_lru_test_0|SafeDefault", out _));
            Assert.True(ArtworkPaletteExtractor.TryGetCached("synth_lru_test_65|SafeDefault", out _));

            // Synchronous cache hit check (already completed task)
            var hitTask = ArtworkPaletteExtractor.ExtractForCoverKeyAsync("synth_lru_test_65");
            Assert.True(hitTask.IsCompletedSuccessfully);
        }
        finally
        {
            for (int i = 0; i < 66; i++)
            {
                CoverArtLoader.UnregisterSyntheticCoverForTest($"synth_lru_test_{i}");
            }
            ArtworkPaletteExtractor.ClearCacheForTest();
        }
    }

    [Fact]
    public void RenderedBackdropPixels_MeetHardReadabilityContrastGates_AcrossAll7SyntheticCovers()
    {
        WpfStaTestRunner.Run(() =>
        {
            var host = new Window
            {
                Width = 700,
                Height = 420,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();

                var scrimChecks = new (BackdropScrimMode Mode, bool Compact, int W, int H)[]
                {
                    (BackdropScrimMode.FullScreen, false, 640, 360),
                    (BackdropScrimMode.MiniCompact, true, 350, 160),
                    (BackdropScrimMode.MiniTray, true, 350, 260)
                };

                Color peak = (Color)Application.Current.FindResource("Color.Neutral.100");
                Color primary = (Color)Application.Current.FindResource("Color.Neutral.92");
                Color secondary = (Color)Application.Current.FindResource("Color.Neutral.80");
                Color tertiary = (Color)Application.Current.FindResource("Color.Neutral.65");

                foreach (string coverName in SyntheticCoverNames)
                {
                    var bmp = CreateSyntheticCover(coverName);
                    var palette = ArtworkPaletteExtractor.ExtractFromBitmapSource(bmp, ArtworkPaletteExtractor.SafeDefault);
                    bool expectNeutral = coverName is "white" or "black" or "grayscale" or "missing";

                    foreach (var sc in scrimChecks)
                    {
                        var ctrl = new ImmersiveBackdropControl
                        {
                            Width = sc.W,
                            Height = sc.H,
                            CompactMode = sc.Compact,
                            ScrimMode = sc.Mode,
                            IsPlaying = false
                        };
                        host.Content = ctrl;
                        host.UpdateLayout();
                        ctrl.ApplyPaletteImmediatelyForTest(palette);

                        var (brR, brG, brB, maxY, maxChromaDelta) = RenderAndScanBackdrop(ctrl, sc.W, sc.H);

                        double crPeak = ArtworkPaletteExtractor.ComputeContrastRatio(peak.R, peak.G, peak.B, brR, brG, brB);
                        double crPrimary = ArtworkPaletteExtractor.ComputeContrastRatio(primary.R, primary.G, primary.B, brR, brG, brB);
                        double crSecondary = ArtworkPaletteExtractor.ComputeContrastRatio(secondary.R, secondary.G, secondary.B, brR, brG, brB);
                        double crTertiary = ArtworkPaletteExtractor.ComputeContrastRatio(tertiary.R, tertiary.G, tertiary.B, brR, brG, brB);
                        double crDist1 = ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityAdjacent, brR, brG, brB);
                        double crDist2 = ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityNear, brR, brG, brB);
                        double crDist3 = ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityFar, brR, brG, brB);
                        double crDot = ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityDotUnfilled, brR, brG, brB);

                        Assert.True(crPeak >= 4.5, $"{coverName}/{sc.Mode}: Peak {crPeak:F2}:1 < 4.5 (brightest Y={maxY:F4})");
                        Assert.True(crPrimary >= 4.5, $"{coverName}/{sc.Mode}: Primary {crPrimary:F2}:1 < 4.5");
                        Assert.True(crSecondary >= 4.5, $"{coverName}/{sc.Mode}: Secondary {crSecondary:F2}:1 < 4.5");
                        Assert.True(crTertiary >= 4.5, $"{coverName}/{sc.Mode}: Tertiary {crTertiary:F2}:1 < 4.5 (brightest RGB={brR},{brG},{brB} Y={maxY:F4})");
                        Assert.True(crDist1 >= 4.5, $"{coverName}/{sc.Mode}: Lyric +-1 {crDist1:F2}:1 < 4.5");
                        Assert.True(crDist2 >= 3.0, $"{coverName}/{sc.Mode}: Lyric +-2 {crDist2:F2}:1 < 3.0");
                        Assert.True(crDist3 >= 3.0, $"{coverName}/{sc.Mode}: Lyric +-3+ {crDist3:F2}:1 < 3.0");
                        Assert.True(crDot >= 3.0, $"{coverName}/{sc.Mode}: Unfilled dot {crDot:F2}:1 < 3.0");

                        if (expectNeutral)
                        {
                            Assert.True(maxChromaDelta <= 1, $"{coverName}/{sc.Mode}: Expected pure neutral rendered pixels, got maxChromaDelta={maxChromaDelta}");
                        }
                        else
                        {
                            Assert.True(maxChromaDelta >= 4, $"{coverName}/{sc.Mode}: Expected visible artwork hue in rendered backdrop, got maxChromaDelta={maxChromaDelta}");
                        }
                    }
                }
            }
            finally
            {
                host.Close();
                GC.Collect();
            }
        });
    }

    [Fact]
    public void FreezeStateMachine_CrossFade_AndRule5AutoFallback_AllVerified()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool prevExt = SpringAnimator.UseExternalClockForTest;
            bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
            bool? prevBatt = PowerStatusHelper.ForceBatterySaverOverrideForTest;
            SpringAnimator.UseExternalClockForTest = true;
            SpringAnimator.ForceReducedMotionOverride = false;
            PowerStatusHelper.ForceBatterySaverOverrideForTest = false;

            var ctrl = new ImmersiveBackdropControl
            {
                Width = 640,
                Height = 360,
                IsPlaying = true,
                IsAnimatedBackdropEnabled = true,
                IsHostActive = true
            };
            var host = new Window
            {
                Width = 660,
                Height = 380,
                Content = ctrl,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                // 1. All conditions met -> animating and drifting
                Assert.True(ctrl.IsDriftAnimating);
                Assert.True(ctrl.IsTickerRegisteredForTest);

                double t0 = ctrl.DriftTimeSecondsForTest;
                for (int i = 0; i < 30; i++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                }
                Assert.True(ctrl.DriftTimeSecondsForTest > t0 + 0.45);

                // 2. Freeze on Pause
                ctrl.IsPlaying = false;
                Assert.False(ctrl.IsDriftAnimating);
                Assert.False(ctrl.IsTickerRegisteredForTest);
                ctrl.IsPlaying = true;
                Assert.True(ctrl.IsDriftAnimating);

                // 3. Freeze on Minimized
                ctrl.SetHostMinimizedForTest(true);
                Assert.False(ctrl.IsDriftAnimating);
                Assert.False(ctrl.IsTickerRegisteredForTest);
                ctrl.SetHostMinimizedForTest(false);
                Assert.True(ctrl.IsDriftAnimating);

                // 4. Freeze on Battery Saver
                PowerStatusHelper.ForceBatterySaverOverrideForTest = true;
                ctrl.RefreshPowerStatusForTest();
                Assert.False(ctrl.IsDriftAnimating);
                Assert.False(ctrl.IsTickerRegisteredForTest);
                PowerStatusHelper.ForceBatterySaverOverrideForTest = false;
                ctrl.RefreshPowerStatusForTest();
                Assert.True(ctrl.IsDriftAnimating);

                // 5. Freeze on Settings "Animated backdrop" toggle OFF
                ctrl.IsAnimatedBackdropEnabled = false;
                Assert.False(ctrl.IsDriftAnimating);
                Assert.False(ctrl.IsTickerRegisteredForTest);
                ctrl.IsAnimatedBackdropEnabled = true;
                Assert.True(ctrl.IsDriftAnimating);

                // 6. Freeze on Reduced Motion ON
                SpringAnimator.ForceReducedMotionOverride = true;
                Assert.False(ctrl.IsDriftAnimating);
                Assert.False(ctrl.IsTickerRegisteredForTest);
                SpringAnimator.ForceReducedMotionOverride = false;
                Assert.True(ctrl.IsDriftAnimating);

                // 7. Freeze on Hidden / Inactive Host (e.g. AlbumView or closed FullScreen)
                ctrl.IsHostActive = false;
                Assert.False(ctrl.IsDriftAnimating);
                Assert.False(ctrl.IsTickerRegisteredForTest);
                ctrl.IsHostActive = true;
                Assert.True(ctrl.IsDriftAnimating);

                // 8. 650 ms A/B Cross-Fade on track change
                var redBmp = CreateSyntheticCover("red")!;
                var pastelBmp = CreateSyntheticCover("pastel")!;
                CoverArtLoader.RegisterSyntheticCoverForTest("xfade_red", redBmp);
                CoverArtLoader.RegisterSyntheticCoverForTest("xfade_pastel", pastelBmp);
                try
                {
                    // Pre-warm cache so synchronous hit triggers cross-fade deterministically
                    ArtworkPaletteExtractor.ExtractForCoverKeyAsync("xfade_red").GetAwaiter().GetResult();
                    ArtworkPaletteExtractor.ExtractForCoverKeyAsync("xfade_pastel").GetAwaiter().GetResult();

                    ctrl.CoverArtCacheKey = "xfade_red";
                    for (int i = 0; i < 60 && ctrl.IsCrossFading; i++)
                    {
                        SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                    }
                    Assert.False(ctrl.IsCrossFading);

                    ctrl.CoverArtCacheKey = "xfade_pastel";
                    Assert.True(ctrl.IsCrossFading);

                    // Mid-crossfade (~325 ms) -> both layers partially visible
                    for (int i = 0; i < 20; i++)
                    {
                        SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                    }
                    Assert.True(ctrl.IsCrossFading);
                    Assert.True(ctrl.LayerAOpacityForTest > 0.1 && ctrl.LayerAOpacityForTest < 0.9);
                    Assert.True(ctrl.LayerBOpacityForTest > 0.1 && ctrl.LayerBOpacityForTest < 0.9);

                    // Complete cross-fade (~700 ms total)
                    for (int i = 0; i < 25; i++)
                    {
                        SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                    }
                    Assert.False(ctrl.IsCrossFading);
                    Assert.Equal(1.0, Math.Max(ctrl.LayerAOpacityForTest, ctrl.LayerBOpacityForTest), precision: 3);
                    Assert.Equal(0.0, Math.Min(ctrl.LayerAOpacityForTest, ctrl.LayerBOpacityForTest), precision: 3);
                }
                finally
                {
                    CoverArtLoader.UnregisterSyntheticCoverForTest("xfade_red");
                    CoverArtLoader.UnregisterSyntheticCoverForTest("xfade_pastel");
                }

                // 9. Rule 5 Performance Auto-Fallback: 2 consecutive slow 2.0 s windows (p95 > 1.5x vsync) freeze backdrop
                Assert.False(ctrl.IsAutoFallbackFrozen);
                for (int i = 0; i < 280; i++)
                {
                    // 15 ms per frame on 165 Hz (vsync = 6.06 ms, 1.5x = 9.09 ms) -> 280 * 15 ms = 4.2 s (> 2 windows)
                    ctrl.RecordFrameDtForFallbackTest(dtMs: 15.0, vsyncMs: 6.06);
                }
                Assert.True(ctrl.IsAutoFallbackFrozen);
                Assert.False(ctrl.IsDriftAnimating);
                Assert.False(ctrl.IsTickerRegisteredForTest);
            }
            finally
            {
                host.Close();
                SpringAnimator.UseExternalClockForTest = prevExt;
                SpringAnimator.ForceReducedMotionOverride = prevRm;
                PowerStatusHelper.ForceBatterySaverOverrideForTest = prevBatt;
            }
        });
    }

    [Fact]
    public void SettingsAnimatedBackdropToggle_PersistsAndControlsMiniPlayerAlbumViewCollapse()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_BackdropSettings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "settings_backdrop.db");

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                using var db = new DatabaseManager(dbPath);
                db.Initialize();
                using var vm1 = new MainViewModel(db: db);

                // Default ON
                Assert.True(vm1.Settings.AnimatedBackdropEnabled);

                // Toggle OFF and confirm SQLite persistence across reload
                vm1.Settings.AnimatedBackdropEnabled = false;
                Assert.Equal("0", db.GetSetting("animated_backdrop"));

                using var vm2 = new MainViewModel(db: db);
                Assert.False(vm2.Settings.AnimatedBackdropEnabled);

                // Verify SettingsView exposes the toggle with expected AutomationId
                var settingsView = new SettingsView { DataContext = vm2 };
                vm2.Settings.ActiveCategory = "Accessibility";
                var settingsHost = new Window
                {
                    Width = 900,
                    Height = 640,
                    Content = settingsView,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Left = -20000,
                    Top = -20000
                };
                try
                {
                    settingsHost.Show();
                    settingsHost.UpdateLayout();
                    var toggle = FindByAutomationId<CheckBox>(settingsView, "Aura.Settings.A11y.AnimatedBackdrop");
                    Assert.NotNull(toggle);
                    Assert.Equal(false, toggle!.IsChecked);
                    toggle.IsChecked = true;
                    Assert.True(vm2.Settings.AnimatedBackdropEnabled);
                    Assert.Equal("1", db.GetSetting("animated_backdrop"));
                }
                finally
                {
                    settingsHost.Close();
                }

                // Verify MiniPlayer hides/pauses StageBackdropControl in AlbumView (350x350 full cover art)
                var mini = new MiniPlayerWindow(vm2)
                {
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000
                };
                try
                {
                    mini.Show();
                    mini.UpdateLayout();
                    vm2.Presentation.IsMiniPlayerAlbumView = false;
                    mini.UpdateLayout();
                    Assert.Equal(Visibility.Visible, mini.StageBackdropControl.Visibility);

                    vm2.Presentation.IsMiniPlayerAlbumView = true;
                    mini.UpdateLayout();
                    Assert.Equal(Visibility.Collapsed, mini.StageBackdropControl.Visibility);
                    Assert.False(mini.StageBackdropControl.IsDriftAnimating);
                }
                finally
                {
                    mini.Close();
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void CapturePhase3BackdropScreenshots_All7SyntheticCovers()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("AURA_CAPTURE_BACKDROP_SHOTS") == "1",
            "Opt-in Phase 3 backdrop screenshot capture (set AURA_CAPTURE_BACKDROP_SHOTS=1).");

        string outDir = Environment.GetEnvironmentVariable("AURA_BACKDROP_SHOTS_OUTDIR")
            ?? Path.Combine(Path.GetTempPath(), "AuraAudio_Phase3_Shots");
        Directory.CreateDirectory(outDir);

        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_BackdropShotsDb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        const string sampleLrc = """
            [au:AuraAudio Synthetic Test Suite]
            [00:02.00]Lewat sudah
            [00:05.00]Pukul dua
            [00:08.50]Makin banyak bicara kita
            [00:15.50]Kau semakin merah delima
            [00:20.00]次 とその次 とその次 と線を引き続けた
            [00:26.00]次の目的地 を目的地 を描くんだ
            [00:31.00]宝島
            """;

        static void SavePng(FrameworkElement target, string path)
        {
            target.UpdateLayout();
            double dpiScale = VisualTreeHelper.GetDpi(target).DpiScaleX;
            int pw = Math.Max(1, (int)Math.Round(target.ActualWidth * dpiScale));
            int ph = Math.Max(1, (int)Math.Round(target.ActualHeight * dpiScale));
            var rtb = new RenderTargetBitmap(pw, ph, 96.0 * dpiScale, 96.0 * dpiScale, PixelFormats.Pbgra32);
            rtb.Render(target);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }

        var report = new StringBuilder(2048);
        report.AppendLine("=== Phase 3 Backdrop 7-Cover Rendered Readability Report (SAFE Default) ===");

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool prevExt = SpringAnimator.UseExternalClockForTest;
                bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
                SpringAnimator.UseExternalClockForTest = true;
                SpringAnimator.ForceReducedMotionOverride = false;

                using var db = new DatabaseManager(Path.Combine(tempDir, "shots.db"));
                db.Initialize();
                var spec = new AudioStreamSpec(AudioCodec.FLAC, 96000, BitDepth.Bit24, 2, 2816);
                using var vm = new MainViewModel(db: db);

                var win = new MainWindow(vm)
                {
                    Width = 1536,
                    Height = 860,
                    Left = 0,
                    Top = 0,
                    ShowInTaskbar = false
                };
                var mini = new MiniPlayerWindow(vm)
                {
                    Left = 100,
                    Top = 100,
                    ShowInTaskbar = false
                };

                try
                {
                    win.Show();
                    mini.Show();
                    vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(sampleLrc, LyricsSourceKind.SidecarLrc));
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(5.4), isPlaying: true);
                    vm.Presentation.IsFullScreenLyricsOpen = true;
                    vm.Presentation.IsFullScreenOpen = true;
                    vm.Presentation.IsMiniPlayerAlbumView = false;
                    vm.Presentation.MiniPlayerTrayMode = RightPaneMode.Lyrics;

                    for (int f = 0; f < 200 && SpringAnimator.HasActiveSprings; f++)
                    {
                        SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                    }
                    win.UpdateLayout();
                    mini.UpdateLayout();

                    var fsView = (FullScreenPlayerView)win.FindName("FullScreenStageOverlay");

                    Color peak = (Color)Application.Current.FindResource("Color.Neutral.100");
                    Color tertiary = (Color)Application.Current.FindResource("Color.Neutral.65");

                    for (int i = 0; i < SyntheticCoverNames.Length; i++)
                    {
                        string name = SyntheticCoverNames[i];
                        string? key = name == "missing" ? null : $"synth_shot_{name}";
                        var bmp = CreateSyntheticCover(name);
                        if (key != null && bmp != null)
                        {
                            CoverArtLoader.RegisterSyntheticCoverForTest(key, bmp);
                        }

                        try
                        {
                            var palette = ArtworkPaletteExtractor.ExtractFromBitmapSource(bmp, ArtworkPaletteExtractor.SafeDefault);
                            vm.CurrentTrack = new Track(
                                id: i + 1,
                                filePath: Path.Combine(tempDir, $"{name}.flac"),
                                title: $"Synthetic Cover: {name.ToUpperInvariant()}",
                                artist: "AuraAudio v2.1 Verification",
                                sortArtist: "AuraAudio",
                                album: $"Backdrop Test ({name})",
                                albumArtist: "AuraAudio",
                                year: 2026,
                                trackNumber: i + 1,
                                discNumber: 1,
                                duration: TimeSpan.FromMinutes(3.5),
                                streamSpec: spec,
                                fileSizeBytes: 42_000_000,
                                dateAdded: DateTime.UtcNow,
                                hasEmbeddedCover: bmp != null,
                                coverArtCacheKey: key);

                            fsView.BackdropControl.ApplyPaletteImmediatelyForTest(palette);
                            mini.StageBackdropControl.ApplyPaletteImmediatelyForTest(palette);
                            mini.TrayBackdropControl.ApplyPaletteImmediatelyForTest(palette);

                            win.UpdateLayout();
                            mini.UpdateLayout();

                            SavePng(win, Path.Combine(outDir, $"fs_{i + 1:D2}_{name}.png"));
                            SavePng(mini, Path.Combine(outDir, $"mini_{i + 1:D2}_{name}.png"));

                            // Scan backdrop pixels without mutating the live window's layout size
                            var (fsR, fsG, fsB, fsMaxY, fsChroma) = RenderExistingVisualAndScan(fsView.BackdropControl);
                            double crPeak = ArtworkPaletteExtractor.ComputeContrastRatio(peak.R, peak.G, peak.B, fsR, fsG, fsB);
                            double crTert = ArtworkPaletteExtractor.ComputeContrastRatio(tertiary.R, tertiary.G, tertiary.B, fsR, fsG, fsB);
                            double crFar = ArtworkPaletteExtractor.ComputeAlphaOverContrastRatio(peak, SyncedLyricsViewport.OpacityFar, fsR, fsG, fsB);

                            report.AppendLine(
                                $"  {name,-9} | Base=({palette.BaseColor.R,3},{palette.BaseColor.G,3},{palette.BaseColor.B,3}) " +
                                $"B1=({palette.Blob1Color.R,3},{palette.Blob1Color.G,3},{palette.Blob1Color.B,3}) " +
                                $"B2=({palette.Blob2Color.R,3},{palette.Blob2Color.G,3},{palette.Blob2Color.B,3}) " +
                                $"B3=({palette.Blob3Color.R,3},{palette.Blob3Color.G,3},{palette.Blob3Color.B,3}) | " +
                                $"BrightestY={fsMaxY:F4} Chroma={fsChroma,2} | Peak={crPeak:F2}:1 Tert={crTert:F2}:1 Far={crFar:F2}:1");
                        }
                        finally
                        {
                            if (key != null)
                            {
                                CoverArtLoader.UnregisterSyntheticCoverForTest(key);
                            }
                        }
                    }
                }
                finally
                {
                    mini.Close();
                    win.Close();
                    SpringAnimator.UseExternalClockForTest = prevExt;
                    SpringAnimator.ForceReducedMotionOverride = prevRm;
                }
            });

            string reportPath = Path.Combine(outDir, "phase3_7cover_contrast.txt");
            File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
            _output.WriteLine(report.ToString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static T? FindByAutomationId<T>(DependencyObject root, string automationId) where T : DependencyObject
    {
        if (root is T match && string.Equals(AutomationProperties.GetAutomationId(root), automationId, StringComparison.Ordinal))
        {
            return match;
        }
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindByAutomationId<T>(VisualTreeHelper.GetChild(root, i), automationId);
            if (found != null) return found;
        }
        return null;
    }
}
