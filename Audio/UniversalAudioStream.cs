using System;
using AuraAudio.Audio.Flac;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Pembungkus universal untuk berbagai decoder file audio (FLAC, WAV, ALAC, MP3).
/// Menjamin kompatibilitas penuh dengan NAudio 3.1 (termasuk buffer Memory Span)
/// dan menyediakan fungsi pencarian posisi lagu (Seeking) yang presisi.
/// </summary>
public class UniversalAudioStream : IAudioStream
{
    private readonly WaveStream _underlyingStream;
    private readonly AudioStreamSpec _spec;
    private readonly string _filePath;
    private bool _isDisposed;

    public string FilePath => _filePath;
    public AudioStreamSpec Spec => _spec;
    public WaveFormat WaveFormat => _underlyingStream.WaveFormat;
    public TimeSpan TotalTime => _underlyingStream.TotalTime;
    public long Length => _underlyingStream.Length;

    public TimeSpan CurrentTime
    {
        get => _underlyingStream.CurrentTime;
        set => _underlyingStream.CurrentTime = value;
    }

    public long Position
    {
        get => _underlyingStream.Position;
        set => _underlyingStream.Position = Math.Clamp(value, 0, _underlyingStream.Length);
    }

    public UniversalAudioStream(string filePath, WaveStream underlyingStream, AudioCodec codec)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _underlyingStream = underlyingStream ?? throw new ArgumentNullException(nameof(underlyingStream));

        int? flacSourceBits = underlyingStream is SafeFlacWaveStream flacStream
            ? flacStream.SourceBitsPerSample
            : null;

        TimeSpan? streamDuration = null;
        try
        {
            if (underlyingStream.TotalTime > TimeSpan.Zero)
            {
                streamDuration = underlyingStream.TotalTime;
            }
        }
        catch
        {
            streamDuration = null;
        }

        _spec = AudioSourceFormatResolver.ResolveFromFile(
            filePath,
            decoderWaveFormat: underlyingStream.WaveFormat,
            decoderDuration: streamDuration,
            knownCodec: codec,
            decoderSourceBitsPerSample: flacSourceBits).Spec;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        return _underlyingStream.Read(buffer, offset, count);
    }

    public int Read(Span<byte> destination)
    {
        return _underlyingStream.Read(destination);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _underlyingStream.Dispose();
        GC.SuppressFinalize(this);
    }
}
