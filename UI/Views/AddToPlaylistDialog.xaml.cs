using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.UI.Helpers;
using AuraAudio.UI.ViewModels;

namespace AuraAudio.UI.Views;

/// <summary>
/// Interaction logic for AddToPlaylistDialog.xaml
/// </summary>
public partial class AddToPlaylistDialog : Window, INotifyPropertyChanged
{
    private readonly DatabaseManager _db;
    private Track _targetTrack;
    private Playlist? _selectedExistingPlaylist;
    private string _newPlaylistName = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Track TargetTrack
    {
        get => _targetTrack;
        set { _targetTrack = value; OnPropertyChanged(); }
    }

    public ObservableCollection<Playlist> Playlists { get; } = new();

    public Playlist? SelectedExistingPlaylist
    {
        get => _selectedExistingPlaylist;
        set { _selectedExistingPlaylist = value; OnPropertyChanged(); }
    }

    public string NewPlaylistName
    {
        get => _newPlaylistName;
        set { _newPlaylistName = value; OnPropertyChanged(); }
    }

    public bool HasPlaylists => Playlists.Count > 0;

    public ICommand CreateAndAddCommand { get; }
    public ICommand AddToSelectedCommand { get; }

    public AddToPlaylistDialog(DatabaseManager db, Track targetTrack)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(targetTrack);

        _db = db;
        _targetTrack = targetTrack;

        InitializeComponent();
        DataContext = this;

        CreateAndAddCommand = new RelayCommand(ExecuteCreateAndAdd);
        AddToSelectedCommand = new RelayCommand(ExecuteAddToSelected);

        LoadPlaylists();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        };
    }

    private void LoadPlaylists()
    {
        Playlists.Clear();
        var list = _db.GetPlaylists();
        foreach (var p in list)
        {
            Playlists.Add(p);
        }
        OnPropertyChanged(nameof(HasPlaylists));
        if (Playlists.Count > 0)
        {
            SelectedExistingPlaylist = Playlists[0];
        }
    }

    private void ExecuteCreateAndAdd()
    {
        string name = NewPlaylistName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("Silakan masukkan nama playlist baru terlebih dahulu.", "Nama Kosong", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _db.CreatePlaylist(name, new[] { _targetTrack.Id });
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Gagal membuat playlist: {ex.Message}", "Kesalahan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExecuteAddToSelected()
    {
        if (SelectedExistingPlaylist == null)
        {
            if (!string.IsNullOrWhiteSpace(NewPlaylistName))
            {
                ExecuteCreateAndAdd();
                return;
            }

            MessageBox.Show("Silakan pilih playlist yang tersedia atau ketik nama playlist baru.", "Pilih Playlist", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _db.AddTrackToPlaylist(SelectedExistingPlaylist.Id, _targetTrack.Id);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Gagal menambahkan lagu ke playlist: {ex.Message}", "Kesalahan", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnPropertyChanged([CallerMemberName] string? propName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
    }
}
