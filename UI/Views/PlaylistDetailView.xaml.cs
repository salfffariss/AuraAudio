using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Playlist Detail Screen (03_design_spec.md Part B.5):
/// 270x270 14px-radius hero cover, inline rename, sticky scroll header,
/// 40px tracklist with 32x32 art, hover play/remove, Delete shortcut, and drag-to-playlist.
/// </summary>
public partial class PlaylistDetailView : UserControl
{
    private Point _dragStartPoint;
    private bool _stickyVisible;

    public PlaylistDetailView()
    {
        InitializeComponent();
    }

    private void DetailScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool shouldShow = DetailScroll.VerticalOffset > 260.0;
        if (shouldShow == _stickyVisible) return;
        _stickyVisible = shouldShow;

        if (shouldShow)
        {
            StickyHeaderBar.Visibility = Visibility.Visible;
            SpringAnimator.SpringOpacity(StickyHeaderBar, 1.0, response: 0.18);
        }
        else
        {
            SpringAnimator.SpringOpacity(
                StickyHeaderBar,
                0.0,
                response: 0.14,
                onComplete: () =>
                {
                    if (!_stickyVisible)
                    {
                        StickyHeaderBar.Visibility = Visibility.Collapsed;
                    }
                });
        }
    }

    private void OpenInlineRename_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedPlaylist != null)
        {
            RenameBox.Text = vm.SelectedPlaylist.Name;
            TitleDisplay.Visibility = Visibility.Collapsed;
            InlineRenamePanel.Visibility = Visibility.Visible;
            RenameBox.Focus();
            RenameBox.SelectAll();
        }
    }

    private void ConfirmRename_Click(object sender, RoutedEventArgs e)
    {
        CommitRename();
    }

    private void CancelRename_Click(object sender, RoutedEventArgs e)
    {
        InlineRenamePanel.Visibility = Visibility.Collapsed;
        TitleDisplay.Visibility = Visibility.Visible;
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            InlineRenamePanel.Visibility = Visibility.Collapsed;
            TitleDisplay.Visibility = Visibility.Visible;
            e.Handled = true;
        }
    }

    private void CommitRename()
    {
        string name = RenameBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name) && DataContext is MainViewModel vm)
        {
            vm.RenameSelectedPlaylistDirectCommand.Execute(name);
        }
        InlineRenamePanel.Visibility = Visibility.Collapsed;
        TitleDisplay.Visibility = Visibility.Visible;
    }

    private void PlaylistTracksList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (PlaylistTracksList.SelectedItem is Track track && DataContext is MainViewModel vm)
        {
            vm.PlaySelectedPlaylistTrackCommand.Execute(track);
        }
    }

    private void PlaylistTracksList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm || PlaylistTracksList.SelectedItem is not Track track) return;

        if (e.Key == Key.Enter)
        {
            vm.PlaySelectedPlaylistTrackCommand.Execute(track);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            vm.RemoveTrackFromCurrentPlaylistCommand.Execute(track);
            e.Handled = true;
        }
    }

    private void PlaylistTracksList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void PlaylistTracksList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (PlaylistTracksList.SelectedItem is Track track)
            {
                DragDrop.DoDragDrop(PlaylistTracksList, new DataObject(typeof(Track), track), DragDropEffects.Copy);
            }
        }
    }

    private static bool IsInsideButton(DependencyObject? source) =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<ButtonBase>(source) != null;
}
