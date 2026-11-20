using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Pure-monochrome in-window modal sheet for adding a track to an existing or newly created playlist.
/// </summary>
public partial class AddToPlaylistSheet : UserControl
{
    public AddToPlaylistSheet()
    {
        InitializeComponent();
    }

    public void PlayOpenAnimation()
    {
        NewPlaylistNameBox.Text = string.Empty;
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
            vm.CloseAddToPlaylistSheetCommand.Execute(null);
        }
        e.Handled = true;
    }

    private void SheetCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void CreateAndAdd_Click(object sender, RoutedEventArgs e)
    {
        CommitCreateAndAdd();
    }

    private void NewPlaylistNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitCreateAndAdd();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.CloseAddToPlaylistSheetCommand.Execute(null);
            }
            e.Handled = true;
        }
    }

    private void CommitCreateAndAdd()
    {
        string name = NewPlaylistNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        if (DataContext is MainViewModel vm)
        {
            vm.CreatePlaylistAndAddTargetTrackCommand.Execute(name);
            NewPlaylistNameBox.Text = string.Empty;
        }
    }
}
