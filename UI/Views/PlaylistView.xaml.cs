using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Interaction logic for PlaylistView.xaml
/// </summary>
public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();
    }

    private void PlaylistListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is Track track)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PlaySelectedPlaylistTrackCommand.Execute(track);
            }
        }
    }

    private void CreatePlaylist_Click(object sender, RoutedEventArgs e)
    {
        SubmitNewPlaylist();
    }

    private void NewPlaylistTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitNewPlaylist();
        }
    }

    private void SubmitNewPlaylist()
    {
        string name = NewPlaylistTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Silakan ketik nama playlist terlebih dahulu.", "Nama Kosong", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (DataContext is MainViewModel vm)
        {
            vm.CreatePlaylistCommand.Execute(name);
            NewPlaylistTextBox.Clear();
        }
    }
}
