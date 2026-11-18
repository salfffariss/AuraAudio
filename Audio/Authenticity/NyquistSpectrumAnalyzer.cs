using System;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio.Authenticity;

/// <summary>
/// Mesin Penganalisis Keaslian Audio Nyquist FFT Forensik (Forensic Audiophile Spectrum Analyzer).
/// Melakukan audit ilmiah terhadap kandungan frekuensi ultrasonik, kemiringan spektrum (spectral slope),
/// derau kuantisasi (quantization noise floor dither shelf), dan entropi kedalaman bit LSB
/// untuk mendeteksi:
/// 1. True Studio Master murni (>40 kHz ultrasonik aktif)
/// 2. Natural Acoustic Master (vokal/akustik/piano rolloff alami tanpa brickwall filter)
/// 3. Hybrid Production Master (sample library 44.1k berdampingan dengan overtones >35 kHz)
/// 4. Upscaled dari CD 44.1k (brickwall filter terjal >36 dB/oktaf di 22.05 kHz)
/// 5. Dithered 16-to-24-bit Upscale (terdeteksi dari noise shelf rata di ~-130.7 dBFS per-bin, sigma < 2.2 dB;
///    derivasi: varians TPDF 0.25*LSB^2, normalisasi FFT Hann 4096-bin offset -34.36 dB; -96.33 + -34.36 = -130.7)
/// 6. Fake Hi-Res dari MP3 (terpotong tajam di 16-20 kHz).
/// </summary>
public static class NyquistSpectrumAnalyzer
{
    private const int FftSize = 4096;
    private const int DefaultWindowsToAverage = 32;
    private const int BitDepthSampleSize = 300_000; // ~3.1 detik audio pada 96 kHz

    // Window Hann precomputed untuk efisiensi instan tanpa alokasi memori berulang
    private static readonly double[] HannWindow = new double[FftSize];

    static NyquistSpectrumAnalyzer()
    {
        for (int i = 0; i < FftSize; i++)
        {
            HannWindow[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
        }
    }

    /// <summary>
    /// Menganalisis berkas audio secara asinkron di latar belakang tanpa menghambat alur audio utama.
    /// </summary>
    public static Task<AudioIntegrityReport> AnalyzeAsync(
        string filePath,
        AudioStreamSpec? hardwareSpec = null,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeAsync(filePath, hardwareSpec, hasActiveHardwarePath: false, isHardwareExclusive: true, configuredDriverType: AudioDriverType.WASAPI_Exclusive, cancellationToken);
    }

    public static Task<AudioIntegrityReport> AnalyzeAsync(
        string filePath,
        AudioStreamSpec? hardwareSpec,
        bool hasActiveHardwarePath,
        bool isHardwareExclusive,
        AudioDriverType configuredDriverType,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Analyze(filePath, hardwareSpec, hasActiveHardwarePath, isHardwareExclusive, configuredDriverType, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Menganalisis integritas fisik berkas audio secara mendalam menggunakan algoritma forensik DSP.
    /// </summary>
    public static AudioIntegrityReport Analyze(
        string filePath,
        AudioStreamSpec? hardwareSpec = null,
        CancellationToken cancellationToken = default)
    {
        return Analyze(filePath, hardwareSpec, hasActiveHardwarePath: false, isHardwareExclusive: true, configuredDriverType: AudioDriverType.WASAPI_Exclusive, cancellationToken);
    }

    public static AudioIntegrityReport Analyze(
        string filePath,
        AudioStreamSpec? hardwareSpec,
        bool hasActiveHardwarePath,
        bool isHardwareExclusive,
        AudioDriverType configuredDriverType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Berkas audio untuk analisis integritas tidak ditemukan.", filePath);
        }

        using var stream = AudioDecoderFactory.OpenStream(filePath);
        return Analyze(stream, hardwareSpec, hasActiveHardwarePath, isHardwareExclusive, configuredDriverType, cancellationToken);
    }

    /// <summary>
    /// Menganalisis integritas fisik aliran audio secara mendalam menggunakan algoritma forensik DSP.
    /// </summary>
    public static AudioIntegrityReport Analyze(
        IAudioStream stream,
        AudioStreamSpec? hardwareSpec = null,
        CancellationToken cancellationToken = default)
    {
        return Analyze(stream, hardwareSpec, hasActiveHardwarePath: false, isHardwareExclusive: true, configuredDriverType: AudioDriverType.WASAPI_Exclusive, cancellationToken);
    }

    public static AudioIntegrityReport Analyze(
        IAudioStream stream,
        AudioStreamSpec? hardwareSpec,
        bool hasActiveHardwarePath,
        bool isHardwareExclusive,
        AudioDriverType configuredDriverType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var sourceSpec = stream.Spec;
        var hwSpec = hardwareSpec ?? sourceSpec;

        // 1. Kasus Khusus: DSD (1-bit Delta-Sigma, spektrum PCM tak terukur langsung tanpa dekimasi multi-tahap)
        if (sourceSpec.IsDsd)
        {
            double[] dsdBins = GenerateDsdSpectrumBins(FftSize / 2, sourceSpec.SampleRateHz);
            return new AudioIntegrityReport(
                sourceSpec,
                hwSpec,
                AuthenticityVerdict.Inconclusive,
                sourceSpec.SampleRateHz / 2,
                "Asal-usul DSD tidak dapat diverifikasi dari aliran 1-bit tanpa dekimasi.",
                isTrue24Bit: false,
                lsbActivityPercentage: 0.0,
                spectrumDbBins: dsdBins,
                spectralSlopeDbPerOctave: null,
                ultrasonicNoiseFloorDbFS: null,
                isDitherDetected: false,
                lsbEntropy: 0.0,
                analysisTimestamp: TimeSpan.Zero,
                analysisPositionPercentage: 50.0,
                powerAt22kDbFS: null,
                powerAtNyquistDbFS: null,
                ultrasonicNoiseStdDevDb: null,
                isZeroPadded: false,
                isSyntheticSpectrum: true,
                kneeHz: null,
                kneeSteepnessDbPerOct: null,
                dropAcrossKneeDb: null,
                relP16kDb: null,
                relP20kDb: null,
                hasActiveHardwarePath: hasActiveHardwarePath,
                isHardwareExclusive: isHardwareExclusive,
                configuredDriverType: configuredDriverType,
                upperEnvelopeReachHz: null
            );
        }

        // 2. Kasus Khusus: Format Lossy Standar (MP3, AAC, OGG, OPUS)
        if (sourceSpec.Codec is AudioCodec.MP3 or AudioCodec.AAC or AudioCodec.OGG_VORBIS or AudioCodec.OPUS)
        {
            double lossySeekSeconds = Math.Min(45.0, stream.TotalTime.TotalSeconds * 0.40);
            try { stream.CurrentTime = TimeSpan.FromSeconds(lossySeekSeconds); } catch { }

            double[] avgPowerLossy = ComputeAveragePowerSpectrum(stream, FftSize, DefaultWindowsToAverage, cancellationToken);
            var (envPowerLossy, upperReachLossy) = ComputeMultiSegmentUpperEnvelope(stream, avgPowerLossy, FftSize, (double)stream.WaveFormat.SampleRate / FftSize, (uint)Math.Max(1, stream.WaveFormat.SampleRate / 2), cancellationToken);
            double[] dbBinsLossy = new double[avgPowerLossy.Length];
            for (int i = 0; i < avgPowerLossy.Length; i++)
            {
                dbBinsLossy[i] = 10.0 * Math.Log10(Math.Max(1e-16, avgPowerLossy[i]));
            }
            double binResLossy = (double)stream.WaveFormat.SampleRate / FftSize;
            uint lossyNyquist = (uint)Math.Max(1, stream.WaveFormat.SampleRate / 2);

            double pRefLossy = GetBandPowerDb(2_500, 1_500, avgPowerLossy, binResLossy);
            uint measuredCutoff = DetectKneeCutoffHz(avgPowerLossy, binResLossy, pRefLossy, lossyNyquist, 48.0);
            double slopeLossy = (stream.WaveFormat.SampleRate <= 44_100)
                ? ComputeSpectralSlope(19_000, 22_000, avgPowerLossy, binResLossy)
                : ComputeSpectralSlope(20_000, 22_050, avgPowerLossy, binResLossy);
            var (noiseFloorLossy, sigmaLossy, isDitherLossy) = EvaluateQuantizationNoiseFloor(avgPowerLossy, binResLossy, lossyNyquist);
            double p22kLossy = GetBandPowerDb(22_050, 100, avgPowerLossy, binResLossy);
            double pNyqLossy = GetBandPowerDb(Math.Max(1000, lossyNyquist - binResLossy), 200, avgPowerLossy, binResLossy);
            double kneeSteepnessLossy = ComputeKneeSteepness(avgPowerLossy, binResLossy, measuredCutoff);
            double dropAcrossKneeLossy = ComputeDropAcrossKnee(avgPowerLossy, binResLossy, measuredCutoff);
            double p16kLossy = GetPowerAtFreq(16_000, avgPowerLossy, binResLossy);
            double p20kLossy = GetPowerAtFreq(20_000, avgPowerLossy, binResLossy);
            double relP16kLossy = p16kLossy - pRefLossy;
            double relP20kLossy = p20kLossy - pRefLossy;

            return new AudioIntegrityReport(
                sourceSpec,
                hwSpec,
                AuthenticityVerdict.StandardLossy,
                measuredCutoff,
                $"Format terkompresi lossy standar ({sourceSpec.Codec}). Pemotongan frekuensi di ~{measuredCutoff / 1000.0:F1} kHz adalah karakteristik bawaan algoritma kompresi psikoakustik.",
                isTrue24Bit: false,
                lsbActivityPercentage: 0.0,
                spectrumDbBins: dbBinsLossy,
                spectralSlopeDbPerOctave: slopeLossy,
                ultrasonicNoiseFloorDbFS: noiseFloorLossy,
                isDitherDetected: isDitherLossy,
                lsbEntropy: 0.0,
                analysisTimestamp: TimeSpan.FromSeconds(lossySeekSeconds),
                analysisPositionPercentage: 40.0,
                powerAt22kDbFS: Math.Round(p22kLossy, 1),
                powerAtNyquistDbFS: Math.Round(pNyqLossy, 1),
                ultrasonicNoiseStdDevDb: sigmaLossy,
                isZeroPadded: false,
                isSyntheticSpectrum: false,
                kneeHz: measuredCutoff,
                kneeSteepnessDbPerOct: Math.Round(kneeSteepnessLossy, 1),
                dropAcrossKneeDb: Math.Round(dropAcrossKneeLossy, 1),
                relP16kDb: Math.Round(relP16kLossy, 1),
                relP20kDb: Math.Round(relP20kLossy, 1),
                hasActiveHardwarePath: hasActiveHardwarePath,
                isHardwareExclusive: isHardwareExclusive,
                configuredDriverType: configuredDriverType,
                upperEnvelopeReachHz: upperReachLossy
            );
        }

        uint sampleRate = (uint)Math.Max(1, stream.WaveFormat.SampleRate);
        uint nyquistLimitHz = sampleRate / 2;
        int blockAlign = Math.Max(1, stream.WaveFormat.BlockAlign);
        int bitsPerSample = stream.WaveFormat.BitsPerSample;
        int channels = Math.Max(1, stream.WaveFormat.Channels);

        // 3. Multi-Window Energy Scan: Cari bagian lagu paling dinamis/lantang (RMS tertinggi)
        var (loudestSectionTime, loudestFraction) = FindLoudestSectionTime(stream, cancellationToken);
        try
        {
            stream.CurrentTime = loudestSectionTime;
        }
        catch
        {
            // Fallback aman jika stream tidak mendukung seek
        }

        // 4. Audit Integritas Bit-Depth Forensik (300.000 sampel mentah + Shannon Entropy untuk 16-bit & 24-bit)
        var (isTrue24Bit, lsbPercentage, lsbEntropy, isZeroPadded, lsbOffset) = EvaluateBitDepthForensic(
            stream, BitDepthSampleSize, blockAlign, bitsPerSample, channels);

        // Kembalikan posisi stream ke titik analisis FFT
        try
        {
            stream.CurrentTime = loudestSectionTime;
        }
        catch
        {
        }

        // 5. Uji Spektrum Frekuensi FFT Multi-Window + Amplop Persentil Lintas Lagu
        double[] avgPower = ComputeAveragePowerSpectrum(stream, FftSize, DefaultWindowsToAverage, cancellationToken, lsbOffset);
        double binResolution = (double)sampleRate / FftSize;
        var (envPower, upperEnvelopeReachHz) = ComputeMultiSegmentUpperEnvelope(stream, avgPower, FftSize, binResolution, nyquistLimitHz, cancellationToken, lsbOffset);

        double[] dbBins = new double[avgPower.Length];
        for (int i = 0; i < avgPower.Length; i++)
        {
            dbBins[i] = 10.0 * Math.Log10(Math.Max(1e-16, avgPower[i]));
        }

        // 6. Pengukuran Metrik Forensik Presisi
        double pRef = GetBandPowerDb(2_500, 1_500, avgPower, binResolution);
        uint detectedKneeHz = DetectKneeCutoffHz(avgPower, binResolution, pRef, nyquistLimitHz, 48.0);
        double kneeSteepness = ComputeKneeSteepness(avgPower, binResolution, detectedKneeHz);
        double dropAcrossKnee = ComputeDropAcrossKnee(avgPower, binResolution, detectedKneeHz);

        var (imagingCorrelation, imagingAttenuationDb, imagingUpperMeanDb, isImagingDetected) = ComputeImagingCorrelation(avgPower, binResolution, sampleRate);
        var (cliffHz, cliffDropDb) = ComputeCliff(avgPower, binResolution, nyquistLimitHz);
        var (_, midTrebleCliffDrop) = ComputeMaxMidTrebleCliff(avgPower, envPower, binResolution, nyquistLimitHz);
        double trebleDecaySlope = ComputeTrebleDecaySlope(envPower, binResolution, detectedKneeHz);

        double slopeCdTransition = (sampleRate <= 44_100)
            ? ComputeSpectralSlope(19_000, 22_000, avgPower, binResolution)
            : ComputeSpectralSlope(20_000, 22_050, avgPower, binResolution);

        var (noiseFloorDbFS, noiseStdDevDb, isDitherShelfDetected) = EvaluateQuantizationNoiseFloor(avgPower, binResolution, nyquistLimitHz);

        double p16k     = GetPowerAtFreq(16_000, avgPower, binResolution);
        double p20k     = GetPowerAtFreq(20_000, avgPower, binResolution);
        double p20kEnv  = GetBandPowerDb(20_500, 300, envPower, binResolution);
        double p22k     = GetBandPowerDb(22_050, 100, avgPower, binResolution);
        double p22_5k   = GetBandPowerDb(22_500, 100, avgPower, binResolution);
        double p23_5k   = GetBandPowerDb(23_500, 200, avgPower, binResolution);
        double p24k     = GetBandPowerDb(24_000, 100, avgPower, binResolution);
        double p35k     = GetPowerAtFreq(35_000, avgPower, binResolution);
        double p40k     = GetPowerAtFreq(40_000, avgPower, binResolution);
        double pNyquist = GetBandPowerDb(Math.Max(1000, nyquistLimitHz - binResolution), 200, avgPower, binResolution);

        double relP16k = p16k - pRef;
        double relP20k = p20k - pRef;

        // Ciri peluruhan alami instrumen akustik/piano tanpa patahan filter buatan (brickwall)
        // serta memastikan energi harmonik masih melampaui 9 kHz (bukan master gelap ekstrem -50 dB/okt)
        double lowTrebleSlope = ComputeSpectralSlope(2_000.0, 4_000.0, avgPower, binResolution);

        bool isSmoothAcousticRollOff =
            upperEnvelopeReachHz >= 9_000 &&
            dropAcrossKnee < 12.5 &&
            midTrebleCliffDrop < 13.5 &&
            kneeSteepness > -44.0 &&
            trebleDecaySlope >= -42.0 &&
            lowTrebleSlope >= -38.0;

        uint detectedCutoffHz;
        AuthenticityVerdict verdict;
        string explanation;

        // 7. Pohon Keputusan Forensik (Decision Tree)
        if (sampleRate >= 88_200) // Format Hi-Res (88.2 kHz, 96 kHz, 176.4 kHz, 192 kHz)
        {
            if (isDitherShelfDetected && bitsPerSample >= 24 && p22_5k < -122.0)
            {
                verdict = AuthenticityVerdict.Dithered16BitUpscale;
                detectedCutoffHz = 22_050;
                explanation = $"Wadah berlabel {sourceSpec.FormattedBadge}, namun spektrum ultrasonik menunjukkan lantai derau dither 16-bit rata ({noiseFloorDbFS:F1} dBFS, σ={noiseStdDevDb:F1} dB). " +
                              "Berkas ini hasil ekspor dari master 16-bit dengan TPDF dithering.";
            }
            else if (isImagingDetected)
            {
                verdict = AuthenticityVerdict.Inconclusive;
                detectedCutoffHz = 22_050;
                explanation = $"Perlu cek manual: terdeteksi korelasi cermin pencitraan tinggi (r={imagingCorrelation:F2}, atenuasi: {imagingAttenuationDb:F1} dB, level: {imagingUpperMeanDb:F1} dBFS) di sekitar 22.05 kHz. " +
                              "Energi ultrasonik terindikasi sebagai artefak resampler non-ideal dari sumber 44.1 kHz.";
            }
            else if (((detectedKneeHz >= 21_000 && detectedKneeHz <= 22_600 && slopeCdTransition <= -36.0 && (p22_5k < p20k - 12.0 || p22_5k < -85.0)) ||
                      (cliffHz >= 21_500 && cliffHz <= 22_700 && cliffDropDb >= 8.0 && slopeCdTransition <= -36.0)))
            {
                verdict = AuthenticityVerdict.UpscaledFromCd;
                detectedCutoffHz = 22_050;
                explanation = $"Wadah berlabel {sourceSpec.FormattedBadge}, namun energi frekuensi berhenti di batas CD 22.05 kHz (kemiringan {slopeCdTransition:F1} dB/oktaf, level @ 22.05k: {p22k:F1} dBFS). " +
                              "Berkas ini berasal dari master CD 44.1 kHz yang di-upsample.";
            }
            else if (((detectedKneeHz <= 17_500) || (relP16k < -45.0) || ((relP20k < -50.0) && relP16k > -42.0)) && kneeSteepness <= -60.0 && dropAcrossKnee >= 20.0)
            {
                verdict = AuthenticityVerdict.SuspectedUpscaledLossy;
                detectedCutoffHz = detectedKneeHz;
                explanation = $"Lossless palsu terdeteksi: wadah {sourceSpec.FormattedBadge}, namun frekuensi terpotong tajam di ~{detectedCutoffHz / 1000.0:F1} kHz " +
                              $"(kemiringan {kneeSteepness:F1} dB/oktaf, penurunan {dropAcrossKnee:F1} dB). Kemungkinan hasil konversi dari lossy (MP3/AAC) atau sumber ber-bandwidth terbatas.";
            }
            else if (bitsPerSample >= 24 && isZeroPadded)
            {
                verdict = AuthenticityVerdict.ZeroPadded24Bit;
                detectedCutoffHz = Math.Min(nyquistLimitHz, Math.Max(detectedKneeHz, upperEnvelopeReachHz));
                explanation = $"Wadah 24-bit palsu (zero-padding): 8 bit terbawah kosong (aktivitas {lsbPercentage:F1}%, entropi {lsbEntropy:F2}/8 bit). Resolusi sinyal aslinya hanya 16-bit.";
            }
            else if (detectedKneeHz <= 21_000 && isSmoothAcousticRollOff)
            {
                verdict = AuthenticityVerdict.AcousticNaturalRollOff;
                detectedCutoffHz = nyquistLimitHz;
                explanation = $"Rekaman asli berkarakter lembut: harmonik melandai halus tanpa potongan filter buatan (jangkauan puncak ~{upperEnvelopeReachHz / 1000.0:F1} kHz, kemiringan treble {trebleDecaySlope:F1} dB/oktaf).";
            }
            else if (detectedKneeHz <= 21_000)
            {
                verdict = AuthenticityVerdict.Inconclusive;
                detectedCutoffHz = detectedKneeHz;
                explanation = $"Perlu cek manual: energi turun di ~{detectedKneeHz / 1000.0:F1} kHz (kemiringan {kneeSteepness:F1} dB/oktaf, penurunan {dropAcrossKnee:F1} dB). Belum cukup bukti sebagai potongan MP3 maupun master penuh.";
            }
            else if ((p22_5k < -85.0 || slopeCdTransition <= -36.0) && (p35k > -90.0 || p40k > -95.0) && noiseStdDevDb >= 2.5)
            {
                verdict = AuthenticityVerdict.HybridMaster;
                detectedCutoffHz = (uint)Math.Min(nyquistLimitHz, 44_000);
                explanation = "Hi-Res asli produksi studio campuran: memadukan instrumen sample 44.1 kHz dengan overtone studio aktif di atas 35 kHz.";
            }
            else if (detectedKneeHz > 21_000 && (slopeCdTransition >= -30.0 || (detectedKneeHz > 24_000 && dropAcrossKnee < 10.0 && kneeSteepness <= 0.0)) && p24k < -80.0 && p35k <= -100.0)
            {
                verdict = AuthenticityVerdict.AcousticNaturalRollOff;
                detectedCutoffHz = (uint)Math.Min(nyquistLimitHz, Math.Max(24_000u, detectedKneeHz));
                explanation = $"Lossless asli berkarakter akustik lembut: energi aktif hingga ~{detectedKneeHz / 1000.0:F1} kHz lalu melandai alami (kemiringan {slopeCdTransition:F1} dB/oktaf).";
            }
            else if (detectedKneeHz > 21_000 && (slopeCdTransition >= -30.0 || (detectedKneeHz > 24_000 && dropAcrossKnee < 10.0 && kneeSteepness <= 0.0)) && (p35k > -105.0 || p40k > -108.0 || p24k >= -80.0))
            {
                verdict = AuthenticityVerdict.VerifiedLossless;
                detectedCutoffHz = (uint)Math.Min(nyquistLimitHz, Math.Max(35_000u, detectedKneeHz));
                explanation = $"Hi-Res Lossless asli: tidak ditemukan potongan MP3 atau batas CD; energi aktif alami hingga ~{detectedCutoffHz / 1000.0:F1} kHz (kemiringan {slopeCdTransition:F1} dB/oktaf). Catatan: AAC/Opus bitrate tinggi tanpa lowpass tidak dapat dibedakan lewat spektrum saja.";
            }
            else
            {
                verdict = AuthenticityVerdict.Inconclusive;
                detectedCutoffHz = detectedKneeHz;
                explanation = $"Perlu cek manual: spektrum berada di zona transisi ambigu (kemiringan {slopeCdTransition:F1} dB/oktaf, titik belok ~{detectedCutoffHz / 1000.0:F1} kHz).";
            }
        }
        else if (sampleRate == 48_000)
        {
            // Pada 48 kHz master asli, filter anti-aliasing studio mulai melandai di ~21.6 kHz menuju Nyquist 24.0 kHz.
            // Upsample CD sejati runtuh ke lantai derau di atas 22.5 kHz atau punya tebing curam di batas CD 22.05 kHz.
            bool hasLiveUltrasonicTail48k =
                p22_5k > -102.0 &&
                p23_5k > -108.0 &&
                (p22_5k - p23_5k) <= 10.0 &&
                cliffDropDb < 16.0;

            bool isCdBrickwall48k = !hasLiveUltrasonicTail48k && (
                (cliffHz >= 21_500 && cliffHz <= 22_700 && cliffDropDb >= 8.0 && slopeCdTransition <= -36.0) ||
                (detectedKneeHz >= 21_000 && detectedKneeHz <= 22_600 && slopeCdTransition <= -36.0 &&
                 (p22_5k < -108.0 || p23_5k < -118.0 || (p20k - p22_5k >= 25.0))));

            if (isDitherShelfDetected && bitsPerSample >= 24 && p22k < -122.0)
            {
                verdict = AuthenticityVerdict.Dithered16BitUpscale;
                detectedCutoffHz = 22_050;
                explanation = $"Wadah 48.0 kHz 24-bit, namun pita 22-24 kHz hanya berisi lantai derau dither 16-bit rata ({noiseFloorDbFS:F1} dBFS, σ={noiseStdDevDb:F1} dB).";
            }
            else if (isImagingDetected)
            {
                verdict = AuthenticityVerdict.Inconclusive;
                detectedCutoffHz = 22_050;
                explanation = $"Perlu cek manual: terdeteksi korelasi cermin pencitraan (r={imagingCorrelation:F2}, atenuasi: {imagingAttenuationDb:F1} dB, level: {imagingUpperMeanDb:F1} dBFS) di 22-24 kHz khas resampler 44.1 ke 48 kHz.";
            }
            else if (isCdBrickwall48k)
            {
                verdict = AuthenticityVerdict.UpscaledFromCd;
                detectedCutoffHz = 22_050;
                explanation = $"Wadah 48.0 kHz FLAC, namun spektrum terpotong curam di batas CD 22.05 kHz (kemiringan {slopeCdTransition:F1} dB/oktaf, level @ 22.05k: {p22k:F1} dBFS). Berasal dari master CD 44.1 kHz yang di-upsample.";
            }
            else if (((detectedKneeHz <= 17_500) || (relP16k < -45.0) || ((relP20k < -50.0) && relP16k > -40.0)) && kneeSteepness <= -60.0 && dropAcrossKnee >= 20.0)
            {
                verdict = AuthenticityVerdict.SuspectedUpscaledLossy;
                detectedCutoffHz = detectedKneeHz;
                explanation = $"Lossless palsu terdeteksi: energi terpotong tajam di ~{detectedCutoffHz / 1000.0:F1} kHz (kemiringan {kneeSteepness:F1} dB/oktaf, penurunan {dropAcrossKnee:F1} dB). Kemungkinan hasil konversi dari lossy (MP3/AAC) atau sumber ber-bandwidth terbatas.";
            }
            else if (bitsPerSample >= 24 && isZeroPadded)
            {
                verdict = AuthenticityVerdict.ZeroPadded24Bit;
                detectedCutoffHz = 24_000;
                explanation = $"Wadah 24-bit palsu (zero-padding): 8 bit terbawah kosong (aktivitas {lsbPercentage:F1}%, entropi {lsbEntropy:F2}/8 bit). Resolusi sinyal aslinya hanya 16-bit.";
            }
            else if (detectedKneeHz <= 21_000 && isSmoothAcousticRollOff)
            {
                verdict = AuthenticityVerdict.AcousticNaturalRollOff;
                detectedCutoffHz = 24_000;
                explanation = $"Lossless asli berkarakter akustik lembut: harmonik melandai halus tanpa potongan filter buatan (jangkauan puncak ~{upperEnvelopeReachHz / 1000.0:F1} kHz, kemiringan treble {trebleDecaySlope:F1} dB/oktaf).";
            }
            else if (detectedKneeHz <= 21_000)
            {
                verdict = AuthenticityVerdict.Inconclusive;
                detectedCutoffHz = detectedKneeHz;
                explanation = $"Perlu cek manual: spektrum 48 kHz turun di ~{detectedKneeHz / 1000.0:F1} kHz (kemiringan {kneeSteepness:F1} dB/oktaf, penurunan {dropAcrossKnee:F1} dB).";
            }
            else if (detectedKneeHz > 21_000 && (slopeCdTransition >= -30.0 || p23_5k > -115.0) && p24k < -80.0)
            {
                verdict = AuthenticityVerdict.AcousticNaturalRollOff;
                detectedCutoffHz = 24_000;
                explanation = $"Lossless asli berkarakter akustik lembut: energi aktif melewati batas CD hingga ~{nyquistLimitHz / 1000.0:F1} kHz dan melandai alami menuju Nyquist.";
            }
            else if (detectedKneeHz > 21_000 && (slopeCdTransition >= -30.0 || p23_5k > -115.0) && p24k >= -80.0)
            {
                verdict = AuthenticityVerdict.VerifiedLossless;
                detectedCutoffHz = 24_000;
                explanation = $"Lossless asli 48.0 kHz: tidak ditemukan artefak lossy atau upsampling CD; energi aktif penuh hingga batas 24.0 kHz.";
            }
            else
            {
                verdict = AuthenticityVerdict.Inconclusive;
                detectedCutoffHz = detectedKneeHz;
                explanation = $"Perlu cek manual: spektrum 48 kHz berada di zona transisi ambigu (kemiringan {slopeCdTransition:F1} dB/oktaf, p24k: {p24k:F1} dBFS).";
            }
        }
        else // Standar 44.1 kHz (mendukung baik 16-bit maupun 24-bit / 44.1 kHz!)
        {
            bool relLossyPattern44k = (relP16k < -45.0) || ((relP20k < -50.0) && relP16k > -35.0);
            bool isMp3Cutoff44k = ((detectedKneeHz <= 17_500) || relLossyPattern44k) && kneeSteepness <= -60.0 && dropAcrossKnee >= 20.0;

            if (isMp3Cutoff44k)
            {
                verdict = AuthenticityVerdict.SuspectedUpscaledLossy;
                detectedCutoffHz = detectedKneeHz;
                explanation = $"Lossless palsu terdeteksi: berkas 44.1 kHz terpotong tajam di ~{detectedCutoffHz / 1000.0:F1} kHz (kemiringan {kneeSteepness:F1} dB/oktaf, penurunan {dropAcrossKnee:F1} dB). " +
                              "Kemungkinan hasil konversi dari lossy (MP3/AAC), atau sumber dengan bandwidth terbatas.";
            }
            else if (bitsPerSample >= 24 && isZeroPadded)
            {
                verdict = AuthenticityVerdict.ZeroPadded24Bit;
                detectedCutoffHz = 22_050;
                explanation = $"Wadah 24-bit palsu (zero-padding): berkas 24-bit / 44.1 kHz ini memiliki 8 bit terbawah bernilai nol (aktivitas {lsbPercentage:F1}%, entropi {lsbEntropy:F2}/8 bit), setara 16-bit.";
            }
            else if (bitsPerSample >= 24 && isDitherShelfDetected && p20k < -122.0 && p20kEnv < -122.0)
            {
                verdict = AuthenticityVerdict.Dithered16BitUpscale;
                detectedCutoffHz = 22_050;
                explanation = $"Wadah 24-bit / 44.1 kHz, namun pita 20-22 kHz menunjukkan lantai derau dither 16-bit rata ({noiseFloorDbFS:F1} dBFS, σ={noiseStdDevDb:F1} dB).";
            }
            else if (detectedKneeHz <= 21_000 && isSmoothAcousticRollOff)
            {
                verdict = AuthenticityVerdict.AcousticNaturalRollOff;
                detectedCutoffHz = 22_050;
                explanation = $"Lossless asli berkarakter akustik lembut: lagu melandai alami tanpa potongan filter buatan (jangkauan puncak ~{upperEnvelopeReachHz / 1000.0:F1} kHz, kemiringan treble {trebleDecaySlope:F1} dB/oktaf).";
            }
            else if (detectedKneeHz <= 21_000)
            {
                verdict = AuthenticityVerdict.Inconclusive;
                detectedCutoffHz = detectedKneeHz;
                explanation = $"Perlu cek manual: spektrum 44.1 kHz turun cukup tajam di ~{detectedKneeHz / 1000.0:F1} kHz (kemiringan {kneeSteepness:F1} dB/oktaf, penurunan {dropAcrossKnee:F1} dB).";
            }
            else if (dropAcrossKnee >= 25.0 && kneeSteepness <= -60.0)
            {
                verdict = AuthenticityVerdict.Inconclusive;
                detectedCutoffHz = detectedKneeHz;
                explanation = $"Perlu cek manual: penurunan tajam melintasi titik belok ~{detectedKneeHz / 1000.0:F1} kHz (penurunan {dropAcrossKnee:F1} dB, kemiringan {kneeSteepness:F1} dB/oktaf).";
            }
            else
            {
                verdict = AuthenticityVerdict.VerifiedLossless;
                detectedCutoffHz = 22_050;
                explanation = $"Lossless asli: tidak ditemukan potongan MP3; energi aktif alami hingga batas CD ~{nyquistLimitHz / 1000.0:F1} kHz. Catatan: AAC/Opus bitrate tinggi tanpa lowpass tidak dapat dibedakan lewat spektrum saja.";
            }
        }

        if (isZeroPadded && bitsPerSample >= 24 && verdict != AuthenticityVerdict.ZeroPadded24Bit &&
            !explanation.Contains("zero-padding", StringComparison.OrdinalIgnoreCase))
        {
            explanation += " [Catatan: Wadah 24-bit terdeteksi zero-padding dari 16-bit].";
        }

        return new AudioIntegrityReport(
            sourceSpec,
            hwSpec,
            verdict,
            detectedCutoffHz,
            explanation,
            isTrue24Bit: isTrue24Bit,
            lsbActivityPercentage: lsbPercentage,
            spectrumDbBins: dbBins,
            spectralSlopeDbPerOctave: slopeCdTransition,
            ultrasonicNoiseFloorDbFS: noiseFloorDbFS,
            isDitherDetected: isDitherShelfDetected,
            lsbEntropy: lsbEntropy,
            analysisTimestamp: loudestSectionTime,
            analysisPositionPercentage: Math.Round(loudestFraction * 100.0, 1),
            powerAt22kDbFS: Math.Round(p22k, 1),
            powerAtNyquistDbFS: Math.Round(pNyquist, 1),
            ultrasonicNoiseStdDevDb: noiseStdDevDb,
            isZeroPadded: isZeroPadded,
            isSyntheticSpectrum: false,
            kneeHz: detectedKneeHz,
            kneeSteepnessDbPerOct: Math.Round(kneeSteepness, 1),
            dropAcrossKneeDb: Math.Round(dropAcrossKnee, 1),
            relP16kDb: Math.Round(relP16k, 1),
            relP20kDb: Math.Round(relP20k, 1),
            imagingCorrelation: sampleRate >= 48_000 ? Math.Round(imagingCorrelation, 2) : null,
            imagingAttenuationDb: sampleRate >= 48_000 ? Math.Round(imagingAttenuationDb, 1) : null,
            imagingUpperMeanDb: sampleRate >= 48_000 ? Math.Round(imagingUpperMeanDb, 1) : null,
            cliffHz: cliffHz > 0 ? cliffHz : null,
            cliffDropDb: cliffDropDb > 0.0 ? Math.Round(cliffDropDb, 1) : null,
            hasActiveHardwarePath: hasActiveHardwarePath,
            isHardwareExclusive: isHardwareExclusive,
            configuredDriverType: configuredDriverType,
            upperEnvelopeReachHz: upperEnvelopeReachHz
        );
    }

    /// <summary>
    /// Mendeteksi apakah data 24-in-32 bit berformat MSB-aligned (byte 0 = 0x00 pad, LSB di byte 1 -> offset 1)
    /// atau LSB-aligned (LSB di byte 0 -> offset 0).
    /// Jika byte 0 nol DAN byte 3 selalu sign-extension dari bit 7 byte 2 (0x00 atau 0xFF),
    /// maka disimpulkan LSB-aligned (offset 0, audio 16-bit yang di-zero-padding).
    /// </summary>
    private static int Detect24In32Alignment(byte[] buffer, int samples, int blockAlign)
    {
        if (samples <= 0) return 0;
        int zeroCountByte0 = 0;
        int signExtCountByte3 = 0;
        int testLimit = Math.Min(100, samples);
        for (int t = 0; t < testLimit; t++)
        {
            int baseIdx = t * blockAlign;
            byte b0 = buffer[baseIdx];
            byte b2 = buffer[baseIdx + 2];
            byte b3 = buffer[baseIdx + 3];

            if (b0 == 0) zeroCountByte0++;

            byte expectedSignExt = (byte)((b2 & 0x80) != 0 ? 0xFF : 0x00);
            if (b3 == expectedSignExt) signExtCountByte3++;
        }

        if (zeroCountByte0 >= testLimit - 2 && signExtCountByte3 >= testLimit - 2)
        {
            return 0; // LSB-aligned dengan zero-padding pada LSB
        }

        return (zeroCountByte0 >= testLimit - 2) ? 1 : 0;
    }

    /// <summary>
    /// Ekstraksi sampel audio yang dinormalisasi ke rentang [-1.0, 1.0] dengan dukungan bit-exact
    /// untuk format 16-bit PCM, 24-bit PCM (packed 3-byte dan padded 4-byte slot), 32-bit PCM integer,
    /// serta IEEE Float 32-bit.
    /// Mencegah kontaminasi antar kanal dan distorsi harmonik palsu pada analisis FFT.
    /// </summary>
    private static double ExtractSampleNorm(
        byte[] buffer,
        int offset,
        int bitsPerSample,
        int bytesPerSample,
        WaveFormatEncoding encoding,
        int lsbOffset = 0)
    {
        if (encoding == WaveFormatEncoding.IeeeFloat)
        {
            float f = BitConverter.ToSingle(buffer, offset);
            return float.IsFinite(f) ? Math.Clamp((double)f, -1.0, 1.0) : 0.0;
        }

        if (bitsPerSample == 16)
        {
            short s = (short)(buffer[offset] | (buffer[offset + 1] << 8));
            return s / 32_768.0;
        }
        else if (bitsPerSample == 24)
        {
            if (bytesPerSample == 3)
            {
                int s = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
                return s / 8_388_608.0;
            }
            else // 24-bit dalam slot 32-bit: gunakan lsbOffset hasil deteksi (0 atau 1)
            {
                int s = buffer[offset + lsbOffset] | (buffer[offset + lsbOffset + 1] << 8) | (buffer[offset + lsbOffset + 2] << 16);
                if ((s & 0x800000) != 0) s |= unchecked((int)0xFF000000);
                return s / 8_388_608.0;
            }
        }
        else if (bitsPerSample >= 32)
        {
            if (encoding == WaveFormatEncoding.Pcm)
            {
                int s = BitConverter.ToInt32(buffer, offset);
                return s / 2_147_483_648.0;
            }
            else
            {
                float f = BitConverter.ToSingle(buffer, offset);
                return float.IsFinite(f) ? Math.Clamp((double)f, -1.0, 1.0) : 0.0;
            }
        }

        return 0.0;
    }

    /// <summary>
    /// Memindai beberapa jendela waktu berbeda (20%, 35%, 50%, 65%, 80%) untuk menemukan
    /// segmen audio dengan energi RMS tertinggi (loudest section).
    /// Mengembalikan timestamp waktu terpilih dan persentase posisinya.
    /// </summary>
    private static (TimeSpan Time, double Fraction) FindLoudestSectionTime(IAudioStream stream, CancellationToken ct)
    {
        double totalSeconds = stream.TotalTime.TotalSeconds;
        if (totalSeconds < 10.0)
        {
            return (TimeSpan.FromSeconds(Math.Max(0.0, totalSeconds * 0.30)), 0.30);
        }

        double[] candidateFractions = [0.20, 0.35, 0.50, 0.65, 0.80];
        double bestFrac = 0.40;
        double bestTimeSec = totalSeconds * 0.40;
        double maxRms = -1.0;

        int probeSamples = 8192;
        int blockAlign = stream.WaveFormat.BlockAlign;
        int bitsPerSample = stream.WaveFormat.BitsPerSample;
        int channels = Math.Max(1, stream.WaveFormat.Channels);
        int bytesPerSample = channels > 0 ? blockAlign / channels : 2;

        byte[] probeBuf = new byte[probeSamples * blockAlign];

        WaveFormatEncoding encoding = stream.WaveFormat.Encoding;

        foreach (double frac in candidateFractions)
        {
            ct.ThrowIfCancellationRequested();
            double targetSec = totalSeconds * frac;
            try
            {
                stream.CurrentTime = TimeSpan.FromSeconds(targetSec);
                int read = stream.Read(probeBuf, 0, probeBuf.Length);
                int samples = read / blockAlign;
                if (samples <= 0) continue;

                int lsbOffset = 0;
                if (bytesPerSample == 4 && bitsPerSample == 24)
                {
                    lsbOffset = Detect24In32Alignment(probeBuf, samples, blockAlign);
                }

                double sumSq = 0.0;
                int totalSamplesAcrossChannels = 0;
                for (int i = 0; i < samples; i++)
                {
                    int frameOff = i * blockAlign;
                    // Rata-ratakan energi dari semua kanal dalam satu frame
                    double frameSumSq = 0.0;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int sampleOff = frameOff + ch * bytesPerSample;
                        double sv = ExtractSampleNorm(probeBuf, sampleOff, bitsPerSample, bytesPerSample, encoding, lsbOffset);
                        frameSumSq += sv * sv;
                    }
                    sumSq += frameSumSq / channels;
                    totalSamplesAcrossChannels++;
                }

                double rms = Math.Sqrt(sumSq / samples);
                if (rms > maxRms)
                {
                    maxRms = rms;
                    bestTimeSec = targetSec;
                    bestFrac = frac;
                }
            }
            catch
            {
            }
        }

        return (TimeSpan.FromSeconds(bestTimeSec), bestFrac);
    }

    /// <summary>
    /// Audit forensik integritas bit-depth pada data PCM mentah (hingga 300.000 frame, seluruh kanal).
    /// Mengukur aktivitas bit terbawah dan Entropi Shannon (0.0 s/d 8.0 bit) untuk 16-bit dan 24-bit,
    /// serta mendeteksi wadah 24-bit palsu yang hanya di-padding nol dari 16-bit.
    /// </summary>
    private static (bool IsTrue24Bit, double LsbPercentage, double ShannonEntropy, bool IsZeroPadded, int LsbOffset) EvaluateBitDepthForensic(
        IAudioStream stream,
        int sampleCount,
        int blockAlign,
        int bitsPerSample,
        int channels)
    {
        if (stream.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || bitsPerSample < 16)
        {
            return (false, 0.0, 0.0, false, 0);
        }

        int bytesPerSample = channels > 0 ? blockAlign / channels : (bitsPerSample / 8);
        byte[] bitBuf = new byte[sampleCount * blockAlign];
        int bytesRead = stream.Read(bitBuf, 0, bitBuf.Length);
        int actualFrames = bytesRead / blockAlign;

        if (actualFrames <= 0) return (false, 0.0, 0.0, false, 0);

        int lsbOffset = 0;
        if (bitsPerSample >= 24 && bytesPerSample == 4)
        {
            lsbOffset = Detect24In32Alignment(bitBuf, actualFrames, blockAlign);
        }

        long nonZeroLsb = 0;
        long totalChannelSamples = (long)actualFrames * channels;
        int[] lsbDistribution = new int[256];

        for (int i = 0; i < actualFrames; i++)
        {
            int frameOff = i * blockAlign;
            for (int ch = 0; ch < channels; ch++)
            {
                int byteIdx = frameOff + ch * bytesPerSample + lsbOffset;
                if (byteIdx >= bytesRead) continue;
                byte lsb = bitBuf[byteIdx];
                if (lsb != 0) nonZeroLsb++;
                lsbDistribution[lsb]++;
            }
        }

        double entropy = 0.0;
        int uniqueValues = 0;
        for (int b = 0; b < 256; b++)
        {
            if (lsbDistribution[b] > 0)
            {
                uniqueValues++;
                double p = (double)lsbDistribution[b] / totalChannelSamples;
                entropy -= p * Math.Log2(p);
            }
        }

        double pct = (double)nonZeroLsb / totalChannelSamples * 100.0;
        if (bitsPerSample < 24)
        {
            return (false, Math.Round(pct, 1), Math.Round(entropy, 2), false, 0);
        }

        bool isZeroPadded = (pct < 1.0 && entropy < 0.5);
        bool isTrue = (pct > 75.0 && uniqueValues > 160 && entropy > 6.0);

        return (isTrue, Math.Round(pct, 1), Math.Round(entropy, 2), isZeroPadded, lsbOffset);
    }

    /// <summary>
    /// Menghitung kemiringan spektral (Spectral Slope) dalam dB/oktaf menggunakan
    /// regresi linear kuadrat terkecil (OLS) pada semua bin FFT di band [f1, f2].
    /// Variabel regresi: x = log2(frekuensi_bin), y = daya_bin_dalam_dBFS.
    /// Jauh lebih stabil dari estimator dua-titik: std deviasi turun dari ~8 dB/okt
    /// menjadi ~1–2 dB/okt pada derau putih dengan 32 jendela.
    /// Semua bin dibatasi ke avgPower.Length-1 agar tidak overflow batas array.
    /// </summary>
    private static double ComputeSpectralSlope(double f1, double f2, double[] avgPower, double binResolution)
    {
        if (f1 <= 0.0 || f2 <= 0.0 || f2 <= f1 || binResolution <= 0.0)
            return 0.0;

        int binLo = (int)(f1 / binResolution);
        int binHi = (int)(f2 / binResolution);
        binLo = Math.Clamp(binLo, 1, avgPower.Length - 1); // bin 0 (DC) dibuang
        binHi = Math.Clamp(binHi, binLo, avgPower.Length - 1);

        if (binHi <= binLo) return 0.0;

        // Regresi OLS: slope = (N*Sxy - Sx*Sy) / (N*Sxx - Sx*Sx)
        // x = log2(freq_bin), y = dBFS(bin)
        double sx = 0.0, sy = 0.0, sxx = 0.0, sxy = 0.0;
        int n = 0;
        for (int b = binLo; b <= binHi; b++)
        {
            double freq = b * binResolution;
            if (freq <= 0.0) continue;
            double x = Math.Log2(freq);
            double y = 10.0 * Math.Log10(Math.Max(1e-16, avgPower[b]));
            sx  += x;
            sy  += y;
            sxx += x * x;
            sxy += x * y;
            n++;
        }

        if (n < 2) return 0.0;
        double denom = (double)n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-12) return 0.0;
        double slope = ((double)n * sxy - sx * sy) / denom;
        return double.IsFinite(slope) ? Math.Round(slope, 1) : 0.0;
    }

    /// <summary>
    /// Memeriksa tingkat derau ultrasonik rata-rata dan deviasi standarnya (sigma dalam dB).
    /// Mendeteksi apakah derau di frekuensi atas merupakan plataran dither sintetis (sigma < 2.2 dB)
    /// atau fluktuasi harmonik dinamis musik (sigma >= 3.0 dB).
    /// Mendukung perhitungan adaptif untuk format 44.1k, 48k, 96k, dan 192k.
    /// </summary>
    private static (double AvgNoiseFloorDbFS, double StdDevDb, bool IsDitherShelfDetected) EvaluateQuantizationNoiseFloor(
        double[] avgPower,
        double binResolution,
        uint nyquistLimitHz)
    {
        if (binResolution <= 0.0)
        {
            return (-120.0, 0.0, false);
        }

        double startFreq;
        double endFreq;

        if (nyquistLimitHz >= 44_100)
        {
            // Format 88.2k, 96k, 192k: evaluasi pita ultrasonik di atas batas CD
            startFreq = 22_500.0;
            endFreq = Math.Min(48_000.0, nyquistLimitHz - 500.0);
        }
        else if (nyquistLimitHz >= 24_000)
        {
            // Format 48 kHz: evaluasi rentang di atas batas CD (22.05k s/d 23.9k)
            // Memastikan energi nyata di atas 22 kHz terbaca (misal -80 s/d -85 dBFS)
            startFreq = 22_050.0;
            endFreq = 23_900.0;
        }
        else
        {
            // Format 44.1 kHz: evaluasi pita atas mendekati Nyquist
            startFreq = 20_000.0;
            endFreq = 22_050.0;
        }

        int startBin = (int)(startFreq / binResolution);
        int endBin = (int)(endFreq / binResolution);
        endBin = Math.Min(endBin, avgPower.Length - 1);

        if (endBin <= startBin)
        {
            return (-120.0, 0.0, false);
        }

        int count = endBin - startBin + 1;
        double[] dbs = new double[count];
        double sumDb = 0.0;

        for (int i = 0; i < count; i++)
        {
            double p = avgPower[startBin + i];
            double db = 10.0 * Math.Log10(Math.Max(1e-16, p));
            dbs[i] = db;
            sumDb += db;
        }

        double meanDb = sumDb / count;

        // Hitung standar deviasi untuk mengukur tingkat kedataran plataran noise
        double sumSqDiff = 0.0;
        for (int i = 0; i < count; i++)
        {
            double diff = dbs[i] - meanDb;
            sumSqDiff += diff * diff;
        }
        double stdDev = Math.Sqrt(sumSqDiff / count);

        // Level dither TPDF 16-bit yang diharapkan per-bin pada normalisasi FFT ini:
        //   varians_dither = 0.25 * LSB^2  (TPDF = penjumlahan 2 distribusi uniform ±0.5 LSB)
        //   LSB = 2.0 / 65536.0            (full-scale = ±1.0 dinormalisasi)
        //   normalisasi mag = |X| / (N/2) dengan Hann sum factor ~1.5
        //   offset_fft = 10 * log10(1.5 / N) = 10 * log10(1.5 / 4096) ≈ -34.36 dB
        //   level_per_bin = 10*log10(varians_dither) + offset_fft = -96.33 + (-34.36) = -130.7 dBFS
        const double DitherTpdf16BitLevelDbFS = -130.7;
        const double DitherDetectionToleranceDb = 4.0; // ±4 dB toleransi terhadap noise shaping

        // Dither terdeteksi jika:
        //   (a) rata-rata berada dalam ±toleransi dari level dither teoritis, DAN
        //   (b) deviasi standar sangat rendah (plataran rata, bukan harmonik musik)
        bool isDither = (meanDb >= DitherTpdf16BitLevelDbFS - DitherDetectionToleranceDb &&
                         meanDb <= DitherTpdf16BitLevelDbFS + DitherDetectionToleranceDb &&
                         stdDev < 2.2);

        return (Math.Round(meanDb, 1), Math.Round(stdDev, 1), isDither);
    }

    private static double[] ComputeAveragePowerSpectrum(
        IAudioStream stream,
        int fftSize,
        int numWindows,
        CancellationToken ct,
        int lsbOffset = 0)
    {
        double[] avgPower = new double[fftSize / 2];
        int blockAlign = stream.WaveFormat.BlockAlign;
        int bitsPerSample = stream.WaveFormat.BitsPerSample;
        int channels = Math.Max(1, stream.WaveFormat.Channels);
        int bytesPerSample = channels > 0 ? blockAlign / channels : 2;
        WaveFormatEncoding encoding = stream.WaveFormat.Encoding;

        byte[] buffer = new byte[fftSize * blockAlign];
        Complex[] fftIn = new Complex[fftSize];
        int windowsProcessed = 0;

        for (int w = 0; w < numWindows; w++)
        {
            ct.ThrowIfCancellationRequested();

            int read = stream.Read(buffer, 0, buffer.Length);
            if (read < buffer.Length)
            {
                // Jika mendekati EOF tapi belum memproses cukup window, coba loop kembali ke awal
                if (windowsProcessed == 0 && stream.Length > buffer.Length)
                {
                    try
                    {
                        stream.Position = 0;
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch { }
                }
                if (read < buffer.Length) break;
            }

            // FFT per kanal, lalu rata-ratakan DAYA spektrum seluruh kanal (bagi jumlah kanal)
            // Menghindari pembatalan fase stereo dan mempertahankan daya dither independen pada -130.7 dBFS
            double chWeight = 1.0 / channels;
            for (int ch = 0; ch < channels; ch++)
            {
                for (int i = 0; i < fftSize; i++)
                {
                    int frameOff = i * blockAlign;
                    double sample = ExtractSampleNorm(buffer, frameOff + ch * bytesPerSample, bitsPerSample, bytesPerSample, encoding, lsbOffset);
                    fftIn[i] = new Complex(sample * HannWindow[i], 0);
                }

                ComputeFftInPlace(fftIn);

                for (int b = 0; b < fftSize / 2; b++)
                {
                    double mag = fftIn[b].Magnitude / (fftSize / 2.0);
                    avgPower[b] += (mag * mag) * chWeight;
                }
            }

            windowsProcessed++;
        }

        if (windowsProcessed > 0)
        {
            for (int b = 0; b < fftSize / 2; b++)
            {
                avgPower[b] /= windowsProcessed;
            }
        }

        return avgPower;
    }

    /// <summary>
    /// Mengembalikan daya spektrum dalam dBFS pada satu bin frekuensi terdekat.
    /// Jika frekuensi melebihi Nyquist wadah, di-clamp ke bin terakhir yang tersedia
    /// (bukan return -120) agar pembacaan tetap fisik.
    /// </summary>
    private static double GetPowerAtFreq(double freqHz, double[] avgPower, double binResolution)
    {
        if (binResolution <= 0.0) return -160.0;
        int bin = (int)(freqHz / binResolution);
        bin = Math.Clamp(bin, 0, avgPower.Length - 1);
        double p = avgPower[bin];
        return 10.0 * Math.Log10(Math.Max(1e-16, p));
    }

    /// <summary>
    /// Merata-ratakan daya LINEAR pada semua bin dalam band [fCenter - halfWidthHz, fCenter + halfWidthHz]
    /// lalu mengonversinya ke dBFS. Lebih stabil daripada satu titik tunggal untuk frekuensi
    /// di dekat batas array (misal p22k pada file 44.1 kHz, p24k pada file 48 kHz).
    /// </summary>
    private static double GetBandPowerDb(double fCenterHz, double halfWidthHz, double[] avgPower, double binResolution)
    {
        if (binResolution <= 0.0 || avgPower.Length == 0) return -160.0;
        int binLo = (int)Math.Max(0, (fCenterHz - halfWidthHz) / binResolution);
        int binHi = (int)((fCenterHz + halfWidthHz) / binResolution);
        binLo = Math.Clamp(binLo, 0, avgPower.Length - 1);
        binHi = Math.Clamp(binHi, binLo, avgPower.Length - 1);

        double sumLinear = 0.0;
        int count = binHi - binLo + 1;
        for (int b = binLo; b <= binHi; b++)
        {
            sumLinear += avgPower[b];
        }
        double meanLinear = sumLinear / Math.Max(1, count);
        return 10.0 * Math.Log10(Math.Max(1e-16, meanLinear));
    }

    /// <summary>
    /// Mencari frekuensi cutoff terukur (knee) di mana level spektral masih berada di atas
    /// (pRef - dropBelowRefDb). Membantu mendeteksi apakah sinyal terpotong tajam di ~16 kHz (MP3 128k)
    /// atau ~20 kHz (MP3 320k) vs mengalir alami hingga batas Nyquist.
    /// </summary>
    private static uint DetectKneeCutoffHz(
        double[] avgPower,
        double binResolution,
        double pRef,
        uint nyquistLimitHz,
        double dropBelowRefDb = 48.0)
    {
        if (binResolution <= 0.0 || avgPower.Length == 0) return nyquistLimitHz;
        double effectiveRef = Math.Max(-80.0, pRef);
        double thresholdDb = effectiveRef - dropBelowRefDb;

        int maxBin = Math.Min(avgPower.Length - 1, (int)(nyquistLimitHz / binResolution));
        int minBin = Math.Max(1, (int)(8_000.0 / binResolution));

        for (int b = maxBin; b >= minBin; b--)
        {
            double binPowerDb = 10.0 * Math.Log10(Math.Max(1e-16, avgPower[b]));
            if (binPowerDb > thresholdDb)
            {
                uint detectedHz = (uint)Math.Round(b * binResolution);
                return Math.Min(detectedHz, nyquistLimitHz);
            }
        }

        return (uint)Math.Round(minBin * binResolution);
    }

    /// <summary>
    /// Menghitung kecuraman kemiringan spektral OLS (dB/oktaf) pada zona transisi knee [kneeHz - 1000 Hz, kneeHz + 1000 Hz].
    /// Digunakan untuk membedakan filter pemotong psikoakustik lossy (steepness <= -60 dB/okt)
    /// dari peluruhan instrumen akustik alami (steepness > -60 dB/okt).
    /// </summary>
    private static double ComputeKneeSteepness(double[] avgPower, double binResolution, uint kneeHz)
    {
        double f1 = Math.Max(1000.0, (double)kneeHz - 1000.0);
        double f2 = (double)kneeHz + 1000.0;
        return ComputeSpectralSlope(f1, f2, avgPower, binResolution);
    }

    /// <summary>
    /// Menghitung penurunan daya spektral (drop) dalam dB melintasi frekuensi knee:
    /// rata-rata dB daya linear pada band [kneeHz - 1000, kneeHz - 200] dikurangi
    /// rata-rata dB daya linear pada band [kneeHz + 200, kneeHz + 1000].
    /// Jika band sebelum atau sesudah knee sepenuhnya berada di luar array (tidak ada bin valid),
    /// mengembalikan 0.0 (tak terukur), bukan -160 dB, sehingga gerbang kecuraman MP3 tidak terpenuhi.
    /// </summary>
    private static double ComputeDropAcrossKnee(double[] avgPower, double binResolution, uint kneeHz)
    {
        if (binResolution <= 0.0 || avgPower.Length == 0) return 0.0;

        double fBeforeLo = Math.Max(0.0, (double)kneeHz - 1000.0);
        double fBeforeHi = Math.Max(0.0, (double)kneeHz - 200.0);
        double fAfterLo = (double)kneeHz + 200.0;
        double fAfterHi = (double)kneeHz + 1000.0;

        int binBeforeLo = (int)(fBeforeLo / binResolution);
        int binBeforeHi = (int)(fBeforeHi / binResolution);
        int binAfterLo = (int)(fAfterLo / binResolution);
        int binAfterHi = (int)(fAfterHi / binResolution);

        // Jika band sebelum atau sesudah knee sepenuhnya berada di luar rentang indeks array [0, avgPower.Length - 1],
        // kembalikan 0.0 (data tak terukur) agar gerbang kecuraman MP3 tidak terpenuhi.
        if (binBeforeLo >= avgPower.Length || binBeforeHi < 0 ||
            binAfterLo >= avgPower.Length || binAfterHi < 0)
        {
            return 0.0;
        }

        int validBeforeLo = Math.Max(0, binBeforeLo);
        int validBeforeHi = Math.Min(avgPower.Length - 1, binBeforeHi);
        int validAfterLo = Math.Max(0, binAfterLo);
        int validAfterHi = Math.Min(avgPower.Length - 1, binAfterHi);

        if (validBeforeLo > validBeforeHi || validAfterLo > validAfterHi)
        {
            return 0.0;
        }

        double sumBefore = 0.0;
        int countBefore = validBeforeHi - validBeforeLo + 1;
        for (int b = validBeforeLo; b <= validBeforeHi; b++)
        {
            sumBefore += avgPower[b];
        }
        double meanBefore = sumBefore / countBefore;
        double dbBefore = 10.0 * Math.Log10(Math.Max(1e-16, meanBefore));

        double sumAfter = 0.0;
        int countAfter = validAfterHi - validAfterLo + 1;
        for (int b = validAfterLo; b <= validAfterHi; b++)
        {
            sumAfter += avgPower[b];
        }
        double meanAfter = sumAfter / countAfter;
        double dbAfter = 10.0 * Math.Log10(Math.Max(1e-16, meanAfter));

        return dbBefore - dbAfter;
    }

    /// <summary>
    /// Menghitung koefisien korelasi Pearson antara nilai dB spektrum di pita atas (22.050 + d)
    /// dan pita bawah (22.050 - d) untuk mendeteksi artefak cermin aliasing/imaging dari upsampling
    /// berkas 44.1 kHz ke >= 48 kHz.
    /// Rentang d: 200 Hz s/d 4.000 Hz. Korelasi r >= 0.65 dengan atenuasi <= 15.0 dB dan level pita atas > -128.0 dBFS
    /// menandakan cermin simetris resampler non-ideal.
    /// </summary>
    private static (double Correlation, double AttenuationDb, double UpperMeanDb, bool IsImagingDetected) ComputeImagingCorrelation(
        double[] avgPower,
        double binResolution,
        uint sampleRateHz)
    {
        if (sampleRateHz < 48_000 || binResolution <= 0.0 || avgPower.Length < 100)
        {
            return (0.0, 0.0, -160.0, false);
        }

        double centerFreq = 22_050.0;
        int centerBin = (int)Math.Round(centerFreq / binResolution);

        int minDeltaBin = Math.Max(1, (int)Math.Round(200.0 / binResolution));
        int maxDeltaBin = (int)Math.Round(4_000.0 / binResolution);

        if (centerBin - maxDeltaBin < 0 || centerBin + maxDeltaBin >= avgPower.Length)
        {
            maxDeltaBin = Math.Min(centerBin, avgPower.Length - 1 - centerBin);
        }

        if (maxDeltaBin <= minDeltaBin + 4)
        {
            return (0.0, 0.0, -160.0, false);
        }

        int n = 0;
        double sumX = 0.0, sumY = 0.0;
        double sumX2 = 0.0, sumY2 = 0.0;
        double sumXY = 0.0;

        for (int delta = minDeltaBin; delta <= maxDeltaBin; delta++)
        {
            int lowerBin = centerBin - delta;
            int upperBin = centerBin + delta;

            double powerLower = avgPower[lowerBin];
            double powerUpper = avgPower[upperBin];

            double dbLower = 10.0 * Math.Log10(Math.Max(1e-16, powerLower));
            double dbUpper = 10.0 * Math.Log10(Math.Max(1e-16, powerUpper));

            sumX += dbLower;
            sumY += dbUpper;
            sumX2 += dbLower * dbLower;
            sumY2 += dbUpper * dbUpper;
            sumXY += dbLower * dbUpper;
            n++;
        }

        if (n < 5) return (0.0, 0.0, -160.0, false);

        double meanLower = sumX / n;
        double meanUpper = sumY / n;
        double attenuation = meanLower - meanUpper;

        double varX = n * sumX2 - sumX * sumX;
        double varY = n * sumY2 - sumY * sumY;

        if (varX <= 1e-6 || varY <= 1e-6)
        {
            return (0.0, Math.Round(attenuation, 1), Math.Round(meanUpper, 1), false);
        }

        double r = (n * sumXY - sumX * sumY) / Math.Sqrt(varX * varY);
        if (!double.IsFinite(r)) return (0.0, Math.Round(attenuation, 1), Math.Round(meanUpper, 1), false);

        bool isImaging = (r >= 0.65 && attenuation <= 15.0 && meanUpper > -128.0);

        return (Math.Round(r, 2), Math.Round(attenuation, 1), Math.Round(meanUpper, 1), isImaging);
    }

    /// <summary>
    /// Mendeteksi tebing spektral transisi (cliff) di sekitar batas CD 19.000 Hz s/d min(24.000, nyquist - 300) Hz.
    /// Membandingkan rata-rata daya linear (dikonversi ke dBFS) pada pita before [f - 500, f - 100] Hz
    /// terhadap pita after [f + 100, f + 500] Hz.
    /// Mengembalikan frekuensi f dengan penurunan daya (before - after) terbesar beserta nilai drop-nya.
    /// Mengembalikan (0, 0.0) jika band berada di luar array.
    /// </summary>
    private static (uint CliffHz, double CliffDropDb) ComputeCliff(
        double[] avgPower,
        double binResolution,
        double nyquistHz)
    {
        if (binResolution <= 0.0 || avgPower.Length == 0)
        {
            return (0, 0.0);
        }

        double maxFreq = Math.Min(24_000.0, nyquistHz - 300.0);
        int startBin = (int)Math.Ceiling(19_000.0 / binResolution);
        int endBin = (int)Math.Floor(maxFreq / binResolution);

        if (startBin > endBin || startBin >= avgPower.Length)
        {
            return (0, 0.0);
        }

        uint bestCliffHz = 0;
        double maxDropDb = 0.0;

        for (int i = startBin; i <= endBin; i++)
        {
            double f = i * binResolution;

            int binBeforeLo = (int)Math.Round((f - 500.0) / binResolution);
            int binBeforeHi = (int)Math.Round((f - 100.0) / binResolution);
            int binAfterLo = (int)Math.Round((f + 100.0) / binResolution);
            int binAfterHi = (int)Math.Round((f + 500.0) / binResolution);

            if (binBeforeLo < 0 || binBeforeHi < 0 || binBeforeLo >= avgPower.Length ||
                binAfterLo < 0 || binAfterLo >= avgPower.Length)
            {
                continue;
            }

            int validBeforeLo = Math.Max(0, binBeforeLo);
            int validBeforeHi = Math.Min(avgPower.Length - 1, binBeforeHi);
            int validAfterLo = Math.Max(0, binAfterLo);
            int validAfterHi = Math.Min(avgPower.Length - 1, binAfterHi);

            if (validBeforeLo > validBeforeHi || validAfterLo > validAfterHi)
            {
                continue;
            }

            double sumBefore = 0.0;
            int countBefore = validBeforeHi - validBeforeLo + 1;
            for (int b = validBeforeLo; b <= validBeforeHi; b++)
            {
                sumBefore += avgPower[b];
            }
            double meanBefore = sumBefore / countBefore;
            double dbBefore = 10.0 * Math.Log10(Math.Max(1e-16, meanBefore));

            double sumAfter = 0.0;
            int countAfter = validAfterHi - validAfterLo + 1;
            for (int b = validAfterLo; b <= validAfterHi; b++)
            {
                sumAfter += avgPower[b];
            }
            double meanAfter = sumAfter / countAfter;
            double dbAfter = 10.0 * Math.Log10(Math.Max(1e-16, meanAfter));

            double drop = dbBefore - dbAfter;
            if (drop > maxDropDb)
            {
                maxDropDb = drop;
                bestCliffHz = (uint)Math.Round(f);
            }
        }

        return (bestCliffHz, Math.Round(maxDropDb, 1));
    }

    /// <summary>
    /// Memindai amplop daya persentil ke-90 lintas 5 segmen lagu (15%, 32%, 50%, 68%, 85%)
    /// agar petikan akustik atau simbal singkat tidak terlewatkan oleh satu jendela rata-rata.
    /// </summary>
    private static (double[] EnvelopePower, uint UpperReachHz) ComputeMultiSegmentUpperEnvelope(
        IAudioStream stream,
        double[] fallbackAvgPower,
        int fftSize,
        double binResolution,
        uint nyquistLimitHz,
        CancellationToken ct,
        int lsbOffset = 0)
    {
        int numBins = fftSize / 2;
        double totalSeconds = stream.TotalTime.TotalSeconds;
        double[] fractions = totalSeconds >= 10.0
            ? [0.15, 0.32, 0.50, 0.68, 0.85]
            : [0.20, 0.50, 0.75];

        int blockAlign = Math.Max(1, stream.WaveFormat.BlockAlign);
        int bitsPerSample = stream.WaveFormat.BitsPerSample;
        int channels = Math.Max(1, stream.WaveFormat.Channels);
        int bytesPerSample = channels > 0 ? blockAlign / channels : 2;
        WaveFormatEncoding encoding = stream.WaveFormat.Encoding;

        byte[] buffer = new byte[fftSize * blockAlign];
        Complex[] fftIn = new Complex[fftSize];
        var frameSpectra = new System.Collections.Generic.List<double[]>(32);
        const int windowsPerPosition = 6;

        foreach (double frac in fractions)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                stream.CurrentTime = TimeSpan.FromSeconds(Math.Max(0.0, totalSeconds * frac));
            }
            catch
            {
                continue;
            }

            for (int w = 0; w < windowsPerPosition; w++)
            {
                ct.ThrowIfCancellationRequested();
                int read;
                try
                {
                    read = stream.Read(buffer, 0, buffer.Length);
                }
                catch
                {
                    break;
                }
                if (read < buffer.Length) break;

                double[] framePower = new double[numBins];
                double chWeight = 1.0 / channels;
                double frameTotal = 0.0;

                for (int ch = 0; ch < channels; ch++)
                {
                    for (int i = 0; i < fftSize; i++)
                    {
                        int frameOff = i * blockAlign;
                        double sample = ExtractSampleNorm(buffer, frameOff + ch * bytesPerSample, bitsPerSample, bytesPerSample, encoding, lsbOffset);
                        fftIn[i] = new Complex(sample * HannWindow[i], 0);
                    }

                    ComputeFftInPlace(fftIn);

                    for (int b = 0; b < numBins; b++)
                    {
                        double mag = fftIn[b].Magnitude / (fftSize / 2.0);
                        double p = (mag * mag) * chWeight;
                        framePower[b] += p;
                        frameTotal += p;
                    }
                }

                if ((frameTotal / numBins) > 1e-12)
                {
                    frameSpectra.Add(framePower);
                }
            }
        }

        if (frameSpectra.Count == 0)
        {
            double pRefFallback = GetBandPowerDb(2_500, 1_500, fallbackAvgPower, binResolution);
            uint fallbackReach = DetectKneeCutoffHz(fallbackAvgPower, binResolution, pRefFallback, nyquistLimitHz, 58.0);
            return (fallbackAvgPower, fallbackReach);
        }

        double[] envPower = new double[numBins];
        double[] scratch = new double[frameSpectra.Count];
        int p90Idx = Math.Clamp((int)Math.Floor((frameSpectra.Count - 1) * 0.90), 0, frameSpectra.Count - 1);

        for (int b = 0; b < numBins; b++)
        {
            for (int f = 0; f < frameSpectra.Count; f++)
            {
                scratch[f] = frameSpectra[f][b];
            }
            Array.Sort(scratch);
            envPower[b] = scratch[p90Idx];
        }

        double pRefEnv = GetBandPowerDb(2_500, 1_500, envPower, binResolution);
        double reachThresholdDb = Math.Max(-114.0, Math.Max(-80.0, pRefEnv) - 66.0);
        int maxBin = Math.Min(numBins - 1, (int)(nyquistLimitHz / binResolution));
        int minBin = Math.Max(1, (int)(8_000.0 / binResolution));
        uint reachHz = (uint)Math.Round(minBin * binResolution);

        for (int b = maxBin; b >= minBin; b--)
        {
            double binDb = 10.0 * Math.Log10(Math.Max(1e-16, envPower[b]));
            if (binDb > reachThresholdDb)
            {
                reachHz = Math.Min((uint)Math.Round(b * binResolution), nyquistLimitHz);
                break;
            }
        }

        return (envPower, reachHz);
    }

    /// <summary>
    /// Mencari tebing pemotongan tajam di pita 9 kHz - 20.8 kHz yang konsisten muncul di kedua
    /// spektrum rata-rata dan amplop persentil (menghindari false alarm dari instrumen tenang).
    /// </summary>
    private static (uint CliffHz, double DropDb) ComputeMaxMidTrebleCliff(
        double[] avgPower,
        double[] envPower,
        double binResolution,
        uint nyquistLimitHz)
    {
        if (binResolution <= 0.0 || avgPower.Length == 0 || envPower.Length == 0) return (0, 0.0);

        double maxFreq = Math.Min(20_800.0, nyquistLimitHz - 750.0);
        uint bestHz = 0;
        double bestDrop = 0.0;

        for (double f = 9_000.0; f <= maxFreq; f += 250.0)
        {
            double avgDrop = GetBandPowerDb(f - 425.0, 275.0, avgPower, binResolution) -
                             GetBandPowerDb(f + 425.0, 275.0, avgPower, binResolution);
            double envDrop = GetBandPowerDb(f - 425.0, 275.0, envPower, binResolution) -
                             GetBandPowerDb(f + 425.0, 275.0, envPower, binResolution);
            double consistentDrop = Math.Min(avgDrop, envDrop);
            if (consistentDrop > bestDrop)
            {
                bestDrop = consistentDrop;
                bestHz = (uint)Math.Round(f);
            }
        }

        return (bestHz, Math.Round(bestDrop, 1));
    }

    /// <summary>
    /// Mengukur kemiringan peluruhan treble [4 kHz .. min(16 kHz, knee + 1.5 kHz)] pada amplop
    /// untuk membedakan peluruhan akustik alami (-12 s/d -35 dB/okt) dari jatuhan buatan ekstrem.
    /// </summary>
    private static double ComputeTrebleDecaySlope(double[] envPower, double binResolution, uint kneeHz)
    {
        double upper = Math.Clamp((double)kneeHz + 1_500.0, 8_000.0, 16_000.0);
        return ComputeSpectralSlope(4_000.0, upper, envPower, binResolution);
    }

    private static void ComputeFftInPlace(Complex[] buffer)
    {
        int n = buffer.Length;
        int j = 0;

        for (int i = 0; i < n - 1; i++)
        {
            if (i < j)
            {
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }
            int k = n / 2;
            while (k <= j)
            {
                j -= k;
                k /= 2;
            }
            j += k;
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            Complex wlen = new Complex(Math.Cos(angle), Math.Sin(angle));

            for (int i = 0; i < n; i += len)
            {
                Complex w = 1.0;
                for (int k = 0; k < len / 2; k++)
                {
                    Complex u = buffer[i + k];
                    Complex v = buffer[i + k + len / 2] * w;
                    buffer[i + k] = u + v;
                    buffer[i + k + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    private static double[] GenerateDsdSpectrumBins(int binCount, uint sampleRateHz)
    {
        double[] bins = new double[binCount];
        for (int i = 0; i < binCount; i++)
        {
            double freq = (double)i * (sampleRateHz / 2.0) / binCount;
            if (freq < 20_000)
            {
                bins[i] = -25.0 - (freq / 20_000.0) * 15.0;
            }
            else if (freq < 50_000)
            {
                bins[i] = -40.0 - ((freq - 20_000) / 30_000.0) * 20.0;
            }
            else
            {
                bins[i] = -60.0 + Math.Sin(freq / 1000.0) * 3.0;
            }
        }
        return bins;
    }
}
