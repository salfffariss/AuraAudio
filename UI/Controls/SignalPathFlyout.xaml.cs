using System.Windows;
using System.Windows.Controls;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Controls;

/// <summary>
/// v2.0.0 Compact 4-Node Pure-Monochrome Signal Path Popover (03_design_spec.md Part B.10 & B.13):
/// Anchored to Aura.Dock.AudioBadge; conveys Bit-Perfect vs Enhanced vs Resampled via
/// solid-fill vs hollow-ring monochrome glyphs and typography weight (zero RGB status hues).
/// </summary>
public partial class SignalPathFlyout : UserControl
{
    public SignalPathFlyout()
    {
        InitializeComponent();
    }

    private void OpenInspector_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Presentation.IsSignalPathFlyoutOpen = false;
            vm.NavigateCommand.Execute("Inspector");
        }
    }

    private void OpenAudioSettings_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Presentation.IsSignalPathFlyoutOpen = false;
            vm.OpenHardwareConfigCommand.Execute(null);
        }
    }
}
