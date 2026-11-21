using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AuraAudio.Core.Models;

namespace AuraAudio.Audio.Authenticity;

/// <summary>
/// Single-worker, low-priority (<see cref="ThreadPriority.BelowNormal"/>) coordinator for forensic
/// Nyquist FFT analyses. Strictly guarantees at most one analysis runs at any time, isolates CPU
/// load from the audio render thread and .NET ThreadPool, and drops cancelled queued requests
/// without opening a file handle or decoder.
/// </summary>
internal sealed class ForensicAnalysisCoordinator
{
    public static ForensicAnalysisCoordinator Instance { get; } = new();

    private sealed class WorkItem
    {
        public required Func<AudioIntegrityReport> Work { get; init; }
        public required CancellationToken CancellationToken { get; init; }
        public required TaskCompletionSource<AudioIntegrityReport> Completion { get; init; }
        public CancellationTokenRegistration Registration { get; set; }
    }

    private readonly ConcurrentQueue<WorkItem> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _workerThread;

    private ForensicAnalysisCoordinator()
    {
        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "AuraForensicAnalyzer"
        };
        _workerThread.Start();
    }

    public Task<AudioIntegrityReport> EnqueueAsync(
        Func<AudioIntegrityReport> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<AudioIntegrityReport>(cancellationToken);
        }

        var tcs = new TaskCompletionSource<AudioIntegrityReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem
        {
            Work = work,
            CancellationToken = cancellationToken,
            Completion = tcs
        };

        if (cancellationToken.CanBeCanceled)
        {
            item.Registration = cancellationToken.Register(
                static state =>
                {
                    var w = (WorkItem)state!;
                    w.Completion.TrySetCanceled(w.CancellationToken);
                },
                item);
        }

        _queue.Enqueue(item);
        _signal.Set();
        return tcs.Task;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            while (_queue.TryDequeue(out var item))
            {
                if (item.CancellationToken.IsCancellationRequested || item.Completion.Task.IsCompleted)
                {
                    item.Completion.TrySetCanceled(item.CancellationToken);
                    item.Registration.Dispose();
                    continue;
                }

                AudioRenderDiagnostics.OnAnalysisStarted();
                try
                {
                    var report = item.Work();
                    if (item.CancellationToken.IsCancellationRequested)
                    {
                        item.Completion.TrySetCanceled(item.CancellationToken);
                    }
                    else
                    {
                        item.Completion.TrySetResult(report);
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Completion.TrySetCanceled(item.CancellationToken);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                }
                finally
                {
                    AudioRenderDiagnostics.OnAnalysisFinished();
                    item.Registration.Dispose();
                }
            }

            _signal.WaitOne();
        }
    }
}
