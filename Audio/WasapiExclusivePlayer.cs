using System;
using System.Threading;
using AuraAudio.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

#pragma warning disable CS0618 // WasapiOut is used for robust exclusive mode stream control

namespace AuraAudio.Audio;

/// <summary>
/// Pembungkus stream audio yang menyalurkan keheningan digital (zero-byte PCM) saat pause / stop,
/// sekaligus menerapkan kontrol volume digital halus bebas-klik (smooth perceptual taper) untuk
/// mode WASAPI Exclusive maupun Shared, dengan passthrough bit-exact 100% pada volume 1.0f.
/// </summary>
internal sealed class SilenceAwareWaveProvider : IWaveProvider
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly IWaveProvider _source;
    public WaveFormat WaveFormat => _source.WaveFormat;
    public volatile bool IsPaused;
    private float _targetVolume = 1.0f;
    private double _smoothedGain = 1.0;

    public float Volume
    {
        get => Volatile.Read(ref _targetVolume);
        set => Volatile.Write(ref _targetVolume, Math.Clamp(value, 0f, 1f));
    }

    public SilenceAwareWaveProvider(IWaveProvider source, float initialVolume = 1.0f)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        float clamped = Math.Clamp(initialVolume, 0f, 1f);
        _targetVolume = clamped;
        _smoothedGain = clamped >= 0.9999f ? 1.0 : (clamped <= 0.0001f ? 0.0 : clamped * clamped);
    }

    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;

        if (IsPaused)
        {
            destination.Clear();
            return destination.Length;
        }

        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = _source.Read(destination.Slice(totalRead));
            if (read <= 0) break;
            totalRead += read;
        }

        if (totalRead < destination.Length)
        {
            destination.Slice(totalRead).Clear();
        }

        if (totalRead > 0)
        {
            ApplyVolumeGain(destination.Slice(0, totalRead));
        }

        return totalRead;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        return Read(new Span<byte>(buffer, offset, count));
    }

    private void ApplyVolumeGain(Span<byte> pcm)
    {
        float vol = Volatile.Read(ref _targetVolume);
        double targetGain = vol >= 0.9999f ? 1.0 : (vol <= 0.0001f ? 0.0 : (double)vol * vol);

        // Protect DoP (DSD over PCM) marker framing: never rescale DoP bits; only allow full unity or digital silence
        bool isDoP = _source is Dsd.DopStreamProvider
            || (_source is GaplessStreamQueue q && q.CurrentStream is Dsd.DopStreamProvider);
        if (isDoP)
        {
            if (targetGain <= 0.00001)
            {
                pcm.Clear();
                _smoothedGain = 0.0;
            }
            else
            {
                _smoothedGain = 1.0;
            }
            return;
        }

        // Bit-exact zero-modification passthrough at 100% unity gain once settled
        if (targetGain >= 0.99999 && _smoothedGain >= 0.99999)
        {
            _smoothedGain = 1.0;
            return;
        }

        // Fast exact digital silence once settled at 0%
        if (targetGain <= 0.00001 && _smoothedGain <= 0.00001)
        {
            pcm.Clear();
            _smoothedGain = 0.0;
            return;
        }

        WaveFormat fmt = _source.WaveFormat;
        int channels = Math.Max(1, fmt.Channels);
        int bitsPerSample = fmt.BitsPerSample;
        int bytesPerSample = bitsPerSample / 8;
        int bytesPerFrame = fmt.BlockAlign;
        if (bytesPerFrame <= 0 || bytesPerSample <= 0) return;

        int frames = pcm.Length / bytesPerFrame;
        if (frames <= 0) return;

        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
            || (fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);

        // ~5 ms exponential smoothing time constant for click-free, zipper-free volume changes
        double rampFrames = Math.Max(1.0, fmt.SampleRate * 0.005);
        double alpha = 1.0 - Math.Exp(-1.0 / rampFrames);
        double currentGain = _smoothedGain;

        for (int f = 0; f < frames; f++)
        {
            double diff = targetGain - currentGain;
            if (Math.Abs(diff) < 1e-6)
            {
                currentGain = targetGain;
            }
            else
            {
                currentGain += diff * alpha;
            }

            int frameOffset = f * bytesPerFrame;
            for (int ch = 0; ch < channels; ch++)
            {
                int sampleOffset = frameOffset + ch * bytesPerSample;
                if (sampleOffset + bytesPerSample > pcm.Length) break;

                switch (bitsPerSample)
                {
                    case 16:
                    {
                        short s = (short)(pcm[sampleOffset] | (pcm[sampleOffset + 1] << 8));
                        int scaled = (int)Math.Round(s * currentGain);
                        if (scaled > short.MaxValue) scaled = short.MaxValue;
                        else if (scaled < short.MinValue) scaled = short.MinValue;
                        pcm[sampleOffset] = (byte)(scaled & 0xFF);
                        pcm[sampleOffset + 1] = (byte)((scaled >> 8) & 0xFF);
                        break;
                    }

                    case 24:
                    {
                        int s = pcm[sampleOffset] | (pcm[sampleOffset + 1] << 8) | (pcm[sampleOffset + 2] << 16);
                        if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
                        int scaled = (int)Math.Round(s * currentGain);
                        if (scaled > 8388607) scaled = 8388607;
                        else if (scaled < -8388608) scaled = -8388608;
                        pcm[sampleOffset] = (byte)(scaled & 0xFF);
                        pcm[sampleOffset + 1] = (byte)((scaled >> 8) & 0xFF);
                        pcm[sampleOffset + 2] = (byte)((scaled >> 16) & 0xFF);
                        break;
                    }

                    case 32 when isFloat:
                    {
                        float s = BitConverter.ToSingle(pcm.Slice(sampleOffset, 4));
                        float scaled = (float)(s * currentGain);
                        BitConverter.TryWriteBytes(pcm.Slice(sampleOffset, 4), scaled);
                        break;
                    }

                    case 32:
                    {
                        int s = BitConverter.ToInt32(pcm.Slice(sampleOffset, 4));
                        double scaledD = Math.Round(s * currentGain);
                        int scaled = scaledD >= int.MaxValue ? int.MaxValue
                            : (scaledD <= int.MinValue ? int.MinValue : (int)scaledD);
                        BitConverter.TryWriteBytes(pcm.Slice(sampleOffset, 4), scaled);
                        break;
                    }
                }
            }
        }

        _smoothedGain = currentGain;
    }
}

/// <summary>
/// Implementasi mesin output audio Bit-Perfect berbasis Windows Core Audio (WASAPI Exclusive Mode).
/// Mengunci endpoint hardware secara eksklusif dan membypass Windows Audio Engine / AudioSrv mixer.
/// </summary>
public class WasapiExclusivePlayer : IAudioOutputEngine
{
    private WasapiOut? _wasapiOut;
    private MMDevice? _mmDevice;
    private AudioDevice? _currentDevice;
    private WaveFormat? _currentFormat;
    private SilenceAwareWaveProvider? _silenceProvider;
    private PlaybackStatus _status = PlaybackStatus.Stopped;
    private bool _isExclusiveActive;
    private bool _isDisposed;
    private float _volume = 1.0f;

    public AudioDevice? CurrentDevice => _currentDevice;
    public WaveFormat? CurrentFormat => _currentFormat;
    public PlaybackStatus Status => _status;
    public bool IsExclusiveLocked => _wasapiOut != null && _isExclusiveActive;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_silenceProvider != null)
            {
                _silenceProvider.Volume = _volume;
            }
        }
    }

    public event EventHandler<PlaybackStatus>? StatusChanged;

    /// <summary>
    /// Menginisialisasi koneksi eksklusif ke perangkat audio dengan buffer event-driven.
    /// </summary>
    public void Initialize(AudioDevice device, IWaveProvider waveProvider, int latencyMs = 50, AudioDriverType driverType = AudioDriverType.WASAPI_Exclusive)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(waveProvider);

        Stop();
        DisposeWasapi();

        _currentDevice = device;
        _currentFormat = waveProvider.WaveFormat;
        _silenceProvider = new SilenceAwareWaveProvider(waveProvider, _volume);

        // Buka MMDevice endpoint yang sesuai dari Windows ID
        using var enumerator = new MMDeviceEnumerator();
        _mmDevice = enumerator.GetDevice(device.Id);

        if (driverType == AudioDriverType.WASAPI_Shared)
        {
            InitializeSharedMode(_silenceProvider, latencyMs);
            UpdateStatus(PlaybackStatus.Stopped);
            return;
        }

        // Coba buka WasapiOut dalam Mode Eksklusif terlebih dahulu
        try
        {
            _wasapiOut = new WasapiOut(
                _mmDevice,
                AudioClientShareMode.Exclusive,
                useEventSync: true,
                latency: Math.Clamp(latencyMs, 20, 200)
            );

            _wasapiOut.PlaybackStopped += OnWasapiPlaybackStopped;
            _wasapiOut.Init(_silenceProvider);
            _isExclusiveActive = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WASAPI Exclusive Notice] Driver soundcard menolak mode Eksklusif ({ex.Message}). Beralih mulus ke mode Shared.");
            DisposeWasapi();
            _silenceProvider = new SilenceAwareWaveProvider(waveProvider, _volume);
            InitializeSharedMode(_silenceProvider, latencyMs);
        }

        UpdateStatus(PlaybackStatus.Stopped);
    }

    private void InitializeSharedMode(IWaveProvider waveProvider, int latencyMs)
    {
        _isExclusiveActive = false;
        try
        {
            if (_currentDevice == null) return;
            using var enumFallback = new MMDeviceEnumerator();
            _mmDevice = enumFallback.GetDevice(_currentDevice.Id);

            _wasapiOut = new WasapiOut(
                _mmDevice,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: Math.Clamp(latencyMs, 20, 200)
            );

            _wasapiOut.PlaybackStopped += OnWasapiPlaybackStopped;

            try
            {
                _wasapiOut.Init(waveProvider);
                _currentFormat = _mmDevice.AudioClient.MixFormat ?? _currentFormat;
            }
            catch
            {
                // Jika format PCM tidak didukung langsung oleh Windows Shared Mixer, lakukan resampling ke MixFormat hardware
                var mixFormat = _mmDevice.AudioClient.MixFormat;
                var resampler = new MediaFoundationResampler(waveProvider, mixFormat)
                {
                    ResamplerQuality = 60
                };
                _wasapiOut.Init(resampler);
                _currentFormat = mixFormat;
            }
        }
        catch (Exception fallbackEx)
        {
            System.Diagnostics.Debug.WriteLine($"[WASAPI Shared Error] Gagal inisialisasi audio endpoint: {fallbackEx.Message}");
        }
    }

    /// <summary>
    /// Memulai aliran PCM ke hardware DAC.
    /// </summary>
    public void Play()
    {
        if (_wasapiOut == null)
        {
            System.Diagnostics.Debug.WriteLine("[AudioEngine Warning] Play() dipanggil sebelum audio diinisialisasi.");
            return;
        }

        if (_silenceProvider != null)
        {
            _silenceProvider.IsPaused = false;
        }

        if (_status != PlaybackStatus.Playing)
        {
            try
            {
                _wasapiOut.Play();
                UpdateStatus(PlaybackStatus.Playing);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioEngine Error] Gagal memutar: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Menjeda pemutaran audio dengan mengalirkan keheningan digital (0x00 PCM),
    /// menjaga lock eksklusif hardware tanpa menyebabkan starvation/dengingan DAC.
    /// </summary>
    public void Pause()
    {
        if (_wasapiOut != null && _status == PlaybackStatus.Playing)
        {
            if (_silenceProvider != null)
            {
                _silenceProvider.IsPaused = true;
            }
            UpdateStatus(PlaybackStatus.Paused);
        }
    }

    /// <summary>
    /// Menghentikan pemutaran audio, membilas buffer hardware dengan sampel 0x00,
    /// dan mereset endpoint stream agar DAC kembali ke kondisi senyap mutlak (0 Volt DC).
    /// </summary>
    public void Stop()
    {
        if (_wasapiOut != null)
        {
            if (_silenceProvider != null)
            {
                _silenceProvider.IsPaused = true;
            }

            // Alirkan keheningan digital selama 80ms (minimal 1-2 siklus buffer WASAPI)
            // agar buffer ring DMA hardware Realtek/DAC terisi penuh dengan sampel 0 (senyap total)
            try
            {
                Thread.Sleep(80);
            }
            catch { }

            try
            {
                _wasapiOut.Stop();
            }
            catch { }

            DisposeWasapi();
            UpdateStatus(PlaybackStatus.Stopped);
        }
    }

    private void OnWasapiPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        // Abaikan jika event berasal dari instance WasapiOut lama yang sudah diganti atau dibuang
        if (sender != _wasapiOut || _wasapiOut == null) return;
        UpdateStatus(PlaybackStatus.Stopped);
    }

    private void UpdateStatus(PlaybackStatus newStatus)
    {
        if (_status != newStatus)
        {
            _status = newStatus;
            StatusChanged?.Invoke(this, newStatus);
        }
    }

    private void DisposeWasapi()
    {
        _isExclusiveActive = false;
        if (_wasapiOut != null)
        {
            _wasapiOut.PlaybackStopped -= OnWasapiPlaybackStopped;
            try
            {
                _wasapiOut.Dispose();
            }
            catch { }
            _wasapiOut = null;
        }

        _silenceProvider = null;

        try
        {
            _mmDevice?.Dispose();
        }
        catch { }
        _mmDevice = null;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Stop();
        DisposeWasapi();
        GC.SuppressFinalize(this);
    }
}
