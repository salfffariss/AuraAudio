using System;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Kontrak antarmuka untuk engine pemutaran audio perangkat keras.
/// Memisahkan logika kontrol pemutaran dari detail implementasi API Windows (WASAPI / ASIO).
/// </summary>
public interface IAudioOutputEngine : IDisposable
{
    /// <summary>Perangkat keras soundcard / DAC yang sedang aktif</summary>
    AudioDevice? CurrentDevice { get; }

    /// <summary>Format gelombang audio PCM yang sedang dikunci di hardware</summary>
    WaveFormat? CurrentFormat { get; }

    /// <summary>Status operasional pemutaran saat ini</summary>
    PlaybackStatus Status { get; }

    /// <summary>Menandakan apakah hardware terkunci dalam mode Bit-Perfect Exclusive</summary>
    bool IsExclusiveLocked { get; }

    /// <summary>Event notifikasi saat status pemutaran berubah</summary>
    event EventHandler<PlaybackStatus>? StatusChanged;

    /// <summary>
    /// Menginisialisasi aliran audio ke perangkat dalam mode WASAPI Exclusive atau Shared.
    /// </summary>
    void Initialize(AudioDevice device, IWaveProvider waveProvider, int latencyMs = 50, AudioDriverType driverType = AudioDriverType.WASAPI_Exclusive);

    /// <summary>Memulai aliran pemutaran audio ke DAC</summary>
    void Play();

    /// <summary>Menjeda pemutaran audio sementara</summary>
    void Pause();

    /// <summary>Menghentikan pemutaran audio dan melepaskan buffer hardware</summary>
    void Stop();
}
