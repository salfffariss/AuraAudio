using System.Windows;
using System.Windows.Input;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Window modal mewah untuk audit keaslian audio secara terpisah (standalone modal).
/// Menerapkan DWM Mica Backdrop dan Immersive Dark Mode.
/// </summary>
public partial class AuthenticityInspectorDialog : Window
{
    public AuthenticityInspectorDialog(MainViewModel viewModel)
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
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
