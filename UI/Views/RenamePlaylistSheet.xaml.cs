using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Pure-monochrome in-window modal sheet for creating or renaming a playlist.
/// </summary>
public partial class RenamePlaylistSheet : UserControl
{
    public RenamePlaylistSheet()
    {
        InitializeComponent();
    }

    public void PlayOpenAnimation()
    {
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

        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            RenameInputBox.Focus();
            RenameInputBox.SelectAll();
        });
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
            vm.CancelRenameSheetCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void SheetCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void RenameInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.ConfirmRenameSheetCommand.Execute(null);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.CancelRenameSheetCommand.Execute(null);
            }
            e.Handled = true;
        }
    }
}
