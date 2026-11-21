using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 All Playlists Overview Screen (03_design_spec.md Part B.5):
/// Responsive GridG mosaic of user playlists with real artwork, hover Play/More actions,
/// inline New Playlist creation, and M3U import.
/// </summary>
public partial class PlaylistsOverviewView : UserControl
{
    public static readonly DependencyProperty GridColumnsProperty =
        DependencyProperty.Register(nameof(GridColumns), typeof(int), typeof(PlaylistsOverviewView), new PropertyMetadata(5));

    public static readonly DependencyProperty TileHeightProperty =
        DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(PlaylistsOverviewView), new PropertyMetadata(210.0));

    public int GridColumns
    {
        get => (int)GetValue(GridColumnsProperty);
        set => SetValue(GridColumnsProperty, value);
    }

    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    public PlaylistsOverviewView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (GridWidthProbe.ActualWidth > 0)
            {
                UpdateGridColumns(GridWidthProbe.ActualWidth);
            }
        };
    }

    private void GridWidthProbe_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        UpdateGridColumns(e.NewSize.Width);
    }

    private void UpdateGridColumns(double innerWidth)
    {
        if (innerWidth <= 0) return;

        int cols = innerWidth >= 1320.0 ? 6 : (innerWidth >= 980.0 ? 5 : (innerWidth >= 620.0 ? 4 : 3));
        if (GridColumns != cols)
        {
            GridColumns = cols;
        }

        double cellWidth = (innerWidth + 20.0) / cols;
        double targetHeight = Math.Max(120.0, Math.Round(cellWidth - 20.0));
        if (Math.Abs(TileHeight - targetHeight) >= 0.5)
        {
            TileHeight = targetHeight;
        }
    }

    public void OpenNewPlaylistBar()
    {
        NewPlaylistNameBox.Text = string.Empty;
        NewPlaylistBar.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => NewPlaylistNameBox.Focus());
    }

    private void ShowNewPlaylistBar_Click(object sender, RoutedEventArgs e)
    {
        OpenNewPlaylistBar();
    }

    private void ConfirmCreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        CommitCreate();
    }

    private void CancelCreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        NewPlaylistBar.Visibility = Visibility.Collapsed;
    }

    private void NewPlaylistNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitCreate();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            NewPlaylistBar.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void CommitCreate()
    {
        string name = NewPlaylistNameBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(name) && DataContext is MainViewModel vm)
        {
            vm.CreatePlaylistCommand.Execute(name);
            NewPlaylistBar.Visibility = Visibility.Collapsed;
        }
    }

    private void PlaylistTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (sender is FrameworkElement fe && fe.DataContext is PlaylistOverviewItem item && DataContext is MainViewModel vm)
        {
            vm.OpenPlaylistCommand.Execute(item);
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
            cur = AuraAudio.UI.Helpers.TreeNavigationHelper.GetParent(cur);
        }
    }

    private static bool IsInsideButton(DependencyObject? source) =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<ButtonBase>(source) != null;
}
