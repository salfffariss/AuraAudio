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
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;
using AuraAudio.UI.Windows;
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
                Assert.Contains("v2.0.2", windowTitle, StringComparison.OrdinalIgnoreCase);

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
                Assert.Contains("v2.0.2", aboutVersionText, StringComparison.OrdinalIgnoreCase);

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
