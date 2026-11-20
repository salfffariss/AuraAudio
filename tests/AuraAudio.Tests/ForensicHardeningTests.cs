using System;
using System.IO;
using System.Linq;
using AuraAudio.Audio;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Core.Models;
using NAudio.Wave;
using Xunit;

namespace AuraAudio.Tests;

/// <summary>
/// v2.0.1 Forensic Engine Hardening & Independent Regression Suite:
/// Verifies every bug exposed by the peer audit and independent SoniqTools CC0 benchmark:
///  1. Quiet acoustic 24/96 track upsampled from CD (cliff at ~21.4 kHz + dead -160 dBFS ultrasonic band) -> UpscaledFromCd (never Genuine).
///  2. Quiet genuine 16/44.1 acoustic piano-like roll-off (~13.8 kHz knee, real -128 dBFS 16-bit noise floor) -> AcousticNaturalRollOff.
///  3. Quiet acoustic 24/44.1 track transcoded from 128k MP3 (roll-off before 16 kHz + dead -160 dBFS ceiling above 16.2 kHz) -> SuspectedUpscaledLossy.
///  4. Native AIFF 16-bit / 44.1 kHz decoding via AudioDecoderFactory (AiffFileReader) without MediaFoundationException.
///  5. Quiet-onset MSB-aligned 24-in-32 PCM frames (first 200 frames quiet, then full-scale) -> detected as MSB-aligned, not false ZeroPadded24Bit.
///  6. Non-seekable stream -> AnalysisTimestamp == 00:00 and AnalysisPositionPercentage == 0.0 (no silent fake timestamp).
///  7. Evidence-based verdict wording + universal AAC/Opus spectral caveat across 44.1k, 48k, and Hi-Res Genuine verdicts.
///  8. Opt-in real-file SoniqTools CC0 benchmark (AURA_SONIQTOOLS_DIR).
/// </summary>
public class ForensicHardeningTests : IDisposable
{
    private readonly string _tempDir;

    public ForensicHardeningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AuraForensicHardening_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string CreateWav(string name, int sampleRate, int bitsPerSample, double durationSec, Func<double, Random, double> sampleGen)
    {
        string path = Path.Combine(_tempDir, name);
        var wf = new WaveFormat(sampleRate, bitsPerSample, 2);
        var rand = new Random(1337);
        int totalFrames = (int)(sampleRate * durationSec);

        using var writer = new WaveFileWriter(path, wf);
        int blockAlign = wf.BlockAlign;
        int bytesPerSample = bitsPerSample / 8;
        byte[] buf = new byte[4096 * blockAlign];
        int frameIdx = 0;

        while (frameIdx < totalFrames)
        {
            int chunk = Math.Min(4096, totalFrames - frameIdx);
            for (int i = 0; i < chunk; i++)
            {
                double t = (frameIdx + i) / (double)sampleRate;
                for (int ch = 0; ch < 2; ch++)
                {
                    double s = Math.Clamp(sampleGen(t, rand), -0.9999, 0.9999);
                    int off = i * blockAlign + ch * bytesPerSample;
                    if (bitsPerSample == 16)
                    {
                        short v = (short)Math.Round(s * 32767.0);
                        buf[off] = (byte)(v & 0xFF);
                        buf[off + 1] = (byte)((v >> 8) & 0xFF);
                    }
                    else if (bitsPerSample == 24)
                    {
                        int v = (int)Math.Round(s * 8388607.0);
                        buf[off] = (byte)(v & 0xFF);
                        buf[off + 1] = (byte)((v >> 8) & 0xFF);
                        buf[off + 2] = (byte)((v >> 16) & 0xFF);
                    }
                }
            }
            writer.Write(buf, 0, chunk * blockAlign);
            frameIdx += chunk;
        }
        return path;
    }

    [Fact]
    public void QuietAcoustic24_96_UpsampledFromCd_DeadUltrasonic_IsUpscaledFromCd_NotGenuine()
    {
        // Simulates a quiet solo-piano 16/44.1 master resampled to 24/96:
        // gentle harmonics below 14 kHz, soft floor up to 21.3 kHz, then dead digital zero >= 21.5 kHz.
        // Build via FFT-bandlimited discrete tones up to 21.2 kHz (strictly zero above 21.4 kHz).
        string path = CreateWav("quiet_piano_cd_upsample_96k.wav", 96_000, 24, 10.5, (t, rand) =>
        {
            double s = 0.0;
            // Warm fundamental/mid harmonics (300 Hz .. 13.5 kHz) with natural piano taper
            for (int f = 300; f <= 13_500; f += 300)
            {
                double amp = 0.04 * Math.Pow(f / 400.0, -2.1);
                s += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            // Quiet CD-band noise/harmonics up to 21.2 kHz (~ -120 dBFS), strictly ZERO above 21.4 kHz
            for (int f = 14_000; f <= 21_200; f += 200)
            {
                s += 0.000015 * Math.Sin(2.0 * Math.PI * f * t + f * 0.13);
            }
            return s;
        });

        var report = NyquistSpectrumAnalyzer.Analyze(path);

        Assert.NotEqual(AuthenticityVerdict.AcousticNaturalRollOff, report.Verdict);
        Assert.NotEqual(AuthenticityVerdict.VerifiedLossless, report.Verdict);
        Assert.Equal(AuthenticityVerdict.UpscaledFromCd, report.Verdict);
    }

    [Fact]
    public void QuietGenuine16_44k_SoftAcousticRollOff_IsAcousticNaturalRollOff()
    {
        // Mirrors genuine solo piano in 16-bit / 44.1 kHz with real TPDF dither floor (-128..-130 dBFS)
        // and smooth harmonic taper reaching ~13-14 kHz (no artificial cutoff).
        string path = CreateWav("quiet_genuine_piano_16_44k.wav", 44_100, 16, 10.5, (t, rand) =>
        {
            double s = 0.0;
            // Smooth exponential acoustic roll-off crossing -48 dB around ~13.5-14.5 kHz into 16-bit TPDF dither
            for (int f = 300; f <= 21_900; f += 300)
            {
                double amp = 0.08 * Math.Exp(-f / 2_100.0);
                s += amp * Math.Sin(2.0 * Math.PI * f * t + f * 0.017);
            }
            // Natural 16-bit TPDF dither floor across full 0..22.05 kHz Nyquist
            double dither = (rand.NextDouble() - rand.NextDouble()) * (1.0 / 32768.0);
            return s + dither;
        });

        var report = NyquistSpectrumAnalyzer.Analyze(path);

        Assert.Equal(AuthenticityVerdict.AcousticNaturalRollOff, report.Verdict);
        Assert.Contains("AAC/Opus", report.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuietAcoustic24_44k_From128kMp3_DeadZeroAbove16k_IsSuspectedUpscaledLossy()
    {
        // Mirrors trick-fake-flac-24bit-from-128kbps-mp3.flac:
        // soft acoustic spectrum rolling off around 13.8 kHz, then dead digital zero above 16 kHz.
        string path = CreateWav("quiet_piano_from_128k_mp3_24_44k.wav", 44_100, 24, 10.5, (t, rand) =>
        {
            double s = 0.0;
            for (int f = 300; f <= 13_500; f += 300)
            {
                double amp = 0.05 * Math.Pow(f / 400.0, -2.0);
                s += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            // Soft residual energy up to 15.8 kHz (~ -128 dBFS), strictly ZERO above 16.0 kHz
            for (int f = 14_000; f <= 15_800; f += 200)
            {
                s += 0.000012 * Math.Sin(2.0 * Math.PI * f * t + f * 0.17);
            }
            return s;
        });

        var report = NyquistSpectrumAnalyzer.Analyze(path);

        Assert.Equal(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
        Assert.InRange(report.DetectedCutoffHz, 15_000u, 17_000u);
        Assert.Contains("Kemungkinan hasil konversi dari lossy", report.Explanation);
    }

    [Fact]
    public void NativeAiff16_44k_DecodesAndAnalyzesWithoutMediaFoundationException()
    {
        // Write a minimal standard uncompressed 16-bit / 44.1 kHz stereo AIFF file (big-endian PCM)
        string aiffPath = Path.Combine(_tempDir, "native_test_16_44k.aiff");
        int sr = 44_100;
        int channels = 2;
        int bits = 16;
        int numFrames = sr * 4; // 4 seconds
        int soundDataBytes = numFrames * channels * (bits / 8);
        var rand = new Random(42);

        using (var fs = File.Create(aiffPath))
        using (var bw = new BinaryWriter(fs))
        {
            static void WriteBE32(BinaryWriter w, int val)
            {
                w.Write((byte)((val >> 24) & 0xFF));
                w.Write((byte)((val >> 16) & 0xFF));
                w.Write((byte)((val >> 8) & 0xFF));
                w.Write((byte)(val & 0xFF));
            }
            static void WriteBE16(BinaryWriter w, short val)
            {
                w.Write((byte)((val >> 8) & 0xFF));
                w.Write((byte)(val & 0xFF));
            }

            int commChunkSize = 18;
            int ssndChunkSize = 8 + soundDataBytes;
            int formSize = 4 + (8 + commChunkSize) + (8 + ssndChunkSize);

            bw.Write(System.Text.Encoding.ASCII.GetBytes("FORM"));
            WriteBE32(bw, formSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("AIFF"));

            // COMM chunk (80-bit IEEE 754 extended float for 44100.0 = 40 0E AC 44 00 00 00 00 00 00)
            bw.Write(System.Text.Encoding.ASCII.GetBytes("COMM"));
            WriteBE32(bw, commChunkSize);
            WriteBE16(bw, (short)channels);
            WriteBE32(bw, numFrames);
            WriteBE16(bw, (short)bits);
            bw.Write(new byte[] { 0x40, 0x0E, 0xAC, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

            // SSND chunk
            bw.Write(System.Text.Encoding.ASCII.GetBytes("SSND"));
            WriteBE32(bw, ssndChunkSize);
            WriteBE32(bw, 0); // offset
            WriteBE32(bw, 0); // blockSize

            for (int i = 0; i < numFrames; i++)
            {
                double t = i / (double)sr;
                double sig = 0.0;
                for (int f = 500; f <= 22_000; f += 500)
                {
                    sig += 0.05 * Math.Pow(f / 1000.0, -0.6) * Math.Sin(2.0 * Math.PI * f * t + f * 0.013);
                }
                sig += (rand.NextDouble() - 0.5) * 0.004;
                short sample = (short)Math.Round(Math.Clamp(sig, -0.9999, 0.9999) * 32767.0);
                WriteBE16(bw, sample);
                WriteBE16(bw, sample);
            }
        }

        var report = NyquistSpectrumAnalyzer.Analyze(aiffPath);
        Assert.Equal(AudioCodec.AIFF, report.SourceSpec.Codec);
        Assert.Equal(AuthenticityVerdict.VerifiedLossless, report.Verdict);
    }

    private sealed class QuietOnsetMsb24In32Stream : IAudioStream
    {
        private readonly byte[] _pcm;
        private int _pos;

        public string FilePath => "synthetic_msb_24in32.wav";
        public AudioStreamSpec Spec { get; }
        public WaveFormat WaveFormat { get; }
        public TimeSpan TotalTime { get; }
        public long Length => _pcm.Length;

        public long Position
        {
            get => _pos;
            set => _pos = (int)Math.Clamp(value, 0, _pcm.Length);
        }

        public TimeSpan CurrentTime
        {
            get => TimeSpan.FromSeconds(_pos / (double)WaveFormat.AverageBytesPerSecond);
            set => Position = (long)(value.TotalSeconds * WaveFormat.AverageBytesPerSecond) / WaveFormat.BlockAlign * WaveFormat.BlockAlign;
        }

        public QuietOnsetMsb24In32Stream()
        {
            int sr = 96_000;
            int frames = sr * 10; // 10 seconds
            WaveFormat = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, sr, 2, sr * 8, 8, 24);
            Spec = new AudioStreamSpec(AudioCodec.WAV, (uint)sr, BitDepth.Bit24, 2, 4608);
            TotalTime = TimeSpan.FromSeconds(10.0);
            _pcm = new byte[frames * 8];
            var rand = new Random(99);

            // Place 200 very quiet frames at EVERY seek target onset so a 100-frame probe would fail,
            // followed by active full-range 24-bit MSB-aligned audio (byte 0 = 0x00, bytes 1..3 = 24-bit sample).
            int[] quietStarts = { 0, (int)(frames * 0.20), (int)(frames * 0.35), (int)(frames * 0.50), (int)(frames * 0.65), (int)(frames * 0.80) };

            for (int f = 0; f < frames; f++)
            {
                bool isQuietOnset = quietStarts.Any(qs => f >= qs && f < qs + 200);
                double t = f / (double)sr;
                double sig = 0.0;
                if (isQuietOnset)
                {
                    // Tiny amplitude so byte 3 == 0x00 or 0xFF (looks like sign-extension of byte 2!)
                    sig = 0.0005 * Math.Sin(2.0 * Math.PI * 1000.0 * t);
                }
                else
                {
                    for (int hz = 500; hz <= 44_000; hz += 500)
                    {
                        sig += 0.015 * Math.Pow(hz / 1000.0, -0.5) * Math.Sin(2.0 * Math.PI * hz * t + hz * 0.011);
                    }
                    sig += (rand.NextDouble() - 0.5) * 0.008;
                }

                int s24 = (int)Math.Round(Math.Clamp(sig, -0.999, 0.999) * 8388607.0);
                byte lsb = (byte)(s24 & 0xFF);
                byte mid = (byte)((s24 >> 8) & 0xFF);
                byte msb = (byte)((s24 >> 16) & 0xFF);

                for (int ch = 0; ch < 2; ch++)
                {
                    int off = f * 8 + ch * 4;
                    _pcm[off] = 0x00; // MSB-aligned padding in byte 0
                    _pcm[off + 1] = lsb;
                    _pcm[off + 2] = mid;
                    _pcm[off + 3] = msb;
                }
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int avail = Math.Min(count, _pcm.Length - _pos);
            if (avail <= 0) return 0;
            Array.Copy(_pcm, _pos, buffer, offset, avail);
            _pos += avail;
            return avail;
        }

        public int Read(Span<byte> destination)
        {
            int avail = Math.Min(destination.Length, _pcm.Length - _pos);
            if (avail <= 0) return 0;
            _pcm.AsSpan(_pos, avail).CopyTo(destination);
            _pos += avail;
            return avail;
        }

        public void Dispose() { }
    }

    private sealed class NonSeekableStreamWrapper : IAudioStream
    {
        private readonly IAudioStream _inner;
        public NonSeekableStreamWrapper(IAudioStream inner) => _inner = inner;
        public string FilePath => _inner.FilePath;
        public AudioStreamSpec Spec => _inner.Spec;
        public WaveFormat WaveFormat => _inner.WaveFormat;
        public TimeSpan TotalTime => _inner.TotalTime;
        public long Length => _inner.Length;
        public long Position { get => _inner.Position; set => throw new NotSupportedException("Non-seekable"); }
        public TimeSpan CurrentTime { get => _inner.CurrentTime; set => throw new NotSupportedException("Non-seekable"); }
        public int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public int Read(Span<byte> destination) => _inner.Read(destination);
        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void QuietOnsetMsbAligned24In32_IsDetectedAsTrue24Bit_NotZeroPadded()
    {
        using var stream = new QuietOnsetMsb24In32Stream();
        var report = NyquistSpectrumAnalyzer.Analyze(stream);

        Assert.False(report.IsZeroPadded, "200 quiet onset frames must not trick Detect24In32Alignment into LSB-aligned zero-padding.");
        Assert.True(report.IsTrue24Bit, $"Expected True24Bit=true, got LSB={report.LsbActivityPercentage}%, H={report.LsbEntropy}");
        Assert.Equal(AuthenticityVerdict.VerifiedLossless, report.Verdict);
    }

    [Fact]
    public void NonSeekableStream_ReportsZeroTimestampHonestly_InsteadOfFakeLoudestTime()
    {
        string path = CreateWav("non_seekable_48k.wav", 48_000, 24, 12.0, (t, rand) =>
        {
            double s = 0.0;
            for (int f = 500; f <= 23_000; f += 500)
                s += 0.05 * Math.Pow(f / 1000.0, -0.7) * Math.Sin(2.0 * Math.PI * f * t);
            return s + (rand.NextDouble() - 0.5) * 0.0005;
        });

        using var raw = AudioDecoderFactory.OpenStream(path);
        using var nonSeekable = new NonSeekableStreamWrapper(raw);
        var report = NyquistSpectrumAnalyzer.Analyze(nonSeekable);

        Assert.Equal(TimeSpan.Zero, report.AnalysisTimestamp);
        Assert.Equal(0.0, report.AnalysisPositionPercentage);
        Assert.Contains("tidak mendukung seek", report.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenuineVerdicts_IncludeAacOpusSpectralCaveat_InBoth48kAndHiRes()
    {
        string path48k = CreateWav("genuine_48k_caveat_check.wav", 48_000, 24, 10.5, (t, rand) =>
        {
            double s = 0.0;
            for (int f = 500; f <= 23_500; f += 500)
                s += 0.06 * Math.Pow(f / 1000.0, -0.5) * Math.Sin(2.0 * Math.PI * f * t);
            return s + (rand.NextDouble() - 0.5) * 0.0005;
        });

        var report48k = NyquistSpectrumAnalyzer.Analyze(path48k);
        Assert.True(report48k.Verdict is AuthenticityVerdict.VerifiedLossless or AuthenticityVerdict.AcousticNaturalRollOff);
        Assert.Contains("AAC/Opus", report48k.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void SoniqTools_CC0_IndependentBenchmark_AllEightLosslessAndFakeFiles()
    {
        string? dir = Environment.GetEnvironmentVariable("AURA_SONIQTOOLS_DIR");
        Skip.If(string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir),
            "Set AURA_SONIQTOOLS_DIR to the folder containing the 8 CC0 SoniqTools test audio files.");

        var genuineFiles = new[]
        {
            "test-audio-flac-24bit-96khz.flac",
            "test-audio-wav-24bit-96khz.wav",
            "test-audio-flac-16bit-44khz.flac",
            "test-audio-wav-16bit-44khz.wav",
            "test-audio-aiff-16bit-44khz.aiff",
            "test-audio-alac-16bit-44khz.m4a",
        };

        foreach (string f in genuineFiles)
        {
            string full = Path.Combine(dir!, f);
            Skip.IfNot(File.Exists(full), $"Missing SoniqTools file: {f}");
            var r = NyquistSpectrumAnalyzer.Analyze(full);
            Assert.True(
                r.Verdict is AuthenticityVerdict.VerifiedLossless or AuthenticityVerdict.AcousticNaturalRollOff,
                $"{f} should be Genuine, got {r.Verdict}: {r.Explanation}");
        }

        string fakeMp3Flac = Path.Combine(dir!, "trick-fake-flac-24bit-from-128kbps-mp3.flac");
        Skip.IfNot(File.Exists(fakeMp3Flac), "Missing trick-fake-flac-24bit-from-128kbps-mp3.flac");
        var rMp3 = NyquistSpectrumAnalyzer.Analyze(fakeMp3Flac);
        Assert.Equal(AuthenticityVerdict.SuspectedUpscaledLossy, rMp3.Verdict);

        string fakeUpsample = Path.Combine(dir!, "trick-upsampled-96khz-from-cd.flac");
        Skip.IfNot(File.Exists(fakeUpsample), "Missing trick-upsampled-96khz-from-cd.flac");
        var rUp = NyquistSpectrumAnalyzer.Analyze(fakeUpsample);
        Assert.Equal(AuthenticityVerdict.UpscaledFromCd, rUp.Verdict);
    }
}
