using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AuraAudio.Audio;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Core.Models;
using AuraAudio.Metadata;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;
using AuraAudio.UI.Windows;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace AuraAudio.Tests;

/// <summary>
/// Step 3 Proof of Rebuild — Runtime Pixel Purity Verification (Item 4c).
/// Launches a real MainWindow with full App.xaml token/style dictionaries (NeutralTokens, SystemOverrides,
/// Typography, Metrics, Motion, Controls), renders all 10 required no-artwork views/states via WPF
/// RenderTargetBitmap, saves lossless PNGs for visual inspection, and verifies programmatically that EVERY
/// rendered pixel satisfies max(|R-G|, |G-B|, |R-B|) <= 2 (zero Windows accent bleed, zero chromatic tint).
/// </summary>
[Collection("WpfSta")]
public class RuntimePixelPurityTests
{
    private readonly ITestOutputHelper _output;

    public RuntimePixelPurityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record PixelReport(
        string Name,
        int Width,
        int Height,
        int DistinctGrays,
        int MaxDelta,
        int PixelsOver2,
        int WorstX,
        int WorstY,
        byte WorstR,
        byte WorstG,
        byte WorstB);

    [Fact]
    public void RuntimePixelCheck_AllNoArtworkViewsAndControlsArePureMonochrome()
    {
        string outDir = Environment.GetEnvironmentVariable("AURA_PIXEL_AUDIT_DIR")
            ?? Path.Combine(Path.GetTempPath(), "AuraAudio_PixelAudit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);

        var reports = new List<PixelReport>();
        string? aboutVersionText = null;
        string? windowTitle = null;

        WpfStaTestRunner.Run(() =>
        {
            bool? prevReduced = SpringAnimator.ForceReducedMotionOverride;
            SpringAnimator.ForceReducedMotionOverride = true;

            MainWindow? win = null;
            try
            {
                win = new MainWindow
                {
                    Width = 1440,
                    Height = 900,
                    ShowInTaskbar = false,
                    Left = -20000,
                    Top = -20000
                };
                win.Show();
                Pump(180);

                var vm = Assert.IsType<MainViewModel>(win.DataContext);

                // Ensure truly artwork-free / empty-queue state (in case bin/ test dir has a local DB)
                vm.CurrentTrack = null;
                vm.QueueTracks.Clear();
                vm.InspectedTrack = null;
                vm.CurrentIntegrityReport = null;
                Pump(100);

                windowTitle = win.Title;
                Assert.Contains("v2.0.3", windowTitle, StringComparison.OrdinalIgnoreCase);

                // 1-5. Settings in all 5 categories
                vm.CurrentView = "Settings";
                Pump(140);

                var categories = new[]
                {
                    ("General",       "01_settings_general.png"),
                    ("Playback",      "02_settings_playback.png"),
                    ("Library",       "03_settings_library.png"),
                    ("Accessibility", "04_settings_accessibility.png"),
                    ("About",         "05_settings_about.png"),
                };

                foreach (var (cat, file) in categories)
                {
                    vm.Settings.ActiveCategory = cat;
                    Pump(140);
                    reports.Add(CaptureAndAudit(win, Path.Combine(outDir, file), file));
                    if (cat == "About")
                    {
                        aboutVersionText = vm.Settings.AppVersionText;
                    }
                }

                Assert.NotNull(aboutVersionText);
                Assert.Contains("v2.0.3", aboutVersionText, StringComparison.OrdinalIgnoreCase);

                // 6. Audio Inspector (no track / no artwork)
                vm.CurrentView = "Inspector";
                Pump(140);
                reports.Add(CaptureAndAudit(win, Path.Combine(outDir, "06_inspector.png"), "06_inspector.png"));

                // 7. Queue Empty State (open right split pane over Inspector — clear queue so true empty state renders)
                vm.ClearQueueCommand.Execute(null);
                vm.QueueTracks.Clear();
                vm.Presentation.SelectQueueTabCommand.Execute("PlayingNext");
                vm.Presentation.ActiveRightPane = RightPaneMode.Queue;
                Pump(180);
                Assert.Empty(vm.QueueTracks);
                reports.Add(CaptureAndAudit(win, Path.Combine(outDir, "07_queue_empty.png"), "07_queue_empty.png"));
                vm.Presentation.ActiveRightPane = RightPaneMode.None;
                Pump(120);

                // 8. Text Selection inside GlobalSearchBox (TextBox SelectionBrush + SelectionTextBrush)
                var searchBox = Assert.IsType<TextBox>(win.FindName("GlobalSearchBox"));
                searchBox.Text = "AuraAudio Monochrome Selection";
                searchBox.Focus();
                Keyboard.Focus(searchBox);
                searchBox.SelectAll();
                Pump(140);
                reports.Add(CaptureAndAudit(win, Path.Combine(outDir, "08_text_selection.png"), "08_text_selection.png"));

                // Focused crop of the search pill so selection highlight is clearly visible
                var searchHost = searchBox.Parent as FrameworkElement ?? searchBox;
                reports.Add(CaptureAndAudit(searchHost, Path.Combine(outDir, "08b_text_selection_crop.png"), "08b_text_selection_crop.png"));
                searchBox.Text = string.Empty;
                vm.Search.IsHintsOpen = false;
                Pump(100);

                // 9. Keyboard Focus Ring (actual Style.FocusVisual.Monochrome adorner on Aura.Nav.Settings)
                vm.CurrentView = "Settings";
                vm.Settings.ActiveCategory = "Root";
                Pump(120);
                var navSettingsBtn = FindDescendant<RadioButton>(win, rb =>
                    string.Equals(AutomationProperties.GetAutomationId(rb), "Aura.Nav.Settings", StringComparison.Ordinal));
                Assert.NotNull(navSettingsBtn);

                var focusStyle = win.TryFindResource("Style.FocusVisual.Monochrome") as Style;
                Assert.NotNull(focusStyle);
                var adornerLayer = AdornerLayer.GetAdornerLayer(navSettingsBtn!);
                Assert.NotNull(adornerLayer);
                var focusControl = new Control { Style = focusStyle, IsHitTestVisible = false };
                var focusAdorner = new FocusPreviewAdorner(navSettingsBtn!, focusControl);
                adornerLayer!.Add(focusAdorner);
                Pump(140);
                reports.Add(CaptureAndAudit(win, Path.Combine(outDir, "09_focus_ring.png"), "09_focus_ring.png"));
                adornerLayer.Remove(focusAdorner);
                Pump(60);

                // 10. Open ContextMenu (styled ContextMenu + MenuItems + Separator + Highlighted state)
                var ctxMenu = new ContextMenu
                {
                    PlacementTarget = navSettingsBtn,
                    Placement = PlacementMode.Bottom
                };
                var itemPlay = new MenuItem { Header = "Play Next", InputGestureText = "Ctrl+Enter" };
                var itemQueue = new MenuItem { Header = "Add to Queue" };
                var itemInfo = new MenuItem { Header = "Get Info", InputGestureText = "Ctrl+I" };
                var itemDisabled = new MenuItem { Header = "Remove from Playlist", IsEnabled = false };
                ctxMenu.Items.Add(itemPlay);
                ctxMenu.Items.Add(itemQueue);
                ctxMenu.Items.Add(new Separator());
                ctxMenu.Items.Add(itemInfo);
                ctxMenu.Items.Add(itemDisabled);
                ctxMenu.IsOpen = true;
                Pump(160);
                itemPlay.Focus();
                Pump(120);

                reports.Add(CaptureAndAudit(
                    ctxMenu,
                    Path.Combine(outDir, "10_context_menu.png"),
                    "10_context_menu.png",
                    explicitWidth: Math.Max(220, (int)Math.Ceiling(ctxMenu.ActualWidth + 8)),
                    explicitHeight: Math.Max(140, (int)Math.Ceiling(ctxMenu.ActualHeight + 8))));

                ctxMenu.IsOpen = false;
                Pump(60);
            }
            finally
            {
                SpringAnimator.ForceReducedMotionOverride = prevReduced;
                try { win?.Close(); } catch { }
            }
        });

        _output.WriteLine($"Window.Title = '{windowTitle}'");
        _output.WriteLine($"Settings.About = '{aboutVersionText}'");
        _output.WriteLine($"Artifacts saved to: {outDir}");
        _output.WriteLine("");

        var lines = new List<string>
        {
            $"Window.Title = {windowTitle}",
            $"Settings.About = {aboutVersionText}"
        };

        foreach (var r in reports)
        {
            string line = $"[{(r.MaxDelta <= 2 && r.DistinctGrays >= 3 ? "PASS" : "FAIL")}] {r.Name,-30} | {r.Width,4}x{r.Height,-4} | grays={r.DistinctGrays,3} | maxDelta={r.MaxDelta,3} | px>2={r.PixelsOver2,6} | worst=({r.WorstX},{r.WorstY})=RGB({r.WorstR},{r.WorstG},{r.WorstB})";
            _output.WriteLine(line);
            lines.Add(line);
        }

        File.WriteAllLines(Path.Combine(outDir, "pixel_purity_report.txt"), lines);

        foreach (var r in reports)
        {
            Assert.True(r.DistinctGrays >= 3, $"{r.Name} rendered blank/flat (only {r.DistinctGrays} gray levels).");
            Assert.True(r.MaxDelta <= 2,
                $"{r.Name} failed monochrome purity: maxDelta={r.MaxDelta} (>2) at ({r.WorstX},{r.WorstY})=RGB({r.WorstR},{r.WorstG},{r.WorstB}), px>2={r.PixelsOver2}");
        }
    }

    [Fact]
    public void Inspector_UpscaledHiRes_RendersMonochromeInBothLanguages()
    {
        // Audit 1d (v2.0.2): Generates a synthetic 192 kHz / 24-bit WAV upsampled from a 96 kHz band limit
        // (46.5 kHz brickwall into digital silence), analyzes it, and verifies that the new UpscaledHiRes
        // verdict renders cleanly and purely monochrome across InspectorView and InfoInspectorSheet in EN & ID.
        string outDir = Environment.GetEnvironmentVariable("AURA_PIXEL_AUDIT_DIR")
            ?? Path.Combine(Path.GetTempPath(), "AuraAudio_UpscaledHiResAudit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);

        string tempWav = Path.Combine(Path.GetTempPath(), "AuraAudio_Synth_UpscaledHiRes_192k_" + Guid.NewGuid().ToString("N")[..8] + ".wav");
        try
        {
            WriteSyntheticUpscaledHiRes192kWav(tempWav);
            AudioIntegrityReport report = NyquistSpectrumAnalyzer.Analyze(tempWav);
            Assert.Equal(AuthenticityVerdict.UpscaledHiRes, report.Verdict);
            Assert.Equal("Caution", report.VerdictCategory);
            Assert.Equal(48_000u, report.DetectedCutoffHz);

            var syntheticTrack = new AuraAudio.Core.Models.Track(
                id: 999_001,
                filePath: tempWav,
                title: "Synthetic 96k-to-192k Hi-Res Upsample",
                artist: "AuraAudio Verification Signal",
                sortArtist: "AuraAudio Verification Signal",
                album: "v2.0.2 Release Audit",
                albumArtist: "AuraAudio Verification Signal",
                year: 2026,
                trackNumber: 1,
                discNumber: 1,
                duration: TimeSpan.FromSeconds(2),
                streamSpec: report.SourceSpec,
                fileSizeBytes: new FileInfo(tempWav).Length,
                dateAdded: DateTime.UtcNow,
                hasEmbeddedCover: false,
                coverArtCacheKey: null);

            var reports = new List<PixelReport>();
            string? enBadge = null;
            string? idBadge = null;

            WpfStaTestRunner.Run(() =>
            {
                bool? prevReduced = SpringAnimator.ForceReducedMotionOverride;
                string prevLang = LocalizationManager.CurrentLanguage;
                SpringAnimator.ForceReducedMotionOverride = true;

                MainWindow? win = null;
                try
                {
                    win = new MainWindow
                    {
                        Width = 1440,
                        Height = 900,
                        ShowInTaskbar = false,
                        Left = -20000,
                        Top = -20000
                    };
                    win.Show();
                    Pump(180);

                    var vm = Assert.IsType<MainViewModel>(win.DataContext);
                    vm.CurrentTrack = null;
                    vm.QueueTracks.Clear();
                    vm.CurrentView = "Inspector";
                    vm.InspectedTrack = syntheticTrack;
                    vm.CurrentIntegrityReport = report;
                    Pump(160);

                    // 1. English Inspector capture
                    SetStaLanguage("en");
                    vm.CurrentIntegrityReport = null;
                    vm.CurrentIntegrityReport = report;
                    Pump(180);
                    enBadge = vm.CurrentIntegrityReport.VerdictBadge;
                    Assert.Contains("Upscaled Hi-Res", enBadge, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("96", vm.CurrentIntegrityReport.LocalizedExplanation, StringComparison.OrdinalIgnoreCase);
                    reports.Add(CaptureAndAudit(win, Path.Combine(outDir, "12_inspector_upscaled_hires_en.png"), "12_inspector_upscaled_hires_en.png"));

                    // 2. Indonesian Inspector capture
                    SetStaLanguage("id");
                    vm.CurrentIntegrityReport = null;
                    vm.CurrentIntegrityReport = report;
                    Pump(180);
                    idBadge = vm.CurrentIntegrityReport.VerdictBadge;
                    Assert.Contains("Upscale Hi-Res", idBadge, StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("96", vm.CurrentIntegrityReport.LocalizedExplanation, StringComparison.OrdinalIgnoreCase);
                    reports.Add(CaptureAndAudit(win, Path.Combine(outDir, "13_inspector_upscaled_hires_id.png"), "13_inspector_upscaled_hires_id.png"));

                    // 3. Get Info Sheet (Authenticity tab) with UpscaledHiRes in English
                    SetStaLanguage("en");
                    vm.PrepareInfoSheetForTrack(syntheticTrack);
                    vm.CurrentIntegrityReport = null;
                    vm.CurrentIntegrityReport = report;
                    vm.Presentation.ActiveInfoTab = "Authenticity";
                    vm.Presentation.IsInfoSheetOpen = true;
                    Pump(180);
                    reports.Add(CaptureAndAudit(win, Path.Combine(outDir, "14_infosheet_upscaled_hires_en.png"), "14_infosheet_upscaled_hires_en.png"));
                    vm.Presentation.CloseInfoSheetCommand.Execute(null);
                    Pump(80);
                }
                finally
                {
                    SpringAnimator.ForceReducedMotionOverride = prevReduced;
                    try { SetStaLanguage(prevLang); } catch { }
                    try { win?.Close(); } catch { }
                }
            });

            _output.WriteLine($"EN Badge = '{enBadge}'");
            _output.WriteLine($"ID Badge = '{idBadge}'");
            foreach (var r in reports)
            {
                _output.WriteLine($"[{(r.MaxDelta <= 2 ? "PASS" : "FAIL")}] {r.Name,-36} | {r.Width,4}x{r.Height,-4} | grays={r.DistinctGrays,3} | maxDelta={r.MaxDelta,3} | px>2={r.PixelsOver2,6}");
                Assert.True(r.DistinctGrays >= 3, $"{r.Name} rendered flat.");
                Assert.True(r.MaxDelta <= 2, $"{r.Name} failed monochrome purity: maxDelta={r.MaxDelta} at ({r.WorstX},{r.WorstY})=RGB({r.WorstR},{r.WorstG},{r.WorstB})");
            }
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
    }

    [SkippableFact]
    public void InfoSheet_Alac16BitAnd24Bit_RenderConsistentSpecsAndMonochrome()
    {
        Skip.IfNot(SourceFormatConsistencyTests.CheckFfmpegAvailable(), "ffmpeg is not installed on PATH; skipping ALAC Info Sheet UI screenshot test.");

        string outDir = Environment.GetEnvironmentVariable("AURA_PIXEL_AUDIT_DIR")
            ?? Path.Combine(Path.GetTempPath(), "AuraAudio_PixelAudit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);

        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_AlacUiAudit_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            string alac16Path = Path.Combine(tempDir, "test_alac_16bit_44k.m4a");
            string alac24Path = Path.Combine(tempDir, "test_alac_24bit_48k.m4a");

            SourceFormatConsistencyTests.RunFfmpeg(
                $"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r=44100:a=0.25\" -ac 2 -c:a alac -sample_fmt s16p -metadata title=\"ALAC 16-bit Test\" -metadata artist=\"AuraAudio Verification\" -metadata album=\"16-bit / 44.1 kHz Audit\" \"{alac16Path}\"");
            SourceFormatConsistencyTests.RunFfmpeg(
                $"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r=48000:a=0.25\" -ac 2 -c:a alac -sample_fmt s32p -metadata title=\"ALAC 24-bit Test\" -metadata artist=\"AuraAudio Verification\" -metadata album=\"24-bit / 48.0 kHz Audit\" \"{alac24Path}\"");

            // Force ISO 14496-12 AudioSampleEntry.samplesize = 16 while ALACSpecificConfig.bitDepth = 24
            SourceFormatConsistencyTests.OverwriteMp4AudioSampleEntrySampleSize(alac24Path, 16);

            var reader = new AudioMetadataReader();
            var meta16 = reader.ReadFile(alac16Path);
            var meta24 = reader.ReadFile(alac24Path);

            using var stream16 = AudioDecoderFactory.OpenStream(alac16Path);
            using var stream24 = AudioDecoderFactory.OpenStream(alac24Path);
            using var eng16 = new SourceFormatConsistencyTests.StubBitPerfectOutputEngine(stream16.WaveFormat);
            using var eng24 = new SourceFormatConsistencyTests.StubBitPerfectOutputEngine(stream24.WaveFormat);
            var sig16 = SignalPathAuditor.Audit(stream16, eng16, AudioDriverType.WASAPI_Exclusive);
            var sig24 = SignalPathAuditor.Audit(stream24, eng24, AudioDriverType.WASAPI_Exclusive);

            var reports = new List<PixelReport>();

            WpfStaTestRunner.Run(() =>
            {
                bool? prevReduced = SpringAnimator.ForceReducedMotionOverride;
                string prevLang = LocalizationManager.CurrentLanguage;
                SpringAnimator.ForceReducedMotionOverride = true;

                MainWindow? win = null;
                try
                {
                    win = new MainWindow
                    {
                        Width = 1440,
                        Height = 900,
                        ShowInTaskbar = false,
                        Left = -20000,
                        Top = -20000
                    };
                    win.Show();
                    Pump(180);

                    var vm = Assert.IsType<MainViewModel>(win.DataContext);
                    vm.QueueTracks.Clear();
                    vm.CurrentView = "Inspector";
                    SetStaLanguage("en");

                    foreach (var (track, sig, expectedResPrefix, expectedPcmLabel, shotName) in new[]
                    {
                        (meta16.Track, sig16, "16-bit / 44.1 kHz", "PCM 1,411 kbps", "15_infosheet_alac_16bit_44k.png"),
                        (meta24.Track, sig24, "24-bit / 48.0 kHz", "PCM 2,304 kbps", "16_infosheet_alac_24bit_48k.png")
                    })
                    {
                        vm.CurrentTrack = track;
                        vm.CurrentSignalPath = sig;
                        vm.SignalPathBadge = sig.BadgeText;
                        vm.PrepareInfoSheetForTrack(track);
                        vm.Presentation.ActiveInfoTab = "Audio";
                        vm.Presentation.IsInfoSheetOpen = true;
                        Pump(220);

                        Assert.StartsWith(expectedResPrefix, vm.InfoTargetDetails.SampleRateAndBitDepth);
                        Assert.Contains(expectedResPrefix, sig.Steps[0].Description);
                        Assert.Contains(vm.InfoTargetDetails.BitrateText, sig.Steps[0].Description);
                        Assert.Contains(expectedPcmLabel, sig.Steps[1].Description);

                        reports.Add(CaptureAndAudit(win, Path.Combine(outDir, shotName), shotName));

                        vm.Presentation.CloseInfoSheetCommand.Execute(null);
                        Pump(100);
                    }
                }
                finally
                {
                    SpringAnimator.ForceReducedMotionOverride = prevReduced;
                    try { SetStaLanguage(prevLang); } catch { }
                    try { win?.Close(); } catch { }
                }
            });

            foreach (var r in reports)
            {
                _output.WriteLine($"[{(r.MaxDelta <= 2 ? "PASS" : "FAIL")}] {r.Name,-36} | {r.Width,4}x{r.Height,-4} | grays={r.DistinctGrays,3} | maxDelta={r.MaxDelta,3} | px>2={r.PixelsOver2,6}");
                Assert.True(r.DistinctGrays >= 3, $"{r.Name} rendered flat.");
                Assert.True(r.MaxDelta <= 2, $"{r.Name} failed monochrome purity: maxDelta={r.MaxDelta} at ({r.WorstX},{r.WorstY})=RGB({r.WorstR},{r.WorstG},{r.WorstB})");
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void SetStaLanguage(string langCode)
    {
        var app = Application.Current;
        if (app != null)
        {
            var merged = app.Resources.MergedDictionaries;
            var existing = merged.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.Contains("/UI/Resources/Strings."));
            var next = new ResourceDictionary { Source = new Uri($"/AuraAudio;component/UI/Resources/Strings.{langCode}.xaml", UriKind.Relative) };
            if (existing != null) merged[merged.IndexOf(existing)] = next;
            else merged.Add(next);
        }

        // Update LocalizationManager._currentLanguage without broadcasting the static LanguageChanged
        // event to disposed MainViewModel instances created by earlier WpfSta tests whose temp DBs
        // were already deleted; re-assigning vm.CurrentIntegrityReport refreshes both report strings
        // and SpekSpectrumControl visuals, while {DynamicResource} bindings update immediately.
        var field = typeof(LocalizationManager).GetField(
            "_currentLanguage",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(null, langCode);
    }

    private static void WriteSyntheticUpscaledHiRes192kWav(string path)
    {
        const int sr = 192_000;
        const int totalSamples = sr * 2;
        var rng = new Random(20206);
        var freqs = new List<double>();
        for (double f = 500.0; f <= 46_500.0; f += 250.0) freqs.Add(f);
        double[] phases = new double[freqs.Count];
        for (int i = 0; i < phases.Length; i++) phases[i] = rng.NextDouble() * 2.0 * Math.PI;

        double[] mono = new double[totalSamples];
        double maxAbs = 1e-12;
        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / sr;
            double s = 0.0;
            for (int k = 0; k < freqs.Count; k++)
            {
                s += Math.Sin(2.0 * Math.PI * freqs[k] * t + phases[k]);
            }
            mono[i] = s;
            double a = Math.Abs(s);
            if (a > maxAbs) maxAbs = a;
        }
        double scale = 0.72 / maxAbs;

        using var writer = new WaveFileWriter(path, new WaveFormat(sr, 24, 2));
        byte[] sampleBuf = new byte[6];
        for (int i = 0; i < totalSamples; i++)
        {
            int v = (int)Math.Round(Math.Clamp(mono[i] * scale, -0.9999, 0.9999) * 8_388_607.0);
            sampleBuf[0] = (byte)(v & 0xFF);
            sampleBuf[1] = (byte)((v >> 8) & 0xFF);
            sampleBuf[2] = (byte)((v >> 16) & 0xFF);
            sampleBuf[3] = sampleBuf[0];
            sampleBuf[4] = sampleBuf[1];
            sampleBuf[5] = sampleBuf[2];
            writer.Write(sampleBuf, 0, 6);
        }
    }

    private sealed class FocusPreviewAdorner : Adorner
    {
        private readonly Control _visual;
        public FocusPreviewAdorner(UIElement adorned, Control visual) : base(adorned)
        {
            _visual = visual;
            AddVisualChild(_visual);
            AddLogicalChild(_visual);
        }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _visual;
        protected override Size ArrangeOverride(Size finalSize)
        {
            _visual.Arrange(new Rect(new Point(0, 0), finalSize));
            return finalSize;
        }
    }

    private static PixelReport CaptureAndAudit(Visual visual, string pngPath, string name, int? explicitWidth = null, int? explicitHeight = null)
    {
        int w = explicitWidth ?? (visual is FrameworkElement fe ? Math.Max(1, (int)Math.Ceiling(fe.ActualWidth)) : 100);
        int h = explicitHeight ?? (visual is FrameworkElement fe2 ? Math.Max(1, (int)Math.Ceiling(fe2.ActualHeight)) : 100);

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            var vb = new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        }
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(pngPath))
        {
            encoder.Save(fs);
        }

        int stride = w * 4;
        byte[] buf = new byte[stride * h];
        rtb.CopyPixels(buf, stride, 0);

        int maxDelta = 0;
        int pxOver2 = 0;
        int wx = 0, wy = 0;
        byte wr = 0, wg = 0, wb = 0;
        var grays = new HashSet<int>();

        for (int y = 0; y < h; y++)
        {
            int row = y * stride;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x * 4;
                byte b = buf[idx];
                byte g = buf[idx + 1];
                byte r = buf[idx + 2];
                byte a = buf[idx + 3];
                if (a == 0) continue;

                if (a < 255)
                {
                    r = (byte)Math.Min(255, r * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    b = (byte)Math.Min(255, b * 255 / a);
                }

                if (grays.Count < 256) grays.Add((r + g + b) / 3);

                int d = Math.Max(Math.Abs(r - g), Math.Max(Math.Abs(g - b), Math.Abs(r - b)));
                if (d > 2) pxOver2++;
                if (d > maxDelta)
                {
                    maxDelta = d;
                    wx = x; wy = y; wr = r; wg = g; wb = b;
                }
            }
        }

        return new PixelReport(name, w, h, grays.Count, maxDelta, pxOver2, wx, wy, wr, wg, wb);
    }

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is T match && (predicate == null || predicate(match))) return match;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindDescendant(VisualTreeHelper.GetChild(root, i), predicate);
            if (found != null) return found;
        }
        return null;
    }

    private static void Pump(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }
}
