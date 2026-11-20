using System.Windows.Controls;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Unified In-App Settings View (03_design_spec.md Part B.12):
/// Clickable 34px Bold NavigationBreadcrumbBar + structured SettingContainer rows absorbing
/// General, Playback/DAC, Library &amp; Folders, Accessibility, and About.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
