using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
using Xunit;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class SyncedLyricsViewportTests
{
    private static readonly string[] RowHeightTestSamples =
    [
        "Makin banyak bicara kita di pukul dua pagi",
        "次 とその次 とその次 と線を引き続けた 宝島を描き出す",
        "Shin Takara Jima 新宝島 - sakanaction (16/44.1 ALAC)",
        "This is an intentionally very long synced lyric line designed to wrap cleanly across four or five visual rows in both the wide full-screen stage and the narrow right split pane without ever overlapping or clipping adjacent rows",
        ""
    ];

    [Fact]
    public void RowHeightMeasurement_MatchesRenderedTextBlockWithinHalfPixel_AcrossScriptsWidthsAndDpis()
    {
        WpfStaTestRunner.Run(() =>
        {
            var fontFamily = (Application.Current?.TryFindResource("Font.Display") as FontFamily)
                ?? new FontFamily("Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI");

            var configs = new (string Label, double Width, double FontSize, double LineHeight, Thickness Padding)[]
            {
                ("FullScreen_520px", 520.0, SyncedLyricsViewport.FullScreenFontSize, SyncedLyricsViewport.FullScreenLineHeight, new Thickness(12, 10, 12, 10)),
                ("Compact_312px", 312.0, SyncedLyricsViewport.CompactFontSize, SyncedLyricsViewport.CompactLineHeight, new Thickness(10, 7, 10, 7))
            };
            double[] dpiScales = [1.0, 1.25, 1.5];

            var host = new Window
            {
                Width = 640,
                Height = 640,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();

                foreach (double dpi in dpiScales)
                {
                    var dpiScale = new DpiScale(dpi, dpi);
                    foreach (var cfg in configs)
                    {
                        foreach (string sample in RowHeightTestSamples)
                        {
                            double measured = SyncedLyricsViewport.MeasureLyricRowHeight(
                                sample,
                                fontFamily,
                                cfg.FontSize,
                                cfg.LineHeight,
                                cfg.Width,
                                cfg.Padding);

                            var tb = new TextBlock
                            {
                                Text = string.IsNullOrEmpty(sample) ? " " : sample,
                                FontFamily = fontFamily,
                                FontWeight = FontWeights.Bold,
                                FontSize = cfg.FontSize,
                                LineHeight = cfg.LineHeight,
                                LineStackingStrategy = LineStackingStrategy.MaxHeight,
                                TextWrapping = TextWrapping.Wrap,
                                TextTrimming = TextTrimming.None,
                                UseLayoutRounding = false
                            };
                            TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Ideal);
                            TextOptions.SetTextRenderingMode(tb, TextRenderingMode.ClearType);

                            var pill = new Border
                            {
                                Width = cfg.Width,
                                Padding = cfg.Padding,
                                HorizontalAlignment = HorizontalAlignment.Left,
                                VerticalAlignment = VerticalAlignment.Top,
                                UseLayoutRounding = false,
                                Child = tb
                            };
                            VisualTreeHelper.SetRootDpi(pill, dpiScale);

                            host.Content = pill;
                            host.UpdateLayout();

                            double actualHeight = pill.ActualHeight;
                            double diff = Math.Abs(measured - actualHeight);

                            Assert.True(
                                diff <= 0.5,
                                $"Row height mismatch for {cfg.Label} @ DPI {(int)(dpi * 100)}% sample='{sample}': measured={measured:F3}, actual={actualHeight:F3}, diff={diff:F3}");
                            Assert.True(
                                actualHeight >= cfg.LineHeight + cfg.Padding.Top + cfg.Padding.Bottom - 0.5,
                                $"Row height collapsed below single line for sample='{sample}': actual={actualHeight:F3}");
                        }
                    }
                }
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void DistanceEmphasisTokens_AndZeroFontReflow_VerifiedAcrossTiers()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool prevExt = SpringAnimator.UseExternalClockForTest;
            bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
            SpringAnimator.UseExternalClockForTest = true;
            SpringAnimator.ForceReducedMotionOverride = false;

            var sb = new StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                sb.AppendLine($"[00:{i * 2:D2}.00]Synced lyric line {i + 1}");
            }

            var lyricsVm = new LyricsViewModel();
            lyricsVm.ApplyParsedResult(LyricsParser.ParseLrcContent(sb.ToString(), LyricsSourceKind.SidecarLrc));

            var view = new LyricsView
            {
                IsFullScreenMode = true,
                DataContext = lyricsVm
            };
            var host = new Window
            {
                Width = 540,
                Height = 620,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                lyricsVm.UpdatePlaybackPosition(TimeSpan.FromSeconds(12.2), isPlaying: true); // index 6 active
                for (int f = 0; f < 180 && SpringAnimator.HasActiveSprings; f++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                }

                var vp = view.SyncedViewportControl;
                Assert.Equal(6, vp.ActiveLogicalIndex);

                var (op0, sc0, _) = vp.GetVisualStateForLogicalIndexForTest(6);
                var (op1, sc1, _) = vp.GetVisualStateForLogicalIndexForTest(7);
                var (opM1, scM1, _) = vp.GetVisualStateForLogicalIndexForTest(5);
                var (op2, sc2, _) = vp.GetVisualStateForLogicalIndexForTest(8);
                var (op3, sc3, _) = vp.GetVisualStateForLogicalIndexForTest(9);

                Assert.InRange(op0, 0.99, 1.001);
                Assert.InRange(sc0, 0.995, 1.005);
                Assert.InRange(op1, SyncedLyricsViewport.OpacityAdjacent - 0.01, SyncedLyricsViewport.OpacityAdjacent + 0.01);
                Assert.InRange(sc1, SyncedLyricsViewport.ScaleAdjacent - 0.01, SyncedLyricsViewport.ScaleAdjacent + 0.01);
                Assert.InRange(opM1, SyncedLyricsViewport.OpacityAdjacent - 0.01, SyncedLyricsViewport.OpacityAdjacent + 0.01);
                Assert.InRange(scM1, SyncedLyricsViewport.ScaleAdjacent - 0.01, SyncedLyricsViewport.ScaleAdjacent + 0.01);
                Assert.InRange(op2, SyncedLyricsViewport.OpacityNear - 0.01, SyncedLyricsViewport.OpacityNear + 0.01);
                Assert.InRange(sc2, SyncedLyricsViewport.ScaleNear - 0.01, SyncedLyricsViewport.ScaleNear + 0.01);
                Assert.InRange(op3, SyncedLyricsViewport.OpacityFar - 0.01, SyncedLyricsViewport.OpacityFar + 0.01);
                Assert.InRange(sc3, SyncedLyricsViewport.ScaleFar - 0.01, SyncedLyricsViewport.ScaleFar + 0.01);

                // Verify constant FontSize / LineHeight / FontWeight across active and inactive rows
                var tbActive = vp.GetTextBlockForLogicalIndexForTest(6)!;
                var tbInactive = vp.GetTextBlockForLogicalIndexForTest(8)!;
                Assert.Equal(SyncedLyricsViewport.FullScreenFontSize, tbActive.FontSize);
                Assert.Equal(tbActive.FontSize, tbInactive.FontSize);
                Assert.Equal(tbActive.LineHeight, tbInactive.LineHeight);
                Assert.Equal(FontWeights.Bold, tbActive.FontWeight);
                Assert.Equal(tbActive.FontWeight, tbInactive.FontWeight);
            }
            finally
            {
                host.Close();
                SpringAnimator.UseExternalClockForTest = prevExt;
                SpringAnimator.ForceReducedMotionOverride = prevRm;
            }
        });
    }

    [Fact]
    public void ThreeDotInterlude_FillsProgressivelyForIntroAndMidTrackGaps_AndHidesWhenInactive()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool prevExt = SpringAnimator.UseExternalClockForTest;
            bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
            SpringAnimator.UseExternalClockForTest = true;
            SpringAnimator.ForceReducedMotionOverride = false;

            const string lrc = """
                [00:12.00]First vocal line after 12s intro
                [00:16.00]Second vocal line
                [00:28.00]Third vocal line after 12s break
                """;
            var parsed = LyricsParser.ParseLrcContent(lrc, LyricsSourceKind.SidecarLrc);
            Assert.Equal(4, parsed.Lines.Count); // Contract preserved: 3 vocal + 1 mid-track gap

            var lyricsVm = new LyricsViewModel();
            lyricsVm.ApplyParsedResult(parsed);

            var view = new LyricsView
            {
                IsFullScreenMode = true,
                DataContext = lyricsVm
            };
            var host = new Window
            {
                Width = 540,
                Height = 620,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                var vp = view.SyncedViewportControl;
                Assert.True(vp.HasIntroGapRow);
                Assert.Equal(5, vp.LogicalRowCount); // virtual intro gap + 4 parsed items

                // 1. Intro gap at 2s (16.7% progress -> Dot 1 half-filled, Dot 2 & 3 unfilled at 0.38)
                lyricsVm.UpdatePlaybackPosition(TimeSpan.FromSeconds(2.0), isPlaying: true);
                Assert.Equal(0, vp.ActiveLogicalIndex);
                var early = vp.LastActiveInterludeDotOpacitiesForTest;
                Assert.True(early.D1 > 0.65 && early.D1 < 0.75, $"Expected Dot1 ~0.69 at 16.7%, got {early.D1:F3}");
                Assert.InRange(early.D2, 0.37, 0.39);
                Assert.InRange(early.D3, 0.37, 0.39);

                // Advance smoothly on external clock by 4s -> now 6s (50% progress -> Dot 1 full, Dot 2 half, Dot 3 unfilled)
                for (int f = 0; f < 400; f++)
                {
                    SpringAnimator.StepExternalClockForTest(0.01);
                }
                var mid = vp.LastActiveInterludeDotOpacitiesForTest;
                Assert.InRange(mid.D1, 0.99, 1.001);
                Assert.True(mid.D2 > 0.65 && mid.D2 < 0.75, $"Expected Dot2 ~0.69 at 50%, got {mid.D2:F3}");
                Assert.InRange(mid.D3, 0.37, 0.39);

                // 2. Enter first vocal line at 12.2s -> intro interlude fades to 0.0
                lyricsVm.UpdatePlaybackPosition(TimeSpan.FromSeconds(12.2), isPlaying: true);
                for (int f = 0; f < 180 && SpringAnimator.HasActiveSprings; f++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                }
                Assert.Equal(1, vp.ActiveLogicalIndex);
                var (introOp, _, _) = vp.GetVisualStateForLogicalIndexForTest(0);
                Assert.InRange(introOp, 0.0, 0.01);

                // 3. Enter mid-track instrumental gap (parsed index 2 -> logical index 3)
                var midGap = lyricsVm.Lines[2];
                Assert.True(midGap.IsInstrumentalGap);
                Assert.True(midGap.Duration.TotalSeconds > 5.0);

                lyricsVm.UpdatePlaybackPosition(midGap.Timestamp + TimeSpan.FromSeconds(midGap.Duration.TotalSeconds * 0.85), isPlaying: true);
                for (int f = 0; f < 240; f++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                    if (f == 180) break;
                }
                Assert.Equal(3, vp.ActiveLogicalIndex);
                var (gapOp, _, _) = vp.GetVisualStateForLogicalIndexForTest(3);
                Assert.InRange(gapOp, 0.98, 1.001);
                var late = vp.LastActiveInterludeDotOpacitiesForTest;
                Assert.InRange(late.D1, 0.99, 1.001);
                Assert.InRange(late.D2, 0.99, 1.001);
                Assert.True(late.D3 > 0.70, $"Expected Dot3 > 0.70 near end of mid-track gap, got {late.D3:F3}");
            }
            finally
            {
                host.Close();
                SpringAnimator.UseExternalClockForTest = prevExt;
                SpringAnimator.ForceReducedMotionOverride = prevRm;
            }
        });
    }

    [Fact]
    public void KeyboardAccessibility_RowsAndFooterImportAreReachableAndSeekWorks()
    {
        WpfStaTestRunner.Run(() =>
        {
            double? seekedSec = null;
            var lyricsVm = new LyricsViewModel(sec => seekedSec = sec);
            const string lrc = """
                [au:Test Composer]
                [00:01.00]Line alpha
                [00:04.00]Line beta
                [00:12.00]Line gamma after break
                """;
            lyricsVm.ApplyParsedResult(LyricsParser.ParseLrcContent(lrc, LyricsSourceKind.SidecarLrc));

            var view = new LyricsView
            {
                IsFullScreenMode = false,
                DataContext = lyricsVm
            };
            var host = new Window
            {
                Width = 340,
                Height = 520,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                Assert.Equal("Aura.Lyrics.List", AutomationProperties.GetAutomationId(view));

                var vp = view.SyncedViewportControl;
                var btn0 = vp.GetButtonForLogicalIndexForTest(0);
                var btn1 = vp.GetButtonForLogicalIndexForTest(1);
                var btn3 = vp.GetButtonForLogicalIndexForTest(3); // index 2 is instrumental gap

                Assert.NotNull(btn0);
                Assert.NotNull(btn1);
                Assert.NotNull(btn3);
                Assert.True(btn0!.Focusable);
                Assert.True(btn0.IsTabStop);
                Assert.NotNull(btn0.FocusVisualStyle);

                // Focus row 0, press Down -> moves to row 1, press Down -> skips gap (row 2) and lands on row 3
                Assert.True(vp.FocusVocalLogicalIndex(0));
                Assert.Same(btn0, Keyboard.FocusedElement);

                btn0.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(btn0)!, 0, Key.Down)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                });
                Assert.Same(btn1, Keyboard.FocusedElement);

                btn1!.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(btn1)!, 0, Key.Down)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                });
                Assert.Same(btn3, Keyboard.FocusedElement);

                // Press Up -> skips gap backwards back to row 1
                btn3!.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(btn3)!, 0, Key.Up)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                });
                Assert.Same(btn1, Keyboard.FocusedElement);

                // Enter/Space click seeks to Line beta (4.0s)
                btn1.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(4.0, seekedSec);
                Assert.Equal(1, lyricsVm.ActiveLineIndex);

                // Footer Import button is keyboard reachable (IsTabStop=True) and reveals Opacity=1.0 on keyboard focus
                var footerImport = Assert.IsType<Button>(view.FindName("FooterImportButton"));
                Assert.Equal("Aura.Lyrics.FooterImport", AutomationProperties.GetAutomationId(footerImport));
                Assert.Equal(Visibility.Visible, footerImport.Visibility);
                Assert.True(footerImport.Focusable);
                Assert.True(footerImport.IsTabStop);
                Assert.Equal(0.0, footerImport.Opacity, precision: 2);

                Assert.True(footerImport.Focus());
                host.UpdateLayout();
                Assert.Equal(1.0, footerImport.Opacity, precision: 2);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void SteadyStateScrollLoop_AllocatesZeroBytesAndZeroLayoutPasses()
    {
        WpfStaTestRunner.Run(() =>
        {
            bool prevExt = SpringAnimator.UseExternalClockForTest;
            bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
            SpringAnimator.UseExternalClockForTest = true;
            SpringAnimator.ForceReducedMotionOverride = false;

            var sb = new StringBuilder();
            for (int i = 0; i < 120; i++)
            {
                int sec = i * 2;
                sb.AppendLine($"[{sec / 60:D2}:{sec % 60:D2}.00]Steady state zero allocation lyric line {i + 1}");
            }

            var lyricsVm = new LyricsViewModel();
            lyricsVm.ApplyParsedResult(LyricsParser.ParseLrcContent(sb.ToString(), LyricsSourceKind.SidecarLrc));

            var view = new LyricsView
            {
                IsFullScreenMode = true,
                DataContext = lyricsVm
            };
            var host = new Window
            {
                Width = 540,
                Height = 620,
                Content = view,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                var vp = view.SyncedViewportControl;
                lyricsVm.UpdatePlaybackPosition(TimeSpan.FromSeconds(4.1), isPlaying: true);
                vp.SyncToActiveLine(immediate: true);
                host.UpdateLayout();

                // Trigger smooth forward scroll within bound pool window
                lyricsVm.UpdatePlaybackPosition(TimeSpan.FromSeconds(10.1), isPlaying: true);

                // Warm up 2 frames
                SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                host.UpdateLayout();

                int layoutPasses = 0;
                void OnLayout(object? s, EventArgs e) => layoutPasses++;
                view.LayoutUpdated += OnLayout;

                double startScroll = vp.CurrentScrollOffset;
                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int f = 0; f < 120; f++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                }
                long allocAfter = GC.GetAllocatedBytesForCurrentThread();
                view.LayoutUpdated -= OnLayout;

                long totalBytes = allocAfter - allocBefore;
                double bytesPerFrame = totalBytes / 120.0;

                // Zero layout passes during steady-state scroll; only WPF DependencyProperty double-boxing (24 B per changed DP)
                Assert.Equal(0, layoutPasses);
                Assert.True(bytesPerFrame < 600.0, $"Expected minimal WPF DP-only boxing (<600 B/frame) during multi-row scroll+emphasis, got {bytesPerFrame:F1} B/frame ({totalBytes} B / 120f).");
                Assert.True(Math.Abs(vp.CurrentScrollOffset - startScroll) > 10.0, "Expected viewport scroll offset to advance smoothly.");
            }
            finally
            {
                host.Close();
                SpringAnimator.UseExternalClockForTest = prevExt;
                SpringAnimator.ForceReducedMotionOverride = prevRm;
            }
        });
    }

    [Fact]
    public void ReducedMotion_AndPlainUnsyncedFallback_BehaveCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_RmTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool prevExt = SpringAnimator.UseExternalClockForTest;
                bool? prevRm = SpringAnimator.ForceReducedMotionOverride;
                SpringAnimator.UseExternalClockForTest = true;
                SpringAnimator.ForceReducedMotionOverride = true;

                using var db = new DatabaseManager(Path.Combine(tempDir, "rm.db"));
                db.Initialize();
                using var vm = new MainViewModel(db: db);
                vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent("[00:01.00]One\n[00:03.00]Two\n[00:05.00]Three", LyricsSourceKind.SidecarLrc));

                var view = new LyricsView
                {
                    IsFullScreenMode = true,
                    DataContext = vm
                };
                var host = new Window
                {
                    Width = 540,
                    Height = 620,
                    Content = view,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Left = -20000,
                    Top = -20000
                };

                try
                {
                    host.Show();
                    host.UpdateLayout();

                    var vp = view.SyncedViewportControl;
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(5.1), isPlaying: false);

                    // In Reduced Motion, scroll snaps immediately with zero active springs
                    Assert.False(SpringAnimator.HasActiveSprings);
                    Assert.Equal(vp.TargetScrollOffset, vp.CurrentScrollOffset, precision: 2);

                    // Switch to plain unsynced fallback
                    vm.Lyrics.ApplyParsedResult(LyricsParser.ParseText("Plain line A\nPlain line B\nPlain line C"));
                    host.UpdateLayout();

                    var plainScroll = Assert.IsType<ScrollViewer>(view.FindName("PlainLyricsScroll"));
                    Assert.Equal(Visibility.Visible, plainScroll.Visibility);
                    Assert.Equal(Visibility.Collapsed, vp.Visibility);
                    Assert.False(vm.Lyrics.SeekToLineCommand.CanExecute(null));
                }
                finally
                {
                    host.Close();
                    SpringAnimator.UseExternalClockForTest = prevExt;
                    SpringAnimator.ForceReducedMotionOverride = prevRm;
                }
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [SkippableFact]
    public void CapturePhase2LyricsScreenshots_LatinAndJapanese()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("AURA_CAPTURE_LYRICS_SHOTS") == "1",
            "Opt-in Phase 2 lyrics screenshot capture (set AURA_CAPTURE_LYRICS_SHOTS=1).");

        string outDir = Environment.GetEnvironmentVariable("AURA_LYRICS_SHOTS_OUTDIR")
            ?? Path.Combine(Path.GetTempPath(), "AuraAudio_Phase2_Shots");
        Directory.CreateDirectory(outDir);

        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_ShotsDb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        const string latinLrc = """
            [au:Salmantyo Ashrizky Priadi]
            [00:02.00]Lewat sudah
            [00:05.00]Pukul dua
            [00:08.50]Makin banyak bicara kita
            [00:15.50]Kau semakin merah delima
            [00:20.00]Lewat sudah
            [00:23.00]Pukul dua
            [00:26.00]Merayakan tahun kesekian bersama
            [00:31.00]Dan kita tetap menunggu pagi yang hangat datang menyapa
            [00:36.00]Di ruang kecil penuh cerita dan tawa
            """;

        const string japaneseLrc = """
            [au:Ichiro Yamaguchi]
            [00:18.00]次 とその次 とその次 と線を引き続けた
            [00:25.00]次の目的地 を目的地 を描くんだ
            [00:32.00]宝島
            [00:36.00]このまま君を連れて行くと
            [00:41.00]丁寧に 丁寧に 丁寧に 描くよ
            [00:47.00]揺れたり 震えたりした線で
            [00:53.00]丁寧に 丁寧に 丁寧に 歌うよ
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
                var spec = new AudioStreamSpec(AudioCodec.ALAC, 44100, BitDepth.Bit16, 2, 912);
                var trackLatin = new Track(1, Path.Combine(tempDir, "latin.m4a"), "Lewat sudah pukul dua, makin banyak bicara kita", "Sal Priadi", "Sal Priadi", "MARKERS AND SUCH PENS FLASHDISKS", "Sal Priadi", 2024, 1, 1, TimeSpan.FromSeconds(236), spec, 28_000_000, DateTime.UtcNow, false, null);
                var trackJp = new Track(2, Path.Combine(tempDir, "jp.flac"), "Shin Takara Jima", "sakanaction", "sakanaction", "Shin Takara Jima - EP", "sakanaction", 2015, 1, 1, TimeSpan.FromSeconds(306), spec, 34_000_000, DateTime.UtcNow, false, null);
                db.InsertTracksBatch([trackLatin, trackJp]);

                using var vm = new MainViewModel(db: db);
                var win = new MainWindow(vm)
                {
                    Width = 1536,
                    Height = 860,
                    Left = 0,
                    Top = 0,
                    ShowInTaskbar = false
                };

                void SettleAll()
                {
                    win.UpdateLayout();
                    for (int f = 0; f < 220 && SpringAnimator.HasActiveSprings; f++)
                    {
                        SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                    }
                    win.UpdateLayout();
                }

                try
                {
                    win.Show();
                    SettleAll();

                    // 1. Full-Screen Split (Latin synced lyrics)
                    vm.CurrentTrack = trackLatin;
                    vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(latinLrc, LyricsSourceKind.SidecarLrc));
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(5.4), isPlaying: true);
                    vm.Presentation.IsFullScreenLyricsOpen = true;
                    vm.Presentation.IsFullScreenOpen = true;
                    SettleAll();
                    SavePng(win, Path.Combine(outDir, "01_fullscreen_latin.png"));

                    // 2. Full-Screen Split (Japanese synced lyrics during active 3-dot intro interlude @ 8s / 18s)
                    vm.CurrentTrack = trackJp;
                    vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(japaneseLrc, LyricsSourceKind.SidecarLrc));
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(8.0), isPlaying: true);
                    SettleAll();
                    SavePng(win, Path.Combine(outDir, "02_fullscreen_japanese_interlude.png"));

                    // 3. Full-Screen Split (Japanese active vocal line)
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(25.5), isPlaying: true);
                    SettleAll();
                    SavePng(win, Path.Combine(outDir, "03_fullscreen_japanese_active.png"));

                    // 4. Right Pane 360px (Latin)
                    vm.Presentation.IsFullScreenOpen = false;
                    vm.CurrentTrack = trackLatin;
                    vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(latinLrc, LyricsSourceKind.SidecarLrc));
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(8.8), isPlaying: true);
                    vm.Presentation.ActiveRightPane = RightPaneMode.Lyrics;
                    SettleAll();
                    SavePng(win, Path.Combine(outDir, "04_rightpane_latin.png"));

                    // 5. Right Pane 360px (Japanese)
                    vm.CurrentTrack = trackJp;
                    vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(japaneseLrc, LyricsSourceKind.SidecarLrc));
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(25.5), isPlaying: true);
                    SettleAll();
                    SavePng(win, Path.Combine(outDir, "05_rightpane_japanese.png"));

                    // 6. Mini-Player Expanded Lyrics Tray (Latin + Japanese)
                    var mini = new MiniPlayerWindow(vm)
                    {
                        ShowInTaskbar = false,
                        Left = 100,
                        Top = 100
                    };
                    try
                    {
                        mini.Show();
                        vm.Presentation.IsMiniPlayerAlbumView = false;
                        vm.Presentation.MiniPlayerTrayMode = RightPaneMode.Lyrics;

                        vm.CurrentTrack = trackLatin;
                        vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(latinLrc, LyricsSourceKind.SidecarLrc));
                        vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(5.4), isPlaying: true);
                        for (int f = 0; f < 220 && SpringAnimator.HasActiveSprings; f++)
                        {
                            SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                        }
                        mini.UpdateLayout();
                        SavePng(mini, Path.Combine(outDir, "06_miniplayer_tray_latin.png"));

                        vm.CurrentTrack = trackJp;
                        vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(japaneseLrc, LyricsSourceKind.SidecarLrc));
                        vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(25.5), isPlaying: true);
                        for (int f = 0; f < 220 && SpringAnimator.HasActiveSprings; f++)
                        {
                            SpringAnimator.StepExternalClockForTest(1.0 / 165.0);
                        }
                        mini.UpdateLayout();
                        SavePng(mini, Path.Combine(outDir, "07_miniplayer_tray_japanese.png"));
                    }
                    finally
                    {
                        mini.Close();
                    }
                }
                finally
                {
                    win.Close();
                    SpringAnimator.UseExternalClockForTest = prevExt;
                    SpringAnimator.ForceReducedMotionOverride = prevRm;
                }
            });
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
