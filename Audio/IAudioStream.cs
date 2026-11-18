using System;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Kontrak aliran audio hasil decode (Decoded Audio Stream).
/// Membungkus format FLAC, WAV, ALAC, MP3 ke dalam satu representasi PCM murni
/// yang siap dialirkan ke WasapiExclusivePlayer tanpa kehilangan bit (Bit-Exact).
/// </summary>
public interface IAudioStream : IWaveProvider, IDisposable
{
    /// <summary>Lokasi fisik file audio yang sedang diputar</summary>
    string FilePath { get; }

    /// <summary>Spesifikasi teknis fisik audio (Sample Rate, Bit Depth, Codec)</summary>
    AudioStreamSpec Spec { get; }

    /// <summary>Total durasi file audio</summary>
    TimeSpan TotalTime { get; }

    /// <summary>Posisi pemutaran saat ini (Mendukung Seeking)</summary>
    TimeSpan CurrentTime { get; set; }

    /// <summary>Posisi byte pembacaan saat ini di dalam stream</summary>
    long Position { get; set; }

    /// <summary>Total panjang byte data audio PCM</summary>
    long Length { get; }

    /// <summary>Membaca data PCM ke buffer byte konvensional</summary>
    int Read(byte[] buffer, int offset, int count);
}
