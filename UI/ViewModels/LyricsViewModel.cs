using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using AuraAudio.Core.Models;
using AuraAudio.Metadata;
using AuraAudio.UI.Helpers;

namespace AuraAudio.UI.ViewModels;

public enum LyricsViewState
{
    NoSong,
    Loading,
    NoLyrics,
    StaticLyricsLoaded,
    TimeBasedLyricsLoaded
}

public enum LyricsLineTier
{
    Past,
    Current,
    Future1,
    FutureN
}

public sealed class LyricsLineItem : ViewModelBase
{
    private LyricsLineTier _tier = LyricsLineTier.FutureN;

    public int Index { get; }
    public TimeSpan Timestamp { get; }
    public string Text { get; }
    public bool IsInstrumentalGap { get; }

    public string TimestampText => Timestamp.TotalHours >= 1
        ? Timestamp.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : Timestamp.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    public LyricsLineTier Tier
    {
        get => _tier;
        set
        {
            if (SetProperty(ref _tier, value))
            {
                OnPropertyChanged(nameof(IsCurrent));
                OnPropertyChanged(nameof(IsNext));
                OnPropertyChanged(nameof(IsPast));
            }
        }
    }

    public bool IsCurrent => _tier == LyricsLineTier.Current;
    public bool IsNext => _tier == LyricsLineTier.Future1;
    public bool IsPast => _tier == LyricsLineTier.Past;

    public LyricsLineItem(int index, TimeSpan timestamp, string text, bool isInstrumentalGap)
    {
        Index = index;
        Timestamp = timestamp;
        Text = text;
        IsInstrumentalGap = isInstrumentalGap;
    }
}

/// <summary>
/// Child ViewModel for the v2.0.0 Lyrics right pane, Full-Screen split, and Mini-Player tray
/// (03_design_spec.md Part B.11, D.6 & D.8).
/// Resolves .lrc sidecars, embedded LRC tags, ID3v2 SYLT frames, and plain-text fallback.
/// </summary>
public class LyricsViewModel : ViewModelBase
{
    private readonly Action<double>? _seekSecondsCallback;
    private CancellationTokenSource? _loadCts;
    private string? _loadedTrackPath;

    private LyricsViewState _state = LyricsViewState.NoSong;
    private string _sourceLabel = string.Empty;
    private int _activeLineIndex = -1;
    private string _plainLyricsText = string.Empty;
    private string _writersText = string.Empty;
    private bool _isSynced;

    public LyricsViewState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsNoSong));
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsNoLyrics));
                OnPropertyChanged(nameof(IsStaticLoaded));
                OnPropertyChanged(nameof(IsTimeBasedLoaded));
                OnPropertyChanged(nameof(HasLyrics));
            }
        }
    }

    public bool IsNoSong => _state == LyricsViewState.NoSong;
    public bool IsLoading => _state == LyricsViewState.Loading;
    public bool IsNoLyrics => _state == LyricsViewState.NoLyrics;
    public bool IsStaticLoaded => _state == LyricsViewState.StaticLyricsLoaded;
    public bool IsTimeBasedLoaded => _state == LyricsViewState.TimeBasedLyricsLoaded;
    public bool HasLyrics => _state == LyricsViewState.TimeBasedLyricsLoaded || _state == LyricsViewState.StaticLyricsLoaded;

    public string SourceLabel
    {
        get => _sourceLabel;
        private set
        {
            if (SetProperty(ref _sourceLabel, value))
            {
                OnPropertyChanged(nameof(HasSourceLabel));
            }
        }
    }

    public bool HasSourceLabel => !string.IsNullOrWhiteSpace(_sourceLabel);

    public BulkObservableCollection<LyricsLineItem> Lines { get; } = new();

    public int ActiveLineIndex
    {
        get => _activeLineIndex;
        private set => SetProperty(ref _activeLineIndex, value);
    }

    public string PlainLyricsText
    {
        get => _plainLyricsText;
        private set => SetProperty(ref _plainLyricsText, value);
    }

    public string WritersText
    {
        get => _writersText;
        private set
        {
            if (SetProperty(ref _writersText, value))
            {
                OnPropertyChanged(nameof(HasWriters));
            }
        }
    }

    public bool HasWriters => !string.IsNullOrWhiteSpace(_writersText);

    public bool IsSynced
    {
        get => _isSynced;
        private set => SetProperty(ref _isSynced, value);
    }

    public ICommand SeekToLineCommand { get; }

    public LyricsViewModel(Action<double>? seekSecondsCallback = null)
    {
        _seekSecondsCallback = seekSecondsCallback;
        SeekToLineCommand = new RelayCommand(
            param =>
            {
                if (IsSynced && param is LyricsLineItem line && !line.IsInstrumentalGap)
                {
                    _seekSecondsCallback?.Invoke(line.Timestamp.TotalSeconds);
                    UpdatePlaybackPosition(line.Timestamp);
                }
            },
            param => IsSynced && (param is not LyricsLineItem line || !line.IsInstrumentalGap));
    }

    public async Task LoadForTrackAsync(Track? track)
    {
        CancelPendingLoad();

        if (track == null)
        {
            _loadedTrackPath = null;
            ResetToNoSong();
            return;
        }

        _loadedTrackPath = track.FilePath;
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        SetLoadingState();

        ParsedLyricsResult result;
        try
        {
            string filePath = track.FilePath;
            result = await Task.Run(() => LyricsParser.LoadForFile(filePath), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            result = ParsedLyricsResult.Empty;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        void Commit()
        {
            if (token.IsCancellationRequested || !string.Equals(_loadedTrackPath, track.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            ApplyParsedResultCore(result);
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(Commit);
        }
        else
        {
            Commit();
        }
    }

    /// <summary>
    /// Applies an already-parsed lyrics payload to ViewModel state (also directly testable in unit tests).
    /// Cancels any in-flight LoadForTrackAsync so stale background reads can never overwrite newly imported lyrics.
    /// </summary>
    public void ApplyParsedResult(ParsedLyricsResult? result)
    {
        CancelPendingLoad();
        ApplyParsedResultCore(result);
    }

    private void ApplyParsedResultCore(ParsedLyricsResult? result)
    {
        if (result == null || !result.HasLyrics)
        {
            State = LyricsViewState.NoLyrics;
            SourceLabel = string.Empty;
            PlainLyricsText = string.Empty;
            WritersText = FormatWriters(result?.Writers);
            IsSynced = false;
            ActiveLineIndex = -1;
            Lines.ReplaceAll(Array.Empty<LyricsLineItem>());
            return;
        }

        WritersText = FormatWriters(result.Writers);

        if (result.IsSynced && result.Lines.Count > 0)
        {
            var items = new List<LyricsLineItem>(result.Lines.Count);
            for (int i = 0; i < result.Lines.Count; i++)
            {
                var line = result.Lines[i];
                var vmItem = new LyricsLineItem(i, line.Timestamp, line.Text, line.IsInstrumentalGap)
                {
                    Tier = i == 0 ? LyricsLineTier.Future1 : LyricsLineTier.FutureN
                };
                items.Add(vmItem);
            }

            Lines.ReplaceAll(items);
            PlainLyricsText = result.PlainText;
            IsSynced = true;
            ActiveLineIndex = -1;
            SourceLabel = result.Source switch
            {
                LyricsSourceKind.SidecarLrc => "Synced · LRC",
                LyricsSourceKind.EmbeddedLrc => "Synced · Embedded LRC",
                LyricsSourceKind.Id3v2Sylt => "Synced · SYLT",
                _ => "Synced"
            };
            State = LyricsViewState.TimeBasedLyricsLoaded;
        }
        else
        {
            Lines.ReplaceAll(Array.Empty<LyricsLineItem>());
            PlainLyricsText = result.PlainText;
            IsSynced = false;
            ActiveLineIndex = -1;
            SourceLabel = "Unsynced Lyrics";
            State = LyricsViewState.StaticLyricsLoaded;
        }
    }

    public void UpdatePlaybackPosition(TimeSpan position)
    {
        if (!IsSynced || Lines.Count == 0) return;

        // O(log n) binary search for latest line with Timestamp <= position (zero allocation)
        int low = 0;
        int high = Lines.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            if (Lines[mid].Timestamp <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found != _activeLineIndex)
        {
            ActiveLineIndex = found;
            UpdateLineTiers(found);
        }
    }

    private void UpdateLineTiers(int activeIndex)
    {
        int count = Lines.Count;
        for (int i = 0; i < count; i++)
        {
            LyricsLineTier target;
            if (activeIndex < 0)
            {
                target = i == 0 ? LyricsLineTier.Future1 : LyricsLineTier.FutureN;
            }
            else if (i < activeIndex)
            {
                target = LyricsLineTier.Past;
            }
            else if (i == activeIndex)
            {
                target = LyricsLineTier.Current;
            }
            else if (i == activeIndex + 1)
            {
                target = LyricsLineTier.Future1;
            }
            else
            {
                target = LyricsLineTier.FutureN;
            }

            if (Lines[i].Tier != target)
            {
                Lines[i].Tier = target;
            }
        }
    }

    private void ResetToNoSong()
    {
        State = LyricsViewState.NoSong;
        SourceLabel = string.Empty;
        PlainLyricsText = string.Empty;
        WritersText = string.Empty;
        IsSynced = false;
        ActiveLineIndex = -1;
        Lines.ReplaceAll(Array.Empty<LyricsLineItem>());
    }

    private void SetLoadingState()
    {
        State = LyricsViewState.Loading;
        SourceLabel = string.Empty;
        PlainLyricsText = string.Empty;
        WritersText = string.Empty;
        IsSynced = false;
        ActiveLineIndex = -1;
        Lines.ReplaceAll(Array.Empty<LyricsLineItem>());
    }

    private void CancelPendingLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private static string FormatWriters(string? rawWriters)
    {
        if (string.IsNullOrWhiteSpace(rawWriters) || rawWriters.Trim() == "—")
        {
            return string.Empty;
        }
        return $"Written by: {rawWriters.Trim()}";
    }
}
