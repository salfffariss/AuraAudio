using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AuraAudio.Audio.Controller;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Scanner;
using AuraAudio.UI.Helpers;
using Microsoft.Win32;

namespace AuraAudio.UI.ViewModels;

/// <summary>
/// Child ViewModel for the unified v2.0.0 Settings screen (03_design_spec.md Part B.12 & D.6).
/// Composes HardwareConfigViewModel, LibraryScanner, DatabaseManager folder/setting APIs,
/// LocalizationManager, and Accessibility Reduced Motion override.
/// </summary>
public class SettingsViewModel : ViewModelBase
{
    private readonly AudiophilePlaybackController _controller;
    private readonly DatabaseManager _db;
    private readonly Action? _onLibraryMutated;
    private readonly Action? _onAudioApplied;
    private CancellationTokenSource? _scanCts;

    private string _activeCategory = "Root";
    private bool _isScanning;
    private string _scanStatusText = string.Empty;
    private double _scanProgressPercent;
    private bool _autoScanOnStartup;
    private string _exportDirectory = string.Empty;
    private string _selectedLanguage;
    private bool _forceReducedMotion;
    private string _engineStatsSummary = string.Empty;

    public HardwareConfigViewModel HardwareConfig { get; }
    public ObservableCollection<string> Breadcrumbs { get; } = new() { "Settings" };
    public ObservableCollection<string> MonitoredFolders { get; } = new();
    public ObservableCollection<string> ExcludedFolders { get; } = new();

    public string ActiveCategory
    {
        get => _activeCategory;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? "Root" : value;
            if (SetProperty(ref _activeCategory, normalized))
            {
                RebuildBreadcrumbs();
                NotifyCategoryFlags();
                if (normalized == "About")
                {
                    RefreshEngineStats();
                }
            }
        }
    }

    public bool IsRootView => _activeCategory.Equals("Root", StringComparison.OrdinalIgnoreCase);
    public bool HasActiveSubCategory => !IsRootView;
    public bool IsGeneralView => _activeCategory.Equals("General", StringComparison.OrdinalIgnoreCase);
    public bool IsPlaybackView => _activeCategory.Equals("Playback", StringComparison.OrdinalIgnoreCase);
    public bool IsLibraryView => _activeCategory.Equals("Library", StringComparison.OrdinalIgnoreCase);
    public bool IsAccessibilityView => _activeCategory.Equals("Accessibility", StringComparison.OrdinalIgnoreCase);
    public bool IsAboutView => _activeCategory.Equals("About", StringComparison.OrdinalIgnoreCase);

    public string ActiveCategoryTitle => _activeCategory switch
    {
        "General" => "General",
        "Playback" => "Playback & DAC",
        "Library" => "Library & Files",
        "Accessibility" => "Accessibility",
        "About" => "About AuraAudio",
        _ => "Settings"
    };

    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value);
    }

    public string ScanStatusText
    {
        get => _scanStatusText;
        set
        {
            if (SetProperty(ref _scanStatusText, value))
            {
                OnPropertyChanged(nameof(HasScanStatus));
            }
        }
    }

    public bool HasScanStatus => !string.IsNullOrWhiteSpace(_scanStatusText);

    public double ScanProgressPercent
    {
        get => _scanProgressPercent;
        set => SetProperty(ref _scanProgressPercent, value);
    }

    public bool AutoScanOnStartup
    {
        get => _autoScanOnStartup;
        set
        {
            if (SetProperty(ref _autoScanOnStartup, value))
            {
                _db.SetAutoScanOnStartup(value);
            }
        }
    }

    public string ExportDirectory
    {
        get => _exportDirectory;
        set
        {
            if (SetProperty(ref _exportDirectory, value) && !string.IsNullOrWhiteSpace(value))
            {
                _db.SetExportDirectory(value);
            }
        }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (SetProperty(ref _selectedLanguage, value))
            {
                LocalizationManager.SetLanguage(value);
                _db.SetSetting("app_language", value);
                OnPropertyChanged(nameof(IsEnglish));
                OnPropertyChanged(nameof(IsIndonesian));
            }
        }
    }

    public bool IsEnglish => string.Equals(_selectedLanguage, "en", StringComparison.OrdinalIgnoreCase);
    public bool IsIndonesian => !IsEnglish;

    public bool ForceReducedMotion
    {
        get => _forceReducedMotion;
        set
        {
            if (SetProperty(ref _forceReducedMotion, value))
            {
                SpringAnimator.ForceReducedMotionOverride = value ? true : null;
                _db.SetSetting("force_reduced_motion", value ? "1" : "0");
            }
        }
    }

    public string AppVersionText
    {
        get
        {
            var asm = typeof(SettingsViewModel).Assembly;
            string? infoVer = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm)?
                .InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
            {
                int plusIdx = infoVer.IndexOf('+');
                if (plusIdx > 0) infoVer = infoVer[..plusIdx];
            }
            string ver = !string.IsNullOrWhiteSpace(infoVer)
                ? infoVer
                : (asm.GetName().Version?.ToString(3) ?? "2.0.3");
            return $"AuraAudio v{ver} (.NET 9 / WPF Pure Monochrome)";
        }
    }

    public string EngineStatsSummary
    {
        get => _engineStatsSummary;
        private set => SetProperty(ref _engineStatsSummary, value);
    }

    public ICommand NavigateCategoryCommand { get; }
    public ICommand NavigateBreadcrumbCommand { get; }
    public ICommand SetLanguageCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand AddExcludedFolderCommand { get; }
    public ICommand RemoveExcludedFolderCommand { get; }
    public ICommand RescanLibraryCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand ClearLibraryCommand { get; }
    public ICommand PickExportDirectoryCommand { get; }
    public ICommand ApplyPlaybackSettingsCommand { get; }

    public SettingsViewModel(
        AudiophilePlaybackController controller,
        DatabaseManager db,
        Action? onLibraryMutated = null,
        Action? onAudioApplied = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _onLibraryMutated = onLibraryMutated;
        _onAudioApplied = onAudioApplied;

        HardwareConfig = new HardwareConfigViewModel(controller, db);
        _autoScanOnStartup = _db.GetAutoScanOnStartup();
        _exportDirectory = _db.GetExportDirectory();
        _selectedLanguage = LocalizationManager.CurrentLanguage;

        string? savedReduced = _db.GetSetting("force_reduced_motion");
        if (savedReduced == "1")
        {
            _forceReducedMotion = true;
            SpringAnimator.ForceReducedMotionOverride = true;
        }

        NavigateCategoryCommand = new RelayCommand(p =>
        {
            if (p is string cat && !string.IsNullOrWhiteSpace(cat))
            {
                ActiveCategory = cat;
            }
        });

        NavigateBreadcrumbCommand = new RelayCommand(p =>
        {
            if (p is string crumb && crumb.Equals("Settings", StringComparison.OrdinalIgnoreCase))
            {
                ActiveCategory = "Root";
            }
        });

        SetLanguageCommand = new RelayCommand(p =>
        {
            if (p is string lang && !string.IsNullOrWhiteSpace(lang))
            {
                SelectedLanguage = lang;
            }
        });

        AddFolderCommand = new RelayCommand(async () =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Music Folder to Monitor"
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                _db.AddLibraryFolder(dlg.FolderName);
                RefreshFolders();
                await ScanSingleOrAllFoldersAsync(new[] { dlg.FolderName });
            }
        });

        RemoveFolderCommand = new RelayCommand(p =>
        {
            if (p is string folder && !string.IsNullOrWhiteSpace(folder))
            {
                _db.RemoveLibraryFolder(folder);
                RefreshFolders();
            }
        });

        AddExcludedFolderCommand = new RelayCommand(() =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Folder to Exclude from Scanning"
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                _db.AddExcludedFolder(dlg.FolderName);
                RefreshFolders();
            }
        });

        RemoveExcludedFolderCommand = new RelayCommand(p =>
        {
            if (p is string folder && !string.IsNullOrWhiteSpace(folder))
            {
                _db.RemoveExcludedFolder(folder);
                RefreshFolders();
            }
        });

        RescanLibraryCommand = new RelayCommand(async () =>
        {
            RefreshFolders();
            if (MonitoredFolders.Count == 0)
            {
                ScanStatusText = "Add at least one monitored music folder before rescanning.";
                return;
            }
            await ScanSingleOrAllFoldersAsync(MonitoredFolders);
        });

        CancelScanCommand = new RelayCommand(() =>
        {
            _scanCts?.Cancel();
        });

        ClearLibraryCommand = new RelayCommand(() =>
        {
            const string prompt = "Remove all indexed tracks, albums, artists, playlists, and playback history from AuraAudio's database? Your audio files on disk will not be deleted.";
            var owner = Application.Current?.MainWindow is { IsVisible: true } mainWin ? mainWin : null;
            var confirm = owner != null
                ? MessageBox.Show(owner, prompt, "Clear Library Database", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(prompt, "Clear Library Database", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm == MessageBoxResult.Yes)
            {
                _db.ClearLibrary();
                ScanStatusText = "Library database cleared.";
                RefreshEngineStats();
                _onLibraryMutated?.Invoke();
            }
        });

        PickExportDirectoryCommand = new RelayCommand(() =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select Playlist & Export Directory"
            };
            if (Directory.Exists(ExportDirectory))
            {
                dlg.InitialDirectory = ExportDirectory;
            }
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                ExportDirectory = dlg.FolderName;
            }
        });

        ApplyPlaybackSettingsCommand = new RelayCommand(() =>
        {
            HardwareConfig.ApplyCommand.Execute(null);
            RefreshEngineStats();
            _onAudioApplied?.Invoke();
        });

        RefreshFolders();
        RefreshEngineStats();
    }

    public void RefreshFolders()
    {
        MonitoredFolders.Clear();
        foreach (var f in _db.GetLibraryFolders()) MonitoredFolders.Add(f);
        ExcludedFolders.Clear();
        foreach (var f in _db.GetExcludedFolders()) ExcludedFolders.Add(f);
    }

    public void RefreshEngineStats()
    {
        try
        {
            long tracks = _db.GetTrackCount();
            int albums = _db.GetAlbums().Count;
            int artists = _db.GetArtists().Count;
            string dev = _controller.CurrentDevice?.Name ?? "Default Render Endpoint";
            EngineStatsSummary = $"{tracks:N0} tracks · {albums:N0} albums · {artists:N0} artists · {_controller.DriverType} ({_controller.LatencyMs} ms) · {dev}";
        }
        catch
        {
            EngineStatsSummary = AppVersionText;
        }
    }

    private async Task ScanSingleOrAllFoldersAsync(System.Collections.Generic.IEnumerable<string> folders)
    {
        if (IsScanning) return;

        var folderList = new System.Collections.Generic.List<string>(folders);
        if (folderList.Count == 0) return;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        IsScanning = true;
        ScanProgressPercent = 0;
        ScanStatusText = "Scanning audio folders...";

        try
        {
            var scanner = new LibraryScanner(_db);
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanProgressPercent = p.TotalFound > 0
                    ? Math.Clamp(p.Percentage, 0.0, 100.0)
                    : 100.0;
                ScanStatusText = p.TotalFound > 0
                    ? $"Scanning {p.Processed}/{p.TotalFound} ({p.Added} new, {p.Skipped} unchanged) · {p.CurrentFile}"
                    : "Checking directory...";
            });

            var result = await Task.Run(() => scanner.ScanDirectoriesAsync(folderList, progress, ct), ct);
            ScanProgressPercent = 100;
            ScanStatusText = result.Removed > 0
                ? $"Scan complete: {result.Added} indexed, {result.Skipped} unchanged, {result.Removed} removed ({result.Elapsed.TotalSeconds:F1}s)."
                : $"Scan complete: {result.Added} indexed, {result.Skipped} unchanged ({result.Elapsed.TotalSeconds:F1}s).";
            RefreshEngineStats();
            _onLibraryMutated?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "Library scan cancelled.";
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        Breadcrumbs.Add("Settings");
        if (!IsRootView)
        {
            Breadcrumbs.Add(ActiveCategoryTitle);
        }
    }

    private void NotifyCategoryFlags()
    {
        OnPropertyChanged(nameof(IsRootView));
        OnPropertyChanged(nameof(HasActiveSubCategory));
        OnPropertyChanged(nameof(IsGeneralView));
        OnPropertyChanged(nameof(IsPlaybackView));
        OnPropertyChanged(nameof(IsLibraryView));
        OnPropertyChanged(nameof(IsAccessibilityView));
        OnPropertyChanged(nameof(IsAboutView));
        OnPropertyChanged(nameof(ActiveCategoryTitle));
    }
}
