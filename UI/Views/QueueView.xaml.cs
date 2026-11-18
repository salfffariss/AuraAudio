using System.Windows.Controls;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Interaction logic for QueueView.xaml
/// </summary>
public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
    }

    private void QueueListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox)
        {
            if (DataContext is MainViewModel vm)
            {
                if (listBox.SelectedItem is QueueTrackItem item)
                {
                    vm.PlayQueueItemCommand.Execute(item.Track);
                }
                else if (listBox.SelectedItem is Track track)
                {
                    vm.PlayQueueItemCommand.Execute(track);
                }
            }
        }
    }

    private void OnListBoxItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }
}
