using System;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Mesin Anti-Pop &amp; Micro-Fade Kelas Audiophile.
/// Menerapkan kurva transisi Raised-Cosine / Hann (C^1 continuous) berdurasi 3-5 ms
/// saat memulai, menjeda, menghentikan lagu, atau berpindah sample rate.
/// Beroperasi secara in-place pada <see cref="Span{T}"/> tanpa alokasi heap di jalur render audio.
/// </summary>
public static class AntiPopFader
{
    public const int DefaultFadeMs = 5;

    public static void ApplyFadeIn(byte[] buffer, int offset, int count, WaveFormat format, int durationMs = DefaultFadeMs)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0) return;
        ApplyFadeIn(buffer.AsSpan(offset, count), format, durationMs);
    }

    public static void ApplyFadeIn(Span<byte> buffer, WaveFormat format, int durationMs = DefaultFadeMs)
    {
        if (buffer.IsEmpty) return;

        int bytesPerFrame = format.BlockAlign;
        if (bytesPerFrame <= 0) return;
        int totalFrames = buffer.Length / bytesPerFrame;
        int fadeFrames = Math.Min(totalFrames, (format.SampleRate * durationMs) / 1000);

        if (fadeFrames <= 1) return;

        ProcessEnvelope(buffer, 0, fadeFrames, format, isFadeIn: true);
    }

    public static void ApplyFadeOut(byte[] buffer, int offset, int count, WaveFormat format, int durationMs = DefaultFadeMs)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0) return;
        ApplyFadeOut(buffer.AsSpan(offset, count), format, durationMs);
    }

    public static void ApplyFadeOut(Span<byte> buffer, WaveFormat format, int durationMs = DefaultFadeMs)
    {
        if (buffer.IsEmpty) return;

        int bytesPerFrame = format.BlockAlign;
        if (bytesPerFrame <= 0) return;
        int totalFrames = buffer.Length / bytesPerFrame;
        int fadeFrames = Math.Min(totalFrames, (format.SampleRate * durationMs) / 1000);

        if (fadeFrames <= 1) return;

        int fadeStartOffset = (totalFrames - fadeFrames) * bytesPerFrame;
        ProcessEnvelope(buffer, fadeStartOffset, fadeFrames, format, isFadeIn: false);
    }

    private static void ProcessEnvelope(Span<byte> buffer, int startOffset, int frames, WaveFormat format, bool isFadeIn)
    {
        int channels = format.Channels;
        int bitsPerSample = format.BitsPerSample;
        int bytesPerSample = bitsPerSample / 8;
        int bytesPerFrame = format.BlockAlign;

        for (int f = 0; f < frames; f++)
        {
            double progress = (double)f / (frames - 1);
            double factor = isFadeIn
                ? 0.5 * (1.0 - Math.Cos(Math.PI * progress))
                : 0.5 * (1.0 + Math.Cos(Math.PI * progress));

            int frameOffset = startOffset + f * bytesPerFrame;

            for (int ch = 0; ch < channels; ch++)
            {
                int sampleOffset = frameOffset + ch * bytesPerSample;

                switch (bitsPerSample)
                {
                    case 16:
                    {
                        short s = (short)(buffer[sampleOffset] | (buffer[sampleOffset + 1] << 8));
                        short faded = (short)Math.Round(s * factor);
                        buffer[sampleOffset] = (byte)(faded & 0xFF);
                        buffer[sampleOffset + 1] = (byte)((faded >> 8) & 0xFF);
                        break;
                    }

                    case 24:
                    {
                        int s = buffer[sampleOffset] | (buffer[sampleOffset + 1] << 8) | (buffer[sampleOffset + 2] << 16);
                        if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
                        int faded = (int)Math.Round(s * factor);
                        buffer[sampleOffset] = (byte)(faded & 0xFF);
                        buffer[sampleOffset + 1] = (byte)((faded >> 8) & 0xFF);
                        buffer[sampleOffset + 2] = (byte)((faded >> 16) & 0xFF);
                        break;
                    }

                    case 32 when format.Encoding == WaveFormatEncoding.IeeeFloat:
                    {
                        float s = BitConverter.ToSingle(buffer.Slice(sampleOffset, 4));
                        float faded = (float)(s * factor);
                        BitConverter.TryWriteBytes(buffer.Slice(sampleOffset, 4), faded);
                        break;
                    }

                    case 32:
                    {
                        int s = BitConverter.ToInt32(buffer.Slice(sampleOffset, 4));
                        int faded = (int)Math.Round(s * factor);
                        BitConverter.TryWriteBytes(buffer.Slice(sampleOffset, 4), faded);
                        break;
                    }
                }
            }
        }
    }
}
