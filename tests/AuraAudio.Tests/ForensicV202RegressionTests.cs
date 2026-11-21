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
