using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Interaction logic for ArtistView.xaml (Gaya Apple Music)
/// </summary>
public partial class ArtistView : UserControl
{
    public ArtistView()
    {
        InitializeComponent();
    }

    private void AlbumItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Album album)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.OpenAlbumDetailCommand.Execute(album);
            }
        }
    }

    private void AlbumGroupHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            if (element.DataContext is ArtistAlbumGroup group)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.OpenAlbumDetailCommand.Execute(group.Album);
                }
            }
            else if (element.DataContext is Album album)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.OpenAlbumDetailCommand.Execute(album);
                }
            }
        }
    }

    private void TrackRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement element && element.DataContext is Track track)
        {
            if (DataContext is MainViewModel vm)
            {
                int idx = vm.ArtistTracks.IndexOf(track);
                _ = vm.Controller.PlayQueueAsync(vm.ArtistTracks, idx >= 0 ? idx : 0);
                vm.RefreshQueue();
            }
        }
    }
}
