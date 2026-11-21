using System;
using System.IO;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Core.Models;
using AuraAudio.UI.Helpers;
using NAudio.Wave;
using Xunit;

namespace AuraAudio.Tests;

/// <summary>
/// Synthetic & opt-in corpus regression tests for AuraAudio v2.0.2 Phase B forensic fixes
/// (FORENSIC #3 through #7). All synthetic test audio is generated in-memory/temp WAV with zero
/// third-party audio committed.
/// </summary>
public sealed class ForensicV202RegressionTests : IDisposable
{
    private readonly string _tempDir;

    public ForensicV202RegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_ForensicV202_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Theory]
    [InlineData(22_050, "22.1 kHz", "22,1 kHz")]
    [InlineData(32_000, "32.0 kHz", "32,0 kHz")]
    [InlineData(35_467, "35.5 kHz", "35,5 kHz")]
    [InlineData(39_000, "39.0 kHz", "39,0 kHz")]
    public void Forensic4_GenuineLowSampleRates_AreVerifiedLossless_WithActualSampleRateInBothLanguages(
        int sampleRate,
        string expectedEnRate,
        string expectedIdRate)
    {
        // Full-band genuine signal extending up to 96% of Nyquist (matching real anti-aliasing filter roll-off)
        string wavPath = WriteBroadbandSineBankWav(
            $"gen_lowsr_{sampleRate}.wav",
            sampleRate: sampleRate,
            bitsPerSample: 16,
            durationSeconds: 3.0,
            maxFreqHz: (sampleRate / 2.0) * 0.96,
            amplitudeScale: 0.55);

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.VerifiedLossless, report.Verdict);
        Assert.DoesNotContain("44.1 kHz", report.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            report.Explanation.Contains(expectedEnRate, StringComparison.OrdinalIgnoreCase) ||
            report.Explanation.Contains(expectedIdRate, StringComparison.OrdinalIgnoreCase),
            $"Indonesian explanation did not contain actual sample rate ({expectedEnRate}/{expectedIdRate}): {report.Explanation}");

        string prevLang = LocalizationManager.CurrentLanguage;
        try
        {
            LocalizationManager.SetLanguage("en");
            string enExpl = report.LocalizedExplanation;
            string enBadge = report.VerdictBadge;
            Assert.DoesNotContain("44.1 kHz", enExpl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CD Quality", enBadge, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                enExpl.Contains(expectedEnRate, StringComparison.OrdinalIgnoreCase) ||
                enExpl.Contains(expectedIdRate, StringComparison.OrdinalIgnoreCase),
                $"English explanation did not contain actual sample rate: {enExpl}");
        }
        finally
        {
            LocalizationManager.SetLanguage(prevLang);
        }
    }

    [Fact]
    public void Forensic4_LowSampleRateWithHardEarlyBrickwall_ReportsActualSampleRateAndFlagsLossy()
    {
        // 32 kHz file (Nyquist 16 kHz) with a sharp brickwall cutoff at 11.5 kHz (71.9% of Nyquist < 85% gate)
        string wavPath = WriteBroadbandSineBankWav(
            "lowsr_32k_brickwall_11500.wav",
            sampleRate: 32_000,
            bitsPerSample: 16,
            durationSeconds: 3.0,
            maxFreqHz: 11_500.0,
            amplitudeScale: 0.60);

        var report = NyquistSpectrumAnalyzer.Analyze(wavPath);

        Assert.Equal(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
        Assert.DoesNotContain("44.1 kHz", report.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            report.Explanation.Contains("32.0 kHz", StringComparison.OrdinalIgnoreCase) ||
            report.Explanation.Contains("32,0 kHz", StringComparison.OrdinalIgnoreCase),
            $"Expected actual sample rate 32.0 kHz in explanation: {report.Explanation}");
    }

    [Fact]
    public void Forensic5_SoftBroadbandGenuine24_96_IsVerifiedLossless_NotHybridMaster()
    {
        // Reproduces genuine 24/96 soft broadband signal (~ -88 dBFS ultrasonic floor, continuous across 20-46 kHz,
        // zero notch between 20 and 22.5 kHz) that previously triggered false HybridMaster.
        string path = Path.Combine(_tempDir, "gen_soft_24_96.wav");
        const int sr = 96_000;
        int totalSamples = sr * 3;
        var rng = new Random(5051);

        var wf = new WaveFormat(sr, 24, 2);
        using (var writer = new WaveFileWriter(path, wf))
        {
            byte[] buf = new byte[3];
            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sr;
                // Modest audible content + continuous flat-ish genuine broadband ultrasonic tail (~ -87 dBFS per FFT bin)
                double audible = 0.08 * Math.Sin(2.0 * Math.PI * 1200.0 * t)
                               + 0.05 * Math.Sin(2.0 * Math.PI * 2800.0 * t)
                               + 0.002 * Math.Sin(2.0 * Math.PI * 19800.0 * t)
                               + 0.002 * Math.Sin(2.0 * Math.PI * 22500.0 * t)
                               + 0.002 * Math.Sin(2.0 * Math.PI * 35000.0 * t)
                               + 0.002 * Math.Sin(2.0 * Math.PI * 42000.0 * t);
                double hfNoise = (rng.NextDouble() * 2.0 - 1.0) * 0.0018;
                double val = Math.Clamp(audible + hfNoise, -0.95, 0.95);
                WritePcmSample(writer, val, 24, buf);
                WritePcmSample(writer, val, 24, buf);
            }
        }

        var report = NyquistSpectrumAnalyzer.Analyze(path);
        Assert.Equal(AuthenticityVerdict.VerifiedLossless, report.Verdict);
    }

    [Fact]
    public void Forensic5_SyntheticHybridMaster_FrequencyNotchAndUltrasonicRebound_FiresHybridMaster()
    {
        // Simultaneous hybrid DAW mix: strong 44.1 kHz band-limited stem (0.3-20.5 kHz) + dead notch at 22.5 kHz
        // + active 96 kHz studio overtones at 35-42 kHz.
        string path = Path.Combine(_tempDir, "hybrid_freq_notch_24_96.wav");
        const int sr = 96_000;
        int totalSamples = sr * 3;
        var rng = new Random(5052);

        double[] freqs = BuildHybridNotchFrequencies(out double[] phases, rng);
        double[] mono = new double[totalSamples];
        double maxAbs = 1e-12;
        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / sr;
            double sum = 0.0;
            for (int k = 0; k < freqs.Length; k++)
            {
                double w = freqs[k] > 30_000.0 ? 0.35 : 1.0;
                sum += w * Math.Sin(2.0 * Math.PI * freqs[k] * t + phases[k]);
            }
            mono[i] = sum;
            double a = Math.Abs(sum);
            if (a > maxAbs) maxAbs = a;
        }

        double scale = 0.65 / maxAbs;
        var wf = new WaveFormat(sr, 24, 2);
        using (var writer = new WaveFileWriter(path, wf))
        {
            byte[] buf = new byte[3];
            for (int i = 0; i < totalSamples; i++)
            {
                double val = mono[i] * scale;
                WritePcmSample(writer, val, 24, buf);
                WritePcmSample(writer, val, 24, buf);
            }
        }

        var report = NyquistSpectrumAnalyzer.Analyze(path);
        Assert.Equal(AuthenticityVerdict.HybridMaster, report.Verdict);
    }

    [Fact]
    public void Forensic5_SyntheticHybridMaster_TimeSplicedCdAndFullBand_FiresHybridMaster()
    {
        // Time-spliced hybrid master (12 s @ 24/96):
        // - First half (0..6 s): louder CD-band-limited section (0.3-20.5 kHz, brickwall above 21 kHz)
        // - Second half (6..12 s): full-band 96 kHz studio section (0.3-44 kHz)
        string path = Path.Combine(_tempDir, "hybrid_time_spliced_24_96.wav");
        const int sr = 96_000;
        int halfSamples = sr * 6;
        var rng = new Random(5053);

        var cdFreqs = new System.Collections.Generic.List<double>();
        for (double f = 400.0; f <= 20_600.0; f += 300.0) cdFreqs.Add(f);
        var fullFreqs = new System.Collections.Generic.List<double>();
        for (double f = 400.0; f <= 44_000.0; f += 400.0) fullFreqs.Add(f);

        double[] cdPhases = new double[cdFreqs.Count];
        for (int k = 0; k < cdPhases.Length; k++) cdPhases[k] = rng.NextDouble() * 2.0 * Math.PI;
        double[] fullPhases = new double[fullFreqs.Count];
        for (int k = 0; k < fullPhases.Length; k++) fullPhases[k] = rng.NextDouble() * 2.0 * Math.PI;

        var wf = new WaveFormat(sr, 24, 2);
        using (var writer = new WaveFileWriter(path, wf))
        {
            byte[] buf = new byte[3];
            double cdScale = 0.045;    // Louder RMS section (0..6s) -> picked as loudest section
            double fullScale = 0.025;  // Slightly quieter full-band section (6..12s) -> captured by P90 envelope

            for (int i = 0; i < halfSamples; i++)
            {
                double t = (double)i / sr;
                double sum = 0.0;
                for (int k = 0; k < cdFreqs.Count; k++)
                {
                    sum += Math.Sin(2.0 * Math.PI * cdFreqs[k] * t + cdPhases[k]);
                }
                double val = Math.Clamp(sum * cdScale / Math.Sqrt(cdFreqs.Count) * 2.5, -0.92, 0.92);
                WritePcmSample(writer, val, 24, buf);
                WritePcmSample(writer, val, 24, buf);
            }

            for (int i = 0; i < halfSamples; i++)
            {
                double t = (double)i / sr;
                double sum = 0.0;
                for (int k = 0; k < fullFreqs.Count; k++)
                {
                    sum += Math.Sin(2.0 * Math.PI * fullFreqs[k] * t + fullPhases[k]);
                }
                double val = Math.Clamp(sum * fullScale / Math.Sqrt(fullFreqs.Count) * 2.5, -0.92, 0.92);
                WritePcmSample(writer, val, 24, buf);
                WritePcmSample(writer, val, 24, buf);
            }
        }

        var report = NyquistSpectrumAnalyzer.Analyze(path);
        Assert.Equal(AuthenticityVerdict.HybridMaster, report.Verdict);
    }

    [Fact]
    public void Forensic7_2_Clean44_1kAntiAliasAt21_4k_IsVerifiedLossless_While20_1kBrickwallRemainsGuarded()
    {
        // 1. Clean 16/44.1 master with steep anti-aliasing filter at 21.4 kHz (97.1% of 22.05 kHz Nyquist) -> VerifiedLossless
        string cleanAntiAlias = WriteBroadbandSineBankWav(
            "gen_44100_antialias_21400.wav",
            sampleRate: 44_100,
            bitsPerSample: 16,
            durationSeconds: 3.0,
            maxFreqHz: 21_400.0,
            amplitudeScale: 0.65);

        var cleanReport = NyquistSpectrumAnalyzer.Analyze(cleanAntiAlias);
        Assert.Equal(AuthenticityVerdict.VerifiedLossless, cleanReport.Verdict);

        // 2. Lossy-like 20.1 kHz brickwall (91.2% of Nyquist, MP3 320k region) must NOT pass as VerifiedLossless
        string brickwall20100 = WriteBroadbandSineBankWav(
            "lossy_44100_brickwall_20100.wav",
            sampleRate: 44_100,
            bitsPerSample: 16,
            durationSeconds: 3.0,
            maxFreqHz: 20_100.0,
            amplitudeScale: 0.65);

        var brickwallReport = NyquistSpectrumAnalyzer.Analyze(brickwall20100);
        Assert.NotEqual(AuthenticityVerdict.VerifiedLossless, brickwallReport.Verdict);
    }

    [Fact]
    public void Forensic7_3_ShortClipWithBandLimitedSpokenIntroAndFullBandMusic_IsNotFalseFake()
    {
        // Reproduces IETF subset/01, 02, 04, 10: 7.0 s clip at 16/44.1 where 0..3.5 s (covering the old static 30% = 2.1 s mark)
        // is band-limited to 10.5 kHz (spoken intro), and 3.5..7.0 s is genuine full-band 21.6 kHz music.
        string path = Path.Combine(_tempDir, "short_clip_intro_plus_music.wav");
        const int sr = 44_100;
        int halfSamples = (int)(sr * 3.5);
        var rng = new Random(7301);

        var introFreqs = new System.Collections.Generic.List<double>();
        for (double f = 300.0; f <= 10_500.0; f += 200.0) introFreqs.Add(f);
        var musicFreqs = new System.Collections.Generic.List<double>();
        for (double f = 300.0; f <= 21_600.0; f += 200.0) musicFreqs.Add(f);

        double[] introPh = new double[introFreqs.Count];
        for (int k = 0; k < introPh.Length; k++) introPh[k] = rng.NextDouble() * 2.0 * Math.PI;
        double[] musicPh = new double[musicFreqs.Count];
        for (int k = 0; k < musicPh.Length; k++) musicPh[k] = rng.NextDouble() * 2.0 * Math.PI;

        var wf = new WaveFormat(sr, 16, 2);
        using (var writer = new WaveFileWriter(path, wf))
        {
            byte[] buf = new byte[2];
            double introScale = 0.22 / Math.Sqrt(introFreqs.Count);
            double musicScale = 0.22 / Math.Sqrt(musicFreqs.Count);

            for (int i = 0; i < halfSamples; i++)
            {
                double t = (double)i / sr;
                double sum = 0.0;
                for (int k = 0; k < introFreqs.Count; k++) sum += Math.Sin(2.0 * Math.PI * introFreqs[k] * t + introPh[k]);
                double v = Math.Clamp(sum * introScale, -0.95, 0.95);
                WritePcmSample(writer, v, 16, buf);
                WritePcmSample(writer, v, 16, buf);
            }

            for (int i = 0; i < halfSamples; i++)
            {
                double t = (double)i / sr;
                double sum = 0.0;
                for (int k = 0; k < musicFreqs.Count; k++) sum += Math.Sin(2.0 * Math.PI * musicFreqs[k] * t + musicPh[k]);
                double v = Math.Clamp(sum * musicScale, -0.95, 0.95);
                WritePcmSample(writer, v, 16, buf);
                WritePcmSample(writer, v, 16, buf);
            }
        }

        var report = NyquistSpectrumAnalyzer.Analyze(path);
        Assert.NotEqual(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
        Assert.Equal(AuthenticityVerdict.VerifiedLossless, report.Verdict);
    }

    [Fact]
    public void Forensic7_3_ShortClipMostlySpokenIntroWithBriefFullBandBurst_DowngradesToNotFalseFake()
    {
        // 7.0 s clip where 80% is loud 10.5 kHz band-limited speech and only 20% has quiet full-band 21.6 kHz energy:
        // P90 multi-segment envelope detects 21.6 kHz, preventing a false SuspectedUpscaledLossy accusation.
        string path = Path.Combine(_tempDir, "short_clip_mostly_intro_brief_burst.wav");
        const int sr = 44_100;
        int introSamples = (int)(sr * 5.6);
        int burstSamples = (int)(sr * 1.4);
        var rng = new Random(7302);

        var introFreqs = new System.Collections.Generic.List<double>();
        for (double f = 300.0; f <= 10_500.0; f += 200.0) introFreqs.Add(f);
        var musicFreqs = new System.Collections.Generic.List<double>();
        for (double f = 300.0; f <= 21_600.0; f += 200.0) musicFreqs.Add(f);

        double[] introPh = new double[introFreqs.Count];
        for (int k = 0; k < introPh.Length; k++) introPh[k] = rng.NextDouble() * 2.0 * Math.PI;
        double[] musicPh = new double[musicFreqs.Count];
        for (int k = 0; k < musicPh.Length; k++) musicPh[k] = rng.NextDouble() * 2.0 * Math.PI;

        var wf = new WaveFormat(sr, 16, 2);
        using (var writer = new WaveFileWriter(path, wf))
        {
            byte[] buf = new byte[2];
            double introScale = 0.28 / Math.Sqrt(introFreqs.Count);
            double musicScale = 0.08 / Math.Sqrt(musicFreqs.Count);

            for (int i = 0; i < introSamples; i++)
            {
                double t = (double)i / sr;
                double sum = 0.0;
                for (int k = 0; k < introFreqs.Count; k++) sum += Math.Sin(2.0 * Math.PI * introFreqs[k] * t + introPh[k]);
                double v = Math.Clamp(sum * introScale, -0.95, 0.95);
                WritePcmSample(writer, v, 16, buf);
                WritePcmSample(writer, v, 16, buf);
            }

            for (int i = 0; i < burstSamples; i++)
            {
                double t = (double)i / sr;
                double sum = 0.0;
                for (int k = 0; k < musicFreqs.Count; k++) sum += Math.Sin(2.0 * Math.PI * musicFreqs[k] * t + musicPh[k]);
                double v = Math.Clamp(sum * musicScale, -0.95, 0.95);
                WritePcmSample(writer, v, 16, buf);
                WritePcmSample(writer, v, 16, buf);
            }
        }

        var report = NyquistSpectrumAnalyzer.Analyze(path);
        Assert.NotEqual(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
    }

    [Fact]
    public void Forensic7_3_ShortClipConsistentLossyBrickwall_StillFlaggedAsLossy()
    {
        // A real 6.0 s MP3-like transcode brickwalled at 16.0 kHz throughout must still be caught cleanly.
        string path = WriteBroadbandSineBankWav(
            "short_clip_fake_16k.wav",
            sampleRate: 44_100,
            bitsPerSample: 16,
            durationSeconds: 6.0,
            maxFreqHz: 16_000.0,
            amplitudeScale: 0.65);

        var report = NyquistSpectrumAnalyzer.Analyze(path);
        Assert.Equal(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
    }

    [Fact]
    public void Forensic3_3_Mp3UpsampledTo24_96_PrioritizesLossyOverUpscaledFromCd()
    {
        // 24/96 container whose content is brickwalled at 16.5 kHz into dead digital silence (-160 dBFS)
        // must classify as SuspectedUpscaledLossy, NOT UpscaledFromCd.
        string path = WriteBroadbandSineBankWav(
            "mp3_128k_in_24_96.wav",
            sampleRate: 96_000,
            bitsPerSample: 24,
            durationSeconds: 3.0,
            maxFreqHz: 16_500.0,
            amplitudeScale: 0.65);

        var report = NyquistSpectrumAnalyzer.Analyze(path);
        Assert.Equal(AuthenticityVerdict.SuspectedUpscaledLossy, report.Verdict);
    }

    [Fact]
    public void Forensic3_1_Quiet16BitMp3BrickwallIntoQuantizationFloor_Detected_WhileSmoothQuiet16BitIsGenuine()
    {
        // 1. Quiet acoustic 16/44.1 signal (tapering smoothly from 1 kHz, then brickwalled at 16.0 kHz straight
        // into the 16-bit quantization/dither floor ~-135 dBFS, never reaching -152 dBFS) -> SuspectedUpscaledLossy.
        string fakeQuiet16 = Path.Combine(_tempDir, "quiet_16b_mp3_16k.wav");
        const int sr = 44_100;
        int totalSamples = sr * 3;
        var rng = new Random(3101);
        var freqs = new System.Collections.Generic.List<double>();
        for (double f = 400.0; f <= 16_000.0; f += 250.0) freqs.Add(f);
        double[] ph = new double[freqs.Count];
        for (int k = 0; k < ph.Length; k++) ph[k] = rng.NextDouble() * 2.0 * Math.PI;

        using (var writer = new WaveFileWriter(fakeQuiet16, new WaveFormat(sr, 16, 2)))
        {
            byte[] buf = new byte[2];
            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sr;
                double sum = 0.0;
                for (int k = 0; k < freqs.Count; k++)
                {
                    double f = freqs[k];
                    double atten = f > 2_000.0 ? Math.Pow(2_000.0 / f, 1.8) : 1.0;
                    sum += atten * Math.Sin(2.0 * Math.PI * f * t + ph[k]);
                }
                double dither = (rng.NextDouble() - rng.NextDouble()) * (0.5 / 32_768.0);
                double v = Math.Clamp(sum * 0.03 + dither, -0.95, 0.95);
                WritePcmSample(writer, v, 16, buf);
                WritePcmSample(writer, v, 16, buf);
            }
        }

        var fakeReport = NyquistSpectrumAnalyzer.Analyze(fakeQuiet16);
        Assert.Equal(AuthenticityVerdict.SuspectedUpscaledLossy, fakeReport.Verdict);

        // 2. Negative control: equally quiet 16/44.1 acoustic track with smooth natural roll-off (no brickwall) -> NOT false-fake
        string genQuiet16 = Path.Combine(_tempDir, "quiet_16b_smooth_genuine.wav");
        var genFreqs = new System.Collections.Generic.List<double>();
        for (double f = 400.0; f <= 21_500.0; f += 250.0) genFreqs.Add(f);
        double[] genPh = new double[genFreqs.Count];
        for (int k = 0; k < genPh.Length; k++) genPh[k] = rng.NextDouble() * 2.0 * Math.PI;

        using (var writer = new WaveFileWriter(genQuiet16, new WaveFormat(sr, 16, 2)))
        {
            byte[] buf = new byte[2];
            for (int i = 0; i < totalSamples; i++)
            {
                double t = (double)i / sr;
                double sum = 0.0;
                for (int k = 0; k < genFreqs.Count; k++)
                {
                    double f = genFreqs[k];
                    double atten = f > 2_000.0 ? Math.Pow(2_000.0 / f, 2.2) : 1.0;
                    sum += atten * Math.Sin(2.0 * Math.PI * f * t + genPh[k]);
                }
                double dither = (rng.NextDouble() - rng.NextDouble()) * (0.5 / 32_768.0);
                double v = Math.Clamp(sum * 0.03 + dither, -0.95, 0.95);
                WritePcmSample(writer, v, 16, buf);
                WritePcmSample(writer, v, 16, buf);
            }
        }

        var genReport = NyquistSpectrumAnalyzer.Analyze(genQuiet16);
        Assert.NotEqual(AuthenticityVerdict.SuspectedUpscaledLossy, genReport.Verdict);
    }

    private static double[] BuildHybridNotchFrequencies(out double[] phases, Random rng)
    {
        var list = new System.Collections.Generic.List<double>();
        for (double f = 400.0; f <= 20_600.0; f += 300.0) list.Add(f);
        // Notch in 20.6..33.0 kHz (empty CD stopband)
        for (double f = 34_000.0; f <= 42_500.0; f += 350.0) list.Add(f);
        phases = new double[list.Count];
        for (int i = 0; i < phases.Length; i++) phases[i] = rng.NextDouble() * 2.0 * Math.PI;
        return list.ToArray();
    }

    private string WriteBroadbandSineBankWav(
        string fileName,
        int sampleRate,
        int bitsPerSample,
        double durationSeconds,
        double maxFreqHz,
        double amplitudeScale)
    {
        string path = Path.Combine(_tempDir, fileName);
        int totalSamples = (int)(sampleRate * durationSeconds);
        int channels = 2;

        // Dense deterministic harmonic bank spaced every 150 Hz up to maxFreqHz
        int numTones = Math.Max(1, (int)((maxFreqHz - 300.0) / 150.0) + 1);
        double[] freqs = new double[numTones];
        double[] phases = new double[numTones];
        var rng = new Random(sampleRate ^ (int)maxFreqHz);
        for (int k = 0; k < numTones; k++)
        {
            freqs[k] = 300.0 + k * 150.0;
            phases[k] = rng.NextDouble() * 2.0 * Math.PI;
        }
        double[] mono = new double[totalSamples];
        double maxAbs = 1e-12;
        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / sampleRate;
            double sum = 0.0;
            for (int k = 0; k < numTones; k++)
            {
                sum += Math.Sin(2.0 * Math.PI * freqs[k] * t + phases[k]);
            }
            mono[i] = sum;
            double a = Math.Abs(sum);
            if (a > maxAbs) maxAbs = a;
        }

        double scale = Math.Min(0.95, amplitudeScale) / maxAbs;
        var wf = new WaveFormat(sampleRate, bitsPerSample, channels);
        using var writer = new WaveFileWriter(path, wf);
        byte[] sampleBuf = new byte[bitsPerSample / 8];

        for (int i = 0; i < totalSamples; i++)
        {
            double val = mono[i] * scale;
            for (int ch = 0; ch < channels; ch++)
            {
                WritePcmSample(writer, val, bitsPerSample, sampleBuf);
            }
        }

        return path;
    }

    private static void WritePcmSample(WaveFileWriter writer, double normSample, int bitsPerSample, byte[] buf)
    {
        if (bitsPerSample == 16)
        {
            short s = (short)Math.Round(normSample * 32_767.0);
            buf[0] = (byte)(s & 0xFF);
            buf[1] = (byte)((s >> 8) & 0xFF);
            writer.Write(buf, 0, 2);
        }
        else if (bitsPerSample == 24)
        {
            int s = (int)Math.Round(normSample * 8_388_607.0);
            buf[0] = (byte)(s & 0xFF);
            buf[1] = (byte)((s >> 8) & 0xFF);
            buf[2] = (byte)((s >> 16) & 0xFF);
            writer.Write(buf, 0, 3);
        }
    }
}
