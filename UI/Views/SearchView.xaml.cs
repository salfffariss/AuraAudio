using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Search View (03_design_spec.md Part B.8):
/// Segmented scope pills (All | Songs | Albums | Artists | Playlists),
/// empty-state 16:9 local library Browse Bricks, and grouped multi-entity search results.
/// </summary>
public partial class SearchView : UserControl
{
    public static readonly DependencyProperty BrowseBrickColumnsProperty =
        DependencyProperty.Register(nameof(BrowseBrickColumns), typeof(int), typeof(SearchView), new PropertyMetadata(3));

    public int BrowseBrickColumns
    {
        get => (int)GetValue(BrowseBrickColumnsProperty);
        set => SetValue(BrowseBrickColumnsProperty, value);
    }

    public SearchView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (BricksWidthProbe.ActualWidth > 0)
            {
                UpdateBrowseBrickColumns(BricksWidthProbe.ActualWidth);
            }
        };
    }

    private void BricksWidthProbe_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        UpdateBrowseBrickColumns(e.NewSize.Width);
    }

    private void UpdateBrowseBrickColumns(double innerWidth)
    {
        if (innerWidth <= 0) return;
        int cols = innerWidth >= 720.0 ? 3 : 2;
        if (BrowseBrickColumns != cols)
        {
            BrowseBrickColumns = cols;
        }
    }

    private void TopResultCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.Search.TopResult != null)
        {
            vm.PlaySelectedTrackCommand.Execute(vm.Search.TopResult);
        }
    }

    private void SearchTrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is Track track && DataContext is MainViewModel vm)
        {
            vm.PlaySelectedTrackCommand.Execute(track);
        }
    }

    private void SearchAlbum_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Album album && DataContext is MainViewModel vm)
        {
            vm.OpenAlbumDetailCommand.Execute(album);
        }
    }

    private void SearchArtist_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is Artist artist && DataContext is MainViewModel vm)
        {
            vm.OpenArtistDetailPage(artist);
        }
    }
}
