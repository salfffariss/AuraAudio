using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.Tests;

/// <summary>
/// Deterministic probe-based motion continuity invariant tests (I1-I5) for every transition fixed
/// in the Stage 6 motion continuity pass. Runs reliably inside the normal STA test suite via
/// SpringAnimator's deterministic frame clock (zero DWM occluded-session flakiness).
/// </summary>
[Collection("WpfSta")]
public class MotionContinuityInvariantTests
{
    private static void RunWithRealisticLibrary(Action<MainViewModel, MainWindow> body, double width = 1536, double height = 860)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_MotionInv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "library.db");

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                bool? prevReduced = SpringAnimator.ForceReducedMotionOverride;
                bool prevExtClock = SpringAnimator.UseExternalClockForTest;
                SpringAnimator.ForceReducedMotionOverride = false;

                using var db = new DatabaseManager(dbPath);
                db.Initialize();
                var spec = new AudioStreamSpec(AudioCodec.FLAC, 48000, BitDepth.Bit24, 2, 2304);
                var tracks = new List<Track>(320);
                int id = 1;
                for (int a = 1; a <= 80; a++)
                {
                    for (int t = 1; t <= 4; t++)
                    {
                        tracks.Add(new Track(
                            id: id++,
                            filePath: Path.Combine(tempDir, $"Album{a:D2}_Track{t:D2}.flac"),
                            title: $"Track {t:D2} of Album {a:D2}",
                            artist: $"Artist {(a % 15) + 1:D2}",
                            sortArtist: $"Artist {(a % 15) + 1:D2}",
                            album: $"Album {a:D2}",
                            albumArtist: $"Artist {(a % 15) + 1:D2}",
                            year: 2020 + (a % 6),
                            trackNumber: t,
                            discNumber: 1,
                            duration: TimeSpan.FromMinutes(3.5),
                            streamSpec: spec,
                            fileSizeBytes: 42_000_000,
                            dateAdded: DateTime.UtcNow.AddDays(-a),
                            hasEmbeddedCover: false,
                            coverArtCacheKey: null));
                    }
                }
                db.InsertTracksBatch(tracks);

                using var vm = new MainViewModel(db: db);
                var win = new MainWindow(vm)
                {
                    Width = width,
                    Height = height,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false
                };

                try
                {
                    win.Show();
                    win.UpdateLayout();
                    MotionProbe.PumpDispatcher(120);

                    if (vm.CurrentTrack == null && vm.DisplayedTracks.Count > 0)
                        vm.CurrentTrack = vm.DisplayedTracks[0];

                    var sb = new StringBuilder(4096);
                    for (int i = 0; i < 120; i++)
                    {
                        int sec = i * 2;
                        sb.AppendLine($"[{sec / 60:D2}:{sec % 60:D2}.00]Line {i + 1:D3}: Synced lyrics continuity line");
                    }
                    vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(sb.ToString(), LyricsSourceKind.SidecarLrc));
                    vm.Lyrics.UpdatePlaybackPosition(TimeSpan.FromSeconds(10));

                    body(vm, win);
                }
                finally
                {
                    SpringAnimator.UseExternalClockForTest = prevExtClock;
                    SpringAnimator.ForceReducedMotionOverride = prevReduced;
                    win.Close();
                }
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void AssertAllInvariantsPass(MotionProbeResult result)
    {
        Assert.True(result.PassI1NoJump, $"[{result.ScenarioName}] I1 failed: {result.FailureReasons}");
        Assert.True(result.PassI2Adjacency, $"[{result.ScenarioName}] I2 failed: {result.FailureReasons}");
        Assert.True(result.PassI3Monotonic, $"[{result.ScenarioName}] I3 failed: {result.FailureReasons}");
        Assert.True(result.PassI4Completion, $"[{result.ScenarioName}] I4 failed: {result.FailureReasons}");
        Assert.True(result.PassI5FrameTime, $"[{result.ScenarioName}] I5 failed: {result.FailureReasons}");
    }

    [Fact]
    public void InlineShell_AlbumsAndSongs_PaneOpenCloseSwitchAndRail_PassI1ToI5()
    {
        RunWithRealisticLibrary((vm, win) =>
        {
            // 1. Wide 1536 AlbumsView: Right Pane Open + Auto-Compact Left Rail simultaneously
            vm.CurrentView = "Albums";
            win.UpdateLayout();

            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "Inline_Albums_PaneOpenAutoRail",
                () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                isInlineMode: true, expectedFinalRailWidth: 56, expectedFinalPaneWidth: 360,
                hz: 60.0, capturePopPixels: true));

            // 2. Queue <-> Lyrics cross-fade switch while pane stays open (zero geometry jump/gap)
            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "Inline_Albums_QueueLyricsSwitch",
                () => vm.Presentation.ToggleQueuePaneCommand.Execute(null),
                isInlineMode: true, expectedFinalRailWidth: 56, expectedFinalPaneWidth: 360,
                hz: 60.0, capturePopPixels: false));

            // 3. Wide 1536 AlbumsView: Right Pane Close + Auto-Expand Left Rail simultaneously
            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "Inline_Albums_PaneCloseAutoRail",
                () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                hz: 60.0, capturePopPixels: true));

            // 4. Standalone Left Rail Collapse & Expand (248 <-> 56)
            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "Inline_Albums_LeftRailCollapse",
                () => vm.Presentation.ToggleSidebarCommand.Execute(null),
                isInlineMode: true, expectedFinalRailWidth: 56, expectedFinalPaneWidth: 0,
                hz: 60.0, capturePopPixels: false));

            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "Inline_Albums_LeftRailExpand",
                () => vm.Presentation.ToggleSidebarCommand.Execute(null),
                isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                hz: 60.0, capturePopPixels: false));

            // 5. Heavy Songs DataGrid (320 tracks): Pure Inline Right Pane Open & Close (rail fixed 248)
            vm.CurrentView = "Songs";
            win.UpdateLayout();

            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "Inline_Songs_PaneOpen",
                () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                hz: 60.0, capturePopPixels: false));

            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "Inline_Songs_PaneClose",
                () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                hz: 60.0, capturePopPixels: false));
        });
    }

    [Fact]
    public void OverlayShell_1280Dips_RightPaneOpenClose_PassI1ToI5()
    {
        RunWithRealisticLibrary((vm, win) =>
        {
            vm.CurrentView = "Albums";
            win.UpdateLayout();

            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "Overlay1280_PaneOpen",
                () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                isInlineMode: false, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                hz: 60.0, capturePopPixels: false));

            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "Overlay1280_PaneClose",
                () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                isInlineMode: false, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                hz: 60.0, capturePopPixels: false));
        }, width: 1280, height: 800);
    }

    [Fact]
    public void ReducedMotion_InlineAndOverlay_ZeroGapAndZeroLateReflow()
    {
        RunWithRealisticLibrary((vm, win) =>
        {
            SpringAnimator.ForceReducedMotionOverride = true;
            vm.CurrentView = "Albums";
            // Warm up LyricsView template once so steady-state Reduced Motion I5 isn't confounded by 1-time XAML inflation
            vm.Presentation.ToggleLyricsPaneCommand.Execute(null);
            win.UpdateLayout();
            MotionProbe.PumpDispatcher(60);
            vm.Presentation.CloseRightPaneCommand.Execute(null);
            win.UpdateLayout();
            MotionProbe.PumpDispatcher(60);

            // Inline Open & Close under Reduced Motion must keep I2 (0 DIP gap) and exact final geometry
            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "ReducedMotion_Inline_PaneOpen",
                () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                isInlineMode: true, expectedFinalRailWidth: 56, expectedFinalPaneWidth: 360,
                hz: 60.0, capturePopPixels: false));

            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "ReducedMotion_Inline_PaneClose",
                () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                isInlineMode: true, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                hz: 60.0, capturePopPixels: false));

            // Overlay Open & Close under Reduced Motion
            win.Width = 1280;
            win.Height = 800;
            win.UpdateLayout();
            MotionProbe.PumpDispatcher(60);

            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "ReducedMotion_Overlay_PaneOpen",
                () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                isInlineMode: false, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 360,
                hz: 60.0, capturePopPixels: false));

            AssertAllInvariantsPass(MotionProbe.RecordShellTransitionDeterministic(
                win, "ReducedMotion_Overlay_PaneClose",
                () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                isInlineMode: false, expectedFinalRailWidth: 248, expectedFinalPaneWidth: 0,
                hz: 60.0, capturePopPixels: false));
        });
    }

    [Fact]
    public void FullScreen_EnterExitAndSplitLyricsMorph_GlideContinuouslyWithoutSnap()
    {
        RunWithRealisticLibrary((vm, win) =>
        {
            var fsView = Assert.IsType<FullScreenPlayerView>(win.FindName("FullScreenStageOverlay"));
            var fsStageRoot = Assert.IsAssignableFrom<FrameworkElement>(fsView.FindName("StageRoot"));
            var fiveCol = Assert.IsType<Grid>(fsView.FindName("FiveColumnStage"));
            var fsArtPanel = Assert.IsAssignableFrom<FrameworkElement>(fiveCol.Children[0]);
            var fsLyricsHost = Assert.IsAssignableFrom<FrameworkElement>(fsView.FindName("LyricsStageHost"));

            vm.Presentation.IsFullScreenLyricsOpen = false;

            SpringAnimator.UseExternalClockForTest = true;

            // 1. Full-Screen Enter
            vm.Presentation.ToggleFullScreenCommand.Execute(null);
            win.UpdateLayout();
            var enterYs = new List<double> { MotionProbe.CaptureBounds(fsStageRoot, win).Top };
            for (int f = 1; f <= 60 && SpringAnimator.HasActiveSprings; f++)
            {
                SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                win.UpdateLayout();
                enterYs.Add(MotionProbe.CaptureBounds(fsStageRoot, win).Top);
            }
            Assert.True(enterYs.Count >= 10, $"Expected smooth multi-frame FullScreen Enter, got {enterYs.Count} frames.");
            Assert.Null(fsStageRoot.CacheMode);

            // 2. Centered -> Split Lyrics Morph (must glide artwork smoothly, NOT 100% Frame 1 jump!)
            double startArtL = MotionProbe.CaptureBounds(fsArtPanel, win).Left;
            vm.Presentation.ToggleFullScreenLyricsCommand.Execute(null);
            win.UpdateLayout();
            var splitLs = new List<double> { startArtL };
            var splitOps = new List<double> { fsLyricsHost.Opacity };
            for (int f = 1; f <= 60 && SpringAnimator.HasActiveSprings; f++)
            {
                SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                win.UpdateLayout();
                splitLs.Add(MotionProbe.CaptureBounds(fsArtPanel, win).Left);
                splitOps.Add(fsLyricsHost.Opacity);
            }
            double openTravel = Math.Abs(splitLs[^1] - splitLs[0]);
            Assert.True(openTravel > 100.0, $"Expected significant split morph travel, got {openTravel:F1}");
            double openFirstPct = Math.Abs(splitLs[1] - splitLs[0]) / openTravel * 100.0;
            Assert.True(openFirstPct <= 10.0, $"FullScreen SplitLyricsOpen 1st-frame jump {openFirstPct:F1}% > 10%");
            Assert.Equal(Visibility.Visible, fsLyricsHost.Visibility);
            Assert.True(splitOps[^1] >= 0.99);

            // 3. Split Lyrics -> Centered Morph (must glide back smoothly and collapse LyricsStageHost at end)
            double closeStartL = MotionProbe.CaptureBounds(fsArtPanel, win).Left;
            vm.Presentation.ToggleFullScreenLyricsCommand.Execute(null);
            win.UpdateLayout();
            var closeLs = new List<double> { closeStartL };
            for (int f = 1; f <= 60 && SpringAnimator.HasActiveSprings; f++)
            {
                SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                win.UpdateLayout();
                closeLs.Add(MotionProbe.CaptureBounds(fsArtPanel, win).Left);
            }
            double closeTravel = Math.Abs(closeLs[^1] - closeLs[0]);
            Assert.True(closeTravel > 100.0);
            double closeFirstPct = Math.Abs(closeLs[1] - closeLs[0]) / closeTravel * 100.0;
            Assert.True(closeFirstPct <= 10.0, $"FullScreen SplitLyricsClose 1st-frame jump {closeFirstPct:F1}% > 10%");
            Assert.Equal(Visibility.Collapsed, fsLyricsHost.Visibility);

            // 4. Full-Screen Exit
            vm.Presentation.ExitFullScreenCommand.Execute(null);
            win.UpdateLayout();
            int exitFrames = 0;
            for (int f = 1; f <= 60 && SpringAnimator.HasActiveSprings; f++)
            {
                SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                win.UpdateLayout();
                exitFrames++;
            }
            Assert.True(exitFrames >= 8, $"Expected smooth FullScreen Exit, got {exitFrames} frames.");
            Assert.Equal(Visibility.Collapsed, fsView.Visibility);
            Assert.Null(fsStageRoot.CacheMode);
        });
    }

    [Fact]
    public void ModalSheets_OpenAndClose_AnimateSymmetricallyWithoutSnap()
    {
        RunWithRealisticLibrary((vm, win) =>
        {
            SpringAnimator.UseExternalClockForTest = true;

            void VerifySheetRoundTrip(string label, FrameworkElement overlay, Action open, Action close)
            {
                var card = Assert.IsAssignableFrom<FrameworkElement>(overlay.FindName("SheetCard"));

                open();
                win.UpdateLayout();
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                int openMoving = 0;
                double prevOp = card.Opacity;
                for (int f = 1; f <= 50 && SpringAnimator.HasActiveSprings; f++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                    win.UpdateLayout();
                    if (Math.Abs(card.Opacity - prevOp) > 0.005) openMoving++;
                    prevOp = card.Opacity;
                }
                Assert.True(openMoving >= 6, $"[{label}] Expected smooth open animation, got {openMoving} moving frames.");
                Assert.Null(card.CacheMode);

                close();
                win.UpdateLayout();
                // Must NOT snap Collapsed instantaneously on Frame 0!
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                int closeMoving = 0;
                prevOp = card.Opacity;
                for (int f = 1; f <= 50 && SpringAnimator.HasActiveSprings; f++)
                {
                    SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                    win.UpdateLayout();
                    if (Math.Abs(card.Opacity - prevOp) > 0.005) closeMoving++;
                    prevOp = card.Opacity;
                }
                Assert.True(closeMoving >= 6, $"[{label}] Expected smooth close animation, got {closeMoving} moving frames.");
                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.Null(card.CacheMode);
            }

            VerifySheetRoundTrip(
                "InfoInspectorSheet",
                Assert.IsType<InfoInspectorSheet>(win.FindName("InfoInspectorSheetOverlay")),
                () => vm.OpenInfoForTrackCommand.Execute(null),
                () => vm.Presentation.CloseInfoSheetCommand.Execute(null));

            VerifySheetRoundTrip(
                "AddToPlaylistSheet",
                Assert.IsType<AddToPlaylistSheet>(win.FindName("AddToPlaylistSheetOverlay")),
                () => vm.IsAddToPlaylistSheetOpen = true,
                () => vm.CloseAddToPlaylistSheetCommand.Execute(null));

            VerifySheetRoundTrip(
                "RenamePlaylistSheet",
                Assert.IsType<RenamePlaylistSheet>(win.FindName("RenamePlaylistSheetOverlay")),
                () => vm.CreatePlaylistCommand.Execute(string.Empty),
                () => vm.CancelRenameSheetCommand.Execute(null));
        });
    }

    [Fact]
    public void MiniPlayer_CompactAlbumAndTrayTransitions_GlideContinuously()
    {
        RunWithRealisticLibrary((vm, _) =>
        {
            var mini = new MiniPlayerWindow(vm)
            {
                ShowInTaskbar = false,
                Left = -20000,
                Top = 100
            };
            try
            {
                mini.Show();
                mini.UpdateLayout();
                MotionProbe.PumpDispatcher(60);

                SpringAnimator.UseExternalClockForTest = true;

                void VerifyHeightGlide(string label, double expectedEndH, Action trigger)
                {
                    double startH = mini.Height;
                    trigger();
                    mini.UpdateLayout();
                    var hs = new List<double> { startH };
                    for (int f = 1; f <= 60 && SpringAnimator.HasActiveSprings; f++)
                    {
                        SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                        mini.UpdateLayout();
                        hs.Add(mini.Height);
                    }
                    double travel = Math.Abs(hs[^1] - hs[0]);
                    Assert.True(travel > 100.0, $"[{label}] Expected travel > 100, got {travel:F1}");
                    double firstPct = Math.Abs(hs[1] - hs[0]) / travel * 100.0;
                    Assert.True(firstPct <= 10.0, $"[{label}] 1st-frame jump {firstPct:F1}% > 10%");
                    Assert.True(hs.Count >= 12, $"[{label}] Expected >= 12 frames, got {hs.Count}");
                    Assert.True(Math.Abs(mini.Height - expectedEndH) <= 1.0, $"[{label}] Expected final H={expectedEndH}, got {mini.Height}");

                    // Monotonic check (damping=1.0)
                    double dir = Math.Sign(hs[^1] - hs[0]);
                    for (int i = 1; i < hs.Count; i++)
                    {
                        Assert.True((hs[i] - hs[i - 1]) * dir >= -0.5, $"[{label}] Reversal detected at frame {i}");
                    }
                }

                VerifyHeightGlide("CompactToAlbumView", 350.0, () => vm.Presentation.IsMiniPlayerAlbumView = true);
                VerifyHeightGlide("AlbumToCompact", 160.0, () => vm.Presentation.IsMiniPlayerAlbumView = false);
                VerifyHeightGlide("TrayExpand", 420.0, () => vm.Presentation.MiniPlayerTrayMode = RightPaneMode.Queue);
                VerifyHeightGlide("TrayCollapse", 160.0, () => vm.Presentation.MiniPlayerTrayMode = RightPaneMode.None);
            }
            finally
            {
                mini.Close();
            }
        });
    }

    [Fact]
    public void ShelfCarousel_PagingAndArrowFadeOut_AnimateContinuously()
    {
        RunWithRealisticLibrary((vm, win) =>
        {
            vm.CurrentView = "Home";
            win.UpdateLayout();
            MotionProbe.PumpDispatcher(100);

            var shelf = FindVisualChild<ShelfCarousel>(win);
            Assert.NotNull(shelf);
            var sv = Assert.IsType<ScrollViewer>(shelf!.FindName("ShelfScroll"));
            var rightBtn = Assert.IsType<Button>(shelf.FindName("RightArrowBtn"));
            Assert.True(sv.ScrollableWidth > 100.0, $"Expected scrollable shelf, got ScrollableWidth={sv.ScrollableWidth}");

            SpringAnimator.UseExternalClockForTest = true;

            // 1. Paging right via ScrollRight_Click must glide smoothly (not instant snap!)
            double startOff = sv.HorizontalOffset;
            rightBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            win.UpdateLayout();
            var offs = new List<double> { startOff };
            for (int f = 1; f <= 60 && SpringAnimator.HasActiveSprings; f++)
            {
                SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                win.UpdateLayout();
                offs.Add(sv.HorizontalOffset);
            }
            double travel = Math.Abs(offs[^1] - offs[0]);
            Assert.True(travel > 100.0, $"Expected shelf scroll travel > 100, got {travel:F1}");
            double firstPct = Math.Abs(offs[1] - offs[0]) / travel * 100.0;
            Assert.True(firstPct <= 10.0, $"ShelfCarousel paging 1st-frame jump {firstPct:F1}% > 10%");
            Assert.True(offs.Count >= 10, $"Expected >= 10 smooth scroll frames, got {offs.Count}");

            // 2. Arrow fade-out must NOT collapse synchronously on Frame 0
            rightBtn.Visibility = Visibility.Visible;
            rightBtn.Opacity = 1.0;
            var toggleMethod = typeof(ShelfCarousel).GetMethod("ToggleArrow",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(toggleMethod);
            toggleMethod!.Invoke(null, new object[] { rightBtn, false });
            Assert.Equal(Visibility.Visible, rightBtn.Visibility);

            int fadeSteps = 0;
            for (int f = 1; f <= 40 && SpringAnimator.HasActiveSprings; f++)
            {
                SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                fadeSteps++;
            }
            Assert.True(fadeSteps >= 5, $"Expected smooth arrow fade-out, got {fadeSteps} steps.");
            Assert.Equal(Visibility.Collapsed, rightBtn.Visibility);
        });
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
