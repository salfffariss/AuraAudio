using System;
using System.Threading;
using AuraAudio.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

#pragma warning disable CS0618 // WasapiOut is used for robust exclusive mode stream control

namespace AuraAudio.Audio;

/// <summary>
/// Pembungkus stream audio yang menyalurkan keheningan digital (zero-byte PCM) saat pause / stop,
/// mencegah DAC soundcard mengalami starvation atau perulangan DMA ring buffer (dengingan hardware Realtek).
/// </summary>
internal sealed class SilenceAwareWaveProvider : IWaveProvider
{
    private readonly IWaveProvider _source;
    public WaveFormat WaveFormat => _source.WaveFormat;
    public volatile bool IsPaused;

    public SilenceAwareWaveProvider(IWaveProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
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

        return totalRead;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        return Read(new Span<byte>(buffer, offset, count));
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

    public AudioDevice? CurrentDevice => _currentDevice;
    public WaveFormat? CurrentFormat => _currentFormat;
    public PlaybackStatus Status => _status;
    public bool IsExclusiveLocked => _wasapiOut != null && _isExclusiveActive;

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
        _silenceProvider = new SilenceAwareWaveProvider(waveProvider);

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
            _silenceProvider = new SilenceAwareWaveProvider(waveProvider);
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
