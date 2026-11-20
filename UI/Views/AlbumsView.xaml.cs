using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Albums Grid Screen (03_design_spec.md Part B.3):
/// Virtualized single-pass GridG (6 / 5 / 4 / 3 frameless SquareLockup columns),
/// dual-corner hover buttons, Sort selector, inline quick filter, and drag-album-to-playlist support.
/// </summary>
public partial class AlbumsView : UserControl
{
    private Point _dragStartPoint;
    private bool _didDrag;

    public AlbumsView()
    {
        InitializeComponent();
    }

    private void ClearAlbumFilter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.AlbumFilterQuery = string.Empty;
        }
    }

    private void AlbumTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _didDrag = false;
    }

    private void AlbumTile_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _didDrag) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Album album)
            {
                _didDrag = true;
                DragDrop.DoDragDrop(fe, new DataObject(typeof(Album), album), DragDropEffects.Copy);
            }
        }
    }

    private void AlbumTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag || IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (sender is FrameworkElement fe && fe.DataContext is Album album && DataContext is MainViewModel vm)
        {
            vm.OpenAlbumDetailCommand.Execute(album);
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not DependencyObject origin) return;
        DependencyObject? cur = origin;
        while (cur != null)
        {
            if (cur is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.Placement = PlacementMode.MousePoint;
                fe.ContextMenu.IsOpen = true;
                return;
            }
            cur = VisualTreeHelper.GetParent(cur);
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        DependencyObject? cur = source;
        while (cur != null)
        {
            if (cur is ButtonBase) return true;
            cur = VisualTreeHelper.GetParent(cur);
        }
        return false;
    }
}
