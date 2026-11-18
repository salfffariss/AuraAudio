using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Interaction logic for AlbumGridView.xaml
/// </summary>
public partial class AlbumGridView : UserControl
{
    public AlbumGridView()
    {
        InitializeComponent();
    }

    private void PlayButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement element && element.DataContext is Album album)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PlayAlbumCommand.Execute(album);
            }
        }
    }

    private void AlbumCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is Album album)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.OpenAlbumDetailCommand.Execute(album);
            }
        }
    }
}
