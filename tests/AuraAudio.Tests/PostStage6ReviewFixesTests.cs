using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Metadata;
using AuraAudio.UI.ViewModels;
using AuraAudio.UI.Views;
using AuraAudio.UI.Windows;
using Microsoft.Data.Sqlite;
using Xunit;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class PostStage6ReviewFixesTests
{
    private static Track CreateTrack(int id, string title, string artist, string filePath)
    {
        var spec = new AudioStreamSpec(AudioCodec.FLAC, 48000, BitDepth.Bit24, 2, 2304);
        return new Track(
            id: id,
            filePath: filePath,
            title: title,
            artist: artist,
            sortArtist: artist,
            album: "Test Album",
            albumArtist: artist,
            year: 2024,
            trackNumber: id,
            discNumber: 1,
            duration: TimeSpan.FromSeconds(226),
            streamSpec: spec,
            fileSizeBytes: 50_000_000,
            dateAdded: DateTime.UtcNow,
            hasEmbeddedCover: false,
            coverArtCacheKey: null);
    }

    private static T? FindByAutomationId<T>(DependencyObject parent, string automationId) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && AutomationProperties.GetAutomationId(child) == automationId)
            {
                return match;
            }
            var nested = FindByAutomationId<T>(child, automationId);
            if (nested != null) return nested;
        }
        return null;
    }

    private static void RunWithIsolatedViewModel(Action<MainViewModel, string> testAction)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_PostS6_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "isolated_test.db");

        try
        {
            WpfStaTestRunner.Run(() =>
            {
                using var db = new DatabaseManager(dbPath);
                db.Initialize();
                using var vm = new MainViewModel(db: db);
                testAction(vm, tempDir);
            });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void LyricsParser_ImportSidecarLyrics_ImportsSyncedLrcAndPlainTextWithoutModifyingAudio()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_ImportLrc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string audioPath = Path.Combine(tempDir, "01. I I I.flac");
            byte[] originalAudioBytes = new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x01, 0x02, 0x03 };
            File.WriteAllBytes(audioPath, originalAudioBytes);

            // 1. Import synced .lrc file
            string sourceLrc = Path.Combine(tempDir, "uploaded_synced.lrc");
            File.WriteAllText(
                sourceLrc,
                "[au:Giga & TeddyLoid]\n[00:01.20]First synced line\n[00:05.80]Second synced line\n",
                new UTF8Encoding(false));

            var syncedResult = LyricsParser.ImportSidecarLyrics(audioPath, sourceLrc);
            string expectedSidecar = Path.Combine(tempDir, "01. I I I.lrc");

            Assert.True(File.Exists(expectedSidecar));
            Assert.Equal(originalAudioBytes, File.ReadAllBytes(audioPath));
            Assert.True(syncedResult.IsSynced);
            Assert.Equal(LyricsSourceKind.SidecarLrc, syncedResult.Source);
            Assert.Equal(2, syncedResult.Lines.Count);
            Assert.Equal("First synced line", syncedResult.Lines[0].Text);
            Assert.Equal("Giga & TeddyLoid", syncedResult.Writers);

            // 2. Replace with plain-text .txt lyrics file
            string sourceTxt = Path.Combine(tempDir, "uploaded_plain.txt");
            File.WriteAllText(sourceTxt, "Plain verse line 1\nPlain verse line 2", new UTF8Encoding(false));

            var plainResult = LyricsParser.ImportSidecarLyrics(audioPath, sourceTxt);

            Assert.True(File.Exists(expectedSidecar));
            Assert.Equal(originalAudioBytes, File.ReadAllBytes(audioPath));
            Assert.False(plainResult.IsSynced);
            Assert.Equal(LyricsSourceKind.PlainText, plainResult.Source);
            Assert.Contains("Plain verse line 1", plainResult.PlainText);
            Assert.Contains("Plain verse line 2", plainResult.PlainText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MainViewModel_ImportLyricsFromPathAsync_UpdatesLiveLyricsAndInfoInspectorImmediately()
    {
        RunWithIsolatedViewModel((vm, tempDir) =>
        {
            string audioPath = Path.Combine(tempDir, "HELP.flac");
            File.WriteAllBytes(audioPath, new byte[] { 0x66, 0x4C, 0x61, 0x43 });

            string sourceLrc = Path.Combine(tempDir, "help_lyrics.lrc");
            File.WriteAllText(
                sourceLrc,
                "[00:02.00]Help me out tonight\n[00:06.50]Singing in monochrome\n",
                new UTF8Encoding(false));

            var track = CreateTrack(901, "HELP!!", "Kobo Kanaeru", audioPath);

            vm.CurrentTrack = track;
            vm.OpenInfoForTrackCommand.Execute(track);
            Assert.False(vm.Lyrics.HasLyrics);

            var importTask = vm.ImportLyricsFromPathAsync(track, sourceLrc);
            var frame = new DispatcherFrame();
            importTask.ContinueWith(_ => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            var imported = importTask.GetAwaiter().GetResult();

            Assert.True(imported.HasLyrics);
            Assert.True(vm.Lyrics.HasLyrics);
            Assert.True(vm.Lyrics.IsTimeBasedLoaded);
            Assert.Equal(2, vm.Lyrics.Lines.Count);
            Assert.Equal("Help me out tonight", vm.Lyrics.Lines[0].Text);
            Assert.True(vm.InfoTargetHasLyrics);
            Assert.Contains("Help me out tonight", vm.InfoTargetLyricsText);
        });
    }

    [Fact]
    public void OpenInfoForTrackCommand_GlobalCallPrefersCurrentTrackOverStaleSelectedTrack()
    {
        RunWithIsolatedViewModel((vm, _) =>
        {
            var staleSelected = CreateTrack(101, "1!2!3!4! Yoroshiku!", "JKT48", @"C:\Music\JKT48.flac");
            var activePlaying = CreateTrack(202, "I I I", "Kobo Kanaeru", @"C:\Music\III.flac");

            vm.SelectedTrack = staleSelected;
            vm.CurrentTrack = activePlaying;

            // Global invocation (Ctrl+I, Dock (i), Full-Screen (i)) must open active CurrentTrack
            vm.OpenInfoForTrackCommand.Execute(null);
            Assert.True(vm.Presentation.IsInfoSheetOpen);
            Assert.Same(activePlaying, vm.InfoTargetTrack);
            Assert.True(vm.IsInfoTargetCurrentTrack);

            // Explicit per-track context menu invocation must still open that specific track
            vm.OpenInfoForTrackCommand.Execute(staleSelected);
            Assert.Same(staleSelected, vm.InfoTargetTrack);
            Assert.False(vm.IsInfoTargetCurrentTrack);
        });
    }

    [Fact]
    public void DatabaseManager_ClearPlayHistory_EmptiesRecentlyPlayedTracks()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_HistoryReset_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "test_history.db");

        try
        {
            using (var db = new DatabaseManager(dbPath))
            {
                db.Initialize();
                var track = CreateTrack(1, "I I I", "Kobo Kanaeru", Path.Combine(tempDir, "III.flac"));
                db.InsertTracksBatch(new[] { track });
                var inserted = db.GetTracksByArtist("Kobo Kanaeru");
                Assert.Single(inserted);

                db.RecordPlayHistory(inserted[0].Id);
                Assert.Single(db.GetRecentPlayHistory(20));

                db.ClearPlayHistory();
                Assert.Empty(db.GetRecentPlayHistory(20));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void InfoInspectorSheet_TabItemContentDoesNotInheritHandCursor()
    {
        RunWithIsolatedViewModel((vm, _) =>
        {
            var sheet = new InfoInspectorSheet { DataContext = vm };
            var host = new Window
            {
                Width = 900,
                Height = 700,
                Content = sheet,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                var detailsTab = FindByAutomationId<TabItem>(sheet, "Aura.Info.Tab.Details");
                Assert.NotNull(detailsTab);

                // TabItem itself must NOT set Cursor=Hand (otherwise every child ScrollViewer/TextBlock inherits Hand)
                Assert.NotEqual(Cursors.Hand, detailsTab!.Cursor);

                // The template's header Grid MUST expose Hand cursor on the clickable tab header only
                var headerGrid = Assert.IsType<Grid>(VisualTreeHelper.GetChild(detailsTab, 0));
                Assert.Equal(Cursors.Hand, headerGrid.Cursor);

                // Tab content body must keep default arrow cursor
                var contentScroll = Assert.IsType<ScrollViewer>(detailsTab.Content);
                Assert.NotEqual(Cursors.Hand, contentScroll.Cursor);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void FullScreenPlayerView_HidesLyricsPanelWhenTrackHasNoLyricsAndShowsWhenLyricsLoaded()
    {
        RunWithIsolatedViewModel((vm, _) =>
        {
            vm.Presentation.IsFullScreenLyricsOpen = true;

            var fullScreenView = new FullScreenPlayerView { DataContext = vm };
            var host = new Window
            {
                Width = 1280,
                Height = 800,
                Content = fullScreenView,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                var lyricsStageHost = Assert.IsType<Border>(fullScreenView.FindName("LyricsStageHost"));

                // Track has NO lyrics -> Lyrics panel must stay hidden even though IsFullScreenLyricsOpen is true
                Assert.False(vm.Lyrics.HasLyrics);
                Assert.Equal(Visibility.Collapsed, lyricsStageHost.Visibility);

                // Load synced lyrics -> Lyrics panel must appear automatically
                vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent("[00:01.00]Hello Full Screen"));
                host.UpdateLayout();

                Assert.True(vm.Lyrics.HasLyrics);
                Assert.Equal(Visibility.Visible, lyricsStageHost.Visibility);

                // Clear back to no lyrics -> Lyrics panel glides closed and collapses cleanly at settle
                bool prevExtClock = AuraAudio.UI.Helpers.SpringAnimator.UseExternalClockForTest;
                AuraAudio.UI.Helpers.SpringAnimator.UseExternalClockForTest = true;
                try
                {
                    vm.Lyrics.ApplyParsedResult(ParsedLyricsResult.Empty);
                    host.UpdateLayout();
                    for (int f = 0; f < 60 && AuraAudio.UI.Helpers.SpringAnimator.HasActiveSprings; f++)
                    {
                        AuraAudio.UI.Helpers.SpringAnimator.StepExternalClockForTest(1.0 / 60.0);
                        host.UpdateLayout();
                    }
                }
                finally
                {
                    AuraAudio.UI.Helpers.SpringAnimator.UseExternalClockForTest = prevExtClock;
                }

                Assert.False(vm.Lyrics.HasLyrics);
                Assert.Equal(Visibility.Collapsed, lyricsStageHost.Visibility);
            }
            finally
            {
                host.Close();
            }
        });
    }

    [Fact]
    public void MainWindow_ModalSheetsRenderAboveFullScreenAndSidebarPlaylistsScaleCleanly()
    {
        RunWithIsolatedViewModel((vm, _) =>
        {
            var win = new MainWindow(vm)
            {
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000
            };

            try
            {
                win.Show();
                win.UpdateLayout();

                // 1. Z-order: InfoInspectorSheet must render ABOVE FullScreenStageOverlay so (i) works in Full-Screen
                var fullScreenOverlay = Assert.IsType<FullScreenPlayerView>(win.FindName("FullScreenStageOverlay"));
                var infoOverlay = FindByAutomationId<InfoInspectorSheet>(win, "Aura.Info.Sheet");
                Assert.NotNull(infoOverlay);
                Assert.True(
                    Panel.GetZIndex(infoOverlay!) > Panel.GetZIndex(fullScreenOverlay),
                    $"Expected InfoInspectorSheet ZIndex ({Panel.GetZIndex(infoOverlay!)}) > FullScreenStageOverlay ZIndex ({Panel.GetZIndex(fullScreenOverlay)}).");

                // 2. Clicking background anchors focus on MainWindow root (not null) so shortcuts remain live
                win.FocusWindowRoot();
                Assert.Same(win, FocusManager.GetFocusedElement(win));

                // Raising a global shortcut directly on the MainWindow root (as WPF does after background click)
                // must route through OnWindowPreviewKeyDown cleanly
                var src = PresentationSource.FromVisual(win);
                Assert.NotNull(src);
                bool beforeMuted = vm.IsMuted;
                var muteKey = new KeyEventArgs(Keyboard.PrimaryDevice, src!, 0, Key.M)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    Source = win
                };
                win.RaiseEvent(muteKey);
                Assert.True(muteKey.Handled);
                Assert.NotEqual(beforeMuted, vm.IsMuted);
                vm.ToggleMuteCommand.Execute(null); // restore initial state

                // 3. Verify Lyrics import buttons exist in BOTH InfoInspectorSheet (Tab 4) and LyricsView
                vm.Presentation.IsInfoSheetOpen = true;
                var infoTabs = Assert.IsType<TabControl>(infoOverlay!.FindName("InfoPivotTabs"));
                infoTabs.SelectedIndex = 3;
                win.UpdateLayout();
                infoOverlay.UpdateLayout();
                var infoImportBtn = FindByAutomationId<Button>(infoOverlay, "Aura.Info.Lyrics.Import");
                Assert.NotNull(infoImportBtn);
                Assert.Same(vm.ImportLyricsFileCommand, infoImportBtn!.Command);
                vm.Presentation.IsInfoSheetOpen = false;

                var lyricsView = new LyricsView { DataContext = vm };
                var lyricsHost = new Window
                {
                    Width = 400,
                    Height = 600,
                    Content = lyricsView,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Left = -20000,
                    Top = -20000
                };
                try
                {
                    lyricsHost.Show();
                    lyricsHost.UpdateLayout();
                    var paneImportBtn = FindByAutomationId<Button>(lyricsView, "Aura.Lyrics.Import");
                    Assert.NotNull(paneImportBtn);
                    Assert.Same(vm.ImportLyricsFileCommand, paneImportBtn!.Command);
                }
                finally
                {
                    lyricsHost.Close();
                }

                // 4. Left Rail Playlists scalability:
                //    - Hidden in compact 56px mode
                //    - Virtualized, foldable, and independently scrollable in expanded 248px mode even with 100 playlists
                var sidebarPlaylistsList = Assert.IsType<ListBox>(win.FindName("SidebarPlaylistsList"));
                var sidebarPlaylistsSection = Assert.IsType<Grid>(sidebarPlaylistsList.Parent);
                var foldToggle = Assert.IsType<ToggleButton>(win.FindName("PlaylistsFoldToggle"));

                Assert.True(VirtualizingPanel.GetIsVirtualizing(sidebarPlaylistsList));

                if (vm.Presentation.IsSidebarExpanded)
                {
                    vm.Presentation.ToggleSidebarCommand.Execute(null);
                }
                win.UpdateLayout();
                Assert.False(vm.Presentation.IsSidebarExpanded);
                Assert.Equal(Visibility.Collapsed, sidebarPlaylistsSection.Visibility);

                vm.Presentation.ToggleSidebarCommand.Execute(null);
                win.UpdateLayout();
                Assert.True(vm.Presentation.IsSidebarExpanded);
                Assert.Equal(Visibility.Visible, sidebarPlaylistsSection.Visibility);

                // Folding playlists hides only the list cleanly while keeping the header toggle visible
                foldToggle.IsChecked = false;
                win.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, sidebarPlaylistsList.Visibility);
                foldToggle.IsChecked = true;
                win.UpdateLayout();
                Assert.Equal(Visibility.Visible, sidebarPlaylistsList.Visibility);
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public void QueuePaneView_ContainsHistoryResetButton()
    {
        RunWithIsolatedViewModel((vm, _) =>
        {
            var view = new QueuePaneView { DataContext = vm };
            var host = new Window
            {
                Width = 400,
                Height = 680,
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

                var resetBtn = FindByAutomationId<Button>(view, "Aura.Queue.ClearHistory");
                Assert.NotNull(resetBtn);
                Assert.Same(vm.ClearPlayHistoryCommand, resetBtn!.Command);
            }
            finally
            {
                host.Close();
            }
        });
    }

    private sealed class StubGridVm
    {
        public System.Collections.Generic.List<Album> DisplayedAlbums { get; set; } = new();
        public string AlbumFilterQuery { get; set; } = string.Empty;
        public string AlbumSortMode { get; set; } = "Title";
        public System.Collections.Generic.List<Playlist> SavedPlaylists { get; set; } = new();
        public System.Collections.Generic.List<PlaylistOverviewItem> SavedPlaylistCards { get; set; } = new();
    }

    [Fact]
    public void AlbumsAndPlaylistsViews_UniformGridFillsRowEdgeToEdgeWithZeroRightVoid()
    {
        WpfStaTestRunner.Run(() =>
        {
            var albums = Enumerable.Range(1, 10)
                .Select(i => new Album(i, $"Album {i:D2}", "Test Artist", 2025, 10, TimeSpan.FromMinutes(35), true, null))
                .ToList();
            var playlists = Enumerable.Range(1, 10)
                .Select(i => new Playlist(i, $"Playlist {i:D2}", 5, DateTime.UtcNow))
                .ToList();
            var playlistCards = playlists.Select(p => new PlaylistOverviewItem(p, null)).ToList();

            var stubVm = new StubGridVm
            {
                DisplayedAlbums = albums,
                SavedPlaylists = playlists,
                SavedPlaylistCards = playlistCards
            };

            void VerifyView(UserControl view, string itemsControlName)
            {
                view.DataContext = stubVm;
                // Exact 1920x1080 @ 125% DPI viewport minus 248px expanded left rail, with UseLayoutRounding=True
                var host = new Window
                {
                    Width = 1288,
                    Height = 820,
                    UseLayoutRounding = true,
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
                    host.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                    host.UpdateLayout();

                    var probe = Assert.IsType<Border>(view.FindName("GridWidthProbe"));
                    var itemsCtrl = Assert.IsType<ItemsControl>(view.FindName(itemsControlName));

                    var c0 = Assert.IsAssignableFrom<ContentPresenter>(itemsCtrl.ItemContainerGenerator.ContainerFromIndex(0));
                    var c4 = Assert.IsAssignableFrom<ContentPresenter>(itemsCtrl.ItemContainerGenerator.ContainerFromIndex(4));

                    var tile0 = Assert.IsType<Grid>(VisualTreeHelper.GetChild(c0, 0));
                    var tile4 = Assert.IsType<Grid>(VisualTreeHelper.GetChild(c4, 0));

                    Point probeTopLeft = probe.TranslatePoint(new Point(0, 0), host);
                    Point probeTopRight = probe.TranslatePoint(new Point(probe.ActualWidth, 0), host);

                    Point tile0TopLeft = tile0.TranslatePoint(new Point(0, 0), host);
                    Point tile4TopRight = tile4.TranslatePoint(new Point(tile4.ActualWidth, 0), host);

                    // All 5 cards must sit on Row 1 (zero 5th-card wrap under UseLayoutRounding / 125% DPI!)
                    Assert.True(
                        Math.Abs(tile4TopRight.Y - tile0TopLeft.Y) < 1.0,
                        $"[{itemsControlName}] Expected 5th card on Row 1 (Y={tile0TopLeft.Y:F1}), got Y={tile4TopRight.Y:F1}.");

                    // Card 0 left edge flush with header left edge; Card 4 right edge flush with header right edge
                    Assert.True(
                        Math.Abs(tile0TopLeft.X - probeTopLeft.X) <= 2.0,
                        $"[{itemsControlName}] Expected Card 0 left ({tile0TopLeft.X:F1}) flush with header left ({probeTopLeft.X:F1}).");
                    Assert.True(
                        Math.Abs(tile4TopRight.X - probeTopRight.X) <= 2.0,
                        $"[{itemsControlName}] Expected Card 4 right ({tile4TopRight.X:F1}) flush with header right ({probeTopRight.X:F1}) — zero right-side void.");

                    // Artwork grid inside tile0 must be a 1:1 square
                    var artGrid = Assert.IsType<Grid>(tile0.Children[0]);
                    Assert.True(artGrid.ActualWidth > 180.0, $"[{itemsControlName}] Expected square artwork width > 180, got {artGrid.ActualWidth:F1}");
                    Assert.True(
                        Math.Abs(artGrid.ActualWidth - artGrid.ActualHeight) < 1.0,
                        $"[{itemsControlName}] Expected 1:1 square artwork, got {artGrid.ActualWidth:F1}x{artGrid.ActualHeight:F1}.");
                }
                finally
                {
                    host.Close();
                }
            }

            VerifyView(new AlbumsView(), "AlbumsItemsControl");
            VerifyView(new PlaylistsOverviewView(), "PlaylistsItemsControl");
        });
    }

    [Fact]
    public void MaximizedMainWindow_DisablesResizeBorderAndScrollBarThumbIsEdgeHitTestable()
    {
        RunWithIsolatedViewModel((vm, _) =>
        {
            var win = new MainWindow(vm)
            {
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000
            };

            try
            {
                win.Show();
                win.UpdateLayout();

                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(win);
                Assert.NotNull(chrome);
                Assert.Equal(new Thickness(6), chrome!.ResizeBorderThickness);

                // Maximizing must clear ResizeBorderThickness to 0 so right-edge ScrollBar & Close button receive clicks to the outermost pixel
                win.WindowState = WindowState.Maximized;
                win.UpdateLayout();
                Assert.Equal(new Thickness(0), chrome.ResizeBorderThickness);

                // Restoring to Normal must re-enable 6px resize borders
                win.WindowState = WindowState.Normal;
                win.UpdateLayout();
                Assert.Equal(new Thickness(6), chrome.ResizeBorderThickness);
            }
            finally
            {
                win.Close();
            }

            // Verify ScrollBar Thumb is hit-testable all the way to its outermost right edge (no dead 2px margin)
            var scrollBar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Minimum = 0,
                Maximum = 100,
                ViewportSize = 50,
                Value = 0,
                Height = 300
            };
            var sbHost = new Window
            {
                Width = 100,
                Height = 300,
                Content = scrollBar,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };
            try
            {
                sbHost.Show();
                sbHost.UpdateLayout();

                // Hit-test at x = ActualWidth - 0.5 (the literal rightmost edge of the scrollbar) inside the thumb's vertical band
                var hit = VisualTreeHelper.HitTest(scrollBar, new Point(scrollBar.ActualWidth - 0.5, 40));
                Assert.NotNull(hit?.VisualHit);

                DependencyObject? cur = hit!.VisualHit;
                bool hitThumb = false;
                while (cur != null)
                {
                    if (cur is Thumb)
                    {
                        hitThumb = true;
                        break;
                    }
                    cur = VisualTreeHelper.GetParent(cur);
                }
                Assert.True(hitThumb, "Expected rightmost edge of vertical ScrollBar to hit Thumb (zero dead edge zone).");
            }
            finally
            {
                sbHost.Close();
            }
        });
    }

    [Fact]
    public void LyricsAndPanePerformance_VirtualizedLyricsAndZeroLayoutThrashPane()
    {
        RunWithIsolatedViewModel((vm, _) =>
        {
            // 1. Load 150 synced lyric lines into LyricsView and verify UI virtualization realizes only visible slice
            var sb = new StringBuilder();
            for (int i = 0; i < 150; i++)
            {
                int sec = i * 2;
                sb.AppendLine($"[{sec / 60:D2}:{sec % 60:D2}.00]Synced performance lyric line number {i + 1}");
            }
            vm.Lyrics.ApplyParsedResult(LyricsParser.ParseLrcContent(sb.ToString(), LyricsSourceKind.SidecarLrc));
            Assert.Equal(150, vm.Lyrics.Lines.Count);

            var lyricsView = new LyricsView { DataContext = vm };
            var host = new Window
            {
                Width = 360,
                Height = 600,
                Content = lyricsView,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -20000,
                Top = -20000
            };

            try
            {
                host.Show();
                host.UpdateLayout();

                var list = Assert.IsType<ListBox>(lyricsView.FindName("SyncedLinesList"));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
                Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));

                int realizedCount = 0;
                for (int i = 0; i < 150; i++)
                {
                    if (list.ItemContainerGenerator.ContainerFromIndex(i) != null) realizedCount++;
                }
                Assert.True(realizedCount > 0 && realizedCount < 60, $"Expected virtualized subset of 150 lyric lines (<60), got {realizedCount}.");
            }
            finally
            {
                host.Close();
            }

            // 2. Opening Right Split Pane (Lyrics) glides continuously via Candidate A shared-progress
            //    with fixed 328px inner pane grid (zero lyric re-wrapping) and passes I1-I5 invariants.
            bool? prevReduced = AuraAudio.UI.Helpers.SpringAnimator.ForceReducedMotionOverride;
            AuraAudio.UI.Helpers.SpringAnimator.ForceReducedMotionOverride = false;
            var win = new MainWindow(vm)
            {
                Width = 1536,
                Height = 820,
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000
            };
            try
            {
                win.Show();
                win.UpdateLayout();

                var innerGrid = Assert.IsType<Grid>(win.FindName("RightPaneInnerGrid"));
                Assert.Equal(328.0, innerGrid.Width);

                // Inline mode (>= 1440): Opening Right Pane glides together with Auto-Compact Left Rail
                var openResult = MotionProbe.RecordShellTransitionDeterministic(
                    win,
                    "Unit_Inline1536_LyricsOpen",
                    () => vm.Presentation.ToggleLyricsPaneCommand.Execute(null),
                    isInlineMode: true,
                    expectedFinalRailWidth: 56.0,
                    expectedFinalPaneWidth: 360.0,
                    hz: 60.0,
                    capturePopPixels: false);

                Assert.True(openResult.PassI1NoJump, $"I1 failed: {openResult.FailureReasons}");
                Assert.True(openResult.PassI2Adjacency, $"I2 failed: {openResult.FailureReasons}");
                Assert.True(openResult.PassI3Monotonic, $"I3 failed: {openResult.FailureReasons}");
                Assert.True(openResult.PassI4Completion, $"I4 failed: {openResult.FailureReasons}");
                Assert.True(openResult.PassI5FrameTime, $"I5 failed: {openResult.FailureReasons}");

                // Closing Right Pane glides back continuously with zero late reflow
                var closeResult = MotionProbe.RecordShellTransitionDeterministic(
                    win,
                    "Unit_Inline1536_LyricsClose",
                    () => vm.Presentation.CloseRightPaneCommand.Execute(null),
                    isInlineMode: true,
                    expectedFinalRailWidth: 248.0,
                    expectedFinalPaneWidth: 0.0,
                    hz: 60.0,
                    capturePopPixels: false);

                Assert.True(closeResult.PassI1NoJump, $"I1 failed: {closeResult.FailureReasons}");
                Assert.True(closeResult.PassI2Adjacency, $"I2 failed: {closeResult.FailureReasons}");
                Assert.True(closeResult.PassI3Monotonic, $"I3 failed: {closeResult.FailureReasons}");
                Assert.True(closeResult.PassI4Completion, $"I4 failed: {closeResult.FailureReasons}");
                Assert.True(closeResult.PassI5FrameTime, $"I5 failed: {closeResult.FailureReasons}");
            }
            finally
            {
                AuraAudio.UI.Helpers.SpringAnimator.ForceReducedMotionOverride = prevReduced;
                win.Close();
            }
        });
    }

    [Fact]
    public void HomeView_ShelfCarousel_HiResBadgeBottomBorderIsNotClippedAt125Dpi()
    {
        RunWithIsolatedViewModel((vm, tempDir) =>
        {
            var hiResSpec = new AudioStreamSpec(AudioCodec.FLAC, 96000, BitDepth.Bit24, 2, 4608);
            var albumTracks = new List<Track>
            {
                new(
                    id: 1,
                    filePath: Path.Combine(tempDir, "hires.flac"),
                    title: "Hi-Res Track",
                    artist: "Ayunda Risu",
                    sortArtist: "Ayunda Risu",
                    album: "1:15 AM",
                    albumArtist: "Ayunda Risu",
                    year: 2024,
                    trackNumber: 1,
                    discNumber: 1,
                    duration: TimeSpan.FromMinutes(3.2),
                    streamSpec: hiResSpec,
                    fileSizeBytes: 60_000_000,
                    dateAdded: DateTime.UtcNow,
                    hasEmbeddedCover: false,
                    coverArtCacheKey: null)
            };
            using (var db = new DatabaseManager(Path.Combine(tempDir, "hires_check.db")))
            {
                db.Initialize();
                db.InsertTracksBatch(albumTracks);
                using var hiResVm = new MainViewModel(db: db);
                hiResVm.CurrentView = "Home";

                var homeView = new HomeView { DataContext = hiResVm };
                var host = new Window
                {
                    Width = 1288,
                    Height = 820,
                    UseLayoutRounding = true,
                    SnapsToDevicePixels = true,
                    Content = homeView,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Left = -20000,
                    Top = -20000
                };

                try
                {
                    host.Show();
                    host.UpdateLayout();
                    host.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                    host.UpdateLayout();

                    var shelf = FindVisualChild<AuraAudio.UI.Controls.ShelfCarousel>(homeView);
                    Assert.NotNull(shelf);
                    var shelfScroll = Assert.IsType<ScrollViewer>(shelf!.FindName("ShelfScroll"));

                    // Find the HI-RES badge Border inside the first album card in ShelfCarousel
                    Border? hiResBorder = null;
                    void Walk(DependencyObject parent)
                    {
                        if (hiResBorder != null) return;
                        int n = VisualTreeHelper.GetChildrenCount(parent);
                        for (int i = 0; i < n; i++)
                        {
                            var ch = VisualTreeHelper.GetChild(parent, i);
                            if (ch is Border b && b.Visibility == Visibility.Visible && b.Child is TextBlock tb && tb.Text == "HI-RES")
                            {
                                hiResBorder = b;
                                return;
                            }
                            Walk(ch);
                        }
                    }
                    Walk(shelfScroll);
                    Assert.NotNull(hiResBorder);

                    // 1. Geometry check: HI-RES bottom edge must sit strictly inside ShelfScroll's viewport (>= 3 DIP clearance)
                    Point badgeBottom = hiResBorder!.TranslatePoint(new Point(0, hiResBorder.ActualHeight), shelfScroll);
                    double clearance = shelfScroll.ActualHeight - badgeBottom.Y;
                    Assert.True(clearance >= 3.0,
                        $"Expected >= 3.0 DIP bottom clearance below HI-RES badge inside ShelfScroll to prevent 125% DPI cropping, got {clearance:F2} DIP (badgeBottom={badgeBottom.Y:F2}, shelfH={shelfScroll.ActualHeight:F2}).");

                    // 2. Pixel check at 120 DPI (125% scaling): verify bottom horizontal border of HI-RES badge renders non-canvas pixels
                    const double scale = 1.25;
                    int bmpW = Math.Max(1, (int)Math.Ceiling(shelfScroll.ActualWidth * scale));
                    int bmpH = Math.Max(1, (int)Math.Ceiling(shelfScroll.ActualHeight * scale));
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(bmpW, bmpH, 96.0 * scale, 96.0 * scale, PixelFormats.Pbgra32);
                    rtb.Render(shelfScroll);
                    byte[] px = new byte[bmpW * bmpH * 4];
                    rtb.CopyPixels(px, bmpW * 4, 0);

                    Point badgeTopLeft = hiResBorder.TranslatePoint(new Point(0, 0), shelfScroll);
                    int midX = (int)Math.Round((badgeTopLeft.X + hiResBorder.ActualWidth * 0.5) * scale);
                    int bottomEdgeY = (int)Math.Round((badgeTopLeft.Y + hiResBorder.ActualHeight) * scale);

                    // Scan 3 physical pixel rows right along the bottom edge of the badge at its horizontal center
                    int maxBottomLuma = 0;
                    for (int y = Math.Max(0, bottomEdgeY - 3); y <= Math.Min(bmpH - 1, bottomEdgeY + 1); y++)
                    {
                        int off = (y * bmpW + Math.Clamp(midX, 0, bmpW - 1)) * 4;
                        int luma = px[off]; // monochrome B==G==R
                        if (luma > maxBottomLuma) maxBottomLuma = luma;
                    }
                    // Brush.Border.Control is #3A3A3A (luma 58) vs canvas #0A0A0A (luma 10) -> must render visible border stroke!
                    Assert.True(maxBottomLuma >= 35,
                        $"Expected visible bottom border stroke on HI-RES badge at 125% DPI (luma >= 35), got maxBottomLuma={maxBottomLuma}.");
                }
                finally
                {
                    host.Close();
                }
            }
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
