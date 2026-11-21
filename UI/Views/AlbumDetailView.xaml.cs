using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.Core.Models;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Album Detail Screen (03_design_spec.md Part B.6):
/// 270x270 14px-radius hero artwork (zero DropShadowEffect), 26px title & clickable artist link,
/// sticky 48px scroll header, hairline 40px tracklist with BouncyBar equalizer,
/// track/album drag-to-playlist, and ContainerDetailTracklistFooter.
/// </summary>
public partial class AlbumDetailView : UserControl
{
    private Point _dragStartPoint;
    private bool _stickyVisible;

    public AlbumDetailView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                AlbumScroll.ScrollToTop();
                _stickyVisible = false;
                StickyHeaderBar.Opacity = 0;
                StickyHeaderBar.Visibility = Visibility.Collapsed;
            }
        };
    }

    private void AlbumScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool shouldShow = AlbumScroll.VerticalOffset > 260.0;
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

    private void Draggable_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void AlbumHeroArt_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (DataContext is MainViewModel vm && vm.SelectedAlbumDetail != null)
            {
                DragDrop.DoDragDrop(this, new DataObject(typeof(Album), vm.SelectedAlbumDetail), DragDropEffects.Copy);
            }
        }
    }

    private void AlbumTrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (AlbumTrackList.SelectedItem is Track track && DataContext is MainViewModel vm)
        {
            vm.PlayAlbumDetailTrackCommand.Execute(track);
        }
    }

    private void AlbumTrackList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && AlbumTrackList.SelectedItem is Track track && DataContext is MainViewModel vm)
        {
            vm.PlayAlbumDetailTrackCommand.Execute(track);
            e.Handled = true;
        }
    }

    private void AlbumTrackList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (AlbumTrackList.SelectedItem is Track track)
            {
                DragDrop.DoDragDrop(AlbumTrackList, new DataObject(typeof(Track), track), DragDropEffects.Copy);
            }
        }
    }

    private void RowMoreButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        FluidBehaviors.OpenAncestorContextMenu(sender as DependencyObject);
    }

    private void AlbumMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.DataContext = DataContext;
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private static bool IsInsideButton(DependencyObject? source) =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<ButtonBase>(source) != null;
}
