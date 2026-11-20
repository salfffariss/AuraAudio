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
/// v2.0.0 Home / Landing Screen (03_design_spec.md Part B.1):
/// 4 real SQLite editorial shelves (Recently Added, Jump Back In, Hi-Res Masters, Your Artists)
/// plus empty-library onboarding CTA and drag-to-sidebar-playlist support.
/// </summary>
public partial class HomeView : UserControl
{
    public static readonly DependencyProperty RecentColumnsProperty =
        DependencyProperty.Register(nameof(RecentColumns), typeof(int), typeof(HomeView), new PropertyMetadata(4));

    public int RecentColumns
    {
        get => (int)GetValue(RecentColumnsProperty);
        set => SetValue(RecentColumnsProperty, value);
    }

    private Point _dragStartPoint;
    private bool _didDrag;

    public HomeView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (RecentGridProbe.ActualWidth > 0)
            {
                UpdateRecentColumns(RecentGridProbe.ActualWidth);
            }
        };
    }

    private void RecentGridProbe_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        UpdateRecentColumns(e.NewSize.Width);
    }

    private void UpdateRecentColumns(double innerWidth)
    {
        if (innerWidth <= 0) return;
        int cols = innerWidth >= 1060.0 ? 4 : (innerWidth >= 740.0 ? 3 : 2);
        if (RecentColumns != cols)
        {
            RecentColumns = cols;
        }
    }

    private void DraggableCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _didDrag = false;
    }

    private void AlbumCard_MouseMove(object sender, MouseEventArgs e)
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
                var data = new DataObject(typeof(Album), album);
                DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
            }
        }
    }

    private void TrackCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _didDrag) return;

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Track track)
            {
                _didDrag = true;
                var data = new DataObject(typeof(Track), track);
                DragDrop.DoDragDrop(fe, data, DragDropEffects.Copy);
            }
        }
    }

    private void AlbumCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag || IsInsideButton(e.OriginalSource as DependencyObject)) return;

        if (sender is FrameworkElement fe && fe.DataContext is Album album && DataContext is MainViewModel vm)
        {
            vm.Home.OpenAlbumCommand.Execute(album);
        }
    }

    private void TrackCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag) return;

        if (sender is FrameworkElement fe && fe.DataContext is Track track && DataContext is MainViewModel vm)
        {
            vm.Home.PlayTrackCommand.Execute(track);
        }
    }

    private void ArtistCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Artist artist && DataContext is MainViewModel vm)
        {
            vm.Home.OpenArtistCommand.Execute(artist);
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
