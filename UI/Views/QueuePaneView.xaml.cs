using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;
using Track = AuraAudio.Core.Models.Track;

namespace AuraAudio.UI.Views;

/// <summary>
/// v2.0.0 Right Split Pane & Mini-Player Queue View (03_design_spec.md Part B.9):
/// Playing Next (drag-to-reorder, Alt+Up/Down, Delete, hover-swap duration -> More & Remove)
/// and History (SQLite play_history with single-click instant replay).
/// </summary>
public partial class QueuePaneView : UserControl
{
    private const string QueueReorderFormat = "AuraQueueTrackItem";

    public static readonly DependencyProperty ShowHeaderTabsProperty =
        DependencyProperty.Register(
            nameof(ShowHeaderTabs),
            typeof(bool),
            typeof(QueuePaneView),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ClearButtonAutomationIdProperty =
        DependencyProperty.Register(
            nameof(ClearButtonAutomationId),
            typeof(string),
            typeof(QueuePaneView),
            new PropertyMetadata("Aura.Queue.Clear"));

    public static readonly DependencyProperty IsDropTargetProperty =
        DependencyProperty.RegisterAttached(
            "IsDropTarget",
            typeof(bool),
            typeof(QueuePaneView),
            new PropertyMetadata(false));

    public bool ShowHeaderTabs
    {
        get => (bool)GetValue(ShowHeaderTabsProperty);
        set => SetValue(ShowHeaderTabsProperty, value);
    }

    public string ClearButtonAutomationId
    {
        get => (string)GetValue(ClearButtonAutomationIdProperty);
        set => SetValue(ClearButtonAutomationIdProperty, value);
    }

    public static bool GetIsDropTarget(DependencyObject obj) => (bool)obj.GetValue(IsDropTargetProperty);
    public static void SetIsDropTarget(DependencyObject obj, bool value) => obj.SetValue(IsDropTargetProperty, value);

    private Point _dragStartPoint;
    private QueueTrackItem? _dragCandidate;
    private bool _didDrag;

    public QueuePaneView()
    {
        InitializeComponent();
    }

    private void QueueListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (QueueListBox.SelectedItem is not QueueTrackItem selected) return;

        bool isAlt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        Key effectiveKey = e.Key == Key.System ? e.SystemKey : e.Key;

        if (isAlt && effectiveKey == Key.Up)
        {
            int targetIdx = Math.Max(0, QueueListBox.SelectedIndex - 1);
            vm.MoveQueueItemUpCommand.Execute(selected);
            if (targetIdx < QueueListBox.Items.Count)
            {
                QueueListBox.SelectedIndex = targetIdx;
            }
            e.Handled = true;
        }
        else if (isAlt && effectiveKey == Key.Down)
        {
            int targetIdx = Math.Min(QueueListBox.Items.Count - 1, QueueListBox.SelectedIndex + 1);
            vm.MoveQueueItemDownCommand.Execute(selected);
            if (targetIdx >= 0 && targetIdx < QueueListBox.Items.Count)
            {
                QueueListBox.SelectedIndex = targetIdx;
            }
            e.Handled = true;
        }
        else if (effectiveKey == Key.Delete)
        {
            vm.RemoveQueueItemCommand.Execute(selected);
            e.Handled = true;
        }
        else if (effectiveKey == Key.Enter)
        {
            vm.PlayQueueItemCommand.Execute(selected);
            e.Handled = true;
        }
    }

    private void QueueItem_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject))
        {
            _dragCandidate = null;
            return;
        }

        _dragStartPoint = e.GetPosition(null);
        _didDrag = false;
        _dragCandidate = (sender as ListBoxItem)?.DataContext as QueueTrackItem;
    }

    private void QueueItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _didDrag || _dragCandidate == null) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        Point current = e.GetPosition(null);
        if (Math.Abs(current.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(current.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _didDrag = true;
            if (sender is DependencyObject dragSource)
            {
                var data = new DataObject();
                data.SetData(QueueReorderFormat, _dragCandidate);
                data.SetData(typeof(Track), _dragCandidate.Track);
                DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Move | DragDropEffects.Copy);
            }
            _dragCandidate = null;
        }
    }

    private void QueueItem_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(QueueReorderFormat))
        {
            e.Effects = DragDropEffects.Move;
            if (sender is ListBoxItem item)
            {
                SetIsDropTarget(item, true);
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void QueueItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            SetIsDropTarget(item, false);
        }
    }

    private void QueueItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is ListBoxItem targetContainer)
        {
            SetIsDropTarget(targetContainer, false);

            if (e.Data.GetData(QueueReorderFormat) is QueueTrackItem dragged &&
                targetContainer.DataContext is QueueTrackItem target &&
                DataContext is MainViewModel vm)
            {
                int fromIdx = dragged.DisplayNumber - 1;
                int toIdx = target.DisplayNumber - 1;
                if (fromIdx >= 0 && toIdx >= 0 && fromIdx != toIdx)
                {
                    vm.Controller.MoveQueueItem(fromIdx, toIdx);
                    vm.RefreshQueue();
                }
            }
        }
        e.Handled = true;
    }

    private void QueueItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (sender is ListBoxItem item && item.DataContext is QueueTrackItem qItem && DataContext is MainViewModel vm)
        {
            vm.PlayQueueItemCommand.Execute(qItem);
            e.Handled = true;
        }
    }

    private void HistoryItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        if (sender is ListBoxItem item && item.DataContext is Track track && DataContext is MainViewModel vm)
        {
            vm.PlaySelectedTrackCommand.Execute(track);
            e.Handled = true;
        }
    }

    private void QueueMoreButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not DependencyObject origin) return;
        DependencyObject? cur = origin;
        while (cur != null)
        {
            if (cur is FrameworkElement fe && fe.ContextMenu != null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.Placement = PlacementMode.MousePoint;
                fe.ContextMenu.IsOpen = true;
                return;
            }
            cur = AuraAudio.UI.Helpers.TreeNavigationHelper.GetParent(cur);
        }
    }

    private static bool IsInsideButton(DependencyObject? source) =>
        AuraAudio.UI.Helpers.TreeNavigationHelper.FindAncestor<ButtonBase>(source) != null;
}
