using System.Windows;
using System.Windows.Input;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Interaction logic for SignalPathDialog.xaml
/// </summary>
public partial class SignalPathDialog : Window
{
    public SignalPathDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (s, e) =>
        {
            DwmBackdropHelper.ApplyBackdrop(this, DwmBackdropHelper.BackdropType.Mica);
        };

        KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };

        MouseDown += (s, e) =>
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
