using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Artists Master-Detail Split Screen (03_design_spec.md Part B.4):
/// Left 280px scrollable artist index with inline filter and inverted active pill;
/// Right live grouped discography pane with inline album covers, tracklists, and drag-to-playlist.
/// </summary>
public partial class ArtistsView : UserControl
{
    private Point _dragStartPoint;
    private bool _didDrag;

    public ArtistsView()
    {
        InitializeComponent();
    }

    private void ArtistMasterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ArtistMasterList.SelectedItem is Artist artist && DataContext is MainViewModel vm)
        {
            if (vm.SelectedArtist == null || !string.Equals(vm.SelectedArtist.Name, artist.Name, StringComparison.OrdinalIgnoreCase))
            {
                vm.ViewArtistDetail(artist);
            }
        }
    }

    private void ClearArtistFilter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.ArtistFilterQuery = string.Empty;
        }
    }

    private void Draggable_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _didDrag = false;
    }

    private void AlbumGroupArt_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _didDrag) return;

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ArtistAlbumGroup grp)
            {
                _didDrag = true;
                DragDrop.DoDragDrop(fe, new DataObject(typeof(Album), grp.Album), DragDropEffects.Copy);
            }
        }
    }

    private void AlbumGroupArt_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) return;
        if (sender is FrameworkElement fe && fe.DataContext is ArtistAlbumGroup grp && DataContext is MainViewModel vm)
        {
            vm.OpenAlbumDetailCommand.Execute(grp.Album);
        }
    }

    private void TrackRow_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _didDrag) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Track track)
            {
                _didDrag = true;
                DragDrop.DoDragDrop(fe, new DataObject(typeof(Track), track), DragDropEffects.Copy);
            }
        }
    }

    private void TrackRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !IsInsideButton(e.OriginalSource as DependencyObject))
        {
            if (sender is FrameworkElement fe && fe.DataContext is Track track && DataContext is MainViewModel vm)
            {
                vm.PlayTrackInArtistContextCommand.Execute(track);
            }
        }
    }

    private static bool IsInsideButton(DependencyObject? source) =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<ButtonBase>(source) != null;
}
