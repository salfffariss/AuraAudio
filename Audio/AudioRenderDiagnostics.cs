using System;
using System.Diagnostics;
using System.Threading;

namespace AuraAudio.Audio;

/// <summary>
/// Lightweight opt-in diagnostics for the audio render path and forensic worker.
/// Disabled by default (zero overhead); enable via <c>AURA_AUDIO_DIAG=1</c> or <see cref="SetEnabled"/>.
/// </summary>
public static class AudioRenderDiagnostics
{
    public readonly record struct Snapshot(
        long Callbacks,
        long LateCallbacks,
        long Underruns,
        double MaxGapMs,
        double MaxReadMs,
        double MaxLockWaitMs,
        long LockContentionCount,
        long DispatcherWaitCount,
        long AnalysisLockWaitCount,
        ThreadPriority RenderThreadPriority,
        bool MmcssRegistered,
        int ActiveAnalyses,
        int PeakConcurrentAnalyses);

    private static volatile bool _enabled =
        string.Equals(Environment.GetEnvironmentVariable("AURA_AUDIO_DIAG"), "1", StringComparison.Ordinal);

    private static long _callbacks;
    private static long _lateCallbacks;
    private static long _underruns;
    private static long _maxGapTicks;
    private static long _maxReadTicks;
    private static long _maxLockWaitTicks;
    private static long _lockContentionCount;
    private static long _dispatcherWaitCount;
    private static long _analysisLockWaitCount;
    private static long _lastCallbackTimestamp;
    private static int _renderThreadPriority = (int)ThreadPriority.Normal;
    private static int _mmcssRegistered;
    private static int _activeAnalyses;
    private static int _peakConcurrentAnalyses;

    public static bool IsEnabled => _enabled;

    public static void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
        {
            Reset();
        }
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _callbacks, 0);
        Interlocked.Exchange(ref _lateCallbacks, 0);
        Interlocked.Exchange(ref _underruns, 0);
        Interlocked.Exchange(ref _maxGapTicks, 0);
        Interlocked.Exchange(ref _maxReadTicks, 0);
        Interlocked.Exchange(ref _maxLockWaitTicks, 0);
        Interlocked.Exchange(ref _lockContentionCount, 0);
        Interlocked.Exchange(ref _dispatcherWaitCount, 0);
        Interlocked.Exchange(ref _analysisLockWaitCount, 0);
        Interlocked.Exchange(ref _lastCallbackTimestamp, 0);
        Interlocked.Exchange(ref _renderThreadPriority, (int)ThreadPriority.Normal);
        Interlocked.Exchange(ref _mmcssRegistered, 0);
        Interlocked.Exchange(ref _activeAnalyses, 0);
        Interlocked.Exchange(ref _peakConcurrentAnalyses, 0);
    }

    public static void RecordRenderThreadState(ThreadPriority priority, bool mmcssRegistered)
    {
        if (!_enabled) return;
        Interlocked.Exchange(ref _renderThreadPriority, (int)priority);
        Interlocked.Exchange(ref _mmcssRegistered, mmcssRegistered ? 1 : 0);
    }

    public static void RecordRenderCallback(long startTimestamp, long endTimestamp, double expectedPeriodMs, double underrunBudgetMs = 50.0)
    {
        if (!_enabled) return;

        Interlocked.Increment(ref _callbacks);
        long readTicks = Math.Max(0, endTimestamp - startTimestamp);
        UpdateMax(ref _maxReadTicks, readTicks);

        long prev = Interlocked.Exchange(ref _lastCallbackTimestamp, startTimestamp);
        if (prev > 0 && startTimestamp > prev)
        {
            long gapTicks = startTimestamp - prev;
            UpdateMax(ref _maxGapTicks, gapTicks);
            double gapMs = gapTicks * 1000.0 / Stopwatch.Frequency;
            if (expectedPeriodMs > 0 && gapMs > expectedPeriodMs * 1.5)
            {
                Interlocked.Increment(ref _lateCallbacks);
            }
            if (gapMs > underrunBudgetMs)
            {
                Interlocked.Increment(ref _underruns);
            }
        }
    }

    public static void RecordLockWait(long waitTicks)
    {
        if (!_enabled || waitTicks <= 0) return;
        Interlocked.Increment(ref _lockContentionCount);
        UpdateMax(ref _maxLockWaitTicks, waitTicks);
    }

    public static void OnAnalysisStarted()
    {
        int current = Interlocked.Increment(ref _activeAnalyses);
        int prev;
        do
        {
            prev = Volatile.Read(ref _peakConcurrentAnalyses);
            if (current <= prev) break;
        } while (Interlocked.CompareExchange(ref _peakConcurrentAnalyses, current, prev) != prev);
    }

    public static void OnAnalysisFinished()
    {
        Interlocked.Decrement(ref _activeAnalyses);
    }

    public static Snapshot GetSnapshot()
    {
        double ticksToMs = 1000.0 / Stopwatch.Frequency;
        return new Snapshot(
            Callbacks: Interlocked.Read(ref _callbacks),
            LateCallbacks: Interlocked.Read(ref _lateCallbacks),
            Underruns: Interlocked.Read(ref _underruns),
            MaxGapMs: Interlocked.Read(ref _maxGapTicks) * ticksToMs,
            MaxReadMs: Interlocked.Read(ref _maxReadTicks) * ticksToMs,
            MaxLockWaitMs: Interlocked.Read(ref _maxLockWaitTicks) * ticksToMs,
            LockContentionCount: Interlocked.Read(ref _lockContentionCount),
            DispatcherWaitCount: Interlocked.Read(ref _dispatcherWaitCount),
            AnalysisLockWaitCount: Interlocked.Read(ref _analysisLockWaitCount),
            RenderThreadPriority: (ThreadPriority)Volatile.Read(ref _renderThreadPriority),
            MmcssRegistered: Volatile.Read(ref _mmcssRegistered) != 0,
            ActiveAnalyses: Volatile.Read(ref _activeAnalyses),
            PeakConcurrentAnalyses: Volatile.Read(ref _peakConcurrentAnalyses));
    }

    private static void UpdateMax(ref long target, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (value <= current) return;
        } while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
