using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AuraAudio.Database;
using AuraAudio.Scanner;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Dialog interaktif untuk mengelola direktori musik terpantau (ala MusicBee),
/// konfigurasi lokasi ekspor manual, pemindaian ulang, dan pembersihan database (Purge).
/// </summary>
public partial class LibraryManagerDialog : Window
{
    private readonly DatabaseManager _db;
    private readonly MainViewModel _mainViewModel;
    private readonly ObservableCollection<string> _folders = new();
    private readonly ObservableCollection<string> _excludedFolders = new();
    private CancellationTokenSource? _scanCts;
    private bool _isScanning = false;

    public LibraryManagerDialog(DatabaseManager db, MainViewModel mainViewModel)
    {
        InitializeComponent();
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));

        FoldersListBox.ItemsSource = _folders;
        ExcludedListBox.ItemsSource = _excludedFolders;
        LoadSettings();

        // Dukungan drag-and-drop folder dari Windows Explorer ke dialog
        AllowDrop = true;
        Drop += OnWindowDrop;
        DragOver += OnWindowDragOver;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private void LoadSettings()
    {
        _folders.Clear();
        var savedFolders = _db.GetLibraryFolders();
        foreach (var folder in savedFolders)
        {
            _folders.Add(folder);
        }

        _excludedFolders.Clear();
        var savedExcluded = _db.GetExcludedFolders();
        foreach (var ex in savedExcluded)
        {
            _excludedFolders.Add(ex);
        }

        AutoScanCheckBox.IsChecked = _db.GetAutoScanOnStartup();
        ExportDirectoryTextBox.Text = _db.GetExportDirectory();

        long trackCount = _db.GetTrackCount();
        StatusTextBlock.Text = $"Status: {trackCount} lagu terdaftar di perpustakaan.";
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Pilih Direktori Musik untuk Dipantau (AuraAudio)",
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true)
            {
                AddFolderInternal(dialog.FolderName);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Gagal membuka pemilih folder: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddFolderInternal(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        string normalized = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!_folders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _folders.Add(normalized);
            _db.AddLibraryFolder(normalized);
            StatusTextBlock.Text = $"Folder ditambahkan: {Path.GetFileName(normalized)}";
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersListBox.SelectedItem is string selectedFolder)
        {
            _folders.Remove(selectedFolder);
            _db.RemoveLibraryFolder(selectedFolder);
            StatusTextBlock.Text = $"Folder dihapus dari pantauan: {Path.GetFileName(selectedFolder)}";
        }
        else
        {
            MessageBox.Show(this, "Silakan pilih salah satu folder dalam daftar terlebih dahulu.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AddExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Pilih Folder / Subfolder yang Ingin Dikecualikan (Abaikan)",
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true)
            {
                string path = Path.GetFullPath(dialog.FolderName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!_excludedFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    _excludedFolders.Add(path);
                    _db.AddExcludedFolder(path);
                    StatusTextBlock.Text = $"Folder dikecualikan: {Path.GetFileName(path)}";
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Gagal memilih folder pengecualian: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveExcludedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ExcludedListBox.SelectedItem is string selectedFolder)
        {
            _excludedFolders.Remove(selectedFolder);
            _db.RemoveExcludedFolder(selectedFolder);
            StatusTextBlock.Text = $"Pengecualian dihapus: {Path.GetFileName(selectedFolder)}";
        }
        else
        {
            MessageBox.Show(this, "Pilih folder yang ingin dihapus dari daftar pengecualian.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BrowseExportDirectory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Pilih Lokasi Penyimpanan Ekspor Musik",
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true)
            {
                string chosen = Path.GetFullPath(dialog.FolderName);
                ExportDirectoryTextBox.Text = chosen;
                _db.SetExportDirectory(chosen);
                StatusTextBlock.Text = "Direktori ekspor berhasil diperbarui.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Gagal memilih folder ekspor: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RescanLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_isScanning)
        {
            _scanCts?.Cancel();
            return;
        }

        var monitored = _folders.ToList();
        if (monitored.Count == 0)
        {
            MessageBox.Show(this, "Tambahkan minimal satu folder musik ke dalam daftar pantauan sebelum memindai.", "Peringatan", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _isScanning = true;
        _scanCts = new CancellationTokenSource();
        SetControlsBusy(true);
        RescanButtonLabel.Text = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en"
            ? "Cancel Scan"
            : "Batalkan Pemindaian";
        ScanProgressBar.IsIndeterminate = false;
        ScanProgressBar.Value = 0;

        var progress = new Progress<ScanProgress>(p =>
        {
            Dispatcher.Invoke(() =>
            {
                if (p.TotalFound > 0)
                {
                    ScanProgressBar.Maximum = p.TotalFound;
                    ScanProgressBar.Value = p.Processed;
                }
                StatusTextBlock.Text = $"Memindai: {p.Processed}/{p.TotalFound} lagu ({p.Added} baru, {p.Skipped} terlewati)...";
            });
        });

        try
        {
            var scanner = new LibraryScanner(_db);
            var result = await Task.Run(() => scanner.ScanDirectoriesAsync(monitored, progress, _scanCts.Token));

            StatusTextBlock.Text = $"Selesai! {result.TotalFound} berkas diproses ({result.Added} baru) dalam {result.Elapsed.TotalSeconds:F1}s.";
            _mainViewModel.LoadLibrary();
            _mainViewModel.LoadAlbums();
            _mainViewModel.LoadArtists();
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Pemindaian dibatalkan oleh pengguna.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Kesalahan pemindaian: {ex.Message}";
        }
        finally
        {
            _isScanning = false;
            SetControlsBusy(false);
            RescanButtonLabel.SetResourceReference(System.Windows.Controls.TextBlock.TextProperty, "Str_Fld_RescanBtn");
            ScanProgressBar.Value = 0;
        }
    }

    private void PurgeLibrary_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Apakah Anda yakin ingin mengosongkan seluruh database perpustakaan lagu?\n\n" +
            "Tindakan ini akan menghapus semua metadata lagu yang terindeks (termasuk file suara game yang tidak diinginkan). Berkas musik asli di disk Anda TIDAK akan dihapus.\n\n" +
            "Setelah dibersihkan, Anda dapat memindai ulang hanya folder lagu pilihan Anda.",
            "Konfirmasi Pembersihan Database Pustaka",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            try
            {
                _mainViewModel.Controller.Stop();
                _db.ClearLibrary();
                _mainViewModel.LoadLibrary();
                _mainViewModel.LoadAlbums();
                _mainViewModel.LoadArtists();
                _mainViewModel.RefreshQueue();

                StatusTextBlock.Text = "Database perpustakaan berhasil dikosongkan total (0 lagu).";
                MessageBox.Show(this, "Seluruh metadata database berhasil dibersihkan! Anda sekarang dapat menambahkan folder musik yang diinginkan dan melakukan pemindaian bersih.", "Pustaka Dibersihkan", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Gagal membersihkan database: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void SetControlsBusy(bool isBusy)
    {
        PurgeButton.IsEnabled = !isBusy;
        FoldersListBox.IsEnabled = !isBusy;
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var item in dropped)
            {
                if (Directory.Exists(item))
                {
                    AddFolderInternal(item);
                }
            }
        }
    }

    private void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        _db.SetAutoScanOnStartup(AutoScanCheckBox.IsChecked == true);
        if (!string.IsNullOrWhiteSpace(ExportDirectoryTextBox.Text))
        {
            _db.SetExportDirectory(ExportDirectoryTextBox.Text);
        }
        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }
}
