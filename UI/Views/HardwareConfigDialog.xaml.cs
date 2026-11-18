using System;
using System.Windows;
using System.Windows.Input;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Interaction logic for HardwareConfigDialog.xaml
/// </summary>
public partial class HardwareConfigDialog : Window
{
    private readonly HardwareConfigViewModel _viewModel;

    public HardwareConfigDialog(HardwareConfigViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;

        _viewModel.RequestClose += () => DialogResult = true;

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DwmBackdropHelper.ApplyBackdrop(this, DwmBackdropHelper.BackdropType.Mica);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
