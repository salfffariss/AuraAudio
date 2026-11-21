using System;
using System.Diagnostics;
using System.Threading;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Data peristiwa saat terjadi transisi lagu gapless.
/// </summary>
public record TrackTransitionEventArgs(IAudioStream PreviousTrack, IAudioStream CurrentTrack);

/// <summary>
/// Antrean Aliran Audio Gapless (Gapless Stream Queue) Kelas Audiophile.
/// Menyediakan pembacaan posisi bebas kunci (lock-free <see cref="CurrentTime"/> / <see cref="TotalTime"/>),
/// mikro-fade Anti-Pop bebas alokasi heap (in-place <see cref="Span{T}"/>), dan seek non-blocking
/// agar thread render audio tidak pernah terhambat menunggu disk seek.
/// </summary>
public class GaplessStreamQueue : IAudioStream
{
    private readonly object _lock = new();
    private IAudioStream _currentStream;
    private IAudioStream? _nextStream;
    private bool _isFirstBuffer = true;
    private bool _isDisposed;

    // Lock-free snapshots read by the 50 ms UI timer and render-thread volume check without contending on _lock
    private long _cachedPositionBytes;
    private long _cachedLengthBytes;
    private int _cachedAvgBytesPerSec;
    private volatile bool _isCurrentDoP;
    private int _isSeeking;

    public IAudioStream CurrentStream
    {
        get { lock (_lock) return _currentStream; }
    }

    public IAudioStream? NextStream
    {
        get { lock (_lock) return _nextStream; }
    }

    internal bool IsCurrentStreamDoP => _isCurrentDoP;

    public string FilePath => CurrentStream.FilePath;
    public AudioStreamSpec Spec => CurrentStream.Spec;
    public WaveFormat WaveFormat => CurrentStream.WaveFormat;

    public TimeSpan TotalTime
    {
        get
        {
            long len = Volatile.Read(ref _cachedLengthBytes);
            int bps = Math.Max(1, Volatile.Read(ref _cachedAvgBytesPerSec));
            return TimeSpan.FromSeconds((double)len / bps);
        }
    }

    public TimeSpan CurrentTime
    {
        get
        {
            long pos = Volatile.Read(ref _cachedPositionBytes);
            int bps = Math.Max(1, Volatile.Read(ref _cachedAvgBytesPerSec));
            return TimeSpan.FromSeconds((double)pos / bps);
        }
        set
        {
            Interlocked.Increment(ref _isSeeking);
            try
            {
                lock (_lock)
                {
                    if (_isDisposed) return;
                    _currentStream.CurrentTime = value;
                    _isFirstBuffer = true;
                    RefreshStreamMetadataUnlocked(_currentStream);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _isSeeking);
            }
        }
    }

    public long Position
    {
        get => Volatile.Read(ref _cachedPositionBytes);
        set
        {
            Interlocked.Increment(ref _isSeeking);
            try
            {
                lock (_lock)
                {
                    if (_isDisposed) return;
                    _currentStream.Position = value;
                    _isFirstBuffer = true;
                    RefreshStreamMetadataUnlocked(_currentStream);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _isSeeking);
            }
        }
    }

    public long Length => Volatile.Read(ref _cachedLengthBytes);

    public event EventHandler<TrackTransitionEventArgs>? TrackTransitioned;
    public event EventHandler<IAudioStream>? FormatChangeRequired;
    public event EventHandler? QueueEnded;

    public GaplessStreamQueue(IAudioStream initialStream)
    {
        _currentStream = initialStream ?? throw new ArgumentNullException(nameof(initialStream));
        RefreshStreamMetadataUnlocked(_currentStream);
    }

    private void RefreshStreamMetadataUnlocked(IAudioStream stream)
    {
        int avgBps = Math.Max(1, stream.WaveFormat.AverageBytesPerSecond);
        Volatile.Write(ref _cachedAvgBytesPerSec, avgBps);
        Volatile.Write(ref _cachedLengthBytes, Math.Max(0, stream.Length));
        Volatile.Write(ref _cachedPositionBytes, Math.Max(0, stream.Position));
        _isCurrentDoP = stream is Dsd.DopStreamProvider;
    }

    public void EnqueueNext(IAudioStream nextStream)
    {
        ArgumentNullException.ThrowIfNull(nextStream);
        lock (_lock)
        {
            _nextStream?.Dispose();
            _nextStream = nextStream;
        }
    }

    public void ClearNext()
    {
        lock (_lock)
        {
            _nextStream?.Dispose();
            _nextStream = null;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return 0;

        return Read(buffer.AsSpan(offset, count));
    }

    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;

        // While an explicit seek is scanning/decoding on another thread, emit clean digital silence
        // immediately instead of stalling the audio render thread and starving the hardware DMA ring.
        if (Volatile.Read(ref _isSeeking) > 0)
        {
            destination.Clear();
            return destination.Length;
        }

        bool lockTaken = false;
        long waitStart = AudioRenderDiagnostics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        try
        {
            if (Volatile.Read(ref _isSeeking) > 0)
            {
                destination.Clear();
                return destination.Length;
            }

            Monitor.TryEnter(_lock, ref lockTaken);
            if (!lockTaken)
            {
                if (Volatile.Read(ref _isSeeking) > 0)
                {
                    destination.Clear();
                    return destination.Length;
                }

                Monitor.Enter(_lock, ref lockTaken);
                if (waitStart > 0)
                {
                    AudioRenderDiagnostics.RecordLockWait(Stopwatch.GetTimestamp() - waitStart);
                }
            }

            if (_isDisposed)
            {
                destination.Clear();
                return 0;
            }

            int totalRead = 0;
            while (totalRead < destination.Length)
            {
                int needed = destination.Length - totalRead;
                int read = _currentStream.Read(destination.Slice(totalRead, needed));

                if (read > 0)
                {
                    if (_isFirstBuffer)
                    {
                        AntiPopFader.ApplyFadeIn(destination.Slice(totalRead, read), _currentStream.WaveFormat);
                        _isFirstBuffer = false;
                    }

                    totalRead += read;
                }
                else
                {
                    if (_nextStream != null)
                    {
                        if (FormatMatches(_currentStream.WaveFormat, _nextStream.WaveFormat))
                        {
                            var prevTrack = _currentStream;
                            var next = _nextStream;
                            _currentStream = next;
                            _nextStream = null;
                            RefreshStreamMetadataUnlocked(_currentStream);

                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                TrackTransitioned?.Invoke(this, new TrackTransitionEventArgs(prevTrack, next));
                                prevTrack.Dispose();
                            });

                            continue;
                        }
                        else
                        {
                            if (totalRead > 0)
                            {
                                AntiPopFader.ApplyFadeOut(destination.Slice(0, totalRead), _currentStream.WaveFormat);
                            }

                            var next = _nextStream;
                            _nextStream = null;
                            ThreadPool.QueueUserWorkItem(_ => FormatChangeRequired?.Invoke(this, next));
                            break;
                        }
                    }
                    else
                    {
                        if (totalRead > 0)
                        {
                            AntiPopFader.ApplyFadeOut(destination.Slice(0, totalRead), _currentStream.WaveFormat);
                        }

                        ThreadPool.QueueUserWorkItem(_ => QueueEnded?.Invoke(this, EventArgs.Empty));
                        break;
                    }
                }
            }

            Volatile.Write(ref _cachedPositionBytes, Math.Max(0, _currentStream.Position));
            return totalRead;
        }
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_lock);
            }
        }
    }

    private static bool FormatMatches(WaveFormat a, WaveFormat b)
    {
        return a.SampleRate == b.SampleRate
            && a.BitsPerSample == b.BitsPerSample
            && a.Channels == b.Channels
            && a.Encoding == b.Encoding;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (_lock)
        {
            _currentStream.Dispose();
            _nextStream?.Dispose();
            _nextStream = null;
        }

        GC.SuppressFinalize(this);
    }
}
