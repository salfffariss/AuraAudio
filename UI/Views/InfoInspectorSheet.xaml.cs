using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// 4-tab pure-monochrome Get Info modal sheet (Details | Audio &amp; Path | Authenticity | Lyrics)
/// (03_design_spec.md Part B.13 &amp; D.9).
/// </summary>
public partial class InfoInspectorSheet : UserControl
{
    public InfoInspectorSheet()
    {
        InitializeComponent();
    }

    public void PlayOpenAnimation()
    {
        if (DataContext is MainViewModel vm)
        {
            string tab = vm.Presentation.ActiveInfoTab;
            InfoPivotTabs.SelectedIndex = tab switch
            {
                "Audio" => 1,
                "Authenticity" => 2,
                "Lyrics" => 3,
                _ => 0
            };
        }

        SpringAnimator.Cancel(SheetScrim, "opacity");
        SpringAnimator.Cancel(SheetCard, "opacity");
        SpringAnimator.Cancel(SheetCard, "scale");
        SpringAnimator.Cancel(SheetCard, "translateY");

        SheetScrim.Opacity = 0.0;
        SpringAnimator.SpringOpacity(SheetScrim, 1.0, response: 0.16);
        SpringAnimator.AnimateSpatialExpand(
            SheetCard,
            origin: new Point(0.5, 0.56),
            fromScale: 0.96,
            fromY: 14.0,
            dampingRatio: 1.0,
            response: 0.24);
    }

    public void PlayCloseAnimation(Action onComplete)
    {
        SpringAnimator.SpringOpacity(SheetScrim, 0.0, response: 0.14);
        SpringAnimator.AnimateSpatialCollapse(
            SheetCard,
            origin: new Point(0.5, 0.56),
            toScale: 0.96,
            toY: 14.0,
            dampingRatio: 1.0,
            response: 0.20,
            onComplete: onComplete);
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Presentation.CloseInfoSheetCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void SheetCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private async void AnalyzeInfoTarget_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            await vm.AnalyzeTrackAsync(vm.InfoTargetTrack);
        }
    }

    private void OpenFullInspector_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.Presentation.CloseInfoSheetCommand.Execute(null);
            vm.NavigateCommand.Execute("Inspector");
        }
    }
}
