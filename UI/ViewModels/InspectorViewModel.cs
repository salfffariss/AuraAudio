using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Audio.Controller;
using AuraAudio.Core.Models;

namespace AuraAudio.UI.ViewModels;

/// <summary>
/// Single source of truth for the Audio Authenticity Inspector state.
///
/// Tracking &amp; External File Mode Rules:
/// 1. Follow Current Track mode (<see cref="IsInspectingExternalFile"/> == false, default):
///    - Subscribes to <see cref="IPlaybackTrackSource.CurrentTrackChanged"/> on creation and unsubscribes on <see cref="Dispose"/>.
///    - On every current-track change (whether the Inspector view is active or inactive) and on every page activation
///      (<see cref="OnActivated"/>), synchronizes <see cref="InspectedTrack"/> to <see cref="IPlaybackTrackSource.CurrentTrack"/>.
///    - Displays a cached <see cref="AudioIntegrityReport"/> ONLY when both the normalized file path AND
///      <see cref="AnalyzerVersion"/> match exactly; otherwise shows the empty "not analyzed" state.
///    - Any in-flight analysis whose track was superseded before completion is discarded via monotonic request ID and cancellation token.
/// 2. External File mode (<see cref="IsInspectingExternalFile"/> == true):
///    - Entered explicitly via <see cref="InspectExternalFileAsync"/> ("Open External File...") or <see cref="ReportExternalFileError"/>.
///    - Stays pinned across playback track changes and Inspector re-activations until the user explicitly returns to the current track
///      via <see cref="AnalyzeCurrentTrackAsync"/> ("Analyze Current Track") or <see cref="ReturnToCurrentTrack"/>.
/// </summary>
public sealed class InspectorViewModel : ViewModelBase, IDisposable
{
    public delegate Task<AudioIntegrityReport> AnalyzeTrackDelegate(
        string filePath,
        AudioStreamSpec? hardwareSpec,
        bool hasActiveHardwarePath,
        bool isHardwareExclusive,
        AudioDriverType configuredDriverType,
        CancellationToken cancellationToken);

    private readonly record struct AnalysisCacheKey(string NormalizedPath, string AnalyzerVersion);

    private const int MaxCachedReports = 128;

    private readonly IPlaybackTrackSource _trackSource;
    private readonly AnalyzeTrackDelegate _analyzer;
    private readonly Func<string, bool> _fileExists;
    private readonly Action<Action> _marshalToUi;
    private readonly Action<Track, AudioStreamSpec>? _onStreamSpecCorrected;
    private readonly string _analyzerVersion;

    private readonly Dictionary<AnalysisCacheKey, AudioIntegrityReport> _reportCache = new();
    private readonly Queue<AnalysisCacheKey> _cacheInsertionOrder = new();

    private Track? _inspectedTrack;
    private AudioIntegrityReport? _currentIntegrityReport;
    private bool _isAnalyzing;
    private string _analysisErrorMessage = string.Empty;
    private bool _isInspectingExternalFile;

    private long _requestId;
    private CancellationTokenSource? _activeCts;
    private bool _isDisposed;

    public InspectorViewModel(
        IPlaybackTrackSource trackSource,
        AnalyzeTrackDelegate? analyzer = null,
        Func<string, bool>? fileExists = null,
        Action<Action>? marshalToUi = null,
        Action<Track, AudioStreamSpec>? onStreamSpecCorrected = null,
        string analyzerVersion = NyquistSpectrumAnalyzer.AnalyzerVersion)
    {
        _trackSource = trackSource ?? throw new ArgumentNullException(nameof(trackSource));
        _analyzer = analyzer ?? NyquistSpectrumAnalyzer.AnalyzeAsync;
        _fileExists = fileExists ?? File.Exists;
        _marshalToUi = marshalToUi ?? DefaultMarshalToUi;
        _onStreamSpecCorrected = onStreamSpecCorrected;
        _analyzerVersion = string.IsNullOrWhiteSpace(analyzerVersion)
            ? NyquistSpectrumAnalyzer.AnalyzerVersion
            : analyzerVersion;

        _trackSource.CurrentTrackChanged += OnTrackSourceCurrentTrackChanged;
        SyncWithTrackSourceCore(_trackSource.CurrentTrack);
    }

    public string AnalyzerVersion => _analyzerVersion;

    public bool IsInspectingExternalFile
    {
        get => _isInspectingExternalFile;
        private set => SetProperty(ref _isInspectingExternalFile, value);
    }

    public Track? InspectedTrack
    {
        get => _inspectedTrack;
        set => SetProperty(ref _inspectedTrack, value);
    }

    public AudioIntegrityReport? CurrentIntegrityReport
    {
        get => _currentIntegrityReport;
        set
        {
            if (SetProperty(ref _currentIntegrityReport, value))
            {
                OnPropertyChanged(nameof(HasActiveIntegrityReport));
                OnPropertyChanged(nameof(IsInspectorEmpty));
            }
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(HasAnalysisError));
                OnPropertyChanged(nameof(HasActiveIntegrityReport));
                OnPropertyChanged(nameof(IsInspectorEmpty));
            }
        }
    }

    public string AnalysisErrorMessage
    {
        get => _analysisErrorMessage;
        set
        {
            if (SetProperty(ref _analysisErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasAnalysisError));
                OnPropertyChanged(nameof(HasActiveIntegrityReport));
                OnPropertyChanged(nameof(IsInspectorEmpty));
            }
        }
    }

    public bool HasAnalysisError => !IsAnalyzing && !string.IsNullOrEmpty(_analysisErrorMessage);
    public bool HasActiveIntegrityReport => !IsAnalyzing && _currentIntegrityReport != null && string.IsNullOrEmpty(_analysisErrorMessage);
    public bool IsInspectorEmpty => !IsAnalyzing && _currentIntegrityReport == null && string.IsNullOrEmpty(_analysisErrorMessage);

    /// <summary>
    /// Refreshes Inspector state when the Inspector page becomes active.
    /// In Follow Current Track mode, synchronizes header, format chip, and cached/empty state to the player's current track.
    /// In External File mode, preserves the pinned external file until the user returns to the current track.
    /// </summary>
    public void OnActivated()
    {
        if (_isDisposed || _isInspectingExternalFile)
        {
            return;
        }

        SyncWithTrackSourceCore(_trackSource.CurrentTrack);
    }

    /// <summary>
    /// Explicitly exits External File mode and synchronizes back to the player's current track.
    /// </summary>
    public void ReturnToCurrentTrack()
    {
        if (_isDisposed)
        {
            return;
        }

        IsInspectingExternalFile = false;
        InvalidateInFlightRequests();
        IsAnalyzing = false;
        SyncWithTrackSourceCore(_trackSource.CurrentTrack);
    }

    /// <summary>
    /// Resolves the player's current track at click time (never from a stale cached field),
    /// exits External File mode, and analyzes that track.
    /// </summary>
    public Task AnalyzeCurrentTrackAsync()
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        IsInspectingExternalFile = false;
        var current = _trackSource.CurrentTrack;
        if (current == null)
        {
            InvalidateInFlightRequests();
            InspectedTrack = null;
            CurrentIntegrityReport = null;
            AnalysisErrorMessage = string.Empty;
            IsAnalyzing = false;
            return Task.CompletedTask;
        }

        return AnalyzeTargetInternalAsync(current, externalMode: false);
    }

    /// <summary>
    /// Explicitly enters External File mode and analyzes the chosen external file.
    /// Remains pinned across playback track changes until the user returns to the current track.
    /// </summary>
    public Task InspectExternalFileAsync(Track externalTrack)
    {
        ArgumentNullException.ThrowIfNull(externalTrack);
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        return AnalyzeTargetInternalAsync(externalTrack, externalMode: true);
    }

    /// <summary>
    /// Records an external-file metadata read failure while staying in explicit External File mode.
    /// </summary>
    public void ReportExternalFileError(string message)
    {
        if (_isDisposed)
        {
            return;
        }

        InvalidateInFlightRequests();
        IsInspectingExternalFile = true;
        IsAnalyzing = false;
        CurrentIntegrityReport = null;
        AnalysisErrorMessage = message ?? string.Empty;
    }

    /// <summary>
    /// Inspects a specific track (for example from a library context menu or Get Info sheet)
    /// without pinning External File mode, so subsequent playback track changes continue following the player.
    /// </summary>
    public Task InspectTrackAsync(Track? track)
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        if (track == null)
        {
            return AnalyzeCurrentTrackAsync();
        }

        return AnalyzeTargetInternalAsync(track, externalMode: false);
    }

    /// <summary>
    /// Prepares preview state for the Get Info modal sheet without entering External File mode.
    /// Shows an exact cached report if available for (path, analyzerVersion); otherwise clears stale report state.
    /// </summary>
    public void PrepareForTrackPreview(Track? target)
    {
        if (_isDisposed)
        {
            return;
        }

        InvalidateInFlightRequests();
        IsInspectingExternalFile = false;
        IsAnalyzing = false;
        ApplyTrackAndCacheState(target);
    }

    /// <summary>
    /// Accepts a report produced outside an explicit Inspector click (e.g., background snapshot)
    /// only when following the current track, idle, and the report matches both normalized path and analyzer version.
    /// </summary>
    public bool TryAcceptExternalReport(AudioIntegrityReport? report)
    {
        if (_isDisposed || report == null || string.IsNullOrWhiteSpace(report.AnalyzedFilePath))
        {
            return false;
        }

        if (!string.Equals(report.AnalyzerVersion, _analyzerVersion, StringComparison.Ordinal))
        {
            return false;
        }

        string reportPath = NormalizePath(report.AnalyzedFilePath);
        StoreInCache(reportPath, report);

        if (_isInspectingExternalFile || IsAnalyzing)
        {
            return false;
        }

        string currentPath = NormalizePath(_trackSource.CurrentTrack?.FilePath);
        string inspectedPath = NormalizePath(_inspectedTrack?.FilePath);
        if (string.IsNullOrEmpty(currentPath) ||
            !string.Equals(reportPath, currentPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(inspectedPath, currentPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AnalysisErrorMessage = string.Empty;
        CurrentIntegrityReport = report;
        return true;
    }

    /// <summary>
    /// Seeds a cached report for a specific track path and version (used by tests or external cache loaders).
    /// </summary>
    public void SeedCachedReport(string filePath, AudioIntegrityReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(report);

        string version = string.IsNullOrWhiteSpace(report.AnalyzerVersion) ? _analyzerVersion : report.AnalyzerVersion;
        string normalized = NormalizePath(filePath);
        var tagged = report with
        {
            AnalyzedFilePath = filePath,
            AnalyzerVersion = version
        };
        StoreInCache(normalized, tagged);
    }

    /// <summary>
    /// Refreshes the hardware output path metadata on the active report when device/driver state changes.
    /// </summary>
    public void RefreshHardwareAuditContext()
    {
        if (_isDisposed || _currentIntegrityReport == null || _inspectedTrack == null)
        {
            return;
        }

        var (hwSpec, hasActivePath, isExclusive, driverType) = _trackSource.GetHardwareAuditContext(_inspectedTrack.FilePath);
        var targetHwSpec = hwSpec ?? _currentIntegrityReport.SourceSpec;
        if (_currentIntegrityReport.HasActiveHardwarePath != hasActivePath ||
            _currentIntegrityReport.IsHardwareExclusive != isExclusive ||
            _currentIntegrityReport.ConfiguredDriverType != driverType ||
            (hasActivePath && _currentIntegrityReport.HardwareSpec != targetHwSpec))
        {
            var updated = _currentIntegrityReport with
            {
                HardwareSpec = targetHwSpec,
                HasActiveHardwarePath = hasActivePath,
                IsHardwareExclusive = isExclusive,
                ConfiguredDriverType = driverType
            };
            CurrentIntegrityReport = updated;

            if (!string.IsNullOrWhiteSpace(updated.AnalyzedFilePath) &&
                string.Equals(updated.AnalyzerVersion, _analyzerVersion, StringComparison.Ordinal))
            {
                StoreInCache(NormalizePath(updated.AnalyzedFilePath), updated);
            }
        }
    }

    private void OnTrackSourceCurrentTrackChanged(object? sender, Track? _)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isInspectingExternalFile)
        {
            return;
        }

        long changeId = InvalidateInFlightRequests();

        _marshalToUi(() =>
        {
            if (_isDisposed || _isInspectingExternalFile)
            {
                return;
            }

            if (Volatile.Read(ref _requestId) != changeId)
            {
                return;
            }

            IsAnalyzing = false;
            SyncWithTrackSourceCore(_trackSource.CurrentTrack);
        });
    }

    private void SyncWithTrackSourceCore(Track? currentTrack)
    {
        bool trackChanged = !PathsEqual(_inspectedTrack?.FilePath, currentTrack?.FilePath);
        if (trackChanged)
        {
            InvalidateInFlightRequests();
            IsAnalyzing = false;
            ApplyTrackAndCacheState(currentTrack);
            return;
        }

        // Same file path: still refresh Track object metadata (e.g. corrected StreamSpec) and ensure report matches cache/version.
        InspectedTrack = currentTrack;
        if (currentTrack == null)
        {
            CurrentIntegrityReport = null;
            AnalysisErrorMessage = string.Empty;
            return;
        }

        if (_currentIntegrityReport != null && !IsReportValidForTrack(_currentIntegrityReport, currentTrack.FilePath))
        {
            CurrentIntegrityReport = TryGetCachedReport(currentTrack.FilePath);
            AnalysisErrorMessage = string.Empty;
        }
        else if (_currentIntegrityReport == null && !IsAnalyzing && string.IsNullOrEmpty(_analysisErrorMessage))
        {
            var cached = TryGetCachedReport(currentTrack.FilePath);
            if (cached != null)
            {
                CurrentIntegrityReport = cached;
            }
        }
    }

    private void ApplyTrackAndCacheState(Track? target)
    {
        InspectedTrack = target;
        AnalysisErrorMessage = string.Empty;
        CurrentIntegrityReport = target != null ? TryGetCachedReport(target.FilePath) : null;
    }

    private async Task AnalyzeTargetInternalAsync(Track target, bool externalMode)
    {
        bool isEn = AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage == "en";

        long requestId = InvalidateInFlightRequests();
        IsInspectingExternalFile = externalMode;
        InspectedTrack = target;

        if (string.IsNullOrWhiteSpace(target.FilePath) || !_fileExists(target.FilePath))
        {
            CurrentIntegrityReport = null;
            IsAnalyzing = false;
            AnalysisErrorMessage = isEn
                ? $"Audio file could not be found: {target.FilePath}"
                : $"Berkas audio tidak ditemukan di penyimpanan: {target.FilePath}";
            return;
        }

        var localCts = new CancellationTokenSource();
        var previousCts = Interlocked.Exchange(ref _activeCts, localCts);
        if (previousCts != null)
        {
            try { previousCts.Cancel(); } catch (ObjectDisposedException) { }
        }

        // If another request superseded us while setting up, cancel immediately.
        if (Volatile.Read(ref _requestId) != requestId || _isDisposed)
        {
            Interlocked.CompareExchange(ref _activeCts, null, localCts);
            localCts.Dispose();
            return;
        }

        AnalysisErrorMessage = string.Empty;
        IsAnalyzing = true;

        try
        {
            var (hwSpec, hasActivePath, isExclusive, driverType) = _trackSource.GetHardwareAuditContext(target.FilePath);
            var report = await _analyzer(
                target.FilePath,
                hwSpec,
                hasActivePath,
                isExclusive,
                driverType,
                localCts.Token);

            // Discard stale result if track changed, a newer request started, or VM was disposed.
            if (_isDisposed ||
                Volatile.Read(ref _requestId) != requestId ||
                localCts.IsCancellationRequested ||
                !PathsEqual(_inspectedTrack?.FilePath, target.FilePath))
            {
                return;
            }

            var stampedReport = report with
            {
                AnalyzedFilePath = target.FilePath,
                AnalyzerVersion = string.IsNullOrWhiteSpace(report.AnalyzerVersion)
                    ? _analyzerVersion
                    : report.AnalyzerVersion
            };

            if (stampedReport.SourceSpec != target.StreamSpec)
            {
                var updated = target with { StreamSpec = stampedReport.SourceSpec };
                InspectedTrack = updated;
                _onStreamSpecCorrected?.Invoke(updated, stampedReport.SourceSpec);
            }

            StoreInCache(NormalizePath(target.FilePath), stampedReport);
            CurrentIntegrityReport = stampedReport;
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation on track switch or newer request.
        }
        catch (Exception ex)
        {
            if (!_isDisposed &&
                Volatile.Read(ref _requestId) == requestId &&
                !localCts.IsCancellationRequested &&
                PathsEqual(_inspectedTrack?.FilePath, target.FilePath))
            {
                CurrentIntegrityReport = null;
                AnalysisErrorMessage = isEn
                    ? $"Unable to analyze this audio file: {ex.Message}"
                    : $"Gagal menganalisis berkas audio ini: {ex.Message}";
            }
            Debug.WriteLine($"Nyquist FFT analysis error: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeCts, null, localCts);
            localCts.Dispose();

            if (!_isDisposed && Volatile.Read(ref _requestId) == requestId)
            {
                IsAnalyzing = false;
            }
        }
    }

    private long InvalidateInFlightRequests()
    {
        long nextId = Interlocked.Increment(ref _requestId);
        var previous = Interlocked.Exchange(ref _activeCts, null);
        if (previous != null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }
        return nextId;
    }

    private bool IsReportValidForTrack(AudioIntegrityReport report, string? trackFilePath)
    {
        if (string.IsNullOrWhiteSpace(report.AnalyzedFilePath) || string.IsNullOrWhiteSpace(trackFilePath))
        {
            return false;
        }

        return PathsEqual(report.AnalyzedFilePath, trackFilePath) &&
               string.Equals(report.AnalyzerVersion, _analyzerVersion, StringComparison.Ordinal);
    }

    private AudioIntegrityReport? TryGetCachedReport(string? filePath)
    {
        string normalized = NormalizePath(filePath);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        var key = new AnalysisCacheKey(normalized, _analyzerVersion);
        return _reportCache.TryGetValue(key, out var cached) ? cached : null;
    }

    private void StoreInCache(string normalizedPath, AudioIntegrityReport report)
    {
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return;
        }

        string version = string.IsNullOrWhiteSpace(report.AnalyzerVersion) ? _analyzerVersion : report.AnalyzerVersion;
        var key = new AnalysisCacheKey(normalizedPath, version);
        if (!_reportCache.ContainsKey(key))
        {
            _cacheInsertionOrder.Enqueue(key);
        }
        _reportCache[key] = report;

        while (_reportCache.Count > MaxCachedReports && _cacheInsertionOrder.Count > 0)
        {
            var oldest = _cacheInsertionOrder.Dequeue();
            _reportCache.Remove(oldest);
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(filePath.Trim()).ToUpperInvariant();
        }
        catch
        {
            return filePath.Trim().ToUpperInvariant();
        }
    }

    private static void DefaultMarshalToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _trackSource.CurrentTrackChanged -= OnTrackSourceCurrentTrackChanged;
        InvalidateInFlightRequests();
    }
}
