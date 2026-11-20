using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.Core.Models;
using AuraAudio.UI.Views;
using Xunit;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.Tests;

[Collection("WpfSta")]
public class UiScrollAndFocusTests
{
    private static void RunInSta(Action action) => WpfStaTestRunner.Run(action);

    private static List<Track> CreateSampleTracks(int count)
    {
        var spec = new AudioStreamSpec(AudioCodec.FLAC, 48000, BitDepth.Bit24, 2, 1600);
        return Enumerable.Range(1, count)
            .Select(i => new Track(
                id: i,
                filePath: $@"C:\Music\Track{i:D2}.flac",
                title: $"Track {i:D2}",
                artist: "Test Artist",
                sortArtist: "Test Artist",
                album: "Test Album",
                albumArtist: "Test Artist",
                year: 2025,
                trackNumber: i,
                discNumber: 1,
                duration: TimeSpan.FromMinutes(3.5),
                streamSpec: spec,
                fileSizeBytes: 35_000_000,
                dateAdded: DateTime.UtcNow,
                hasEmbeddedCover: false,
                coverArtCacheKey: null))
            .ToList();
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

    private static void VerifyInlineTracklistBubblesMouseWheel(
        UserControl view,
        string scrollViewerName,
        string listBoxName,
        object dataContext)
    {
        view.DataContext = dataContext;
        var host = new Window
        {
            Width = 1000,
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

            var outerScroll = Assert.IsType<ScrollViewer>(view.FindName(scrollViewerName));
            var listBox = Assert.IsType<ListBox>(view.FindName(listBoxName));
            listBox.UpdateLayout();

            // Invariant 1: Inline ListBox must have NO internal ScrollViewer swallowing MouseWheel
            Assert.Null(FindVisualChild<ScrollViewer>(listBox));
            Assert.True(outerScroll.ScrollableHeight > 50, $"Expected {scrollViewerName} to be scrollable.");

            // Invariant 2: MouseWheel raised directly on a track ListBoxItem scrolls the outer page ScrollViewer
            var itemContainer = Assert.IsAssignableFrom<ListBoxItem>(
                listBox.ItemContainerGenerator.ContainerFromIndex(0));

            Assert.Equal(0.0, outerScroll.VerticalOffset);

            var wheelDown = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -240)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = itemContainer
            };
            itemContainer.RaiseEvent(wheelDown);
            host.UpdateLayout();

            Assert.True(outerScroll.VerticalOffset > 0.0, $"Expected wheel down on {listBoxName} item to scroll {scrollViewerName}.");

            double scrolledOffset = outerScroll.VerticalOffset;
            var wheelUp = new MouseWheelEventArgs(Mouse.PrimaryDevice, 1, 240)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = itemContainer
            };
            itemContainer.RaiseEvent(wheelUp);
            host.UpdateLayout();

            Assert.True(outerScroll.VerticalOffset < scrolledOffset, $"Expected wheel up on {listBoxName} item to scroll {scrollViewerName} back up.");
        }
        finally
        {
            host.Close();
        }
    }

    [Fact]
    public void AlbumDetailView_TracklistMouseWheelScrollsOuterPageScrollViewer()
    {
        RunInSta(() =>
        {
            var tracks = CreateSampleTracks(25);
            var album = new Album(1, "Test Album", "Test Artist", 2025, 25, TimeSpan.FromMinutes(85), true, null);
            var vm = new
            {
                SelectedAlbumDetail = album,
                SelectedAlbumDetailTracks = tracks,
                AlbumDetailSummaryText = "2025 • 25 songs, 1 hr 25 min",
                AlbumDetailAudioBadge = "24-bit / 48 kHz FLAC",
                AlbumDetailFooterText = "25 songs",
                CurrentTrack = (Track?)null,
                IsPlaying = false
            };

            VerifyInlineTracklistBubblesMouseWheel(new AlbumDetailView(), "AlbumScroll", "AlbumTrackList", vm);
        });
    }

    [Fact]
    public void ArtistDetailView_FeaturedTracksMouseWheelScrollsOuterPageScrollViewer()
    {
        RunInSta(() =>
        {
            var tracks = CreateSampleTracks(8);
            var album = new Album(1, "Latest Album", "Test Artist", 2026, 8, TimeSpan.FromMinutes(28), true, null);
            var artist = new Artist("Test Artist", 8, 1, null);
            var vm = new
            {
                SelectedArtist = artist,
                ArtistStatsSummary = "1 Album • 8 Tracks • 28 min",
                ArtistLatestRelease = album,
                ArtistFeaturedTracks = tracks,
                ArtistAlbums = new[] { album },
                ArtistTracks = tracks,
                ArtistTotalRuntimeText = "28 min",
                ArtistFormatsSummary = "FLAC",
                ArtistHiResSummaryText = "8 of 8 tracks Hi-Res",
                CurrentTrack = (Track?)null,
                IsPlaying = false
            };

            VerifyInlineTracklistBubblesMouseWheel(new ArtistDetailView(), "ArtistScroll", "FeaturedTracksList", vm);
        });
    }

    [Fact]
    public void PlaylistDetailView_TracklistMouseWheelScrollsOuterPageScrollViewer()
    {
        RunInSta(() =>
        {
            var tracks = CreateSampleTracks(25);
            var playlist = new Playlist(1, "My Playlist", 25, DateTime.UtcNow);
            var vm = new
            {
                SelectedPlaylist = playlist,
                SelectedPlaylistCoverKey = (string?)null,
                SelectedPlaylistSummaryText = "25 Tracks • 85 min",
                SelectedPlaylistTracks = tracks,
                CurrentTrack = (Track?)null,
                IsPlaying = false
            };

            VerifyInlineTracklistBubblesMouseWheel(new PlaylistDetailView(), "DetailScroll", "PlaylistTracksList", vm);
        });
    }

    [Fact]
    public void MainWindow_AltAndEscapeClearStuckDockButtonFocus()
    {
        RunInSta(() =>
        {
            var win = new AuraAudio.UI.Windows.MainWindow
            {
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000
            };

            try
            {
                win.Show();
                win.UpdateLayout();

                // Locate the Mini-Player dock button via AutomationId
                var miniBtn = FindButtonByAutomationId(win, "Aura.Dock.MiniPlayer");
                Assert.NotNull(miniBtn);

                // Simulate focus parked on the Mini-Player button
                FocusManager.SetFocusedElement(win, miniBtn);
                Assert.Same(miniBtn, FocusManager.GetFocusedElement(win));

                // Bare LeftAlt must move focus off the dock button onto the MainWindow root
                // (preventing stuck white focus rings while preserving window-wide keyboard routing)
                var src = PresentationSource.FromVisual(win);
                Assert.NotNull(src);
                var altDown = new KeyEventArgs(Keyboard.PrimaryDevice, src!, 0, Key.LeftAlt)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    Source = miniBtn
                };
                miniBtn!.RaiseEvent(altDown);

                Assert.True(altDown.Handled);
                Assert.NotSame(miniBtn, FocusManager.GetFocusedElement(win));
                Assert.Same(win, FocusManager.GetFocusedElement(win));

                // Re-focus button (as if via Tab) -> Escape must return focus to MainWindow root cleanly
                FocusManager.SetFocusedElement(win, miniBtn);
                Assert.Same(miniBtn, FocusManager.GetFocusedElement(win));

                var escDown = new KeyEventArgs(Keyboard.PrimaryDevice, src!, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    Source = miniBtn
                };
                miniBtn.RaiseEvent(escDown);

                Assert.True(escDown.Handled);
                Assert.NotSame(miniBtn, FocusManager.GetFocusedElement(win));
                Assert.Same(win, FocusManager.GetFocusedElement(win));
            }
            finally
            {
                win.Close();
            }
        });
    }

    private static System.Windows.Controls.Primitives.ButtonBase? FindButtonByAutomationId(DependencyObject parent, string automationId)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.Primitives.ButtonBase btn &&
                System.Windows.Automation.AutomationProperties.GetAutomationId(btn) == automationId)
            {
                return btn;
            }
            var nested = FindButtonByAutomationId(child, automationId);
            if (nested != null) return nested;
        }
        return null;
    }
}
