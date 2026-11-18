using System;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Mesin Anti-Pop & Micro-Fade Kelas Audiophile.
/// Menerapkan kurva transisi Raised-Cosine / Hann (C^1 continuous) berdurasi 3-5 ms
/// saat memulai, menjeda, menghentikan lagu, atau berpindah sample rate.
/// Mengeliminasi lonjakan tegangan DC (Dirac impulse) sehingga speaker, headphone,
/// dan DAC terbebas dari bunyi letupan / klik ("pop").
/// </summary>
public static class AntiPopFader
{
    public const int DefaultFadeMs = 5; // Durasi default 5 milidetik (di luar ambang persepsi telinga)

    /// <summary>
    /// Menerapkan Fade-In (0.0 -> 1.0) secara in-place pada awal buffer PCM.
    /// </summary>
    public static void ApplyFadeIn(byte[] buffer, int offset, int count, WaveFormat format, int durationMs = DefaultFadeMs)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0) return;

        int bytesPerFrame = format.BlockAlign;
        int totalFrames = count / bytesPerFrame;
        int fadeFrames = Math.Min(totalFrames, (format.SampleRate * durationMs) / 1000);

        if (fadeFrames <= 1) return;

        ProcessEnvelope(buffer, offset, fadeFrames, format, isFadeIn: true);
    }

    /// <summary>
    /// Menerapkan Fade-Out (1.0 -> 0.0) secara in-place pada akhir buffer PCM.
    /// </summary>
    public static void ApplyFadeOut(byte[] buffer, int offset, int count, WaveFormat format, int durationMs = DefaultFadeMs)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0) return;

        int bytesPerFrame = format.BlockAlign;
        int totalFrames = count / bytesPerFrame;
        int fadeFrames = Math.Min(totalFrames, (format.SampleRate * durationMs) / 1000);

        if (fadeFrames <= 1) return;

        int fadeStartOffset = offset + (totalFrames - fadeFrames) * bytesPerFrame;
        ProcessEnvelope(buffer, fadeStartOffset, fadeFrames, format, isFadeIn: false);
    }

    /// <summary>
    /// Memproses modulasi envelope kurva Raised-Cosine berdasarkan kedalaman bit (16-bit, 24-bit, 32-bit).
    /// </summary>
    private static void ProcessEnvelope(byte[] buffer, int startOffset, int frames, WaveFormat format, bool isFadeIn)
    {
        int channels = format.Channels;
        int bitsPerSample = format.BitsPerSample;
        int bytesPerSample = bitsPerSample / 8;
        int bytesPerFrame = format.BlockAlign;

        for (int f = 0; f < frames; f++)
        {
            double progress = (double)f / (frames - 1);
            // Raised-Cosine Window: w = 0.5 * (1 - cos(pi * x))
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
                        if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000); // Sign extension
                        int faded = (int)Math.Round(s * factor);
                        buffer[sampleOffset] = (byte)(faded & 0xFF);
                        buffer[sampleOffset + 1] = (byte)((faded >> 8) & 0xFF);
                        buffer[sampleOffset + 2] = (byte)((faded >> 16) & 0xFF);
                        break;
                    }

                    case 32 when format.Encoding == WaveFormatEncoding.IeeeFloat:
                    {
                        float s = BitConverter.ToSingle(buffer, sampleOffset);
                        float faded = (float)(s * factor);
                        BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), faded);
                        break;
                    }

                    case 32:
                    {
                        int s = BitConverter.ToInt32(buffer, sampleOffset);
                        int faded = (int)Math.Round(s * factor);
                        BitConverter.TryWriteBytes(buffer.AsSpan(sampleOffset, 4), faded);
                        break;
                    }
                }
            }
        }
    }
}
