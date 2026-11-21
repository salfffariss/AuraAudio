using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.UI.ViewModels;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Songs Library Screen (03_design_spec.md Part B.2):
/// Toggleable 3-pane cascading ColumnBrowser (Format | Artist | Album),
/// reorderable/resizable monochrome DataGrid with column visibility ContextMenu,
/// virtualized 40px rows with BouncyBar equalizer, multi-select, and drag-to-playlist.
/// </summary>
public partial class SongsView : UserControl
{
    private Point _dragStartPoint;

    public SongsView()
    {
        InitializeComponent();
    }

    private void ToggleColumnVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string colName)
        {
            DataGridColumn? col = colName switch
            {
                "ColArtist" => ColArtist,
                "ColAlbum" => ColAlbum,
                "ColYear" => ColYear,
                "ColFormat" => ColFormat,
                "ColBitrate" => ColBitrate,
                "ColTime" => ColTime,
                _ => null
            };
            if (col != null)
            {
                col.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void SongsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (SongsDataGrid.SelectedItem is Track track && DataContext is MainViewModel vm)
        {
            vm.PlaySelectedTrackCommand.Execute(track);
        }
    }

    private void SongsDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SongsDataGrid.SelectedItem is Track track && DataContext is MainViewModel vm)
        {
            vm.PlaySelectedTrackCommand.Execute(track);
            e.Handled = true;
        }
    }

    private void SongsDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void SongsDataGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) != null) return;
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) == null) return;

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var selectedTracks = SongsDataGrid.SelectedItems.OfType<Track>().ToList();
            if (selectedTracks.Count == 0) return;

            var data = new DataObject();
            data.SetData("AuraTrackList", selectedTracks);
            data.SetData(typeof(Track), selectedTracks[0]);
            DragDrop.DoDragDrop(SongsDataGrid, data, DragDropEffects.Copy);
        }
    }

    private void RowMoreButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = FindAncestor<DataGridRow>(sender as DependencyObject);
        if (row?.ContextMenu != null)
        {
            row.IsSelected = true;
            row.ContextMenu.PlacementTarget = row;
            row.ContextMenu.Placement = PlacementMode.MousePoint;
            row.ContextMenu.IsOpen = true;
        }
    }

    private static bool IsInsideButton(DependencyObject? source) =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<ButtonBase>(source) != null;

    private static T? FindAncestor<T>(DependencyObject? origin) where T : DependencyObject =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<T>(origin);
}
