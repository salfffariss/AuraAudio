using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AuraAudio.Core.Models;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Windows;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private WindowsMediaControlsManager? _mediaControlsManager;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        _viewModel.RequestOpenHardwareConfig += OpenHardwareConfigDialog;
        _viewModel.RequestOpenLibraryManager += OpenLibraryManagerDialog;
        DataContext = _viewModel;

        // Terapkan efek visual Windows 11 Mica / Acrylic
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _viewModel.RequestOpenHardwareConfig += OpenHardwareConfigDialog;
        _viewModel.RequestOpenLibraryManager += OpenLibraryManagerDialog;
        DataContext = _viewModel;

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // Aktifkan DWM Mica Backdrop dan Immersive Dark Mode
        DwmBackdropHelper.ApplyBackdrop(this, DwmBackdropHelper.BackdropType.Mica);

        // Inisialisasi Windows 11 SMTC dan HWND Hook untuk Hardware Media Keys
        var helper = new WindowInteropHelper(this);
        IntPtr hwnd = helper.Handle;

        if (hwnd != IntPtr.Zero)
        {
            _mediaControlsManager = new WindowsMediaControlsManager(hwnd, _viewModel.Controller);
            _viewModel.SetMediaControlsManager(_mediaControlsManager);

            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(HwndMessageHook);
        }

        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _viewModel.SaveSession();
        _hwndSource?.RemoveHook(HwndMessageHook);
        _mediaControlsManager?.Dispose();
        _viewModel.Dispose();
    }

    // Titlebar Window Drag
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BtnMaximize_Click(sender, e);
        }
        else
        {
            DragMove();
        }
    }

    // Window Control Buttons
    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // Interactive Column Header Sorting
    private void SortTitle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ApplySort("Title");
    }

    private void SortAlbum_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ApplySort("Album");
    }

    private void SortFormat_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.ApplySort("Format");
    }

    // Double click lagu untuk memutar instan
    private void TrackListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox listBox && listBox.SelectedItem is Track track)
        {
            _viewModel.PlaySelectedTrackCommand.Execute(track);
        }
    }

    // Right-click otomatis menyorot ListBoxItem sebelum ContextMenu terbuka
    private void OnListBoxItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    // Scrubber Slider Dragging Handlers
    private void Slider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _viewModel.OnSliderDragStarted();
    }

    private void Slider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider)
        {
            _viewModel.OnSliderDragCompleted(slider.Value);
        }
    }

    // Signal Path Modal / Dialog Trigger
    private void SignalPathPill_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var dialog = new AuraAudio.UI.Views.SignalPathDialog(_viewModel)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SignalPathDialog Error] {ex.Message}");
        }
    }

    // Trigger Inspeksi Keaslian dari Spec Pill di Player Bar
    private void SpecPill_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.CurrentTrack != null)
        {
            vm.InspectTrackCommand.Execute(vm.CurrentTrack);
        }
    }

    // Trigger Konfigurasi DAC dari Hardware Endpoint Card di Sidebar
    private void HardwareCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        OpenHardwareConfigDialog();
    }

    private void OpenHardwareConfigDialog()
    {
        try
        {
            var configVm = new AuraAudio.UI.ViewModels.HardwareConfigViewModel(_viewModel.Controller, _viewModel.Database);
            var dialog = new AuraAudio.UI.Views.HardwareConfigDialog(configVm)
            {
                Owner = this
            };
            dialog.ShowDialog();
            _viewModel.RefreshSidebarDriverStatus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HardwareConfigDialog Error] {ex.Message}");
        }
    }

    private void OpenLibraryManagerDialog()
    {
        try
        {
            var dialog = new AuraAudio.UI.Views.LibraryManagerDialog(_viewModel.Database, _viewModel)
            {
                Owner = this
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LibraryManagerDialog Error] {ex.Message}");
        }
    }

    // =========================================================================
    // WINDOWS MESSAGES HOOK (WM_APPCOMMAND) UNTUK TOMBOL HARDWARE MEDIA KEYBOARD
    // =========================================================================
    private const int WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_VOLUME_MUTE = 8;
    private const int APPCOMMAND_VOLUME_DOWN = 9;
    private const int APPCOMMAND_VOLUME_UP = 10;
    private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    private const int APPCOMMAND_MEDIA_STOP = 13;
    private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
    private const int APPCOMMAND_MEDIA_PLAY = 46;
    private const int APPCOMMAND_MEDIA_PAUSE = 47;

    private IntPtr HwndMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APPCOMMAND)
        {
            int cmd = (int)((long)lParam >> 16) & ~0xF000;
            switch (cmd)
            {
                case APPCOMMAND_MEDIA_PLAY_PAUSE:
                    _viewModel.TogglePlayPauseCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_PLAY:
                    _viewModel.Controller.Play();
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_PAUSE:
                    _viewModel.Controller.Pause();
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_NEXTTRACK:
                    _viewModel.NextTrackCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_PREVIOUSTRACK:
                    _viewModel.PreviousTrackCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_MEDIA_STOP:
                    _viewModel.Controller.Stop();
                    handled = true;
                    break;
                case APPCOMMAND_VOLUME_MUTE:
                    _viewModel.ToggleMuteCommand.Execute(null);
                    handled = true;
                    break;
                case APPCOMMAND_VOLUME_UP:
                    _viewModel.Volume = Math.Min(1.0f, _viewModel.Volume + 0.05f);
                    handled = true;
                    break;
                case APPCOMMAND_VOLUME_DOWN:
                    _viewModel.Volume = Math.Max(0.0f, _viewModel.Volume - 0.05f);
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    // =========================================================================
    // IN-APP GLOBAL KEYBOARD SHORTCUTS (AUDIOPHILE STANDARD)
    // =========================================================================
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Jangan interupsi jika pengguna sedang mengetik di input pencarian
        if (e.OriginalSource is TextBox textBox)
        {
            if (e.Key == Key.Escape)
            {
                textBox.Text = string.Empty;
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            return;
        }

        bool isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.TogglePlayPauseCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Left:
                if (isCtrl)
                {
                    _viewModel.PreviousTrackCommand.Execute(null);
                }
                else
                {
                    // Lompat mundur 5 detik
                    var target = _viewModel.CurrentTime - TimeSpan.FromSeconds(5);
                    if (target < TimeSpan.Zero) target = TimeSpan.Zero;
                    _viewModel.Controller.Seek(target);
                }
                e.Handled = true;
                break;

            case Key.Right:
                if (isCtrl)
                {
                    _viewModel.NextTrackCommand.Execute(null);
                }
                else
                {
                    // Lompat maju 5 detik
                    var target = _viewModel.CurrentTime + TimeSpan.FromSeconds(5);
                    if (target > _viewModel.TotalTime) target = _viewModel.TotalTime;
                    _viewModel.Controller.Seek(target);
                }
                e.Handled = true;
                break;

            case Key.Up:
                _viewModel.Volume = Math.Min(1.0f, _viewModel.Volume + 0.05f);
                e.Handled = true;
                break;

            case Key.Down:
                _viewModel.Volume = Math.Max(0.0f, _viewModel.Volume - 0.05f);
                e.Handled = true;
                break;

            case Key.M:
                _viewModel.ToggleMuteCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.S:
                _viewModel.ToggleShuffleCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.R:
                _viewModel.ToggleRepeatCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.I:
                _viewModel.NavigateCommand.Execute("Inspector");
                e.Handled = true;
                break;

            case Key.Q:
                _viewModel.NavigateCommand.Execute("Queue");
                e.Handled = true;
                break;

            case Key.H:
                OpenHardwareConfigDialog();
                e.Handled = true;
                break;

            case Key.P when isCtrl:
                OpenHardwareConfigDialog();
                e.Handled = true;
                break;

            case Key.F when isCtrl:
                SearchTextBox?.Focus();
                SearchTextBox?.SelectAll();
                e.Handled = true;
                break;

            case Key.Escape:
                if (!string.IsNullOrEmpty(_viewModel.SearchQuery))
                {
                    _viewModel.SearchQuery = string.Empty;
                    e.Handled = true;
                }
                else if (_viewModel.CurrentView != "Library")
                {
                    _viewModel.CurrentView = "Library";
                    e.Handled = true;
                }
                break;
        }
    }
}
