using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AuraAudio.Audio;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Audio.Controller;
using AuraAudio.Core.Models;
using AuraAudio.UI.ViewModels;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace AuraAudio.Tests;

[CollectionDefinition("AudioRenderStability", DisableParallelization = true)]
public sealed class AudioRenderStabilityCollection { }

[Collection("AudioRenderStability")]
public sealed class AudioRenderStabilityTests
{
    private readonly ITestOutputHelper _output;

    public AudioRenderStabilityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static class NativeTimer
    {
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);
    }

    /// <summary>
    /// Deterministic, zero-hardware audio sink that paces callbacks at real-time WASAPI cadence
    /// through the real <see cref="SilenceAwareWaveProvider"/> (including MMCSS + thread priority).
    /// </summary>
    private sealed class PacedNullSinkEngine : IAudioOutputEngine
    {
        private readonly double _periodMs;
        private readonly double _underrunBudgetMs;
        private readonly bool _capturePcm;
        private readonly object _sync = new();

        private SilenceAwareWaveProvider? _silenceProvider;
        private AudioDevice? _device;
        private WaveFormat? _format;
        private PlaybackStatus _status = PlaybackStatus.Stopped;
        private float _volume = 1.0f;
        private Thread? _renderThread;
        private volatile bool _running;
        private MemoryStream? _captured;

        private int _callbackCount;
        private int _lateCount;
        private int _underrunCount;
        private double _maxGapMs;
        private double _maxReadMs;
        private long _emergencyRegId;

        public PacedNullSinkEngine(double periodMs = 20.0, double underrunBudgetMs = 50.0, bool capturePcm = false)
        {
            _periodMs = periodMs;
            _underrunBudgetMs = underrunBudgetMs;
            _capturePcm = capturePcm;
            _emergencyRegId = EmergencyAudioSilencer.Register(EmergencySilenceAndRelease);
        }

        public AudioDevice? CurrentDevice => _device;
        public WaveFormat? CurrentFormat => _format;
        public PlaybackStatus Status => _status;
        public bool IsExclusiveLocked => _status != PlaybackStatus.Stopped;
        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_silenceProvider != null) _silenceProvider.Volume = _volume;
            }
        }
        public event EventHandler<PlaybackStatus>? StatusChanged;

        public int CallbackCount => Volatile.Read(ref _callbackCount);
        public int LateCount => Volatile.Read(ref _lateCount);
        public int UnderrunCount => Volatile.Read(ref _underrunCount);
        public double MaxGapMs => _maxGapMs;
        public double MaxReadMs => _maxReadMs;
        internal SilenceAwareWaveProvider? ActiveSilenceProvider => _silenceProvider;

        public byte[] GetCapturedPcm()
        {
            lock (_sync)
            {
                return _captured?.ToArray() ?? Array.Empty<byte>();
            }
        }

        public void Initialize(AudioDevice device, IWaveProvider waveProvider, int latencyMs = 50, AudioDriverType driverType = AudioDriverType.WASAPI_Exclusive)
        {
            Stop();
            lock (_sync)
            {
                _device = device;
                _format = waveProvider.WaveFormat;
                _silenceProvider = new SilenceAwareWaveProvider(waveProvider, _volume, _periodMs);
                _captured?.Dispose();
                _captured = _capturePcm ? new MemoryStream(2 * 1024 * 1024) : null;
                _callbackCount = 0;
                _lateCount = 0;
                _underrunCount = 0;
                _maxGapMs = 0;
                _maxReadMs = 0;
                _status = PlaybackStatus.Stopped;
            }
        }

        public void Play()
        {
            lock (_sync)
            {
                if (_silenceProvider == null) return;
                _silenceProvider.IsPaused = false;
                if (!_running)
                {
                    _running = true;
                    _renderThread = new Thread(RenderLoop)
                    {
                        IsBackground = true,
                        Name = "TestNullAudioRender",
                        Priority = ThreadPriority.Normal // Intentionally starts Normal; SilenceAwareWaveProvider boosts it
                    };
                    _renderThread.Start();
                }
                _status = PlaybackStatus.Playing;
            }
            StatusChanged?.Invoke(this, PlaybackStatus.Playing);
        }

        public void Pause()
        {
            if (_silenceProvider != null) _silenceProvider.IsPaused = true;
            _status = PlaybackStatus.Paused;
            StatusChanged?.Invoke(this, PlaybackStatus.Paused);
        }

        public void Stop()
        {
            var sp = _silenceProvider;
            if (sp != null) sp.BeginStop();
            _running = false;
            var t = _renderThread;
            _renderThread = null;
            if (t != null && t.IsAlive)
            {
                t.Join(1000);
            }

            lock (_sync)
            {
                _silenceProvider = null;
                _status = PlaybackStatus.Stopped;
            }
            StatusChanged?.Invoke(this, PlaybackStatus.Stopped);
        }

        private void EmergencySilenceAndRelease(int timeoutMs)
        {
            var sp = _silenceProvider;
            if (sp != null) sp.BeginStop();
            _running = false;
            var t = _renderThread;
            _renderThread = null;
            if (t != null && t.IsAlive)
            {
                t.Join(Math.Min(timeoutMs, 100));
            }
            _status = PlaybackStatus.Stopped;
        }

        private void RenderLoop()
        {
            var provider = _silenceProvider;
            var fmt = _format;
            if (provider == null || fmt == null) return;

            int framesPerPeriod = Math.Max(1, (int)Math.Round(fmt.SampleRate * (_periodMs / 1000.0)));
            int bytesPerPeriod = framesPerPeriod * fmt.BlockAlign;
            byte[] buf = new byte[bytesPerPeriod];

            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            long periodTicks = (long)Math.Round(_periodMs * ticksPerMs);
            long nextTick = Stopwatch.GetTimestamp();
            long prevCbTick = nextTick;
            bool first = true;

            while (_running)
            {
                long now = Stopwatch.GetTimestamp();
                long rem = nextTick - now;
                if (rem > 0)
                {
                    double remMs = rem / ticksPerMs;
                    if (remMs > 2.5) Thread.Sleep((int)(remMs - 1.5));
                    while (_running && Stopwatch.GetTimestamp() < nextTick) Thread.SpinWait(32);
                }
                if (!_running) break;

                long cbStart = Stopwatch.GetTimestamp();
                if (!first)
                {
                    double gapMs = (cbStart - prevCbTick) / ticksPerMs;
                    lock (_sync)
                    {
                        if (gapMs > _maxGapMs) _maxGapMs = gapMs;
                        if (gapMs > _periodMs * 1.5) _lateCount++;
                        if (gapMs > _underrunBudgetMs) _underrunCount++;
                    }
                }
                first = false;
                prevCbTick = cbStart;

                long rStart = Stopwatch.GetTimestamp();
                int read = provider.Read(buf.AsSpan(0, bytesPerPeriod));
                double readMs = (Stopwatch.GetTimestamp() - rStart) / ticksPerMs;

                lock (_sync)
                {
                    if (readMs > _maxReadMs) _maxReadMs = readMs;
                    if (readMs > _underrunBudgetMs) _underrunCount++;
                    if (_captured != null && read > 0)
                    {
                        _captured.Write(buf, 0, read);
                    }
                }

                Interlocked.Increment(ref _callbackCount);
                nextTick += periodTicks;
                long after = Stopwatch.GetTimestamp();
                if (after - nextTick > periodTicks * 4) nextTick = after + periodTicks;
            }
        }

        public void Dispose()
        {
            EmergencyAudioSilencer.Unregister(Interlocked.Exchange(ref _emergencyRegId, 0));
            Stop();
            lock (_sync)
            {
                _captured?.Dispose();
                _captured = null;
            }
        }
    }

    private static string WriteSyntheticWav(string dir, string name, int sampleRate, int bitsPerSample, double durationSec, double baseFreqHz)
    {
        string path = Path.Combine(dir, name);
        int channels = 2;
        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channels * bytesPerSample;
        int totalFrames = (int)(sampleRate * durationSec);
        int dataBytes = totalFrames * blockAlign;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        using var bw = new BinaryWriter(fs);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * blockAlign);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataBytes);

        byte[] chunk = new byte[4096 * blockAlign];
        int framesWritten = 0;
        while (framesWritten < totalFrames)
        {
            int batch = Math.Min(4096, totalFrames - framesWritten);
            for (int i = 0; i < batch; i++)
            {
                double t = (framesWritten + i) / (double)sampleRate;
                double sL = 0.25 * Math.Sin(2.0 * Math.PI * baseFreqHz * t)
                          + 0.12 * Math.Sin(2.0 * Math.PI * (baseFreqHz * 3.0) * t)
                          + 0.06 * Math.Sin(2.0 * Math.PI * 12000.0 * t);
                double sR = 0.25 * Math.Sin(2.0 * Math.PI * (baseFreqHz + 2.0) * t)
                          + 0.12 * Math.Sin(2.0 * Math.PI * (baseFreqHz * 3.0 + 3.0) * t)
                          + 0.06 * Math.Sin(2.0 * Math.PI * 12050.0 * t);

                int off = i * blockAlign;
                WriteSample(chunk, off, sL, bitsPerSample);
                WriteSample(chunk, off + bytesPerSample, sR, bitsPerSample);
            }
            fs.Write(chunk, 0, batch * blockAlign);
            framesWritten += batch;
        }

        return path;
    }

    private static void WriteSample(byte[] buf, int off, double norm, int bitsPerSample)
    {
        if (bitsPerSample == 16)
        {
            short v = (short)Math.Clamp((int)Math.Round(norm * 32767.0), short.MinValue, short.MaxValue);
            buf[off] = (byte)(v & 0xFF);
            buf[off + 1] = (byte)((v >> 8) & 0xFF);
        }
        else if (bitsPerSample == 24)
        {
            int v = Math.Clamp((int)Math.Round(norm * 8388607.0), -8388608, 8388607);
            buf[off] = (byte)(v & 0xFF);
            buf[off + 1] = (byte)((v >> 8) & 0xFF);
            buf[off + 2] = (byte)((v >> 16) & 0xFF);
        }
    }

    private static Track MakeTrack(long id, string title, string filePath, uint sampleRate, BitDepth bitDepth, double durationSec)
    {
        var spec = new AudioStreamSpec(AudioCodec.WAV, sampleRate, bitDepth, 2, (uint)(sampleRate * (int)bitDepth * 2 / 1000));
        return new Track(
            id: id,
            filePath: filePath,
            title: title,
            artist: "StabilityTest",
            album: "StabilityTest",
            albumArtist: "StabilityTest",
            sortArtist: "StabilityTest",
            year: 2026,
            trackNumber: (int)id,
            discNumber: 1,
            duration: TimeSpan.FromSeconds(durationSec),
            streamSpec: spec,
            fileSizeBytes: new FileInfo(filePath).Length,
            dateAdded: DateTime.UtcNow);
    }

    private static byte[] DecodeReference(string filePath, double maxSeconds, out WaveFormat fmt)
    {
        using var stream = AudioDecoderFactory.OpenStream(filePath);
        fmt = stream.WaveFormat;
        int bytes = (int)(fmt.AverageBytesPerSecond * maxSeconds);
        bytes -= bytes % fmt.BlockAlign;
        byte[] buf = new byte[bytes];
        int total = 0;
        while (total < bytes)
        {
            int r = stream.Read(buf.AsSpan(total, bytes - total));
            if (r <= 0) break;
            total += r;
        }
        if (total != buf.Length) Array.Resize(ref buf, total);
        return buf;
    }

    private static (long MismatchBytes, double MaxSampleJump) ComparePcmAndJump(byte[] rendered, byte[] reference, WaveFormat fmt, int skipBytes)
    {
        int cmpLen = Math.Min(rendered.Length, reference.Length);
        long mismatch = 0;
        for (int i = skipBytes; i < cmpLen; i++)
        {
            if (rendered[i] != reference[i]) mismatch++;
        }

        int blockAlign = fmt.BlockAlign;
        int channels = fmt.Channels;
        int bps = fmt.BitsPerSample / 8;
        double[] prev = new double[channels];
        bool hasPrev = false;
        double maxJump = 0;

        for (int off = skipBytes; off + blockAlign <= rendered.Length; off += blockAlign)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                int sOff = off + ch * bps;
                double val = fmt.BitsPerSample switch
                {
                    16 => (short)(rendered[sOff] | (rendered[sOff + 1] << 8)) / 32768.0,
                    24 => (((rendered[sOff] | (rendered[sOff + 1] << 8) | (rendered[sOff + 2] << 16)) << 8) >> 8) / 8388608.0,
                    _ => 0.0
                };
                if (hasPrev)
                {
                    double jump = Math.Abs(val - prev[ch]);
                    if (jump > maxJump) maxJump = jump;
                }
                prev[ch] = val;
            }
            hasPrev = true;
        }

        return (mismatch, maxJump);
    }

    [Fact]
    public async Task PlaybackPlus200AnalyzeSpamIn20Seconds_ZeroExceptionsSingleConcurrencyAndBitExactOutput()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"aura_stability_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AudioRenderDiagnostics.SetEnabled(true);
        NativeTimer.timeBeginPeriod(1);

        try
        {
            string trackAPath = WriteSyntheticWav(tempDir, "track_a_48k_24b.wav", 48_000, 24, durationSec: 30.0, baseFreqHz: 440.0);
            string trackBPath = WriteSyntheticWav(tempDir, "track_b_44k_16b.wav", 44_100, 16, durationSec: 30.0, baseFreqHz: 523.25);

            var trackA = MakeTrack(1, "Track A", trackAPath, 48_000, BitDepth.Bit24, 30.0);
            var trackB = MakeTrack(2, "Track B", trackBPath, 44_100, BitDepth.Bit16, 30.0);
            byte[] refA = DecodeReference(trackAPath, 12.0, out WaveFormat fmtA);

            // PART 1: Bit-exact PCM continuity under rapid 50 Analyze spam (no track switch)
            using (var bitExactEngine = new PacedNullSinkEngine(periodMs: 20.0, underrunBudgetMs: 50.0, capturePcm: true))
            using (var bitExactController = new AudiophilePlaybackController(bitExactEngine))
            using (var bitExactInspector = new InspectorViewModel(bitExactController, marshalToUi: a => a()))
            {
                await bitExactController.PlayTrackAsync(trackA);
                var spamTasks = new List<Task>(50);
                for (int i = 0; i < 50; i++)
                {
                    spamTasks.Add(bitExactInspector.AnalyzeCurrentTrackAsync());
                    await Task.Delay(60);
                }
                await Task.WhenAll(spamTasks);
                bitExactController.Stop();

                byte[] rendered = bitExactEngine.GetCapturedPcm();
                int skipBytes = (int)(fmtA.AverageBytesPerSecond * 0.010);
                skipBytes -= skipBytes % fmtA.BlockAlign;
                Assert.True(rendered.Length > skipBytes + fmtA.AverageBytesPerSecond * 2, "Expected at least 2 seconds of rendered PCM.");

                var (mismatchBytes, maxJump) = ComparePcmAndJump(rendered, refA, fmtA, skipBytes);
                _output.WriteLine($"[BitExact Under Spam] RenderedBytes={rendered.Length}, MismatchBytes={mismatchBytes}, MaxSampleJump={maxJump:F4}, Underruns={bitExactEngine.UnderrunCount}, MaxGapMs={bitExactEngine.MaxGapMs:F2}, MaxReadMs={bitExactEngine.MaxReadMs:F2}");
                Assert.Equal(0, mismatchBytes);
                Assert.Equal(0, bitExactEngine.UnderrunCount);
            }

            // PART 2: 200 Analyze requests over 20 seconds + concurrent playback + track changes
            AudioRenderDiagnostics.Reset();
            int gen2Before = GC.CollectionCount(2);
            TimeSpan gcPauseBefore = GC.GetTotalPauseDuration();

            using var engine = new PacedNullSinkEngine(periodMs: 20.0, underrunBudgetMs: 50.0, capturePcm: false);
            using var controller = new AudiophilePlaybackController(engine);
            using var inspector = new InspectorViewModel(controller, marshalToUi: a => a());

            var publishedMismatches = new List<string>();
            inspector.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(InspectorViewModel.CurrentIntegrityReport))
                {
                    var rep = inspector.CurrentIntegrityReport;
                    var cur = controller.CurrentTrack;
                    if (rep != null && cur != null &&
                        !string.Equals(Path.GetFullPath(rep.AnalyzedFilePath ?? ""), Path.GetFullPath(cur.FilePath), StringComparison.OrdinalIgnoreCase))
                    {
                        lock (publishedMismatches)
                        {
                            publishedMismatches.Add($"Published {rep.AnalyzedFilePath} while current track was {cur.FilePath}");
                        }
                    }
                }
            };

            await controller.PlayQueueAsync(new[] { trackA, trackB }, 0);

            int exceptions = 0;
            var allTasks = new List<Task>(200);
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < 200; i++)
            {
                if (i == 50 || i == 100 || i == 150)
                {
                    var nextTrack = (i / 50) % 2 == 1 ? trackB : trackA;
                    await controller.PlayTrackAsync(nextTrack);
                }

                allTasks.Add(inspector.AnalyzeCurrentTrackAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Interlocked.Increment(ref exceptions);
                    }
                }));

                // Pace 200 requests cleanly across 20 seconds (100 ms interval)
                long targetMs = (i + 1) * 100L;
                long waitMs = targetMs - sw.ElapsedMilliseconds;
                if (waitMs > 0)
                {
                    await Task.Delay((int)waitMs);
                }
            }

            await Task.WhenAll(allTasks);
            sw.Stop();
            controller.Stop();

            var diag = AudioRenderDiagnostics.GetSnapshot();
            int gen2Delta = GC.CollectionCount(2) - gen2Before;
            double gcPauseMs = (GC.GetTotalPauseDuration() - gcPauseBefore).TotalMilliseconds;

            _output.WriteLine($"[200-in-20s Spam + TrackChanges] ElapsedMs={sw.ElapsedMilliseconds}, Exceptions={exceptions}, PeakConcurrentAnalyses={diag.PeakConcurrentAnalyses}, StalePublished={publishedMismatches.Count}, RenderThreadPriority={diag.RenderThreadPriority}, MmcssRegistered={diag.MmcssRegistered}, LockContention={diag.LockContentionCount}, MaxLockWaitMs={diag.MaxLockWaitMs:F3}, DispatcherWaits={diag.DispatcherWaitCount}, AnalysisLockWaits={diag.AnalysisLockWaitCount}, Underruns={engine.UnderrunCount}, MaxGapMs={engine.MaxGapMs:F2}, MaxReadMs={engine.MaxReadMs:F2}, Gen2={gen2Delta}, GcPauseMs={gcPauseMs:F2}");

            Assert.Equal(0, exceptions);
            Assert.True(diag.PeakConcurrentAnalyses <= 1, $"Expected <= 1 concurrent analysis, saw {diag.PeakConcurrentAnalyses}");
            Assert.Empty(publishedMismatches);
            Assert.Equal(ThreadPriority.Highest, diag.RenderThreadPriority);
            Assert.True(diag.MmcssRegistered, "Expected MMCSS 'Pro Audio' registration on render thread.");
            Assert.Equal(0, diag.DispatcherWaitCount);
            Assert.Equal(0, diag.AnalysisLockWaitCount);
            Assert.Equal(0, engine.UnderrunCount);
            Assert.NotNull(inspector.CurrentIntegrityReport);
            Assert.Equal(
                Path.GetFullPath(controller.CurrentTrack!.FilePath),
                Path.GetFullPath(inspector.CurrentIntegrityReport!.AnalyzedFilePath!));
        }
        finally
        {
            NativeTimer.timeEndPeriod(1);
            AudioRenderDiagnostics.SetEnabled(false);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EmergencyAudioSilencer_SilencesOutputImmediatelyWithinBoundedTimeout()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"aura_emerg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string wavPath = WriteSyntheticWav(tempDir, "loud.wav", 48_000, 16, durationSec: 5.0, baseFreqHz: 1000.0);
            var track = MakeTrack(1, "Loud", wavPath, 48_000, BitDepth.Bit16, 5.0);

            using var engine = new PacedNullSinkEngine(periodMs: 20.0, capturePcm: false);
            using var stream = AudioDecoderFactory.OpenStream(wavPath);
            using var queue = new GaplessStreamQueue(stream);
            engine.Initialize(new AudioDevice("null", "Null", AudioDriverType.WASAPI_Exclusive, new uint[] { 48_000 }, BitDepth.Bit16, true), queue);
            engine.Play();

            var sp = engine.ActiveSilenceProvider;
            Assert.NotNull(sp);

            // Before emergency silence: non-zero audio PCM flows
            byte[] probe = new byte[1920];
            int r1 = sp!.Read(probe.AsSpan());
            Assert.True(r1 > 0);
            Assert.Contains(probe, b => b != 0);

            // Simulate fatal UI crash handler calling EmergencyAudioSilencer.SilenceAllActive(150)
            var sw = Stopwatch.StartNew();
            EmergencyAudioSilencer.SilenceAllActive(timeoutMs: 150);
            sw.Stop();

            Assert.True(sw.ElapsedMilliseconds <= 200, $"Emergency silence took {sw.ElapsedMilliseconds} ms");
            Assert.True(sp.IsPaused);
            Assert.Equal(PlaybackStatus.Stopped, engine.Status);

            // After emergency silence: provider emits strictly 0x00 digital silence
            Array.Fill(probe, (byte)0x55);
            int r2 = sp.Read(probe.AsSpan());
            Assert.Equal(probe.Length, r2);
            Assert.All(probe, b => Assert.Equal(0, b));

            _output.WriteLine($"[EmergencySilence] ElapsedMs={sw.ElapsedMilliseconds}, IsPaused={sp.IsPaused}, EngineStatus={engine.Status}, AllZeroAfterSilence=True");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PooledAnalysisBuffers_SteadyStateEliminatesLohThrash()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"aura_pool_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string wavPath = WriteSyntheticWav(tempDir, "pool_24b_48k.wav", 48_000, 24, durationSec: 12.0, baseFreqHz: 440.0);

            // Warmup call populates LargePcmBufferPool and ArrayPool<T>.Shared
            var warm = await NyquistSpectrumAnalyzer.AnalyzeAsync(wavPath);
            Assert.NotNull(warm);

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            int gen2Before = GC.CollectionCount(2);
            long allocBefore = GC.GetTotalAllocatedBytes(precise: true);

            const int iterations = 10;
            for (int i = 0; i < iterations; i++)
            {
                var rep = await NyquistSpectrumAnalyzer.AnalyzeAsync(wavPath);
                Assert.Equal(warm.Verdict, rep.Verdict);
            }

            long allocPerCallBytes = (GC.GetTotalAllocatedBytes(precise: true) - allocBefore) / iterations;
            int gen2Delta = GC.CollectionCount(2) - gen2Before;

            _output.WriteLine($"[PooledAnalysisBuffers] Iterations={iterations}, AvgAllocKBPerCall={allocPerCallBytes / 1024.0:F1} KB (vs ~2530 KB pre-fix), Gen2Delta={gen2Delta}");
            Assert.True(allocPerCallBytes < 512 * 1024, $"Expected < 512 KB/call after pooling, got {allocPerCallBytes / 1024.0:F1} KB/call.");
            Assert.Equal(0, gen2Delta);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SoakTest_PeriodicAnalysisAndTrackChanges_ReportsUnderrunsGapGcAndMemory()
    {
        // Configurable soak:
        // - Default routine dotnet test: 30 minutes of rendered PCM audio (90,000 x 20ms callbacks) in accelerated mode
        //   + 15 seconds real-time paced verification with periodic analysis & track changes.
        // - Set AURA_SOAK_MINUTES=30 for a full 30-minute real-time wall-clock paced soak.
        string? envMin = Environment.GetEnvironmentVariable("AURA_SOAK_MINUTES");
        double realTimeMinutes = double.TryParse(envMin, System.Globalization.CultureInfo.InvariantCulture, out double parsed) && parsed > 0
            ? parsed
            : 0.25; // 15 seconds default real-time paced check

        string tempDir = Path.Combine(Path.GetTempPath(), $"aura_soak_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        AudioRenderDiagnostics.SetEnabled(true);
        NativeTimer.timeBeginPeriod(1);

        try
        {
            string wavA = WriteSyntheticWav(tempDir, "soak_a.wav", 48_000, 24, durationSec: 20.0, baseFreqHz: 440.0);
            string wavB = WriteSyntheticWav(tempDir, "soak_b.wav", 48_000, 24, durationSec: 20.0, baseFreqHz: 660.0);
            var trackA = MakeTrack(1, "Soak A", wavA, 48_000, BitDepth.Bit24, 20.0);
            var trackB = MakeTrack(2, "Soak B", wavB, 48_000, BitDepth.Bit24, 20.0);

            // Phase A: Accelerated 30-minute audio-equivalent soak (90,000 x 20ms buffers = 1,800s of 48kHz/24-bit PCM)
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            long memStartAccel = GC.GetTotalMemory(forceFullCollection: true);
            int gen2StartAccel = GC.CollectionCount(2);
            TimeSpan gcPauseStartAccel = GC.GetTotalPauseDuration();

            const int callbacks30Min = 90_000; // 90,000 * 20 ms = 1,800,000 ms = 30.0 minutes of audio
            int bytesPer20Ms = (48_000 / 50) * 6; // 960 frames * 6 bytes = 5,760 bytes
            byte[] renderBuf = new byte[bytesPer20Ms];
            int trackSwitches = 0;
            int analysesRun = 0;
            double maxRenderReadMs = 0;

            IAudioStream currentStream = AudioDecoderFactory.OpenStream(wavA);
            var queue = new GaplessStreamQueue(currentStream);
            var silenceProvider = new SilenceAwareWaveProvider(queue, 1.0f, 20.0);
            Task? inflightAnalysis = null;

            try
            {
                for (int cb = 0; cb < callbacks30Min; cb++)
                {
                    if (cb > 0 && cb % 3_000 == 0) // Every 60s of audio (30 track switches over 30 min)
                    {
                        trackSwitches++;
                        string nextFile = (trackSwitches % 2 == 0) ? wavA : wavB;
                        queue.EnqueueNext(AudioDecoderFactory.OpenStream(nextFile));
                        // Jump to EOF of current stream so gapless transition occurs cleanly
                        queue.Position = Math.Max(0, queue.Length - bytesPer20Ms);
                    }

                    if (cb % 1_500 == 0 && (inflightAnalysis == null || inflightAnalysis.IsCompleted)) // Every 30s of audio (~60 analyses over 30 min)
                    {
                        analysesRun++;
                        string analyzeFile = (analysesRun % 2 == 0) ? wavA : wavB;
                        inflightAnalysis = NyquistSpectrumAnalyzer.AnalyzeAsync(analyzeFile);
                    }

                    long t0 = Stopwatch.GetTimestamp();
                    int read = silenceProvider.Read(renderBuf.AsSpan());
                    double readMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (readMs > maxRenderReadMs) maxRenderReadMs = readMs;

                    if (read == 0)
                    {
                        queue.Position = 0;
                    }
                }

                if (inflightAnalysis != null) await inflightAnalysis;
            }
            finally
            {
                silenceProvider.BeginStop();
                queue.Dispose();
            }

            long memEndAccel = GC.GetTotalMemory(forceFullCollection: true);
            int gen2Accel = GC.CollectionCount(2) - gen2StartAccel;
            double gcPauseAccelMs = (GC.GetTotalPauseDuration() - gcPauseStartAccel).TotalMilliseconds;
            double memGrowthAccelMb = (memEndAccel - memStartAccel) / (1024.0 * 1024.0);

            _output.WriteLine($"[30-Min Audio-Equivalent Soak] Callbacks={callbacks30Min} (30.0 min PCM), TrackSwitches={trackSwitches}, Analyses={analysesRun}, MaxReadMs={maxRenderReadMs:F2}, Gen2={gen2Accel}, GcPauseMs={gcPauseAccelMs:F2}, ManagedMemGrowthMB={memGrowthAccelMb:F2}");
            Assert.True(maxRenderReadMs < 50.0, $"Render read exceeded 50ms budget: {maxRenderReadMs:F2} ms");
            Assert.True(memGrowthAccelMb < 25.0, $"Unexpected managed memory growth: {memGrowthAccelMb:F2} MB");

            // Phase B: Real-time wall-clock paced soak (default 15s, or 30 min with AURA_SOAK_MINUTES=30)
            AudioRenderDiagnostics.Reset();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            long memStartRt = GC.GetTotalMemory(forceFullCollection: true);
            long wsStartRt = Process.GetCurrentProcess().WorkingSet64;
            int gen2StartRt = GC.CollectionCount(2);
            TimeSpan gcPauseStartRt = GC.GetTotalPauseDuration();

            using var engine = new PacedNullSinkEngine(periodMs: 20.0, underrunBudgetMs: 50.0, capturePcm: false);
            using var controller = new AudiophilePlaybackController(engine);
            using var inspector = new InspectorViewModel(controller, marshalToUi: a => a());

            await controller.PlayQueueAsync(new[] { trackA, trackB }, 0);

            var targetDuration = TimeSpan.FromMinutes(realTimeMinutes);
            var sw = Stopwatch.StartNew();
            int rtSwitches = 0;
            int rtAnalyses = 0;

            while (sw.Elapsed < targetDuration)
            {
                rtAnalyses++;
                await inspector.AnalyzeCurrentTrackAsync();
                if (rtAnalyses % 3 == 0)
                {
                    rtSwitches++;
                    await controller.PlayTrackAsync((rtSwitches % 2 == 0) ? trackA : trackB);
                }
                await Task.Delay(500);
            }

            controller.Stop();

            long memEndRt = GC.GetTotalMemory(forceFullCollection: true);
            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            int gen2Rt = GC.CollectionCount(2) - gen2StartRt;
            double gcPauseRtMs = (GC.GetTotalPauseDuration() - gcPauseStartRt).TotalMilliseconds;
            double memGrowthRtMb = (memEndRt - memStartRt) / (1024.0 * 1024.0);
            double wsGrowthRtMb = (proc.WorkingSet64 - wsStartRt) / (1024.0 * 1024.0);
            var diag = AudioRenderDiagnostics.GetSnapshot();

            _output.WriteLine($"[Real-Time Paced Soak ({realTimeMinutes:F2} min)] Callbacks={engine.CallbackCount}, TrackSwitches={rtSwitches}, Analyses={rtAnalyses}, Underruns={engine.UnderrunCount}, MaxGapMs={engine.MaxGapMs:F2}, MaxReadMs={engine.MaxReadMs:F2}, PeakConc={diag.PeakConcurrentAnalyses}, Gen2={gen2Rt}, GcPauseMs={gcPauseRtMs:F2}, ManagedGrowthMB={memGrowthRtMb:F2}, WorkingSetGrowthMB={wsGrowthRtMb:F2}");

            Assert.Equal(0, engine.UnderrunCount);
            Assert.True(diag.PeakConcurrentAnalyses <= 1);
            Assert.True(memGrowthRtMb < 25.0, $"Unexpected real-time soak memory growth: {memGrowthRtMb:F2} MB");
        }
        finally
        {
            NativeTimer.timeEndPeriod(1);
            AudioRenderDiagnostics.SetEnabled(false);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
