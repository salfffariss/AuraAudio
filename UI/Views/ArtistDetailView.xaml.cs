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
/// v2.0.0 Artist Detail Screen (03_design_spec.md Part B.7):
/// Full-bleed monochrome-scrimmed 220px hero banner (42px Bold title + 40px Play disc),
/// 2-column Latest Release & Featured Tracks split, horizontal Albums ShelfCarousel,
/// real SQLite library summary grid, and sticky 48px scroll header.
/// </summary>
public partial class ArtistDetailView : UserControl
{
    private Point _dragStartPoint;
    private bool _didDrag;
    private bool _stickyVisible;

    public ArtistDetailView()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                ArtistScroll.ScrollToTop();
                _stickyVisible = false;
                StickyHeaderBar.Opacity = 0;
                StickyHeaderBar.Visibility = Visibility.Collapsed;
            }
        };
    }

    private void ArtistScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool shouldShow = ArtistScroll.VerticalOffset > 200.0;
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
        _didDrag = false;
    }

    private void LatestRelease_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _didDrag) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (DataContext is MainViewModel vm && vm.ArtistLatestRelease != null)
            {
                _didDrag = true;
                DragDrop.DoDragDrop(LatestReleaseCard, new DataObject(typeof(Album), vm.ArtistLatestRelease), DragDropEffects.Copy);
            }
        }
    }

    private void LatestRelease_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag || IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (DataContext is MainViewModel vm && vm.ArtistLatestRelease != null)
        {
            vm.OpenAlbumDetailCommand.Execute(vm.ArtistLatestRelease);
        }
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
                DragDrop.DoDragDrop(fe, new DataObject(typeof(Album), album), DragDropEffects.Copy);
            }
        }
    }

    private void AlbumCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_didDrag || IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (sender is FrameworkElement fe && fe.DataContext is Album album && DataContext is MainViewModel vm)
        {
            vm.OpenAlbumDetailCommand.Execute(album);
        }
    }

    private void FeaturedTracksList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (FeaturedTracksList.SelectedItem is Track track && DataContext is MainViewModel vm)
        {
            vm.PlayTrackInArtistContextCommand.Execute(track);
        }
    }

    private void FeaturedTracksList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (FeaturedTracksList.SelectedItem is Track track)
            {
                DragDrop.DoDragDrop(FeaturedTracksList, new DataObject(typeof(Track), track), DragDropEffects.Copy);
            }
        }
    }

    private void RowMoreButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        FluidBehaviors.OpenAncestorContextMenu(sender as DependencyObject);
    }

    private void CardMoreButton_Click(object sender, RoutedEventArgs e)
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
            cur = AuraAudio.UI.Helpers.TreeNavigationHelper.GetParent(cur);
        }
    }

    private static bool IsInsideButton(DependencyObject? source) =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<ButtonBase>(source) != null;
}
