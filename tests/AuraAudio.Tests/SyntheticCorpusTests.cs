using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AuraAudio.Audio;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Core.Models;
using AuraAudio.Metadata;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace AuraAudio.Tests;

/// <summary>
/// Suite pengujian validasi akurasi forensik audio AuraAudio menggunakan korpus audio sintetis
/// yang dibangkitkan sepenuhnya di dalam kode (in-memory bitstream to WAV tanpa berkas eksternal).
/// Menguji 7 kondisi kritis dasar + pengujian multi-sample-rate, kontrol negatif, DSD, dan telemetri keaslian:
/// 1. True Studio Master 24/96 -> VerifiedLossless
/// 2. Upscaled dari CD 44.1k (Brickwall 22.05k) -> UpscaledFromCd
/// 3. Zero-Padded 16-to-24-bit -> IsZeroPadded: true + Warning + Verdict Assertion
/// 4. Dithered 16-to-24-bit (TPDF -130.7 dBFS independen L/R) -> Dithered16BitUpscale
/// 5. Upscaled Lossy (MP3 Cutoff 16k) -> SuspectedUpscaledLossy
/// 6. Resampler Imaging Mirror (Aliasing 22.05k ± d) -> Inconclusive
/// 7. Natural Acoustic Roll-off -> AcousticNaturalRollOff
/// 8. Native 44.1k 16-bit -> VerifiedLossless
/// 9. Studio 48k dengan roll-off alami -> AcousticNaturalRollOff / VerifiedLossless
/// 10. Studio 48k hasil upsample CD 44.1k -> UpscaledFromCd
/// 11. 44.1k FLAC terpotong 16k -> SuspectedUpscaledLossy
/// 12. Kontrol Negatif: Sinyal akustik lirih (-30 dBFS RMS) -> Bukan SuspectedUpscaledLossy
/// 13. Kontrol Negatif: 48k Master di-lowpass 20k -> Inconclusive / UpscaledFromCd (Bukan SuspectedUpscaledLossy)
/// 14. Format 1-bit DSD Stream -> Inconclusive (aliran 1-bit tanpa dekimasi)
/// 15. Transcode Lossy FFmpeg -> Skip jika ffmpeg tidak terpasang di PATH
/// 16. Matriks Konfusi & Telemetri Diagnostik
/// </summary>
public class SyntheticCorpusTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly ITestOutputHelper _output;

    public SyntheticCorpusTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDirectory = Path.Combine(Path.GetTempPath(), "AuraAudio_CorpusTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Test_1_NativeHiRes24_96_ShouldBe_VerifiedLossless()
    {
        // Sinyal 24-bit 96 kHz dengan deretan harmonik kontinu hingga 44 kHz
        // Kemiringan wajar (~ -6 dB/oktaf) dan energi aktif menembus >40 kHz
        string wavPath = CreateSyntheticWav(
            fileName: "case1_native_24_96.wav",
            sampleRate: 96_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                // Sisipkan harmonik kontinu setiap 500 Hz dari 500 Hz hingga 47.500 Hz (mendekati batas Nyquist 48k)
                for (int f = 500; f <= 47_500; f += 500)
                {
                    double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }

                // Nada dekat batas Nyquist (47.9 kHz) untuk pita pNyquist
                signal += 0.02 * Math.Sin(2.0 * Math.PI * 47_900.0 * t);

                // Tambahkan derau termal mikrofon analog 24-bit untuk keaktifan LSB nyata
                double analogNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0002;
                return signal + analogNoise;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.VerifiedLossless, report.Verdict);
        Assert.NotNull(report.SpectralSlopeDbPerOctave);
        Assert.True(report.SpectralSlopeDbPerOctave.Value >= -30.0, $"Slope harus wajar (>= -30 dB/okt), terukur: {report.SpectralSlopeDbPerOctave}");
        Assert.True(report.PowerAtNyquistDbFS > -105.0, $"Daya dekat Nyquist harus aktif, terukur: {report.PowerAtNyquistDbFS}");
        Assert.True(report.IsTrue24Bit, "LSB 24-bit harus terdeteksi aktif");
        Assert.False(report.IsZeroPadded, "Tidak boleh terdeteksi zero-padded");
    }

    [Fact]
    public void Test_2_UpscaledFromCd_ShouldBe_UpscaledFromCd()
    {
        // Sinyal 96 kHz yang memiliki konten hanya hingga 21.0 kHz, terpotong curam di 22.05 kHz (Brickwall filter CD)
        string wavPath = CreateSyntheticWav(
            fileName: "case2_cd_upscaled.wav",
            sampleRate: 96_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                // Konten frekuensi terbatas hanya hingga 21.0 kHz
                for (int f = 500; f <= 21_000; f += 500)
                {
                    double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }

                // Di atas 21.5 kHz tidak ada sinyal (hanya derau kuantisasi sangat lirih)
                double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 1e-6;
                return signal + floorNoise;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.UpscaledFromCd, report.Verdict);
        Assert.Equal(22_050u, report.DetectedCutoffHz);
        Assert.NotNull(report.SpectralSlopeDbPerOctave);
        Assert.True(report.SpectralSlopeDbPerOctave.Value <= -36.0, $"Kemiringan filter CD harus terjal (<= -36 dB/okt), terukur: {report.SpectralSlopeDbPerOctave}");
    }

    [Fact]
    public void Test_3_ZeroPadded16to24Bit_ShouldDetect_IsZeroPadded()
    {
        // Sinyal 24-bit di mana 8 bit LSB terendah diisi nol (0x00)
        string wavPath = CreateSyntheticWav(
            fileName: "case3_zero_padded.wav",
            sampleRate: 96_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            zeroPadLsb: true,
            sampleFunc: (t, rand) =>
            {
                return 0.35 * Math.Sin(2.0 * Math.PI * 440.0 * t) +
                       0.30 * Math.Sin(2.0 * Math.PI * 1_000.0 * t) +
                       0.15 * Math.Sin(2.0 * Math.PI * 12_000.0 * t);
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.True(report.IsZeroPadded, "Flag IsZeroPadded harus true untuk data dengan LSB 0x00");
        Assert.True(report.LsbActivityPercentage < 1.0, $"Aktivitas LSB harus < 1.0%, terukur: {report.LsbActivityPercentage}%");
        Assert.True(report.LsbEntropy < 0.5, $"Entropi LSB harus < 0.5 bit, terukur: {report.LsbEntropy}");
        Assert.Contains("zero-padding", report.Explanation, StringComparison.OrdinalIgnoreCase);

        // Sinyal hanya berisi konten hingga 12 kHz dan kontainer 24-bit berisi byte LSB nol (zero-padded)
        Assert.Equal(AuthenticityVerdict.ZeroPadded24Bit, report.Verdict);
    }

    [Fact]
    public void Test_4_Dithered16BitUpscale_ShouldBe_Dithered16BitUpscale()
    {
        // Sinyal CD audio di-upsample ke 24/96 dengan TPDF dither 16-bit independen per kanal (L & R terpisah)
        // Level daya dither TPDF 16-bit: -130.7 dBFS dengan deviasi standar sigma < 2.2 dB
        const double lsb16 = 2.0 / 65536.0;
        double ditherScale = Math.Sqrt(1.5) * lsb16; // Menghasilkan tepat varians 0.25 * LSB^2

        string wavPath = CreateSyntheticWavStereo(
            fileName: "case4_dithered_16bit.wav",
            sampleRate: 96_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, ch, rand) =>
            {
                // Sinyal musik terbatas di bawah 20 kHz
                double music =
                    0.30 * Math.Sin(2.0 * Math.PI * 440.0 * t) +
                    0.25 * Math.Sin(2.0 * Math.PI * 1_000.0 * t) +
                    0.15 * Math.Sin(2.0 * Math.PI * 12_000.0 * t) +
                    0.08 * Math.Sin(2.0 * Math.PI * 19_500.0 * t);

                // Dither TPDF 16-bit independen per kanal (derau tidak berkorelasi antar kanal)
                double u1 = rand.NextDouble() - 0.5;
                double u2 = rand.NextDouble() - 0.5;
                double tpdfDither = (u1 + u2) * ditherScale;

                return music + tpdfDither;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.Dithered16BitUpscale, report.Verdict);
        Assert.True(report.IsDitherDetected, "Dither shelf harus terdeteksi di pita ultrasonik");
        Assert.NotNull(report.UltrasonicNoiseFloorDbFS);
        Assert.InRange(report.UltrasonicNoiseFloorDbFS.Value, -134.7, -126.7);
        Assert.NotNull(report.UltrasonicNoiseStdDevDb);
        Assert.True(report.UltrasonicNoiseStdDevDb.Value < 2.2, $"Sigma dither shelf harus < 2.2 dB, terukur: {report.UltrasonicNoiseStdDevDb.Value}");
    }

    [Fact]
    public void Test_5_UpscaledLossy_CutoffAt16k_ShouldBe_SuspectedUpscaledLossy()
    {
        // Sinyal 96 kHz FLAC yang terpotong tajam di 15.5 kHz (khas MP3 128 kbps)
        string wavPath = CreateSyntheticWav(
            fileName: "case5_fake_mp3_cutoff.wav",
            sampleRate: 96_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                // Konten hanya sampai 15.0 kHz
                double signal =
                    0.35 * Math.Sin(2.0 * Math.PI * 440.0 * t) +
                    0.30 * Math.Sin(2.0 * Math.PI * 1_000.0 * t) +
                    0.20 * Math.Sin(2.0 * Math.PI * 3_500.0 * t) +
                    0.15 * Math.Sin(2.0 * Math.PI * 8_000.0 * t) +
                    0.10 * Math.Sin(2.0 * Math.PI * 14_500.0 * t);

                // Di atas 15.5 kHz energi drop total (noise lantai sangat rendah)
                double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 1e-7;
                return signal + floorNoise;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
        Assert.True(report.DetectedCutoffHz <= 20_000u, $"Cutoff harus terdeteksi <= 20 kHz, terukur: {report.DetectedCutoffHz}");
    }

    [Fact]
    public void Test_6_ResamplerImagingMirror_ShouldBe_Inconclusive()
    {
        // Sinyal upsampled yang memiliki cermin pencitraan aliasing simetris di sekitar 22.05 kHz:
        // Komponen di (22.050 - d) tercermin persis di (22.050 + d)
        string wavPath = CreateSyntheticWav(
            fileName: "case6_imaging_mirror.wav",
            sampleRate: 96_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double music =
                    0.25 * Math.Sin(2.0 * Math.PI * 440.0 * t) +
                    0.25 * Math.Sin(2.0 * Math.PI * 1_000.0 * t) +
                    0.20 * Math.Sin(2.0 * Math.PI * 3_500.0 * t) +
                    0.15 * Math.Sin(2.0 * Math.PI * 8_000.0 * t) +
                    0.10 * Math.Sin(2.0 * Math.PI * 12_000.0 * t) +
                    0.08 * Math.Sin(2.0 * Math.PI * 16_000.0 * t);

                // Buat 4 pasang cermin simetris di sekitar 22.050 Hz: d = 500, 1200, 2000, 3100 Hz
                double d1 = 500.0;
                double d2 = 1200.0;
                double d3 = 2000.0;
                double d4 = 3100.0;

                double mirrorLower =
                    0.05 * Math.Sin(2.0 * Math.PI * (22_050.0 - d1) * t) +
                    0.04 * Math.Sin(2.0 * Math.PI * (22_050.0 - d2) * t) +
                    0.03 * Math.Sin(2.0 * Math.PI * (22_050.0 - d3) * t) +
                    0.02 * Math.Sin(2.0 * Math.PI * (22_050.0 - d4) * t);

                // Pantulan cermin resampler di pita atas dengan amplitudo proporsional
                double mirrorUpper =
                    0.04 * Math.Sin(2.0 * Math.PI * (22_050.0 + d1) * t) +
                    0.032 * Math.Sin(2.0 * Math.PI * (22_050.0 + d2) * t) +
                    0.024 * Math.Sin(2.0 * Math.PI * (22_050.0 + d3) * t) +
                    0.016 * Math.Sin(2.0 * Math.PI * (22_050.0 + d4) * t);

                double analogNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0001;
                return music + mirrorLower + mirrorUpper + analogNoise;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        // Keberadaan cermin resampler (r >= 0.65) harus menurunkan vonis menjadi Inconclusive
        Assert.Equal(AuthenticityVerdict.Inconclusive, report.Verdict);
        Assert.Contains("pencitraan", report.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test_7_AcousticNaturalRollOff_ShouldBe_AcousticNaturalRollOff()
    {
        // Sinyal instrumen akustik alami (vokal / nilon / piano) yang melandai mulus tanpa filter brickwall:
        // Kemiringan wajar (slope >= -30 dB/okt), namun daya di 24k dan 35k sudah di bawah ambang kebisingan
        string wavPath = CreateSyntheticWav(
            fileName: "case7_acoustic_natural_rolloff.wav",
            sampleRate: 96_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                // Peluruhan harmonik mulus dari 500 Hz s/d 23.500 Hz dengan kemiringan ~ -12 dB/oktaf
                for (int f = 500; f <= 23_500; f += 500)
                {
                    double amp = 0.12 * Math.Pow((double)f / 1000.0, -1.8);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }

                // Di atas 24k energi melandai menuju lantai noise ~ -110 dBFS
                double softFloor = (rand.NextDouble() - rand.NextDouble()) * 0.000005;
                return signal + softFloor;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.AcousticNaturalRollOff, report.Verdict);
        Assert.NotNull(report.SpectralSlopeDbPerOctave);
        Assert.True(report.SpectralSlopeDbPerOctave.Value >= -30.0, $"Slope akustik harus >= -30 dB/okt, terukur: {report.SpectralSlopeDbPerOctave}");
        Assert.True(report.PowerAt22kDbFS < -65.0, $"Daya di 22.05k harus lembut, terukur: {report.PowerAt22kDbFS}");
    }

    [Fact]
    public void Test_8_Native44_1k_16Bit_ShouldBe_VerifiedLossless()
    {
        // Sinyal CD audio murni 44.1 kHz 16-bit dengan spektrum penuh hingga batas 22.05 kHz
        string wavPath = CreateSyntheticWav(
            fileName: "case8_native_44_1k_16bit.wav",
            sampleRate: 44_100,
            bitsPerSample: 16,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                for (int f = 500; f <= 21_000; f += 500)
                {
                    double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                signal += 0.03 * Math.Sin(2.0 * Math.PI * 21_800.0 * t);
                double dither = (rand.NextDouble() - rand.NextDouble()) * (1.0 / 32768.0);
                return signal + dither;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.VerifiedLossless, report.Verdict);
        Assert.Equal(22_050u, report.DetectedCutoffHz);
    }

    [Fact]
    public void Test_9_Studio48k_NaturalRollOff_ShouldBe_AcousticOrVerified()
    {
        // Sinyal 48 kHz dengan peluruhan akustik alami (vokal / instrumen akustik)
        string wavPath = CreateSyntheticWav(
            fileName: "case9_studio_48k_acoustic.wav",
            sampleRate: 48_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                for (int f = 500; f <= 23_000; f += 500)
                {
                    double amp = 0.10 * Math.Pow((double)f / 1000.0, -1.5);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                double analogNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0001;
                return signal + analogNoise;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.True(report.Verdict is AuthenticityVerdict.AcousticNaturalRollOff or AuthenticityVerdict.VerifiedLossless,
            $"Vonis harus AcousticNaturalRollOff atau VerifiedLossless, terukur: {report.Verdict}");
    }

    [Fact]
    public void Test_10_Studio48k_UpscaledFromCd_ShouldBe_UpscaledFromCd()
    {
        // Sinyal 48 kHz hasil upsample master CD 44.1 kHz dengan filter potong curam di 22.05 kHz
        string wavPath = CreateSyntheticWav(
            fileName: "case10_studio_48k_upscaled_from_cd.wav",
            sampleRate: 48_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                for (int f = 500; f <= 21_000; f += 500)
                {
                    double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 1e-6;
                return signal + floorNoise;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.UpscaledFromCd, report.Verdict);
        Assert.Equal(22_050u, report.DetectedCutoffHz);
    }

    [Fact]
    public void Test_11_Standard44_1k_CutoffAt16k_ShouldBe_SuspectedUpscaledLossy()
    {
        // Sinyal 44.1 kHz yang terpotong tajam di 15.5 kHz (hasil transcode dari MP3 128 kbps)
        string wavPath = CreateSyntheticWav(
            fileName: "case11_standard_44_1k_mp3_cutoff.wav",
            sampleRate: 44_100,
            bitsPerSample: 16,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                for (int f = 500; f <= 15_500; f += 500)
                {
                    double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 1e-6;
                return signal + floorNoise;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
        Assert.True(report.DetectedCutoffHz <= 16_000u, $"Cutoff harus terdeteksi <= 16 kHz, terukur: {report.DetectedCutoffHz}");
    }

    [Fact]
    public void Test_12_NegativeControl_QuietAcousticSignal_ShouldNotBe_SuspectedUpscaledLossy()
    {
        // Uji pada 44.1 kHz dengan sinyal akustik ber-RMS rendah (-30 dBFS) dan peluruhan lembut
        // HARUS BUKAN SuspectedUpscaledLossy meskipun level energi mutlak di frekuensi tinggi sangat kecil
        string wavPath44k = CreateSyntheticWav(
            fileName: "case12_quiet_acoustic_44k.wav",
            sampleRate: 44_100,
            bitsPerSample: 16,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                for (int f = 400; f <= 21_500; f += 400)
                {
                    double amp = 0.01 * Math.Pow((double)f / 1000.0, -1.2);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                return signal;
            });

        var report44k = NyquistSpectrumAnalyzer.Analyze(wavPath44k);
        Assert.NotEqual(AuthenticityVerdict.SuspectedUpscaledLossy, report44k.Verdict);

        // Uji hal yang sama pada 48 kHz
        string wavPath48k = CreateSyntheticWav(
            fileName: "case12_quiet_acoustic_48k.wav",
            sampleRate: 48_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                for (int f = 400; f <= 23_000; f += 400)
                {
                    double amp = 0.01 * Math.Pow((double)f / 1000.0, -1.2);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                return signal;
            });

        var report48k = NyquistSpectrumAnalyzer.Analyze(wavPath48k);
        Assert.NotEqual(AuthenticityVerdict.SuspectedUpscaledLossy, report48k.Verdict);
    }

    [Fact]
    public void Test_13_NegativeControl_Studio48k_LowpassedMasterAt20k_ShouldBe_Inconclusive()
    {
        // Sinyal 48 kHz di-lowpass di 20 kHz (bukan batas CD 22.05 kHz).
        // Walaupun kemiringan OLS di sekitar 20 kHz curam saat runtuh ke lantai derau,
        // knee terukur di ~20 kHz berada di luar rentang CD [21.0k, 22.6k].
        // Vonis HARUS Inconclusive (bukan UpscaledFromCd dan bukan SuspectedUpscaledLossy).
        string wavPath = CreateSyntheticWav(
            fileName: "case13_studio_48k_lowpassed_20k.wav",
            sampleRate: 48_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                for (int f = 500; f <= 20_000; f += 500)
                {
                    double rolloff = f > 17_000 ? Math.Pow(17_000.0 / f, 3.0) : 1.0;
                    double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6) * rolloff;
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0001;
                return signal + floorNoise;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.NotEqual(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
        Assert.NotEqual(AuthenticityVerdict.UpscaledFromCd, report.Verdict);
        Assert.Equal(AuthenticityVerdict.Inconclusive, report.Verdict);
    }

    [Fact]
    public void Test_13b_NegativeControl_HiRes96k_LowpassedAt20k_ShouldBe_Inconclusive_NotUpscaledFromCd()
    {
        // Sinyal 24/96 lossless di-lowpass di 20 kHz (misal master pita analog atau filter studio).
        // Walaupun energi di atas 20 kHz runtuh ke lantai derau, knee berada di ~20 kHz (bukan batas CD 21.0k - 22.6k).
        // Vonis HARUS Inconclusive, BUKAN UpscaledFromCd.
        string wavPath = CreateSyntheticWav(
            fileName: "case13b_hires_96k_lowpass_20k.wav",
            sampleRate: 96_000,
            bitsPerSample: 24,
            durationSec: 10.5,
            sampleFunc: (t, rand) =>
            {
                double signal = 0.0;
                for (int f = 500; f <= 20_000; f += 500)
                {
                    double rolloff = f > 17_000 ? Math.Pow(17_000.0 / f, 3.0) : 1.0;
                    double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6) * rolloff;
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0001;
                return signal + floorNoise;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.NotEqual(AuthenticityVerdict.UpscaledFromCd, report.Verdict);
        Assert.Equal(AuthenticityVerdict.Inconclusive, report.Verdict);
    }

    [Fact]
    public void Test_14_DsdStream_ShouldBe_Inconclusive()
    {
        var dsdStream = new MockDsdAudioStream();
        var report = NyquistSpectrumAnalyzer.Analyze(dsdStream);

        Assert.Equal(AuthenticityVerdict.Inconclusive, report.Verdict);
        Assert.Contains("Asal-usul DSD tidak dapat diverifikasi", report.Explanation);
        Assert.False(report.IsTrue24Bit);
        Assert.Equal(0.0, report.LsbActivityPercentage);
        Assert.Equal(0.0, report.LsbEntropy);
    }

    [SkippableFact]
    public void Test_15_FFmpeg_TranscodeLossyVerification_ShouldDetectCutoffs()
    {
        Skip.IfNot(IsFFmpegAvailable(), "FFmpeg tidak ditemukan di system PATH host (timeout 3 dtk). Pengujian transcode dynamic di-skip secara aman.");

        string sourceWav;
        string? envSource = Environment.GetEnvironmentVariable("AURA_CORPUS_SOURCE");
        if (!string.IsNullOrWhiteSpace(envSource) && File.Exists(envSource))
        {
            sourceWav = envSource;
        }
        else
        {
            sourceWav = CreateSyntheticWav("ffmpeg_source_24_96.wav", 96_000, 24, 10.5, (t, rand) =>
            {
                double signal = 0.0;
                for (int f = 200; f <= 22_000; f += 200)
                {
                    double amp = 0.05 * Math.Pow((double)f / 1000.0, -0.5);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                double pinkNoise = (rand.NextDouble() - rand.NextDouble()) * 0.001;
                return signal + pinkNoise;
            });
        }

        var codecsToTest = new (string Ext, string CodecName, string EncArgs)[]
        {
            ("mp3", "MP3 128 kbps", "-c:a libmp3lame -b:a 128k"),
            ("mp3", "MP3 192 kbps", "-c:a libmp3lame -b:a 192k"),
            ("mp3", "MP3 320 kbps", "-c:a libmp3lame -b:a 320k"),
            ("m4a", "AAC 128 kbps", "-c:a aac -b:a 128k"),
            ("opus", "Opus 128 kbps", "-c:a libopus -b:a 128k")
        };

        _output.WriteLine("=========================================================================================================================");
        _output.WriteLine("                                      FFMPEG CODEC TRANSCODE FORENSIC TELEMETRY                                          ");
        _output.WriteLine("=========================================================================================================================");
        _output.WriteLine(string.Format("{0,-20} | {1,-10} | {2,-10} | {3,-10} | {4,-10} | {5,-10} | {6,-10} | {7,-22}",
            "Codec/Bitrate", "Cutoff(Hz)", "Knee(Hz)", "Steepness", "Drop(dB)", "relP16k", "relP20k", "Verdict"));
        _output.WriteLine("-------------------------------------------------------------------------------------------------------------------------");

        var mp3Reports = new List<(string Name, AudioIntegrityReport Report)>();
        int aacOpusFalseClaimCount = 0;
        int aacOpusTotalCount = 0;

        foreach (var (ext, name, encArgs) in codecsToTest)
        {
            string encodedPath = Path.Combine(_tempDirectory, $"test_transcode_{ext}_{Guid.NewGuid():N}.{ext}");
            string decodedWav44k = Path.Combine(_tempDirectory, $"test_decoded_{ext}_44k_{Guid.NewGuid():N}.wav");
            string decodedWav48k = Path.Combine(_tempDirectory, $"test_decoded_{ext}_48k_{Guid.NewGuid():N}.wav");

            try
            {
                RunProcess("ffmpeg", $"-y -i \"{sourceWav}\" {encArgs} \"{encodedPath}\"");

                // Decode ke 44.1 kHz
                RunProcess("ffmpeg", $"-y -i \"{encodedPath}\" -ar 44100 -ac 2 -c:a pcm_s16le \"{decodedWav44k}\"");
                if (File.Exists(decodedWav44k))
                {
                    var r44 = NyquistSpectrumAnalyzer.Analyze(decodedWav44k);
                    if (name.StartsWith("MP3"))
                    {
                        mp3Reports.Add((name + " @ 44.1k", r44));
                    }
                    else
                    {
                        aacOpusTotalCount++;
                        if (r44.Verdict is AuthenticityVerdict.VerifiedLossless or AuthenticityVerdict.AcousticNaturalRollOff)
                        {
                            aacOpusFalseClaimCount++;
                        }
                    }

                    _output.WriteLine(string.Format("{0,-20} | {1,-10} | {2,-10} | {3,-10:F1} | {4,-10:F1} | {5,-10:F1} | {6,-10:F1} | {7,-22}",
                        name + " @ 44.1k", r44.DetectedCutoffHz, r44.KneeHz ?? 0, r44.KneeSteepnessDbPerOct ?? 0.0,
                        r44.DropAcrossKneeDb ?? 0.0, r44.RelP16kDb ?? 0.0, r44.RelP20kDb ?? 0.0, r44.Verdict));
                }

                // Decode ke 48.0 kHz
                RunProcess("ffmpeg", $"-y -i \"{encodedPath}\" -ar 48000 -ac 2 -c:a pcm_s16le \"{decodedWav48k}\"");
                if (File.Exists(decodedWav48k))
                {
                    var r48 = NyquistSpectrumAnalyzer.Analyze(decodedWav48k);
                    if (name.StartsWith("MP3"))
                    {
                        mp3Reports.Add((name + " @ 48k", r48));
                    }
                    else
                    {
                        aacOpusTotalCount++;
                        if (r48.Verdict is AuthenticityVerdict.VerifiedLossless or AuthenticityVerdict.AcousticNaturalRollOff)
                        {
                            aacOpusFalseClaimCount++;
                        }
                    }

                    _output.WriteLine(string.Format("{0,-20} | {1,-10} | {2,-10} | {3,-10:F1} | {4,-10:F1} | {5,-10:F1} | {6,-10:F1} | {7,-22}",
                        name + " @ 48k", r48.DetectedCutoffHz, r48.KneeHz ?? 0, r48.KneeSteepnessDbPerOct ?? 0.0,
                        r48.DropAcrossKneeDb ?? 0.0, r48.RelP16kDb ?? 0.0, r48.RelP20kDb ?? 0.0, r48.Verdict));
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Gagal transcode {name}: {ex.Message}");
            }
        }
        _output.WriteLine("=========================================================================================================================");
        _output.WriteLine($"Ringkasan AAC/Opus: salah-klaim {aacOpusFalseClaimCount} dari {aacOpusTotalCount} " +
            "(AAC/Opus bitrate tinggi tanpa brickwall lowpass lolos filter cutoff spektral).");
        _output.WriteLine("=========================================================================================================================");

        foreach (var (mp3Name, r) in mp3Reports)
        {
            Assert.NotEqual(AuthenticityVerdict.VerifiedLossless, r.Verdict);
            Assert.NotEqual(AuthenticityVerdict.AcousticNaturalRollOff, r.Verdict);
        }
    }

    public enum GroundTruthCategory
    {
        Genuine,
        Fake,
        Ambiguous
    }

    [Fact]
    public void Test_16_ConfusionMatrix_And_TelemetryReport()
    {
        var testDefinitions = new List<(string Name, Func<AudioIntegrityReport> Runner, AuthenticityVerdict Expected, GroundTruthCategory GroundTruth, bool KnownLimitation)>
        {
            ("Native Hi-Res 24/96", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase1()), AuthenticityVerdict.VerifiedLossless, GroundTruthCategory.Genuine, false),
            ("Upscaled from CD", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase2()), AuthenticityVerdict.UpscaledFromCd, GroundTruthCategory.Fake, false),
            ("Zero-Padded 24-bit (12k limit)", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase3()), AuthenticityVerdict.ZeroPadded24Bit, GroundTruthCategory.Fake, false),
            ("Dithered 16-to-24-bit", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase4()), AuthenticityVerdict.Dithered16BitUpscale, GroundTruthCategory.Fake, false),
            ("Upscaled Lossy 16k Cutoff", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase5()), AuthenticityVerdict.SuspectedUpscaledLossy, GroundTruthCategory.Fake, false),
            ("Resampler Imaging Mirror", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase6()), AuthenticityVerdict.Inconclusive, GroundTruthCategory.Ambiguous, false),
            ("Natural Acoustic Roll-Off", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase7()), AuthenticityVerdict.AcousticNaturalRollOff, GroundTruthCategory.Genuine, false),
            ("Native 44.1k 16-bit", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase8()), AuthenticityVerdict.VerifiedLossless, GroundTruthCategory.Genuine, false),
            ("Studio 48k Acoustic Roll-Off", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase9()), AuthenticityVerdict.AcousticNaturalRollOff, GroundTruthCategory.Genuine, false),
            ("48k Upscaled from CD", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase10()), AuthenticityVerdict.UpscaledFromCd, GroundTruthCategory.Fake, false),
            ("44.1k Cutoff 16k MP3", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase11()), AuthenticityVerdict.SuspectedUpscaledLossy, GroundTruthCategory.Fake, false),
            ("Quiet Acoustic 44.1k", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase12_44k()), AuthenticityVerdict.Inconclusive, GroundTruthCategory.Genuine, false),
            ("Quiet Acoustic 48k", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase12_48k()), AuthenticityVerdict.AcousticNaturalRollOff, GroundTruthCategory.Genuine, false),
            ("Studio 48k Lowpassed 20k", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase13()), AuthenticityVerdict.Inconclusive, GroundTruthCategory.Genuine, false),
            ("Hi-Res 96k Lowpassed 20k", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase13b()), AuthenticityVerdict.Inconclusive, GroundTruthCategory.Genuine, false),
            ("Format 1-bit DSD Stream", () => NyquistSpectrumAnalyzer.Analyze(new MockDsdAudioStream()), AuthenticityVerdict.Inconclusive, GroundTruthCategory.Ambiguous, false),
            ("Dark Lossless 44.1k -30 dB/oct", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase17a()), AuthenticityVerdict.AcousticNaturalRollOff, GroundTruthCategory.Genuine, false),
            ("Dark Lossless 44.1k -50 dB/oct", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase17b()), AuthenticityVerdict.Inconclusive, GroundTruthCategory.Genuine, false),
            ("Documented Lowpass 15k", () => NyquistSpectrumAnalyzer.Analyze(GenerateCase18()), AuthenticityVerdict.SuspectedUpscaledLossy, GroundTruthCategory.Genuine, true),
        };

        var allVerdicts = new[]
        {
            AuthenticityVerdict.VerifiedLossless,
            AuthenticityVerdict.AcousticNaturalRollOff,
            AuthenticityVerdict.HybridMaster,
            AuthenticityVerdict.ZeroPadded24Bit,
            AuthenticityVerdict.UpscaledFromCd,
            AuthenticityVerdict.Dithered16BitUpscale,
            AuthenticityVerdict.SuspectedUpscaledLossy,
            AuthenticityVerdict.StandardLossy,
            AuthenticityVerdict.Inconclusive
        };

        int n = allVerdicts.Length;
        int[,] matrix = new int[n, n]; // [Expected, Actual]
        int correct = 0;
        int falseClaimsCount = 0;
        int abstainCount = 0;
        int knownLimitationsCount = 0;
        var falseClaimsList = new List<string>();
        var misclassifications = new List<string>();

        _output.WriteLine("=========================================================================================================================");
        _output.WriteLine("                                AURA AUDIO FORENSIC CORPUS DETAILED CASE RESULTS                                         ");
        _output.WriteLine("=========================================================================================================================");
        _output.WriteLine(string.Format("{0,-32} | {1,-24} | {2,-24} | {3,-8}", "Test Case", "Expected Verdict", "Actual Verdict", "Match?"));
        _output.WriteLine("-------------------------------------------------------------------------------------------------------------------------");

        foreach (var def in testDefinitions)
        {
            var r = def.Runner();
            int expIdx = Array.IndexOf(allVerdicts, def.Expected);
            int actIdx = Array.IndexOf(allVerdicts, r.Verdict);

            if (expIdx >= 0 && actIdx >= 0)
            {
                matrix[expIdx, actIdx]++;
            }

            string kneeStr = r.KneeHz.HasValue ? $"{r.KneeHz.Value} Hz" : "-- Hz";
            string steepStr = r.KneeSteepnessDbPerOct.HasValue ? $"{r.KneeSteepnessDbPerOct.Value:F1} dB/okt" : "-- dB/okt";
            string dropStr = r.DropAcrossKneeDb.HasValue ? $"{r.DropAcrossKneeDb.Value:F1} dB" : "-- dB";
            string rel16Str = r.RelP16kDb.HasValue ? $"{r.RelP16kDb.Value:F1} dB" : "-- dB";
            string rel20Str = r.RelP20kDb.HasValue ? $"{r.RelP20kDb.Value:F1} dB" : "-- dB";

            bool match = r.Verdict == def.Expected;
            if (match)
            {
                correct++;
            }
            else
            {
                misclassifications.Add(
                    $"MISCLASSIFICATION: {def.Name}\n" +
                    $"  Expected: {def.Expected}, Actual: {r.Verdict}\n" +
                    $"  p22k: {r.PowerAt22kDbFS:F1} dBFS, pNyquist: {r.PowerAtNyquistDbFS:F1} dBFS\n" +
                    $"  Cutoff: {r.DetectedCutoffHz} Hz, Knee: {kneeStr}\n" +
                    $"  Steepness: {steepStr}, Drop: {dropStr}\n" +
                    $"  relP16k: {rel16Str}, relP20k: {rel20Str}\n" +
                    $"  NoiseFloor: {r.UltrasonicNoiseFloorDbFS:F1} dBFS, Sigma: {r.UltrasonicNoiseStdDevDb:F1} dB\n" +
                    $"  Explanation: {r.Explanation}");
            }

            // 1. Abstain: vonis Inconclusive
            if (r.Verdict == AuthenticityVerdict.Inconclusive)
            {
                abstainCount++;
            }

            // 2. Keterbatasan yang diakui
            if (def.KnownLimitation)
            {
                knownLimitationsCount++;
            }

            // 3. Salah-klaim:
            // - ground-truth lossless (Genuine) dan bukan known limitation, tetapi divonis SuspectedUpscaledLossy/UpscaledFromCd/Dithered16BitUpscale
            // - ATAU ground-truth palsu (Fake), tetapi divonis VerifiedLossless/AcousticNaturalRollOff
            bool isFalseClaim = false;
            if (def.GroundTruth == GroundTruthCategory.Genuine && !def.KnownLimitation)
            {
                if (r.Verdict is AuthenticityVerdict.SuspectedUpscaledLossy
                              or AuthenticityVerdict.UpscaledFromCd
                              or AuthenticityVerdict.Dithered16BitUpscale
                              or AuthenticityVerdict.ZeroPadded24Bit)
                {
                    isFalseClaim = true;
                }
            }
            else if (def.GroundTruth == GroundTruthCategory.Fake)
            {
                if (r.Verdict is AuthenticityVerdict.VerifiedLossless
                              or AuthenticityVerdict.AcousticNaturalRollOff)
                {
                    isFalseClaim = true;
                }
            }

            if (isFalseClaim)
            {
                falseClaimsCount++;
                falseClaimsList.Add(
                    $"SALAH-KLAIM: {def.Name}\n" +
                    $"  GroundTruth: {def.GroundTruth}, Actual: {r.Verdict}\n" +
                    $"  Cutoff: {r.DetectedCutoffHz} Hz, Knee: {kneeStr}\n" +
                    $"  Steepness: {steepStr}, Drop: {dropStr}\n" +
                    $"  relP16k: {rel16Str}, relP20k: {rel20Str}\n" +
                    $"  Explanation: {r.Explanation}");
            }

            _output.WriteLine(string.Format("{0,-32} | {1,-24} | {2,-24} | {3,-8}",
                def.Name, def.Expected, r.Verdict, match ? "MATCH" : "MISMATCH"));
        }

        // Cetak Matriks Konfusi 2D
        _output.WriteLine("\n=========================================================================================================================");
        _output.WriteLine("                                    2D CONTINGENCY CONFUSION MATRIX (Rows: Expected, Cols: Actual)                      ");
        _output.WriteLine("=========================================================================================================================");
        string[] shortNames = { "VerLoss", "AcoRoll", "HybMast", "Zero24", "UpsCd", "Dith16", "SuspLoss", "StdLoss", "Inconc" };

        string header = string.Format("{0,-12} | ", "Exp \\ Act");
        for (int j = 0; j < n; j++) header += string.Format("{0,8} ", shortNames[j]);
        header += "|    Total";
        _output.WriteLine(header);
        _output.WriteLine(new string('-', 96));

        int[] colTotals = new int[n];
        for (int i = 0; i < n; i++)
        {
            int rowTotal = 0;
            string rowStr = string.Format("{0,-12} | ", shortNames[i]);
            for (int j = 0; j < n; j++)
            {
                int val = matrix[i, j];
                rowTotal += val;
                colTotals[j] += val;
                rowStr += string.Format("{0,8} ", val);
            }
            rowStr += string.Format("| {0,8}", rowTotal);
            _output.WriteLine(rowStr);
        }
        _output.WriteLine(new string('-', 96));
        string colTotalStr = string.Format("{0,-12} | ", "Total Pred");
        for (int j = 0; j < n; j++) colTotalStr += string.Format("{0,8} ", colTotals[j]);
        colTotalStr += string.Format("| {0,8}", testDefinitions.Count);
        _output.WriteLine(colTotalStr);

        // Metrik Per-Kelas (Precision, Recall, TP, FP, FN)
        _output.WriteLine("\n=========================================================================================================================");
        _output.WriteLine("                                                PER-CLASS ACCURACY METRICS                                              ");
        _output.WriteLine("=========================================================================================================================");
        _output.WriteLine(string.Format("{0,-24} | {1,-7} | {2,-4} | {3,-4} | {4,-4} | {5,-11} | {6,-11}",
            "Class Name", "Support", "TP", "FP", "FN", "Precision", "Recall"));
        _output.WriteLine("-------------------------------------------------------------------------------------------------------------------------");

        for (int i = 0; i < n; i++)
        {
            int tp = matrix[i, i];
            // Test 18 adalah Known Limitation, bukan True Positive alami untuk deteksi lossy asli
            if (allVerdicts[i] == AuthenticityVerdict.SuspectedUpscaledLossy)
            {
                tp = Math.Max(0, tp - 1);
            }

            int fn = 0;
            for (int j = 0; j < n; j++) if (j != i) fn += matrix[i, j];
            int fp = 0;
            for (int r = 0; r < n; r++) if (r != i) fp += matrix[r, i];

            int support = tp + fn;
            if (support == 0 && fp == 0) continue;

            double precision = (tp + fp) > 0 ? (double)tp / (tp + fp) * 100.0 : 100.0;
            double recall = support > 0 ? (double)tp / support * 100.0 : 100.0;

            _output.WriteLine(string.Format("{0,-24} | {1,7} | {2,4} | {3,4} | {4,4} | {5,10:F1}% | {6,10:F1}%",
                allVerdicts[i], support, tp, fp, fn, precision, recall));
        }

        // Ringkasan Tiga Ukuran Evaluasi
        double coverage = (double)(testDefinitions.Count - abstainCount) / testDefinitions.Count * 100.0;
        _output.WriteLine("\n=========================================================================================================================");
        _output.WriteLine("                                       RINGKASAN METRIK EVALUASI 3 UKURAN                                                ");
        _output.WriteLine("=========================================================================================================================");
        _output.WriteLine($"Jumlah Kasus Diuji         : {testDefinitions.Count}");
        _output.WriteLine($"Salah-Klaim tak diketahui  : {falseClaimsCount} (Target: 0); salah-klaim diketahui: {knownLimitationsCount}");
        _output.WriteLine($"Jumlah Abstain             : {abstainCount} (Vonis Inconclusive pada spektrum ambigu / roll-off lembut / kontrol negatif)");
        _output.WriteLine($"Keterbatasan Diakui        : {knownLimitationsCount} (Test 18: Lossless lowpass 15k terdeteksi SuspectedUpscaledLossy)");
        _output.WriteLine($"Coverage (Tingkat Putusan) : {coverage:F1}% ({testDefinitions.Count - abstainCount} / {testDefinitions.Count})");
        _output.WriteLine("=========================================================================================================================");

        if (falseClaimsList.Count > 0)
        {
            _output.WriteLine("\nDAFTAR KASUS SALAH-KLAIM BESERTA TELEMETRI:");
            foreach (var fc in falseClaimsList)
            {
                _output.WriteLine(fc);
            }
        }

        Assert.Equal(0, falseClaimsCount);
        Assert.Empty(misclassifications);
    }

    [Fact]
    public void Test_17a_NegativeControl_DarkLossless_30dBPerOct_ShouldNotBe_SuspectedLossy()
    {
        string wavPath = GenerateCase17a();
        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.NotEqual(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
        Assert.Equal(AuthenticityVerdict.AcousticNaturalRollOff, report.Verdict);
    }

    [Fact]
    public void Test_17b_NegativeControl_DarkLossless_50dBPerOct_ShouldNotBe_SuspectedLossy()
    {
        string wavPath = GenerateCase17b();
        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.NotEqual(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
        Assert.Equal(AuthenticityVerdict.Inconclusive, report.Verdict);
    }

    [Fact]
    public void Test_18_DocumentedBoundary_LosslessWithLowpass15k_Yields_SuspectedUpscaledLossy()
    {
        string wavPath = GenerateCase18();
        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
        Assert.Contains("Kemungkinan hasil konversi dari lossy", report.Explanation);
    }

    [SkippableFact]
    public void Test_19_Optional_RealCorpus_TelemetryBenchmark()
    {
        string? corpusPath = Environment.GetEnvironmentVariable("AURA_REAL_CORPUS");
        if (string.IsNullOrWhiteSpace(corpusPath) && Directory.Exists(@"E:\corpus_aura"))
        {
            corpusPath = @"E:\corpus_aura";
        }

        Skip.IfNot(!string.IsNullOrWhiteSpace(corpusPath) && Directory.Exists(corpusPath),
            "Environment variable AURA_REAL_CORPUS tidak disetel atau direktori tidak ditemukan. Pengujian dilewati.");

        var files = Directory.GetFiles(corpusPath!, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f))
            .ToList();

        if (files.Count == 0)
        {
            _output.WriteLine($"Tidak ditemukan berkas audio (.wav, .flac, .mp3) di direktori {corpusPath}.");
            return;
        }

        // Baca labels.csv bila ada
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string labelsCsvPath = Path.Combine(corpusPath!, "labels.csv");
        if (File.Exists(labelsCsvPath))
        {
            foreach (var line in File.ReadAllLines(labelsCsvPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',', 2);
                if (parts.Length > 0 && !parts[0].Trim().Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    labels[parts[0].Trim()] = parts.Length > 1 ? parts[1].Trim() : string.Empty;
                }
            }
        }

        _output.WriteLine("==================================================================================================================================================================================================================================================================");
        _output.WriteLine("                                                                                               AURA AUDIO REAL CORPUS TELEMETRY BENCHMARK                                                                                                 ");
        _output.WriteLine("==================================================================================================================================================================================================================================================================");
        _output.WriteLine(string.Format("{0,-32} | {1,-10} | {2,-6} | {3,-10} | {4,-10} | {5,-10} | {6,-10} | {7,-8} | {8,-6} | {9,-10} | {10,-12} | {11,-10} | {12,-12} | {13,-8} | {14,-8} | {15,-22} | {16}",
            "File Name", "Label", "Rate", "Cutoff(Hz)", "Knee(Hz)", "Steepness", "Drop(dB)", "Slope", "ImgR", "ImgAtt(dB)", "ImgUpper(dB)", "Cliff(Hz)", "CliffDrop(dB)", "relP16k", "relP20k", "Verdict", "Short Explanation"));
        _output.WriteLine("------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------");

        var fullExplanations = new List<(string Name, string Explanation)>();
        int totalControls = 0;
        int correctDefinitive = 0;
        int abstains = 0;
        int falseClaims = 0;

        foreach (var file in files)
        {
            try
            {
                var r = NyquistSpectrumAnalyzer.Analyze(file);
                string fileName = Path.GetFileName(file);
                string label = labels.TryGetValue(fileName, out var lbl) && !string.IsNullOrWhiteSpace(lbl) ? lbl : "--";
                string displayFileName = fileName.Length > 32 ? fileName.Substring(0, 29) + "..." : fileName;
                string shortExpl = r.Explanation.Length > 60 ? r.Explanation.Substring(0, 57) + "..." : r.Explanation;

                fullExplanations.Add((fileName, r.Explanation));

                _output.WriteLine(string.Format("{0,-32} | {1,-10} | {2,-6} | {3,-10} | {4,-10} | {5,-10:F1} | {6,-10:F1} | {7,-8:F1} | {8,-6} | {9,-10} | {10,-12} | {11,-10} | {12,-12} | {13,-8:F1} | {14,-8:F1} | {15,-22} | {16}",
                    displayFileName,
                    label,
                    r.SourceSpec.SampleRateHz,
                    r.DetectedCutoffHz,
                    r.KneeHz ?? 0,
                    r.KneeSteepnessDbPerOct ?? 0.0,
                    r.DropAcrossKneeDb ?? 0.0,
                    r.SpectralSlopeDbPerOctave ?? 0.0,
                    r.ImagingCorrelation.HasValue ? r.ImagingCorrelation.Value.ToString("F2") : "--",
                    r.ImagingAttenuationDb.HasValue ? r.ImagingAttenuationDb.Value.ToString("F1") : "--",
                    r.ImagingUpperMeanDb.HasValue ? r.ImagingUpperMeanDb.Value.ToString("F1") : "--",
                    r.CliffHz.HasValue ? r.CliffHz.Value.ToString() : "--",
                    r.CliffDropDb.HasValue ? r.CliffDropDb.Value.ToString("F1") : "--",
                    r.RelP16kDb ?? 0.0,
                    r.RelP20kDb ?? 0.0,
                    r.Verdict,
                    shortExpl));

                if (fileName.StartsWith("ctrl_", StringComparison.OrdinalIgnoreCase))
                {
                    totalControls++;
                    if (r.Verdict is AuthenticityVerdict.VerifiedLossless or AuthenticityVerdict.AcousticNaturalRollOff)
                    {
                        falseClaims++;
                    }
                    else if (r.Verdict == AuthenticityVerdict.Inconclusive)
                    {
                        abstains++;
                    }
                    else
                    {
                        correctDefinitive++;
                    }

                    // Dynamic asserts untuk berkas kontrol yang ada
                    if (fileName.Contains("_mp3_", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Contains("_cd_upsample", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("ctrl_lulu_cd_badresample.flac", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.NotEqual(AuthenticityVerdict.VerifiedLossless, r.Verdict);
                        Assert.NotEqual(AuthenticityVerdict.AcousticNaturalRollOff, r.Verdict);
                    }

                    if (fileName.Equals("ctrl_lulu_cd_upsample.flac", StringComparison.OrdinalIgnoreCase) ||
                        fileName.Equals("ctrl_bc_cd_upsample.flac", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.Equal(AuthenticityVerdict.UpscaledFromCd, r.Verdict);
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Gagal menganalisis {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        _output.WriteLine("==================================================================================================================================================================================================================================================================");
        _output.WriteLine($"RINGKASAN KONTROL REAL CORPUS: Total={totalControls}, Benar Tegas={correctDefinitive}, Abstain={abstains}, Salah-Klaim={falseClaims}");
        _output.WriteLine("==================================================================================================================================================================================================================================================================");

        _output.WriteLine("\n=========================================================================================================================");
        _output.WriteLine("                                             EXPLANATION LENGKAP TIAP BERKAS                                             ");
        _output.WriteLine("=========================================================================================================================");
        foreach (var (name, expl) in fullExplanations)
        {
            _output.WriteLine($"[{name}]");
            _output.WriteLine($"  {expl}\n");
        }
        _output.WriteLine("=========================================================================================================================");
    }

    [Fact]
    public void Test_20_SyntheticImagingResampler_ShouldDetectLinearAndPassPolyphase()
    {
        // Bangkitkan derau berwarna 44.1k dengan spektrum aktif di 18.0k - 21.8k
        int srIn = 44_100;
        int srOut = 96_000;
        double duration = 10.5;
        int numIn = (int)(srIn * duration);
        int numOut = (int)(srOut * duration);

        double[] xIn = new double[numIn];
        var rng = new Random(42);

        // Komponen harmonik bervariasi di 18.0k - 21.8k agar korelasi Pearson cermin (22.05k +- d) tinggi
        int numTones = 80;
        var freqs = new double[numTones];
        var amps = new double[numTones];
        var phases = new double[numTones];

        for (int i = 0; i < numTones; i++)
        {
            freqs[i] = 18_000.0 + (21_800.0 - 18_000.0) * (i / (double)(numTones - 1));
            amps[i] = 0.05 * (1.0 + 0.6 * Math.Sin(i * 0.3));
            phases[i] = rng.NextDouble() * 2.0 * Math.PI;
        }

        // Tambahkan konten audio dasar di passband 1k - 16k
        int baseTones = 30;
        var baseFreqs = new double[baseTones];
        var baseAmps = new double[baseTones];
        for (int i = 0; i < baseTones; i++)
        {
            baseFreqs[i] = 500.0 + (16_000.0 - 500.0) * (i / (double)(baseTones - 1));
            baseAmps[i] = 0.08 * Math.Pow(baseFreqs[i] / 1000.0, -0.4);
        }

        for (int n = 0; n < numIn; n++)
        {
            double t = (double)n / srIn;
            double s = 0.0;
            for (int i = 0; i < baseTones; i++)
            {
                s += baseAmps[i] * Math.Sin(2.0 * Math.PI * baseFreqs[i] * t);
            }
            for (int i = 0; i < numTones; i++)
            {
                s += amps[i] * Math.Sin(2.0 * Math.PI * freqs[i] * t + phases[i]);
            }
            xIn[n] = s;
        }

        // 1. Interpolasi Linear 44.1k -> 96k (Resampler Buruk / Tanpa Anti-Imaging Filter)
        double[] xOutLinear = new double[numOut];
        double step = (double)srIn / srOut;
        for (int m = 0; m < numOut; m++)
        {
            double pos = m * step;
            int idx = (int)pos;
            double frac = pos - idx;
            if (idx + 1 < numIn)
            {
                xOutLinear[m] = (1.0 - frac) * xIn[idx] + frac * xIn[idx + 1];
            }
            else if (idx < numIn)
            {
                xOutLinear[m] = xIn[idx];
            }
        }

        // Sinyal linear level keras
        string pathLoud = CreateSyntheticWavFromBuffer("imaging_linear_loud.wav", srOut, 24, xOutLinear);
        var reportLoud = NyquistSpectrumAnalyzer.Analyze(pathLoud);

        // Sinyal linear level -30 dBFS
        double scale30dB = Math.Pow(10.0, -30.0 / 20.0);
        double[] xOutQuiet = new double[numOut];
        for (int m = 0; m < numOut; m++) xOutQuiet[m] = xOutLinear[m] * scale30dB;
        string pathQuiet = CreateSyntheticWavFromBuffer("imaging_linear_quiet.wav", srOut, 24, xOutQuiet);
        var reportQuiet = NyquistSpectrumAnalyzer.Analyze(pathQuiet);

        // Keduanya harus terdeteksi imaging -> Inconclusive
        Assert.Equal(AuthenticityVerdict.Inconclusive, reportLoud.Verdict);
        Assert.True(reportLoud.ImagingCorrelation >= 0.65, $"Korelasi r keras harus >= 0.65, terukur {reportLoud.ImagingCorrelation}");
        Assert.True(reportLoud.ImagingAttenuationDb <= 15.0, $"Atenuasi keras harus <= 15 dB, terukur {reportLoud.ImagingAttenuationDb}");
        Assert.True(reportLoud.ImagingUpperMeanDb > -128.0, $"Level atas keras harus > -128 dBFS, terukur {reportLoud.ImagingUpperMeanDb}");

        Assert.Equal(AuthenticityVerdict.Inconclusive, reportQuiet.Verdict);
        Assert.True(reportQuiet.ImagingCorrelation >= 0.65, $"Korelasi r -30dB harus >= 0.65, terukur {reportQuiet.ImagingCorrelation}");
        Assert.True(reportQuiet.ImagingAttenuationDb <= 15.0, $"Atenuasi -30dB harus <= 15 dB, terukur {reportQuiet.ImagingAttenuationDb}");
        Assert.True(reportQuiet.ImagingUpperMeanDb > -128.0, $"Level atas -30dB harus > -128 dBFS, terukur {reportQuiet.ImagingUpperMeanDb}");

        // 2. Versi Polyphase Bagus (Anti-Imaging Filter dengan atenuasi > 20 dB di atas 22.05 kHz)
        // Menerapkan lowpass FIR curam pada 21.8 kHz
        int filterTaps = 81;
        double fc = 21_900.0 / srOut; // Frekuensi cutoff ternormalisasi
        double[] fir = new double[filterTaps];
        int halfTaps = (filterTaps - 1) / 2;
        double sumFir = 0.0;
        for (int k = 0; k < filterTaps; k++)
        {
            int n = k - halfTaps;
            double sinc = n == 0 ? 2.0 * Math.PI * fc : Math.Sin(2.0 * Math.PI * fc * n) / n;
            // Blackman-Harris window
            double w = 0.35875 - 0.48829 * Math.Cos(2.0 * Math.PI * k / (filterTaps - 1))
                               + 0.14128 * Math.Cos(4.0 * Math.PI * k / (filterTaps - 1))
                               - 0.01168 * Math.Cos(6.0 * Math.PI * k / (filterTaps - 1));
            fir[k] = sinc * w;
            sumFir += fir[k];
        }
        for (int k = 0; k < filterTaps; k++) fir[k] /= sumFir;

        double[] xOutPolyphase = new double[numOut];
        for (int m = halfTaps; m < numOut - halfTaps; m++)
        {
            double conv = 0.0;
            for (int k = 0; k < filterTaps; k++)
            {
                conv += fir[k] * xOutLinear[m - halfTaps + k];
            }
            xOutPolyphase[m] = conv;
        }

        string pathPoly = CreateSyntheticWavFromBuffer("imaging_polyphase.wav", srOut, 24, xOutPolyphase);
        var reportPoly = NyquistSpectrumAnalyzer.Analyze(pathPoly);

        // Versi polyphase bagus tidak boleh terdeteksi imaging
        Assert.False(reportPoly.ImagingCorrelation >= 0.65 && reportPoly.ImagingAttenuationDb <= 15.0 && reportPoly.ImagingUpperMeanDb > -128.0,
            $"Polyphase bagus tidak boleh memenuhi kriteria imaging (atenuasi terukur {reportPoly.ImagingAttenuationDb} dB)");
        Assert.NotEqual(AuthenticityVerdict.Inconclusive, reportPoly.Verdict);
    }

    [Fact]
    public void Test_21_SyntheticCliffDetection_VoicePatternAndGenuine48k()
    {
        // 1. Pola Voice: Berkas 48k hasil upsample CD 44.1k dengan potongan di 22.05 kHz,
        // diikuti lembah di 22 kHz lalu derau naik ke batas Nyquist (-100 dBFS flat + nada 23.95 kHz)
        string pathVoice = CreateSyntheticWav("case21_cliff_voice_pattern.wav", 48_000, 24, 10.5, (t, rand) =>
        {
            double music = 0.0;
            // Musik/derau berwarna aktif hanya hingga 21.8 kHz (drop tajam melintasi 22.05 kHz)
            for (int f = 400; f <= 21_800; f += 200)
            {
                music += 0.04 * Math.Pow((double)f / 1000.0, -0.4) * Math.Sin(2.0 * Math.PI * f * t);
            }

            // Derau datar di ~-100 dBFS
            double flatNoise = (rand.NextDouble() - rand.NextDouble()) * 2e-5;

            // Nada frekuensi tinggi dekat Nyquist (23.95 kHz) yang membuat knee deteksi melompat ke Nyquist
            double nyqSpike = 0.002 * Math.Sin(2.0 * Math.PI * 23_950.0 * t);

            return music + flatNoise + nyqSpike;
        });

        var reportVoice = NyquistSpectrumAnalyzer.Analyze(pathVoice);

        // Harus terdeteksi sebagai UpscaledFromCd melalui deteksi tebing (cliff)
        Assert.Equal(AuthenticityVerdict.UpscaledFromCd, reportVoice.Verdict);
        Assert.NotNull(reportVoice.CliffHz);
        Assert.True(reportVoice.CliffHz.Value >= 21_500 && reportVoice.CliffHz.Value <= 22_700,
            $"Cliff Hz harus di 21.5k-22.7k, terukur: {reportVoice.CliffHz}");
        Assert.NotNull(reportVoice.CliffDropDb);
        Assert.True(reportVoice.CliffDropDb.Value >= 8.0,
            $"Cliff drop harus >= 8.0 dB, terukur: {reportVoice.CliffDropDb}");

        // 2. Master 48k asli dengan lowpass di 23.5 kHz (bukan upsample CD)
        string pathGenuine48k = CreateSyntheticWav("case21_genuine_48k_lowpass23_5.wav", 48_000, 24, 10.5, (t, rand) =>
        {
            double music = 0.0;
            // Musik aktif membentang mulus melewati batas CD (22.05 kHz) hingga 23.5 kHz
            for (int f = 400; f <= 23_500; f += 200)
            {
                music += 0.04 * Math.Pow((double)f / 1000.0, -0.4) * Math.Sin(2.0 * Math.PI * f * t);
            }

            double analogNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0001;
            return music + analogNoise;
        });

        var reportGenuine = NyquistSpectrumAnalyzer.Analyze(pathGenuine48k);

        // Master 48k asli dengan lowpass 23.5k tidak boleh UpscaledFromCd
        Assert.NotEqual(AuthenticityVerdict.UpscaledFromCd, reportGenuine.Verdict);
    }

    private static bool IsFFmpegAvailable()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            bool exited = proc.WaitForExit(3000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RunProcess(string fileName, string args, int timeoutMs = 15000)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        var stdoutBuilder = new System.Text.StringBuilder();
        var stderrBuilder = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdoutBuilder.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderrBuilder.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool exited = proc.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { proc.Kill(); } catch { }
            throw new TimeoutException($"Proses '{fileName} {args}' timeout setelah {timeoutMs} ms.");
        }

        proc.WaitForExit();

        if (proc.ExitCode != 0)
        {
            string error = stderrBuilder.ToString().Trim();
            throw new InvalidOperationException($"Proses '{fileName}' gagal dengan exit code {proc.ExitCode}: {error}");
        }
    }

    private string GenerateCase1() => CreateSyntheticWav(
        "case1_native_24_96.wav", 96_000, 24, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 500; f <= 47_500; f += 500)
            {
                double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            signal += 0.02 * Math.Sin(2.0 * Math.PI * 47_900.0 * t);
            double analogNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0002;
            return signal + analogNoise;
        });

    private string GenerateCase2() => CreateSyntheticWav(
        "case2_cd_upscaled.wav", 96_000, 24, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 500; f <= 21_000; f += 500)
            {
                double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 1e-6;
            return signal + floorNoise;
        });

    private string GenerateCase3() => CreateSyntheticWav(
        "case3_zero_padded.wav", 96_000, 24, 10.5, (t, rand) =>
        {
            return 0.35 * Math.Sin(2.0 * Math.PI * 440.0 * t) +
                   0.30 * Math.Sin(2.0 * Math.PI * 1_000.0 * t) +
                   0.15 * Math.Sin(2.0 * Math.PI * 12_000.0 * t);
        }, zeroPadLsb: true);

    private string GenerateCase4()
    {
        const double lsb16 = 2.0 / 65536.0;
        double ditherScale = Math.Sqrt(1.5) * lsb16;
        return CreateSyntheticWavStereo("case4_dithered_16bit.wav", 96_000, 24, 10.5, (t, ch, rand) =>
        {
            double music = 0.30 * Math.Sin(2.0 * Math.PI * 440.0 * t) +
                           0.25 * Math.Sin(2.0 * Math.PI * 1_000.0 * t) +
                           0.15 * Math.Sin(2.0 * Math.PI * 12_000.0 * t) +
                           0.08 * Math.Sin(2.0 * Math.PI * 19_500.0 * t);
            double u1 = rand.NextDouble() - 0.5;
            double u2 = rand.NextDouble() - 0.5;
            double tpdfDither = (u1 + u2) * ditherScale;
            return music + tpdfDither;
        });
    }

    private string GenerateCase5() => CreateSyntheticWav(
        "case5_fake_mp3_cutoff.wav", 96_000, 24, 10.5, (t, rand) =>
        {
            double signal = 0.35 * Math.Sin(2.0 * Math.PI * 440.0 * t) +
                            0.30 * Math.Sin(2.0 * Math.PI * 1_000.0 * t) +
                            0.20 * Math.Sin(2.0 * Math.PI * 3_500.0 * t) +
                            0.15 * Math.Sin(2.0 * Math.PI * 8_000.0 * t) +
                            0.10 * Math.Sin(2.0 * Math.PI * 14_500.0 * t);
            double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 1e-7;
            return signal + floorNoise;
        });

    private string GenerateCase6() => CreateSyntheticWav(
        "case6_imaging_mirror.wav", 96_000, 24, 10.5, (t, rand) =>
        {
            double music = 0.25 * Math.Sin(2.0 * Math.PI * 440.0 * t) +
                           0.25 * Math.Sin(2.0 * Math.PI * 1_000.0 * t) +
                           0.20 * Math.Sin(2.0 * Math.PI * 3_500.0 * t) +
                           0.15 * Math.Sin(2.0 * Math.PI * 8_000.0 * t) +
                           0.10 * Math.Sin(2.0 * Math.PI * 12_000.0 * t) +
                           0.08 * Math.Sin(2.0 * Math.PI * 16_000.0 * t);

            double d1 = 500.0, d2 = 1200.0, d3 = 2000.0, d4 = 3100.0;
            double mirrorLower = 0.05 * Math.Sin(2.0 * Math.PI * (22_050.0 - d1) * t) +
                                 0.04 * Math.Sin(2.0 * Math.PI * (22_050.0 - d2) * t) +
                                 0.03 * Math.Sin(2.0 * Math.PI * (22_050.0 - d3) * t) +
                                 0.02 * Math.Sin(2.0 * Math.PI * (22_050.0 - d4) * t);
            double mirrorUpper = 0.04 * Math.Sin(2.0 * Math.PI * (22_050.0 + d1) * t) +
                                 0.032 * Math.Sin(2.0 * Math.PI * (22_050.0 + d2) * t) +
                                 0.024 * Math.Sin(2.0 * Math.PI * (22_050.0 + d3) * t) +
                                 0.016 * Math.Sin(2.0 * Math.PI * (22_050.0 + d4) * t);

            double analogNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0001;
            return music + mirrorLower + mirrorUpper + analogNoise;
        });

    private string GenerateCase7() => CreateSyntheticWav(
        "case7_acoustic_natural_rolloff.wav", 96_000, 24, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 500; f <= 23_500; f += 500)
            {
                double amp = 0.12 * Math.Pow((double)f / 1000.0, -1.8);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double softFloor = (rand.NextDouble() - rand.NextDouble()) * 0.000005;
            return signal + softFloor;
        });

    private string GenerateCase8() => CreateSyntheticWav(
        "case8_native_44_1k_16bit.wav", 44_100, 16, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 500; f <= 21_000; f += 500)
            {
                double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            signal += 0.03 * Math.Sin(2.0 * Math.PI * 21_800.0 * t);
            double dither = (rand.NextDouble() - rand.NextDouble()) * (1.0 / 32768.0);
            return signal + dither;
        });

    private string GenerateCase9() => CreateSyntheticWav(
        "case9_studio_48k_acoustic.wav", 48_000, 24, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 500; f <= 23_000; f += 500)
            {
                double amp = 0.10 * Math.Pow((double)f / 1000.0, -1.5);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double analogNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0001;
            return signal + analogNoise;
        });

    private string GenerateCase10() => CreateSyntheticWav(
        "case10_studio_48k_upscaled_from_cd.wav", 48_000, 24, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 500; f <= 21_000; f += 500)
            {
                double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 1e-6;
            return signal + floorNoise;
        });

    private string GenerateCase11() => CreateSyntheticWav(
        "case11_standard_44_1k_mp3_cutoff.wav", 44_100, 16, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 500; f <= 15_500; f += 500)
            {
                double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 1e-6;
            return signal + floorNoise;
        });

    private string GenerateCase12_44k() => CreateSyntheticWav(
        "case12_quiet_acoustic_44k.wav", 44_100, 16, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 400; f <= 21_500; f += 400)
            {
                double amp = 0.01 * Math.Pow((double)f / 1000.0, -1.2);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            return signal;
        });

    private string GenerateCase12_48k() => CreateSyntheticWav(
        "case12_quiet_acoustic_48k.wav", 48_000, 24, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 400; f <= 23_000; f += 400)
            {
                double amp = 0.01 * Math.Pow((double)f / 1000.0, -1.2);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            return signal;
        });

    private string GenerateCase13() => CreateSyntheticWav(
        "case13_studio_48k_lowpassed_20k.wav", 48_000, 24, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 500; f <= 20_000; f += 500)
            {
                double rolloff = f > 17_000 ? Math.Pow(17_000.0 / f, 3.0) : 1.0;
                double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6) * rolloff;
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0001;
            return signal + floorNoise;
        });

    private string GenerateCase13b() => CreateSyntheticWav(
        "case13b_hires_96k_lowpass_20k.wav", 96_000, 24, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 500; f <= 20_000; f += 500)
            {
                double rolloff = f > 17_000 ? Math.Pow(17_000.0 / f, 3.0) : 1.0;
                double amp = 0.08 * Math.Pow((double)f / 1000.0, -0.6) * rolloff;
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0001;
            return signal + floorNoise;
        });

    private string GenerateCase17a() => CreateSyntheticWav(
        "case17a_dark_lossless_30db.wav", 44_100, 16, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 200; f <= 21_500; f += 200)
            {
                double rolloff = f > 2000 ? Math.Pow(2000.0 / f, 4.98) : 1.0;
                double amp = 0.02 * rolloff;
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double dither = (rand.NextDouble() - rand.NextDouble()) * (1.0 / 32768.0);
            return signal + dither;
        });

    private string GenerateCase17b() => CreateSyntheticWav(
        "case17b_dark_lossless_50db.wav", 44_100, 16, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 200; f <= 21_500; f += 200)
            {
                double rolloff = f > 2000 ? Math.Pow(2000.0 / f, 8.30) : 1.0;
                double amp = 0.02 * rolloff;
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double dither = (rand.NextDouble() - rand.NextDouble()) * (1.0 / 32768.0);
            return signal + dither;
        });

    private string GenerateCase18() => CreateSyntheticWav(
        "case18_lossless_lowpass_15k.wav", 44_100, 16, 10.5, (t, rand) =>
        {
            double signal = 0.0;
            for (int f = 200; f <= 15_000; f += 200)
            {
                double amp = 0.015 * Math.Pow((double)f / 1000.0, -0.5);
                signal += amp * Math.Sin(2.0 * Math.PI * f * t);
            }
            double dither = (rand.NextDouble() - rand.NextDouble()) * (1.0 / 32768.0);
            return signal + dither;
        });

    /// <summary>
    /// Helper pembangun berkas WAV PCM bit-exact di disk untuk pengujian audio forensik dengan kanal stereo independen.
    /// </summary>
    private string CreateSyntheticWavStereo(
        string fileName,
        int sampleRate,
        int bitsPerSample,
        double durationSec,
        Func<double, int, Random, double> sampleFunc,
        bool zeroPadLsb = false)
    {
        string filePath = Path.Combine(_tempDirectory, fileName);
        if (File.Exists(filePath)) return filePath;
        int channels = 2;
        int totalFrames = (int)(sampleRate * durationSec);
        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channels * bytesPerSample;
        int byteRate = sampleRate * blockAlign;
        int dataSize = totalFrames * blockAlign;
        int chunkSize = 36 + dataSize;

        var rand = new Random(42); // Seed konstan untuk determinisme pengujian

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs);

        // Header RIFF
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(chunkSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // Chunk "fmt "
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16); // PCM chunk size
        bw.Write((short)1); // PCM integer
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);

        // Chunk "data"
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        for (int i = 0; i < totalFrames; i++)
        {
            double t = (double)i / sampleRate;

            // Amplop energi RMS: puncak klimaks di tengah (50% durasi lagu)
            // agar FindLoudestSectionTime secara konsisten memilih titik 50%
            double envelope = 0.6 + 0.4 * Math.Sin(Math.PI * t / durationSec);

            for (int ch = 0; ch < channels; ch++)
            {
                double sampleVal = sampleFunc(t, ch, rand) * envelope;
                sampleVal = Math.Clamp(sampleVal, -0.99, 0.99);

                if (bitsPerSample == 16)
                {
                    short s = (short)Math.Round(sampleVal * 32767.0);
                    bw.Write(s);
                }
                else if (bitsPerSample == 24)
                {
                    int s = (int)Math.Round(sampleVal * 8388607.0);
                    if (zeroPadLsb)
                    {
                        s &= unchecked((int)0xFFFFFF00); // Nolkan byte LSB terendah
                    }

                    byte b0 = (byte)(s & 0xFF);
                    byte b1 = (byte)((s >> 8) & 0xFF);
                    byte b2 = (byte)((s >> 16) & 0xFF);

                    bw.Write(b0);
                    bw.Write(b1);
                    bw.Write(b2);
                }
            }
        }

        return filePath;
    }

    /// <summary>
    /// Overload kompatibel untuk pembangun berkas WAV mono/simetris.
    /// </summary>
    private string CreateSyntheticWav(
        string fileName,
        int sampleRate,
        int bitsPerSample,
        double durationSec,
        Func<double, Random, double> sampleFunc,
        bool zeroPadLsb = false)
    {
        return CreateSyntheticWavStereo(
            fileName,
            sampleRate,
            bitsPerSample,
            durationSec,
            (t, ch, rand) => sampleFunc(t, rand),
            zeroPadLsb);
    }

    private string CreateSyntheticWavFromBuffer(string fileName, int sampleRate, int bitsPerSample, double[] samples)
    {
        string filePath = Path.Combine(_tempDirectory, fileName);
        int channels = 2;
        int totalFrames = samples.Length;
        int bytesPerSample = bitsPerSample / 8;
        int blockAlign = channels * bytesPerSample;
        int byteRate = sampleRate * blockAlign;
        int subchunk2Size = totalFrames * blockAlign;
        int chunkSize = 36 + subchunk2Size;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(chunkSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(subchunk2Size);

        for (int i = 0; i < totalFrames; i++)
        {
            double sampleVal = Math.Clamp(samples[i], -0.99, 0.99);
            for (int ch = 0; ch < channels; ch++)
            {
                if (bitsPerSample == 16)
                {
                    short s = (short)Math.Round(sampleVal * 32767.0);
                    bw.Write(s);
                }
                else if (bitsPerSample == 24)
                {
                    int s = (int)Math.Round(sampleVal * 8388607.0);
                    byte b0 = (byte)(s & 0xFF);
                    byte b1 = (byte)((s >> 8) & 0xFF);
                    byte b2 = (byte)((s >> 16) & 0xFF);
                    bw.Write(b0);
                    bw.Write(b1);
                    bw.Write(b2);
                }
            }
        }
        return filePath;
    }

    [Fact]
    public void Test_22_SoftSoundtrack44_1k_ShouldBe_AcousticNaturalRollOff_WithReal16BitLsb()
    {
        // Simulasi lagu soundtrack akustik 16-bit / 44.1 kHz dengan rolloff halus mulai ~3.5 kHz (-26 dB/okt, tanpa brickwall cliff)
        string wavPath = CreateSyntheticWavStereo(
            fileName: "case22_soft_soundtrack_44k_16bit.wav",
            sampleRate: 44_100,
            bitsPerSample: 16,
            durationSec: 10.5,
            sampleFunc: (t, ch, rand) =>
            {
                double signal = 0.0;
                double phaseOffset = ch * 0.27;
                for (int f = 180; f <= 21_500; f += 180)
                {
                    double rolloff = f > 3_500 ? Math.Pow(3_500.0 / f, 4.3) : 1.0;
                    double amp = 0.025 * rolloff;
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t + phaseOffset + f * 0.001);
                }
                double dither = (rand.NextDouble() - rand.NextDouble()) * (1.0 / 32768.0);
                return signal + dither;
            });

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.AcousticNaturalRollOff, report.Verdict);
        Assert.True(report.LsbActivityPercentage > 90.0, $"LSB 16-bit harus aktif (> 90%), terukur: {report.LsbActivityPercentage:F2}%");
        Assert.True(report.LsbEntropy > 7.5, $"Entropi LSB 16-bit harus tinggi (> 7.5), terukur: {report.LsbEntropy:F2}");
    }

    [Fact]
    public void Test_23_Container24Bit44_1k_GenuineVsZeroPadded()
    {
        string genuinePath = CreateSyntheticWavStereo(
            fileName: "case23_genuine_24bit_44k.wav",
            sampleRate: 44_100,
            bitsPerSample: 24,
            durationSec: 10.5,
            zeroPadLsb: false,
            sampleFunc: (t, ch, rand) =>
            {
                double signal = 0.0;
                double phase = ch * 0.21;
                for (int f = 200; f <= 22_000; f += 200)
                {
                    double amp = 0.015 * Math.Pow((double)f / 1000.0, -0.45);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t + phase);
                }
                double floorNoise = (rand.NextDouble() - rand.NextDouble()) * 0.0002;
                return signal + floorNoise;
            });

        string zeroPaddedPath = CreateSyntheticWavStereo(
            fileName: "case23_zeropadded_24bit_44k.wav",
            sampleRate: 44_100,
            bitsPerSample: 24,
            durationSec: 10.5,
            zeroPadLsb: true,
            sampleFunc: (t, ch, rand) =>
            {
                double signal = 0.0;
                for (int f = 200; f <= 21_500; f += 200)
                {
                    double amp = 0.015 * Math.Pow((double)f / 1000.0, -0.45);
                    signal += amp * Math.Sin(2.0 * Math.PI * f * t);
                }
                return signal;
            });

        var genuineReport = NyquistSpectrumAnalyzer.Analyze(genuinePath);
        var zeroReport = NyquistSpectrumAnalyzer.Analyze(zeroPaddedPath);

        Assert.False(genuineReport.IsZeroPadded);
        Assert.True(genuineReport.IsTrue24Bit);
        Assert.Contains(genuineReport.Verdict, new[]
        {
            AuthenticityVerdict.VerifiedLossless,
            AuthenticityVerdict.AcousticNaturalRollOff
        });

        Assert.True(zeroReport.IsZeroPadded);
        Assert.False(zeroReport.IsTrue24Bit);
        Assert.Equal(AuthenticityVerdict.ZeroPadded24Bit, zeroReport.Verdict);
    }

    [Fact]
    public void Test_24_HardwareContext_InactiveVsExclusiveBitPerfectVsSharedResampled()
    {
        string wavPath = GenerateCase8();
        var matchingHwSpec = AudioStreamSpec.StandardCd(AudioCodec.WAV);

        var inactiveReport = NyquistSpectrumAnalyzer.Analyze(
            wavPath,
            hardwareSpec: matchingHwSpec,
            hasActiveHardwarePath: false,
            isHardwareExclusive: true,
            configuredDriverType: AudioDriverType.WASAPI_Exclusive);

        var exclusiveMatchReport = NyquistSpectrumAnalyzer.Analyze(
            wavPath,
            hardwareSpec: matchingHwSpec,
            hasActiveHardwarePath: true,
            isHardwareExclusive: true,
            configuredDriverType: AudioDriverType.WASAPI_Exclusive);

        var sharedReport = NyquistSpectrumAnalyzer.Analyze(
            wavPath,
            hardwareSpec: new AudioStreamSpec(AudioCodec.WAV, 48_000, BitDepth.Bit24, 2, 0),
            hasActiveHardwarePath: true,
            isHardwareExclusive: false,
            configuredDriverType: AudioDriverType.WASAPI_Shared);

        Assert.False(inactiveReport.HasActiveHardwarePath);
        Assert.False(inactiveReport.IsBitPerfect);

        Assert.True(exclusiveMatchReport.HasActiveHardwarePath);
        Assert.True(exclusiveMatchReport.IsHardwareExclusive);
        Assert.True(exclusiveMatchReport.IsBitPerfect);

        Assert.True(sharedReport.HasActiveHardwarePath);
        Assert.False(sharedReport.IsHardwareExclusive);
        Assert.False(sharedReport.IsBitPerfect);
    }

    [SkippableFact]
    public void Test_25_M4aCodecDetection_AacLossyVsAlacLossless()
    {
        Skip.IfNot(IsFFmpegAvailable(), "FFmpeg tidak ditemukan di system PATH host. Pengujian codec .m4a dilewati.");

        string wavSource = GenerateCase8();
        string aacM4aPath = Path.Combine(_tempDirectory, "case25_aac.m4a");
        string alacM4aPath = Path.Combine(_tempDirectory, "case25_alac.m4a");

        RunProcess("ffmpeg", $"-y -i \"{wavSource}\" -c:a aac -b:a 192k \"{aacM4aPath}\"");
        RunProcess("ffmpeg", $"-y -i \"{wavSource}\" -c:a alac \"{alacM4aPath}\"");

        Skip.IfNot(File.Exists(aacM4aPath) && File.Exists(alacM4aPath), "FFmpeg gagal membuat berkas uji .m4a AAC/ALAC.");

        var reader = new AudioMetadataReader();
        var aacMeta = reader.ReadFile(aacM4aPath);
        var alacMeta = reader.ReadFile(alacM4aPath);

        Assert.Equal(AudioCodec.AAC, aacMeta.Track.StreamSpec.Codec);
        Assert.Equal(AudioCodec.ALAC, alacMeta.Track.StreamSpec.Codec);

        using var aacStream = AudioDecoderFactory.OpenStream(aacM4aPath);
        using var alacStream = AudioDecoderFactory.OpenStream(alacM4aPath);

        Assert.Equal(AudioCodec.AAC, aacStream.Spec.Codec);
        Assert.Equal(AudioCodec.ALAC, alacStream.Spec.Codec);
    }

    [SkippableFact]
    public void Test_26_Optional_RealMusicLibrary_EmpiricalVerification()
    {
        Skip.IfNot(Directory.Exists(@"D:\Music"), @"Direktori D:\Music tidak tersedia di mesin ini.");

        var allFlacs = Directory.GetFiles(@"D:\Music", "*.flac", SearchOption.AllDirectories);
        var targetNames = new[]
        {
            "2.06 不協（和）音.flac",
            "15 JKT48 - Honest Man.flac",
            "01 名もなき何もかも.flac",
            "01.ハッピーヒプノシズム.flac",
            "01. はらぱん ぱんぱんぱん.flac",
            "lulu..flac",
            "Green Day - Basket Case.flac",
            "12. またね.flac"
        };

        var foundAny = false;
        foreach (string name in targetNames)
        {
            string? match = allFlacs.FirstOrDefault(p =>
                string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));
            if (match == null) continue;

            foundAny = true;
            var r = NyquistSpectrumAnalyzer.Analyze(match);
            _output.WriteLine($"[REAL] {name}");
            _output.WriteLine($"  Verdict: {r.Verdict} | Badge: {r.VerdictBadge} | Cutoff: {r.DetectedCutoffHz} Hz | Knee: {r.KneeHz?.ToString() ?? "--"} Hz");
            _output.WriteLine($"  LSB: {r.LsbActivityPercentage:F2}% | Entropy: {r.LsbEntropy:F2}/8 bit | Slope: {r.SpectralSlopeDbPerOctave?.ToString("F1") ?? "--"} dB/oct");
            _output.WriteLine($"  Container: {r.SpecContainerValue} | Freq: {r.SpecFrequencyValue} | BitDepth: {r.SpecBitDepthValue} | Output: {r.SpecOutputValue}");
            _output.WriteLine($"  Explain: {r.LocalizedExplanation}");

            if (name == "2.06 不協（和）音.flac")
            {
                Assert.Contains(r.Verdict, new[] { AuthenticityVerdict.AcousticNaturalRollOff, AuthenticityVerdict.VerifiedLossless });
                Assert.True(r.LsbActivityPercentage > 90.0, $"LSB 16-bit harus > 90%: {r.LsbActivityPercentage:F2}%");
                Assert.True(r.LsbEntropy > 7.5, $"Entropi harus > 7.5: {r.LsbEntropy:F2}");
            }
            else if (name == "15 JKT48 - Honest Man.flac")
            {
                Assert.Contains(r.Verdict, new[] { AuthenticityVerdict.VerifiedLossless, AuthenticityVerdict.AcousticNaturalRollOff });
                Assert.True(r.LsbActivityPercentage > 90.0, $"LSB 16-bit harus > 90%: {r.LsbActivityPercentage:F2}%");
                Assert.True(r.LsbEntropy > 7.5, $"Entropi harus > 7.5: {r.LsbEntropy:F2}");
            }
            else if (name == "01 名もなき何もかも.flac")
            {
                Assert.Contains(r.Verdict, new[] { AuthenticityVerdict.VerifiedLossless, AuthenticityVerdict.AcousticNaturalRollOff });
                Assert.True(r.IsTrue24Bit);
            }
            else if (name == "01.ハッピーヒプノシズム.flac")
            {
                Assert.Equal(AuthenticityVerdict.UpscaledFromCd, r.Verdict);
            }
        }

        Skip.IfNot(foundAny, @"Tidak ada berkas target benchmark di D:\Music.");
    }

    private class MockDsdAudioStream : IAudioStream
    {
        public string FilePath => "synthetic_test.dsf";
        public AudioStreamSpec Spec => AudioStreamSpec.Dsd64();
        public TimeSpan TotalTime => TimeSpan.FromMinutes(4);
        public TimeSpan CurrentTime { get; set; } = TimeSpan.Zero;
        public long Position { get; set; } = 0;
        public long Length => 10_000_000;
        public WaveFormat WaveFormat => new WaveFormat(44100, 16, 2);
        public int Read(byte[] buffer, int offset, int count) => count;
        public int Read(Span<byte> buffer) => buffer.Length;
        public void Dispose() { }
    }
}
