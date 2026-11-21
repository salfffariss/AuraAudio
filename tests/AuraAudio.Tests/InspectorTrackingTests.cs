using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Audio.Controller;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;
using Xunit;

namespace AuraAudio.Tests;

public sealed class InspectorTrackingTests
{
    private sealed class FakePlaybackTrackSource : IPlaybackTrackSource
    {
        private EventHandler<Track?>? _currentTrackChanged;

        public Track? CurrentTrack { get; private set; }
        public int SubscriberCount => _currentTrackChanged?.GetInvocationList().Length ?? 0;

        public event EventHandler<Track?>? CurrentTrackChanged
        {
            add => _currentTrackChanged += value;
            remove => _currentTrackChanged -= value;
        }

        public void SetCurrentTrack(Track? track)
        {
            CurrentTrack = track;
            _currentTrackChanged?.Invoke(this, track);
        }

        public (AudioStreamSpec? HardwareSpec, bool HasActivePath, bool IsExclusive, AudioDriverType DriverType) GetHardwareAuditContext(string? targetFilePath)
        {
            if (CurrentTrack != null &&
                !string.IsNullOrWhiteSpace(targetFilePath) &&
                string.Equals(CurrentTrack.FilePath, targetFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return (CurrentTrack.StreamSpec, true, true, AudioDriverType.WASAPI_Exclusive);
            }

            return (null, false, false, AudioDriverType.WASAPI_Exclusive);
        }
    }

    private static Track MakeTrack(long id, string title, string filePath, AudioCodec codec, uint sampleRate, BitDepth bitDepth)
    {
        var spec = new AudioStreamSpec(codec, sampleRate, bitDepth, 2, 1411);
        return new Track(
            id: id,
            filePath: filePath,
            title: title,
            artist: "Test Artist",
            album: "Test Album",
            albumArtist: "Test Artist",
            sortArtist: "Test Artist",
            year: 2026,
            trackNumber: (int)id,
            discNumber: 1,
            duration: TimeSpan.FromMinutes(3),
            streamSpec: spec,
            fileSizeBytes: 30_000_000,
            dateAdded: DateTime.UtcNow);
    }

    private static AudioIntegrityReport MakeReport(Track track, AuthenticityVerdict verdict, string? analyzerVersion = null)
    {
        return new AudioIntegrityReport(
            sourceSpec: track.StreamSpec,
            hardwareSpec: track.StreamSpec,
            verdict: verdict,
            detectedCutoffHz: track.StreamSpec.SampleRateHz / 2,
            explanation: $"Analysis for {track.Title}",
            isTrue24Bit: track.StreamSpec.BitDepth == BitDepth.Bit24,
            lsbActivityPercentage: 95.0,
            spectrumDbBins: new double[256],
            analyzedFilePath: track.FilePath,
            analyzerVersion: analyzerVersion ?? NyquistSpectrumAnalyzer.AnalyzerVersion);
    }

    [Fact]
    public async Task TrackChangesWhileInspectorInactive_OnActivationShowsLatestTrackAndAnalyzesLatest()
    {
        var trackA = MakeTrack(1, "Track Alpha", @"C:\music\alpha.flac", AudioCodec.FLAC, 44_100, BitDepth.Bit16);
        var trackB = MakeTrack(2, "Track Beta", @"C:\music\beta.flac", AudioCodec.FLAC, 96_000, BitDepth.Bit24);

        var analyzedPaths = new List<string>();
        var player = new FakePlaybackTrackSource();
        player.SetCurrentTrack(trackA);

        using var inspector = new InspectorViewModel(
            player,
            analyzer: (path, _, _, _, _, _) =>
            {
                analyzedPaths.Add(path);
                var match = string.Equals(path, trackA.FilePath, StringComparison.OrdinalIgnoreCase) ? trackA : trackB;
                return Task.FromResult(MakeReport(match, AuthenticityVerdict.VerifiedLossless));
            },
            fileExists: _ => true,
            marshalToUi: action => action());

        // User opens Inspector while Track A is playing and analyzes Track A.
        inspector.OnActivated();
        await inspector.AnalyzeCurrentTrackAsync();
        Assert.Equal("Track Alpha", inspector.InspectedTrack?.Title);
        Assert.NotNull(inspector.CurrentIntegrityReport);
        Assert.Equal(trackA.FilePath, inspector.CurrentIntegrityReport!.AnalyzedFilePath);

        // User navigates away (Inspector is inactive); player switches to Track B from Songs/Queue.
        player.SetCurrentTrack(trackB);

        // User returns to Inspector -> OnActivated() refreshes header & format chip to Track B, empty "not analyzed" state.
        inspector.OnActivated();
        Assert.Equal("Track Beta", inspector.InspectedTrack?.Title);
        Assert.Equal(trackB.StreamSpec.FormattedBadge, inspector.InspectedTrack?.StreamSpec.FormattedBadge);
        Assert.Null(inspector.CurrentIntegrityReport);
        Assert.True(inspector.IsInspectorEmpty);

        // Clicking "Analyze Current Track" resolves Track B at click time, never stale Track A.
        await inspector.AnalyzeCurrentTrackAsync();
        Assert.Equal(2, analyzedPaths.Count);
        Assert.Equal(trackB.FilePath, analyzedPaths[1]);
        Assert.NotNull(inspector.CurrentIntegrityReport);
        Assert.Equal(trackB.FilePath, inspector.CurrentIntegrityReport!.AnalyzedFilePath);
    }

    [Fact]
    public async Task TrackChangesWhileInspectorActive_UpdatesHeaderAndValidatesCachePathAndVersion()
    {
        var trackA = MakeTrack(1, "Track Alpha", @"C:\music\alpha.flac", AudioCodec.FLAC, 44_100, BitDepth.Bit16);
        var trackB = MakeTrack(2, "Track Beta", @"C:\music\beta.m4a", AudioCodec.ALAC, 48_000, BitDepth.Bit24);
        var trackC = MakeTrack(3, "Track Gamma", @"C:\music\gamma.wav", AudioCodec.WAV, 96_000, BitDepth.Bit24);

        int analyzeCallCount = 0;
        var player = new FakePlaybackTrackSource();
        player.SetCurrentTrack(trackA);

        using var inspector = new InspectorViewModel(
            player,
            analyzer: (path, _, _, _, _, _) =>
            {
                analyzeCallCount++;
                var match = string.Equals(path, trackA.FilePath, StringComparison.OrdinalIgnoreCase) ? trackA : trackB;
                return Task.FromResult(MakeReport(match, AuthenticityVerdict.VerifiedLossless));
            },
            fileExists: _ => true,
            marshalToUi: action => action(),
            analyzerVersion: "2.0.2");

        inspector.OnActivated();
        await inspector.AnalyzeCurrentTrackAsync();
        Assert.Equal(1, analyzeCallCount);
        Assert.NotNull(inspector.CurrentIntegrityReport);

        // Seed a report for Track C from an OLD analyzer version — must NOT hit cache.
        inspector.SeedCachedReport(trackC.FilePath, MakeReport(trackC, AuthenticityVerdict.VerifiedLossless, analyzerVersion: "2.0.1"));

        // Switch to Track B while Inspector is active -> header & chip update, empty "not analyzed" state.
        player.SetCurrentTrack(trackB);
        Assert.Equal("Track Beta", inspector.InspectedTrack?.Title);
        Assert.Equal(trackB.StreamSpec.FormattedBadge, inspector.InspectedTrack?.StreamSpec.FormattedBadge);
        Assert.Null(inspector.CurrentIntegrityReport);
        Assert.True(inspector.IsInspectorEmpty);

        // Switch to Track C -> mismatched analyzer version is NOT shown; stays empty.
        player.SetCurrentTrack(trackC);
        Assert.Equal("Track Gamma", inspector.InspectedTrack?.Title);
        Assert.Null(inspector.CurrentIntegrityReport);
        Assert.True(inspector.IsInspectorEmpty);

        // Switch back to Track A -> exact (path, version) cache hit shown without re-running analyzer.
        player.SetCurrentTrack(trackA);
        Assert.Equal("Track Alpha", inspector.InspectedTrack?.Title);
        Assert.NotNull(inspector.CurrentIntegrityReport);
        Assert.Equal(trackA.FilePath, inspector.CurrentIntegrityReport!.AnalyzedFilePath);
        Assert.Equal("2.0.2", inspector.CurrentIntegrityReport!.AnalyzerVersion);
        Assert.Equal(1, analyzeCallCount);
    }

    [Fact]
    public async Task InFlightAnalysisOfTrackA_ArrivingAfterTrackBecomesB_IsDiscarded()
    {
        var trackA = MakeTrack(1, "Track Alpha", @"C:\music\alpha.flac", AudioCodec.FLAC, 44_100, BitDepth.Bit16);
        var trackB = MakeTrack(2, "Track Beta", @"C:\music\beta.flac", AudioCodec.FLAC, 96_000, BitDepth.Bit24);

        var trackAGate = new TaskCompletionSource<AudioIntegrityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trackBGate = new TaskCompletionSource<AudioIntegrityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool trackATokenCanceled = false;

        var player = new FakePlaybackTrackSource();
        player.SetCurrentTrack(trackA);

        using var inspector = new InspectorViewModel(
            player,
            analyzer: (path, _, _, _, _, ct) =>
            {
                if (string.Equals(path, trackA.FilePath, StringComparison.OrdinalIgnoreCase))
                {
                    ct.Register(() => trackATokenCanceled = true);
                    // Intentionally ignores cancellation and resolves late to verify request-id discard guard.
                    return trackAGate.Task;
                }

                return trackBGate.Task;
            },
            fileExists: _ => true,
            marshalToUi: action => action());

        // Start analyzing Track A (stays in flight).
        var analyzeATask = inspector.AnalyzeCurrentTrackAsync();
        Assert.True(inspector.IsAnalyzing);
        Assert.Equal("Track Alpha", inspector.InspectedTrack?.Title);

        // Player switches to Track B before Track A completes.
        player.SetCurrentTrack(trackB);
        Assert.True(trackATokenCanceled);
        Assert.Equal("Track Beta", inspector.InspectedTrack?.Title);
        Assert.False(inspector.IsAnalyzing);
        Assert.Null(inspector.CurrentIntegrityReport);

        // Start analyzing Track B while Track A's stale task is still pending.
        var analyzeBTask = inspector.AnalyzeCurrentTrackAsync();
        Assert.True(inspector.IsAnalyzing);

        // Track A's late result now arrives — MUST be completely discarded and must not clear Track B's IsAnalyzing.
        trackAGate.SetResult(MakeReport(trackA, AuthenticityVerdict.UpscaledFromCd));
        await analyzeATask;

        Assert.Equal("Track Beta", inspector.InspectedTrack?.Title);
        Assert.True(inspector.IsAnalyzing);
        Assert.Null(inspector.CurrentIntegrityReport);

        // Now Track B finishes — Track B's report is published cleanly.
        trackBGate.SetResult(MakeReport(trackB, AuthenticityVerdict.VerifiedLossless));
        await analyzeBTask;

        Assert.False(inspector.IsAnalyzing);
        Assert.NotNull(inspector.CurrentIntegrityReport);
        Assert.Equal(trackB.FilePath, inspector.CurrentIntegrityReport!.AnalyzedFilePath);
        Assert.Equal(AuthenticityVerdict.VerifiedLossless, inspector.CurrentIntegrityReport!.Verdict);
    }

    [Fact]
    public async Task ExternalFileMode_StaysPinnedUntilUserReturnsToCurrentTrack()
    {
        var trackA = MakeTrack(1, "Track Alpha", @"C:\music\alpha.flac", AudioCodec.FLAC, 44_100, BitDepth.Bit16);
        var trackB = MakeTrack(2, "Track Beta", @"C:\music\beta.flac", AudioCodec.FLAC, 96_000, BitDepth.Bit24);
        var externalFile = MakeTrack(99, "External Sample", @"D:\downloads\external.flac", AudioCodec.FLAC, 192_000, BitDepth.Bit24);

        var player = new FakePlaybackTrackSource();
        player.SetCurrentTrack(trackA);

        using var inspector = new InspectorViewModel(
            player,
            analyzer: (path, _, _, _, _, _) =>
            {
                var t = string.Equals(path, externalFile.FilePath, StringComparison.OrdinalIgnoreCase)
                    ? externalFile
                    : (string.Equals(path, trackB.FilePath, StringComparison.OrdinalIgnoreCase) ? trackB : trackA);
                return Task.FromResult(MakeReport(t, AuthenticityVerdict.VerifiedLossless));
            },
            fileExists: _ => true,
            marshalToUi: action => action());

        // Enter explicit External File mode.
        await inspector.InspectExternalFileAsync(externalFile);
        Assert.True(inspector.IsInspectingExternalFile);
        Assert.Equal("External Sample", inspector.InspectedTrack?.Title);
        Assert.Equal(externalFile.FilePath, inspector.CurrentIntegrityReport?.AnalyzedFilePath);

        // Track changes to B and page re-activates -> External File stays pinned.
        player.SetCurrentTrack(trackB);
        inspector.OnActivated();
        Assert.True(inspector.IsInspectingExternalFile);
        Assert.Equal("External Sample", inspector.InspectedTrack?.Title);
        Assert.Equal(externalFile.FilePath, inspector.CurrentIntegrityReport?.AnalyzedFilePath);

        // Clicking "Analyze Current Track" exits External File mode and analyzes live current track B.
        await inspector.AnalyzeCurrentTrackAsync();
        Assert.False(inspector.IsInspectingExternalFile);
        Assert.Equal("Track Beta", inspector.InspectedTrack?.Title);
        Assert.Equal(trackB.FilePath, inspector.CurrentIntegrityReport?.AnalyzedFilePath);

        // Re-enter external mode, then explicitly ReturnToCurrentTrack() -> restores Track B with its cached report.
        await inspector.InspectExternalFileAsync(externalFile);
        Assert.True(inspector.IsInspectingExternalFile);
        inspector.ReturnToCurrentTrack();
        Assert.False(inspector.IsInspectingExternalFile);
        Assert.Equal("Track Beta", inspector.InspectedTrack?.Title);
        Assert.Equal(trackB.FilePath, inspector.CurrentIntegrityReport?.AnalyzedFilePath);
    }

    [Fact]
    public async Task AnalyzeCurrentTrack_ResolvesTrackAtClickTimeNeverFromStaleInspectedField()
    {
        var trackA = MakeTrack(1, "Track Alpha", @"C:\music\alpha.flac", AudioCodec.FLAC, 44_100, BitDepth.Bit16);
        var trackB = MakeTrack(2, "Track Beta", @"C:\music\beta.flac", AudioCodec.FLAC, 96_000, BitDepth.Bit24);

        string? lastAnalyzed = null;
        var player = new FakePlaybackTrackSource();
        player.SetCurrentTrack(trackB);

        using var inspector = new InspectorViewModel(
            player,
            analyzer: (path, _, _, _, _, _) =>
            {
                lastAnalyzed = path;
                return Task.FromResult(MakeReport(trackB, AuthenticityVerdict.VerifiedLossless));
            },
            fileExists: _ => true,
            marshalToUi: action => action());

        // Simulate stale cached reference on InspectedTrack.
        inspector.InspectedTrack = trackA;
        Assert.Equal("Track Alpha", inspector.InspectedTrack?.Title);

        // "Analyze Current Track" must ignore the stale field and resolve player.CurrentTrack (Track B).
        await inspector.AnalyzeCurrentTrackAsync();
        Assert.Equal(trackB.FilePath, lastAnalyzed);
        Assert.Equal("Track Beta", inspector.InspectedTrack?.Title);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTrackSourceWithNoLeaks()
    {
        var trackA = MakeTrack(1, "Track Alpha", @"C:\music\alpha.flac", AudioCodec.FLAC, 44_100, BitDepth.Bit16);
        var trackB = MakeTrack(2, "Track Beta", @"C:\music\beta.flac", AudioCodec.FLAC, 96_000, BitDepth.Bit24);

        var player = new FakePlaybackTrackSource();
        player.SetCurrentTrack(trackA);

        var inspector = new InspectorViewModel(player, fileExists: _ => true, marshalToUi: action => action());
        Assert.Equal(1, player.SubscriberCount);
        Assert.Equal("Track Alpha", inspector.InspectedTrack?.Title);

        inspector.Dispose();
        Assert.Equal(0, player.SubscriberCount);

        // Subsequent track changes after disposal must not update or throw.
        player.SetCurrentTrack(trackB);
        Assert.Equal("Track Alpha", inspector.InspectedTrack?.Title);
    }
}
