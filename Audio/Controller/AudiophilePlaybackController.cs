using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Audio.Queue;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using NAudio.Wave;

namespace AuraAudio.Audio.Controller;

/// <summary>
/// Kontroler Pemutaran Audio Kelas Audiophile (Audiophile Playback Controller & Thread-Safe State Store).
/// Mengorkestrasi seluruh subsistem audio AuraAudio:
/// 1. WasapiExclusivePlayer (Hardware Engine)
/// 2. AudioDecoderFactory (Universal Lossless/DSD Decoders)
/// 3. PlaybackQueueManager (Antrean Fisher-Yates Shuffle & Repeat)
/// 4. GaplessStreamQueue (Penyambungan lagu tanpa jeda)
/// 5. SignalPathAuditor (Audit transparansi Bit-Perfect live)
/// 6. NyquistSpectrumAnalyzer (Audit integritas spektrum ultrasonik anti-fake FLAC)
/// Menggunakan System.Threading.Channels untuk komunikasi bebas kunci (Lock-Free) dengan UI Thread (120 FPS).
/// </summary>
public class AudiophilePlaybackController : IDisposable
{
    private readonly object _lock = new();
    private readonly IAudioOutputEngine _outputEngine;
    private readonly DeviceEnumerator _deviceEnumerator;
    private readonly PlaybackQueueManager _queueManager = new();
    private readonly Timer _positionTimer;

    private readonly SemaphoreSlim _playbackLock = new(1, 1);
    private readonly Channel<PlaybackStateSnapshot> _stateChannel =
        Channel.CreateBounded<PlaybackStateSnapshot>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false
        });

    private AudioDevice? _currentDevice;
    private AudioDriverType _driverType = AudioDriverType.WASAPI_Exclusive;
    private DsdPlaybackMode _dsdMode = DsdPlaybackMode.PcmDecimation;
    private int _latencyMs = 50;

    private GaplessStreamQueue? _activeQueue;
    private Track? _currentTrack;
    private SignalPathSummary? _currentSignalPath;
    private AudioIntegrityReport? _currentIntegrityReport;

    private float _volume = 1.0f;
    private bool _isMuted = false;
    private bool _isDisposed;

    // Properti Akses Publik
    public ChannelReader<PlaybackStateSnapshot> StateReader => _stateChannel.Reader;
    public AudioDevice? CurrentDevice => _currentDevice;
    public AudioDriverType DriverType => _driverType;
    public DsdPlaybackMode DsdMode => _dsdMode;
    public int LatencyMs => _latencyMs;
    public DeviceEnumerator DeviceEnumerator => _deviceEnumerator;
    public Track? CurrentTrack => _currentTrack;
    public PlaybackQueueManager QueueManager => _queueManager;
    public IReadOnlyList<Track> Playlist => _queueManager.Items;
    public int CurrentIndex => _queueManager.CurrentIndex;
    public PlaybackStatus Status => _outputEngine.Status;
    public PlaybackStateSnapshot CurrentSnapshot => GenerateSnapshot();
    public PlaybackRepeatMode RepeatMode => _queueManager.RepeatMode;
    public bool IsShuffleEnabled => _queueManager.IsShuffleEnabled;
    public bool IsExclusiveOutputActive => _outputEngine.IsExclusiveLocked;

    /// <summary>
    /// Mengambil konteks perangkat keras output aktif saat ini untuk audit jalur sinyal di Inspector.
    /// </summary>
    public (AudioStreamSpec? HardwareSpec, bool HasActivePath, bool IsExclusive, AudioDriverType DriverType) GetHardwareAuditContext(string? targetFilePath)
    {
        lock (_lock)
        {
            var fmt = _outputEngine.CurrentFormat;
            bool isTargetPlaying =
                !string.IsNullOrWhiteSpace(targetFilePath) &&
                _currentTrack != null &&
                string.Equals(_currentTrack.FilePath, targetFilePath, StringComparison.OrdinalIgnoreCase) &&
                _outputEngine.Status != PlaybackStatus.Stopped &&
                fmt != null;

            if (!isTargetPlaying || fmt == null)
            {
                return (null, false, false, _driverType);
            }

            BitDepth hwBits = fmt.BitsPerSample switch
            {
                >= 32 => BitDepth.Bit32Float,
                >= 24 => BitDepth.Bit24,
                _ => BitDepth.Bit16
            };

            var hwSpec = new AudioStreamSpec(
                _currentTrack!.StreamSpec.Codec,
                (uint)Math.Max(1, fmt.SampleRate),
                hwBits,
                (ushort)Math.Max(1, fmt.Channels),
                _currentTrack.StreamSpec.BitrateKbps);

            bool isExcl = _outputEngine.IsExclusiveLocked && _driverType == AudioDriverType.WASAPI_Exclusive;
            return (hwSpec, true, isExcl, _driverType);
        }
    }
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            _isMuted = _volume <= 0.0001f;
            _outputEngine.Volume = _volume;
            PublishSnapshot();
        }
    }

    // Event Langsung untuk Subscriber Konvensional
    public event EventHandler<PlaybackStateSnapshot>? StateChanged;
    public event EventHandler<Track>? TrackChanged;
    public event EventHandler<AudioIntegrityReport>? IntegrityReportUpdated;
    public event EventHandler<SignalPathSummary>? SignalPathUpdated;

    public AudiophilePlaybackController(IAudioOutputEngine? outputEngine = null)
    {
        _outputEngine = outputEngine ?? new WasapiExclusivePlayer();
        _deviceEnumerator = new DeviceEnumerator();
        _currentDevice = _deviceEnumerator.GetDefaultRenderDevice();

        _outputEngine.StatusChanged += OnOutputEngineStatusChanged;

        // Timer polling posisi waktu (setiap 50 ms untuk animasi slider halus 60-120 FPS)
        _positionTimer = new Timer(OnPositionTimerTick, null, 100, 50);
    }

    /// <summary>
    /// Memutar satu lagu langsung ke hardware secara instan.
    /// </summary>
    public async Task PlayTrackAsync(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);

        _queueManager.SetQueue(new[] { track }, startIndex: 0);
        await StartTrackInternalAsync(track);
    }

    /// <summary>
    /// Memuat daftar putar antrean (Queue) dan memulai dari lagu pada indeks tertentu.
    /// </summary>
    public async Task PlayQueueAsync(IEnumerable<Track> tracks, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        _queueManager.SetQueue(tracks, startIndex);
        var targetTrack = _queueManager.CurrentTrack;

        if (targetTrack != null)
        {
            await StartTrackInternalAsync(targetTrack);
        }
    }

    /// <summary>
    /// Menyisipkan lagu agar diputar tepat setelah lagu saat ini (Play Next).
    /// </summary>
    public void PlayNext(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _queueManager.PlayNext(track);
        PrimeNextTrackInQueue();
        PublishSnapshot();
    }

    /// <summary>
    /// Menambahkan lagu ke akhir antrean (Add to Queue).
    /// </summary>
    public void Enqueue(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        _queueManager.Add(track);
        PrimeNextTrackInQueue();
        PublishSnapshot();
    }

    /// <summary>
    /// Menghapus lagu dari antrean pada indeks tertentu.
    /// </summary>
    public void RemoveFromQueue(int index)
    {
        _queueManager.RemoveAt(index);
        PrimeNextTrackInQueue();
        PublishSnapshot();
    }

    /// <summary>
    /// Mengatur ulang posisi urutan lagu di antrean (Drag-and-Drop Reorder).
    /// </summary>
    public void MoveQueueItem(int fromIndex, int toIndex)
    {
        _queueManager.Move(fromIndex, toIndex);
        PrimeNextTrackInQueue();
        PublishSnapshot();
    }

    /// <summary>
    /// Memulai pemutaran audio atau melanjutkan jika dijeda.
    /// </summary>
    public void Play()
    {
        if (_activeQueue == null)
        {
            var targetTrack = _queueManager.CurrentTrack;
            if (targetTrack != null)
            {
                _ = Task.Run(async () => await StartTrackInternalAsync(targetTrack));
            }
            return;
        }

        _outputEngine.Play();
        PublishSnapshot();
    }

    /// <summary>
    /// Menjeda pemutaran audio sementara tanpa melepas lock eksklusif soundcard.
    /// </summary>
    public void Pause()
    {
        _outputEngine.Pause();
        PublishSnapshot();
    }

    /// <summary>
    /// Mengganti status antara Play dan Pause.
    /// </summary>
    public void TogglePlayPause()
    {
        if (_outputEngine.Status == PlaybackStatus.Playing)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    /// <summary>
    /// Menghentikan pemutaran audio.
    /// </summary>
    public void Stop()
    {
        _outputEngine.Stop();
        lock (_lock)
        {
            _activeQueue?.Dispose();
            _activeQueue = null;
        }
        PublishSnapshot();
    }

    /// <summary>
    /// Melakukan lompat waktu (Seeking) ke detik tertentu tanpa letupan klik.
    /// </summary>
    public void Seek(TimeSpan targetTime)
    {
        lock (_lock)
        {
            if (_activeQueue != null)
            {
                _activeQueue.CurrentTime = targetTime;
                PublishSnapshot();
            }
        }
    }

    /// <summary>
    /// Berpindah ke lagu berikutnya dalam antrean (mempertimbangkan Shuffle & Repeat).
    /// </summary>
    public async Task NextAsync()
    {
        var nextTrack = _queueManager.AdvanceNext();
        if (nextTrack != null)
        {
            await StartTrackInternalAsync(nextTrack);
        }
        else
        {
            Stop();
        }
    }

    /// <summary>
    /// Berpindah ke lagu sebelumnya (berdasarkan history stack), atau rewind ke awal jika sudah >3 detik.
    /// </summary>
    public async Task PreviousAsync()
    {
        if (_activeQueue != null && _activeQueue.CurrentTime.TotalSeconds > 3.0)
        {
            Seek(TimeSpan.Zero);
            return;
        }

        var prevTrack = _queueManager.StepPrevious();
        if (prevTrack != null)
        {
            await StartTrackInternalAsync(prevTrack);
        }
    }

    /// <summary>
    /// Mengatur mode acak Fisher-Yates Shuffle.
    /// </summary>
    public void SetShuffle(bool enable)
    {
        _queueManager.SetShuffle(enable);
        PrimeNextTrackInQueue();
        PublishSnapshot();
    }

    /// <summary>
    /// Toggle mode acak (Shuffle On/Off).
    /// </summary>
    public void ToggleShuffle()
    {
        SetShuffle(!_queueManager.IsShuffleEnabled);
    }

    /// <summary>
    /// Mengatur mode perulangan antrean (Off, RepeatAll, RepeatOne).
    /// </summary>
    public void SetRepeatMode(PlaybackRepeatMode mode)
    {
        _queueManager.RepeatMode = mode;
        PublishSnapshot();
    }

    /// <summary>
    /// Mengatur perangkat output audio yang dituju.
    /// </summary>
    public void SetDevice(AudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lock)
        {
            _currentDevice = device;
            if (_currentTrack != null && _outputEngine.Status == PlaybackStatus.Playing)
            {
                _ = StartTrackInternalAsync(_currentTrack);
            }
            else
            {
                PublishSnapshot();
            }
        }
    }

    /// <summary>
    /// Mengatur mode driver (WASAPI Exclusive vs WASAPI Shared).
    /// </summary>
    public void SetDriverType(AudioDriverType driverType)
    {
        lock (_lock)
        {
            _driverType = driverType;
            if (_currentTrack != null && _outputEngine.Status == PlaybackStatus.Playing)
            {
                _ = StartTrackInternalAsync(_currentTrack);
            }
            else
            {
                PublishSnapshot();
            }
        }
    }

    /// <summary>
    /// Mengatur ukuran buffer latensi hardware (20 ms s/d 250 ms).
    /// </summary>
    public void SetLatency(int latencyMs)
    {
        lock (_lock)
        {
            _latencyMs = Math.Clamp(latencyMs, 20, 250);
            if (_currentTrack != null && _outputEngine.Status == PlaybackStatus.Playing)
            {
                _ = StartTrackInternalAsync(_currentTrack);
            }
            else
            {
                PublishSnapshot();
            }
        }
    }

    /// <summary>
    /// Mengatur mode pemutaran berkas DSD (DoP vs Decimation FIR Filter).
    /// </summary>
    public void SetDsdMode(DsdPlaybackMode dsdMode)
    {
        _dsdMode = dsdMode;
        if (_currentTrack != null && _currentTrack.StreamSpec.IsDsd)
        {
            _ = StartTrackInternalAsync(_currentTrack);
        }
    }

    /// <summary>
    /// Menyimpan sesi pemutaran saat ini ke database SQLite (Lagu, Posisi ms, Shuffle, Repeat, Queue).
    /// </summary>
    public void SaveSession(DatabaseManager db)
    {
        ArgumentNullException.ThrowIfNull(db);

        int timeMs = (int)(_activeQueue?.CurrentTime.TotalMilliseconds ?? 0);
        db.SavePlaybackSession(
            _currentTrack?.Id,
            timeMs,
            _queueManager.RepeatMode,
            _queueManager.IsShuffleEnabled,
            _queueManager.GetTrackIds()
        );
    }

    /// <summary>
    /// Memulihkan sesi pemutaran terakhir dari database SQLite (antrean, posisi, mode repeat, dan shuffle).
    /// </summary>
    public async Task<bool> RestoreSessionAsync(DatabaseManager db, bool autoPlay = false)
    {
        ArgumentNullException.ThrowIfNull(db);

        var session = db.LoadPlaybackSession();
        if (session == null) return false;

        var (currentTrackId, currentTimeMs, repeatMode, isShuffle, queueTrackIds) = session.Value;

        var tracks = new List<Track>();
        foreach (var id in queueTrackIds)
        {
            var t = db.GetTrackById(id);
            if (t != null && File.Exists(t.FilePath))
            {
                tracks.Add(t);
            }
        }

        if (tracks.Count == 0) return false;

        int startIndex = 0;
        if (currentTrackId.HasValue)
        {
            int foundIdx = tracks.FindIndex(t => t.Id == currentTrackId.Value);
            if (foundIdx >= 0) startIndex = foundIdx;
        }

        _queueManager.SetQueue(tracks, startIndex);
        _queueManager.RepeatMode = repeatMode;
        if (isShuffle)
        {
            _queueManager.SetShuffle(true);
        }

        var targetTrack = _queueManager.CurrentTrack;
        if (targetTrack != null)
        {
            if (autoPlay)
            {
                await StartTrackInternalAsync(targetTrack);
                if (currentTimeMs > 0)
                {
                    Seek(TimeSpan.FromMilliseconds(currentTimeMs));
                }
            }
            else
            {
                _currentTrack = targetTrack;
                PublishSnapshot();
            }
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------------
    // LOGIKA INTERNAL MESIN PEMUTAR & ORKESTRASI
    // ---------------------------------------------------------------------------------------------
    private async Task StartTrackInternalAsync(Track track)
    {
        await Task.Yield();

        if (track == null || string.IsNullOrWhiteSpace(track.FilePath) || !File.Exists(track.FilePath))
        {
            System.Diagnostics.Debug.WriteLine($"[AudioController Warning] Berkas musik tidak ditemukan: {track?.FilePath}");
            Stop();
            return;
        }

        if (!await _playbackLock.WaitAsync(3000))
        {
            // Drop concurrent overlapping requests
            return;
        }

        try
        {
            _outputEngine.Stop();

            lock (_lock)
            {
                _activeQueue?.Dispose();

                // 1. Buat stream audio melalui AudioDecoderFactory
                var rawStream = AudioDecoderFactory.OpenStream(track.FilePath, _dsdMode);

                // 2. Bungkus ke dalam antrean Gapless dengan proteksi Anti-Pop
                _activeQueue = new GaplessStreamQueue(rawStream);
                _activeQueue.TrackTransitioned += OnGaplessTrackTransitioned;
                _activeQueue.FormatChangeRequired += OnGaplessFormatChangeRequired;
                _activeQueue.QueueEnded += OnGaplessQueueEnded;

                _currentTrack = track;
                _currentDevice ??= _deviceEnumerator.GetDefaultRenderDevice();

                // 3. Inisialisasi engine output hardware (sinkronkan volume sebelum & sesudah init)
                if (_currentDevice != null)
                {
                    _outputEngine.Volume = _volume;
                    _outputEngine.Initialize(_currentDevice, _activeQueue, latencyMs: _latencyMs, driverType: _driverType);
                    _outputEngine.Volume = _volume;
                }

                // 4. Pre-buffer lagu berikutnya ke dalam memori jika tersedia di antrean
                PrimeNextTrackInQueue();

                // 5. Audit Jalur Sinyal (Signal Path Live)
                _currentSignalPath = SignalPathAuditor.Audit(_activeQueue, _outputEngine, _driverType);
            }

            // 6. Mulai aliran audio ke speaker
            _outputEngine.Play();

            TrackChanged?.Invoke(this, track);
            SignalPathUpdated?.Invoke(this, _currentSignalPath);
            PublishSnapshot();

            // 7. Luncurkan analisis keaslian spektrum Nyquist FFT secara asinkron di latar belakang
            _ = RunBackgroundIntegrityAnalysisAsync(track);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AudioController Error] Gagal memutar berkas '{track.FilePath}': {ex.Message}");
            Stop();
        }
        finally
        {
            _playbackLock.Release();
        }
    }

    private void PrimeNextTrackInQueue()
    {
        if (_activeQueue == null) return;

        var nextTrack = _queueManager.PeekNextTrack();
        if (nextTrack != null && nextTrack.FilePath != _currentTrack?.FilePath)
        {
            try
            {
                var nextStream = AudioDecoderFactory.OpenStream(nextTrack.FilePath, _dsdMode);
                _activeQueue.EnqueueNext(nextStream);
            }
            catch
            {
                // Pengabaian graceful jika berkas berikutnya bermasalah
            }
        }
    }

    private async Task RunBackgroundIntegrityAnalysisAsync(Track track)
    {
        try
        {
            var (hwSpec, hasActivePath, isExclusive, driverType) = GetHardwareAuditContext(track.FilePath);
            var report = await NyquistSpectrumAnalyzer.AnalyzeAsync(
                track.FilePath,
                hwSpec,
                hasActivePath,
                isExclusive,
                driverType);
            lock (_lock)
            {
                if (_currentTrack?.FilePath == track.FilePath)
                {
                    _currentIntegrityReport = report;
                }
            }
            IntegrityReportUpdated?.Invoke(this, report);
            PublishSnapshot();
        }
        catch
        {
            // Analisis latar belakang gagal tanpa mengganggu pemutaran
        }
    }

    private void OnGaplessTrackTransitioned(object? sender, TrackTransitionEventArgs e)
    {
        var nextTrack = _queueManager.AdvanceNext();
        lock (_lock)
        {
            _currentTrack = nextTrack;
            if (_activeQueue != null)
            {
                _currentSignalPath = SignalPathAuditor.Audit(_activeQueue, _outputEngine, _driverType);
            }
        }

        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            lock (_lock)
            {
                PrimeNextTrackInQueue();
            }
        });

        if (_currentTrack != null)
        {
            TrackChanged?.Invoke(this, _currentTrack);
            _ = RunBackgroundIntegrityAnalysisAsync(_currentTrack);
        }

        SignalPathUpdated?.Invoke(this, _currentSignalPath!);
        PublishSnapshot();
    }

    private void OnGaplessFormatChangeRequired(object? sender, IAudioStream nextStream)
    {
        _ = NextAsync();
    }

    private void OnGaplessQueueEnded(object? sender, EventArgs e)
    {
        _ = NextAsync();
    }

    private void OnOutputEngineStatusChanged(object? sender, PlaybackStatus status)
    {
        PublishSnapshot();
    }

    private void OnPositionTimerTick(object? state)
    {
        if (_outputEngine.Status == PlaybackStatus.Playing)
        {
            PublishSnapshot();
        }
    }

    private PlaybackStateSnapshot GenerateSnapshot()
    {
        lock (_lock)
        {
            TimeSpan curTime = _activeQueue?.CurrentTime ?? TimeSpan.Zero;
            TimeSpan totalTime = _activeQueue?.TotalTime ?? _currentTrack?.Duration ?? TimeSpan.Zero;

            return new PlaybackStateSnapshot
            {
                CurrentTrack = _currentTrack,
                Status = _outputEngine.Status,
                CurrentTime = curTime,
                TotalTime = totalTime,
                SignalPath = _currentSignalPath,
                IntegrityReport = _currentIntegrityReport,
                Volume = _volume,
                IsMuted = _isMuted,
                IsExclusiveLocked = _outputEngine.IsExclusiveLocked,
                ActiveDevice = _currentDevice,
                QueueIndex = _queueManager.CurrentIndex,
                QueueCount = _queueManager.Count,
                RepeatMode = _queueManager.RepeatMode,
                IsShuffleEnabled = _queueManager.IsShuffleEnabled
            };
        }
    }

    private void PublishSnapshot()
    {
        var snapshot = GenerateSnapshot();
        _stateChannel.Writer.TryWrite(snapshot);
        StateChanged?.Invoke(this, snapshot);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _positionTimer.Dispose();
        _activeQueue?.Dispose();
        _outputEngine.Dispose();
        _deviceEnumerator.Dispose();

        GC.SuppressFinalize(this);
    }
}
