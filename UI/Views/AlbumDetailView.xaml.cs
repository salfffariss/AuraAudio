using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Interaction logic for AlbumDetailView.xaml
/// </summary>
public partial class AlbumDetailView : UserControl
{
    public AlbumDetailView()
    {
        InitializeComponent();
    }

    private void AlbumDetailListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is Track track)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PlayAlbumDetailTrackCommand.Execute(track);
            }
        }
    }

    private void OnListBoxItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }
}
