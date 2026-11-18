using System.Windows;
using System.Windows.Input;

namespace AuraAudio.UI.Views;

/// <summary>
/// Interaction logic for RenamePlaylistDialog.xaml
/// </summary>
public partial class RenamePlaylistDialog : Window
{
    public string PlaylistName { get; set; } = string.Empty;

    public RenamePlaylistDialog(string initialName)
    {
        PlaylistName = initialName;
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ConfirmSave();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ConfirmSave();
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void ConfirmSave()
    {
        if (string.IsNullOrWhiteSpace(PlaylistName))
        {
            MessageBox.Show("Nama playlist tidak boleh kosong.", "Validasi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PlaylistName = PlaylistName.Trim();
        DialogResult = true;
        Close();
    }
}
