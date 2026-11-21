using System;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Silent, zero-hardware <see cref="IAudioOutputEngine"/> used by automated tests and UI smoke checks
/// so no audio ever reaches a physical speaker, headphone, or DAC.
/// </summary>
public sealed class NullAudioOutputEngine : IAudioOutputEngine
{
    private readonly object _sync = new();
    private AudioDevice? _currentDevice;
    private WaveFormat? _currentFormat;
    private IWaveProvider? _waveProvider;
    private PlaybackStatus _status = PlaybackStatus.Stopped;
    private bool _isExclusiveLocked;
    private float _volume = 1.0f;

    public AudioDevice? CurrentDevice => _currentDevice;
    public WaveFormat? CurrentFormat => _currentFormat;
    public PlaybackStatus Status => _status;
    public bool IsExclusiveLocked => _isExclusiveLocked;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public event EventHandler<PlaybackStatus>? StatusChanged;

    public void Initialize(
        AudioDevice device,
        IWaveProvider waveProvider,
        int latencyMs = 50,
        AudioDriverType driverType = AudioDriverType.WASAPI_Exclusive)
    {
        ArgumentNullException.ThrowIfNull(waveProvider);

        lock (_sync)
        {
            _currentDevice = device;
            _waveProvider = waveProvider;
            _currentFormat = waveProvider.WaveFormat;
            _isExclusiveLocked = driverType == AudioDriverType.WASAPI_Exclusive;
            _status = PlaybackStatus.Stopped;
        }
    }

    public void Play()
    {
        bool changed = false;
        lock (_sync)
        {
            if (_waveProvider != null && _status != PlaybackStatus.Playing)
            {
                _status = PlaybackStatus.Playing;
                changed = true;
            }
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, PlaybackStatus.Playing);
        }
    }

    public void Pause()
    {
        bool changed = false;
        lock (_sync)
        {
            if (_status == PlaybackStatus.Playing)
            {
                _status = PlaybackStatus.Paused;
                changed = true;
            }
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, PlaybackStatus.Paused);
        }
    }

    public void Stop()
    {
        bool changed = false;
        lock (_sync)
        {
            if (_status != PlaybackStatus.Stopped)
            {
                _status = PlaybackStatus.Stopped;
                changed = true;
            }
            _isExclusiveLocked = false;
            _waveProvider = null;
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, PlaybackStatus.Stopped);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
