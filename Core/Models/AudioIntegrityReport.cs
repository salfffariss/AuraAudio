using System;

namespace AuraAudio.Core.Models;

/// <summary>
/// Status vonis keaslian berkas audio berdasarkan analisis spektrum frekuensi dan format biner.
/// </summary>
public enum AuthenticityVerdict
{
    /// <summary>
    /// Berkas terverifikasi Lossless murni sesuai kapasitas sample rate wadahnya.
    /// </summary>
    VerifiedLossless,

    /// <summary>
    /// Rekaman asli studio dengan karakteristik instrumen akustik lembut (vokal, piano, orkestra)
    /// yang melandai alami tanpa filter pemotong buatan.
    /// </summary>
    AcousticNaturalRollOff,

    /// <summary>
    /// Rekaman produksi DAW modern yang memadukan instrumen 44.1 kHz dengan overtone aktif di atas 35 kHz.
    /// </summary>
    HybridMaster,

    /// <summary>
    /// Wadah berlabel 48/96/192 kHz, namun isi suaranya berasal dari master CD 44.1 kHz (terpotong di 22.05 kHz).
    /// </summary>
    UpscaledFromCd,

    /// <summary>
    /// Wadah Hi-Res (mis. 96/192/384 kHz) yang di-upsample dari master ber-sample-rate lebih rendah (mis. 48/88.2/96/192 kHz).
    /// </summary>
    UpscaledHiRes,

    /// <summary>
    /// Berkas berwadah 24-bit yang diisi derau dither 16-bit tanpa detail rekaman 24-bit nyata.
    /// </summary>
    Dithered16BitUpscale,

    /// <summary>
    /// Berkas berwadah 24-bit yang 8 bit paling bawahnya bernilai nol (padding dari 16-bit).
    /// </summary>
    ZeroPadded24Bit,

    /// <summary>
    /// Berkas lossless palsu hasil konversi dari format kompresi lossy (seperti MP3/AAC).
    /// </summary>
    SuspectedUpscaledLossy,

    /// <summary>
    /// Berkas format terkompresi lossy standar sejak awal (MP3, AAC, OGG, Opus).
    /// </summary>
    StandardLossy,

    /// <summary>
    /// Bukti spektral belum meyakinkan (zona transisi atau terindikasi artefak cermin resampler).
    /// </summary>
    Inconclusive,

    /// <summary>
    /// Belum dianalisis oleh modul FFT analyzer.
    /// </summary>
    NotAnalyzed
}

/// <summary>
/// Laporan lengkap integritas, keaslian, dan spesifikasi ramah pengguna yang dihasilkan oleh AuraAudio.
/// </summary>
public record AudioIntegrityReport
{
    public AudioStreamSpec SourceSpec { get; init; }
    public AudioStreamSpec HardwareSpec { get; init; }
    public AuthenticityVerdict Verdict { get; init; }
    public uint DetectedCutoffHz { get; init; }
    public bool IsTrue24Bit { get; init; }
    public bool IsZeroPadded { get; init; }
    public double LsbActivityPercentage { get; init; }
    public double? SpectralSlopeDbPerOctave { get; init; }
    public double? UltrasonicNoiseFloorDbFS { get; init; }
    public bool IsDitherDetected { get; init; }
    public double LsbEntropy { get; init; }
    public TimeSpan AnalysisTimestamp { get; init; }
    public double AnalysisPositionPercentage { get; init; }
    public double? PowerAt22kDbFS { get; init; }
    public double? PowerAtNyquistDbFS { get; init; }
    public double? UltrasonicNoiseStdDevDb { get; init; }
    public bool IsSyntheticSpectrum { get; init; }
    public uint? KneeHz { get; init; }
    public double? KneeSteepnessDbPerOct { get; init; }
    public double? DropAcrossKneeDb { get; init; }
    public double? RelP16kDb { get; init; }
    public double? RelP20kDb { get; init; }
    public double? ImagingCorrelation { get; init; }
    public double? ImagingAttenuationDb { get; init; }
    public double? ImagingUpperMeanDb { get; init; }
    public uint? CliffHz { get; init; }
    public double? CliffDropDb { get; init; }
    public bool HasActiveHardwarePath { get; init; }
    public bool IsHardwareExclusive { get; init; }
    public AudioDriverType ConfiguredDriverType { get; init; }
    public uint? UpperEnvelopeReachHz { get; init; }

    public uint NyquistLimitHz => SourceSpec.SampleRateHz / 2;

    public bool IsBitPerfect =>
        HasActiveHardwarePath &&
        IsHardwareExclusive &&
        SourceSpec.SampleRateHz == HardwareSpec.SampleRateHz &&
        SourceSpec.BitDepth == HardwareSpec.BitDepth;

    public string Explanation { get; init; }

    private static bool IsEnglish =>
        string.Equals(AuraAudio.UI.Helpers.LocalizationManager.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Kategori status untuk memilih ikon status dan warna semantik tanpa bergantung warna semata.
    /// </summary>
    public string VerdictCategory => Verdict switch
    {
        AuthenticityVerdict.VerifiedLossless => "Genuine",
        AuthenticityVerdict.AcousticNaturalRollOff => "Genuine",
        AuthenticityVerdict.HybridMaster => "Info",
        AuthenticityVerdict.UpscaledFromCd => "Caution",
        AuthenticityVerdict.UpscaledHiRes => "Caution",
        AuthenticityVerdict.Dithered16BitUpscale => "Caution",
        AuthenticityVerdict.ZeroPadded24Bit => "Caution",
        AuthenticityVerdict.SuspectedUpscaledLossy => "Fake",
        AuthenticityVerdict.StandardLossy => "Neutral",
        _ => "Neutral"
    };

    /// <summary>
    /// Label ringkas hasil deteksi dalam bahasa lugas (ID/EN).
    /// </summary>
    public string VerdictBadge
    {
        get
        {
            bool en = IsEnglish;
            return Verdict switch
            {
                AuthenticityVerdict.VerifiedLossless => SourceSpec.IsHiRes
                    ? (en ? "Genuine Hi-Res Lossless" : "Hi-Res Lossless Asli")
                    : SourceSpec.SampleRateHz < 44_100
                        ? (en ? $"Genuine Lossless ({SourceSpec.SampleRateHz / 1000.0:F1} kHz)" : $"Lossless Asli ({SourceSpec.SampleRateHz / 1000.0:F1} kHz)")
                        : (en ? "Genuine Lossless (CD Quality)" : "Lossless Asli (Kualitas CD)"),
                AuthenticityVerdict.AcousticNaturalRollOff => en
                    ? "Genuine Lossless (Soft Acoustic)"
                    : "Lossless Asli (Instrumen Akustik Lembut)",
                AuthenticityVerdict.HybridMaster => en
                    ? "Genuine Hi-Res (Hybrid Studio Mix)"
                    : "Hi-Res Asli (Produksi Studio Campuran)",
                AuthenticityVerdict.UpscaledFromCd => en
                    ? "Upscaled from CD (44.1 kHz)"
                    : "Upscale dari Kualitas CD (44.1 kHz)",
                AuthenticityVerdict.UpscaledHiRes => en
                    ? "Upscaled Hi-Res (Lower Source Rate)"
                    : "Upscale Hi-Res (Rate Sumber Lebih Rendah)",
                AuthenticityVerdict.Dithered16BitUpscale => en
                    ? "Fake 24-Bit (16-Bit with Dither)"
                    : "24-Bit Palsu (Isi 16-Bit + Dither)",
                AuthenticityVerdict.ZeroPadded24Bit => en
                    ? "Fake 24-Bit (Zero-Padded 16-Bit)"
                    : "24-Bit Palsu (Isi Asli 16-Bit)",
                AuthenticityVerdict.SuspectedUpscaledLossy => en
                    ? "Fake Lossless (From MP3/Lossy)"
                    : "Lossless Palsu (Konversi MP3/Lossy)",
                AuthenticityVerdict.StandardLossy => en
                    ? "Compressed Lossy Format"
                    : "Format Kompresi Lossy",
                AuthenticityVerdict.Inconclusive => en
                    ? "Manual Check Needed"
                    : "Perlu Pemeriksaan Manual",
                _ => en ? "Not Analyzed" : "Belum Dianalisis"
            };
        }
    }

    /// <summary>
    /// Deskripsi kedalaman bit yang jelas untuk manusia.
    /// </summary>
    public string BitDepthDescription
    {
        get
        {
            bool en = IsEnglish;
            if (SourceSpec.IsDsd)
            {
                return en ? "1-Bit DSD Stream" : "Aliran DSD 1-Bit";
            }
            if (Verdict == AuthenticityVerdict.SuspectedUpscaledLossy)
            {
                int bits = (int)SourceSpec.BitDepth;
                return en ? $"{bits}-Bit Wrapper (Lossy Source)" : $"Wadah {bits}-Bit (Sumber Lossy)";
            }
            if (SourceSpec.BitDepth == BitDepth.Bit16)
            {
                return en ? "16-Bit Native (CD Standard)" : "16-Bit Asli (Standar CD)";
            }
            if (IsZeroPadded || Verdict == AuthenticityVerdict.ZeroPadded24Bit)
            {
                return en ? "Fake 24-Bit (16-Bit Zero-Padded)" : "24-Bit Palsu (Isi 16-Bit + Nol)";
            }
            if (IsDitherDetected || Verdict == AuthenticityVerdict.Dithered16BitUpscale)
            {
                return en ? "24-Bit from 16-Bit Dither" : "24-Bit dari Master 16-Bit";
            }
            if (SourceSpec.BitDepth == BitDepth.Bit32Float)
            {
                return en ? "32-Bit Floating-Point PCM" : "32-Bit Floating-Point PCM";
            }
            if (IsTrue24Bit)
            {
                return en ? "True 24-Bit Studio Depth" : "24-Bit Asli (Detail Penuh)";
            }
            return en ? "24-Bit (Low Lower-Bit Activity)" : "24-Bit (Aktivitas Bit Bawah Rendah)";
        }
    }

    /// <summary>
    /// Penjelasan hasil analisis dalam bahasa yang mudah dipahami (mengikuti pilihan bahasa UI ID/EN).
    /// </summary>
    public string LocalizedExplanation => IsEnglish ? BuildEnglishExplanation() : Explanation;

    // 4 Properti Kartu Ringkasan Spesifikasi Ramah Pengguna (2x2 Grid)
    public string SpecContainerValue
    {
        get
        {
            string chText = SourceSpec.Channels == 2 ? "Stereo" : $"{SourceSpec.Channels} ch";
            if (SourceSpec.BitrateKbps > 0)
            {
                return $"{SourceSpec.Codec} • {SourceSpec.BitrateKbps} kbps • {chText}";
            }
            return $"{SourceSpec.Codec} • {chText}";
        }
    }

    public string SpecContainerMeaning
    {
        get
        {
            bool en = IsEnglish;
            if (SourceSpec.IsDsd)
            {
                return en
                    ? "1-bit Delta-Sigma high-rate SACD bitstream."
                    : "Aliran 1-bit berkecepatan MHz standar SACD tanpa kuantisasi PCM.";
            }
            return SourceSpec.Codec switch
            {
                AudioCodec.FLAC => en
                    ? "Lossless compression: file size is smaller without discarding audio data."
                    : "Kompresi lossless: ukuran berkas ringkas tanpa membuang data suara.",
                AudioCodec.WAV or AudioCodec.AIFF => en
                    ? "Uncompressed PCM audio stored directly in studio master layout."
                    : "Audio PCM tanpa kompresi langsung dalam struktur studio.",
                AudioCodec.ALAC => en
                    ? "Apple Lossless container: preserves exact PCM audio bits."
                    : "Wadah Apple Lossless: menjaga seluruh data PCM tetap utuh.",
                _ => en
                    ? "Lossy perceptual codec: discards high frequencies and quiet details to save space."
                    : "Kompresi lossy: membuang sebagian detail suara agar ukuran berkas kecil."
            };
        }
    }

    public string SpecFrequencyValue
    {
        get
        {
            bool en = IsEnglish;
            if (SourceSpec.IsDsd)
            {
                double mhz = Math.Round(SourceSpec.SampleRateHz / 1_000_000.0, 2);
                return $"{mhz:F2} MHz (1-bit DSD)";
            }
            double srKhz = SourceSpec.SampleRateHz / 1000.0;
            double maxKhz = NyquistLimitHz / 1000.0;
            return en
                ? $"{srKhz:F1} kHz (Max {maxKhz:F2} kHz)"
                : $"{srKhz:F1} kHz (Maks {maxKhz:F2} kHz)";
        }
    }

    public string SpecFrequencyMeaning
    {
        get
        {
            bool en = IsEnglish;
            if (IsSyntheticSpectrum || SourceSpec.IsDsd)
            {
                return en
                    ? "1-bit DSD ultrasonic profile cannot be measured directly with standard PCM FFT."
                    : "Profil frekuensi DSD 1-bit tidak diukur langsung dengan FFT PCM standar.";
            }

            uint reachHz = UpperEnvelopeReachHz ?? DetectedCutoffHz;
            double reachKhz = reachHz / 1000.0;
            double nyqKhz = NyquistLimitHz / 1000.0;
            double cutKhz = DetectedCutoffHz / 1000.0;

            return Verdict switch
            {
                AuthenticityVerdict.AcousticNaturalRollOff => en
                    ? $"Harmonics reach ~{reachKhz:F1} kHz and taper smoothly with no artificial lowpass brickwall."
                    : $"Harmonik lagu mencapai ~{reachKhz:F1} kHz lalu melandai halus secara alami tanpa potongan filter.",
                AuthenticityVerdict.VerifiedLossless => en
                    ? $"Audio energy extends naturally up to ~{nyqKhz:F1} kHz across the full file bandwidth."
                    : $"Energi suara terisi penuh dan alami mendekati batas kapasitas file (~{nyqKhz:F1} kHz).",
                AuthenticityVerdict.HybridMaster => en
                    ? $"Contains both 44.1 kHz sample elements and genuine studio overtones above 35 kHz."
                    : $"Gabungan instrumen sample 44.1 kHz dan detail studio asli yang menembus di atas 35 kHz.",
                AuthenticityVerdict.UpscaledFromCd => en
                    ? $"File claims {SourceSpec.SampleRateHz / 1000.0:F1} kHz, but real audio stops at the 22.05 kHz CD limit."
                    : $"Wadah {SourceSpec.SampleRateHz / 1000.0:F1} kHz, tetapi isi suara berhenti di batas CD 22.05 kHz.",
                AuthenticityVerdict.UpscaledHiRes => en
                    ? $"File claims {SourceSpec.SampleRateHz / 1000.0:F1} kHz, but real audio stops at the ~{cutKhz:F1} kHz limit of a lower-rate master."
                    : $"Wadah {SourceSpec.SampleRateHz / 1000.0:F1} kHz, tetapi isi suara berhenti di batas ~{cutKhz:F1} kHz dari master ber-rate lebih rendah.",
                AuthenticityVerdict.SuspectedUpscaledLossy => en
                    ? $"Sharp frequency cutoff at ~{cutKhz:F1} kHz (typical MP3/AAC encoding limit)."
                    : $"Suara terpotong tajam di ~{cutKhz:F1} kHz (ciri khas hasil kompresi MP3/AAC).",
                AuthenticityVerdict.StandardLossy => en
                    ? $"Audible energy extends up to ~{cutKhz:F1} kHz depending on bitrate."
                    : $"Jangkauan suara tersisa hingga ~{cutKhz:F1} kHz sesuai bitrate kompresi.",
                _ => en
                    ? $"Measured spectral drop starts around ~{cutKhz:F1} kHz (upper reach ~{reachKhz:F1} kHz)."
                    : $"Penurunan energi terlihat di ~{cutKhz:F1} kHz (jangkauan puncak ~{reachKhz:F1} kHz)."
            };
        }
    }

    public string SpecBitDepthValue => BitDepthDescription;

    public string SpecBitDepthMeaning
    {
        get
        {
            bool en = IsEnglish;
            if (SourceSpec.IsDsd)
            {
                return en
                    ? "1-bit modulation stream; integer bit-depth audit does not apply."
                    : "Aliran modulasi 1-bit; tidak memakai lapisan bit PCM 16/24-bit.";
            }
            if (SourceSpec.BitDepth == BitDepth.Bit32Float)
            {
                return en
                    ? "32-bit floating-point format; integer LSB padding checks do not apply."
                    : "Format floating-point 32-bit; pemeriksaan bit integer tidak berlaku.";
            }
            if (Verdict == AuthenticityVerdict.SuspectedUpscaledLossy)
            {
                return en
                    ? "Decoded from a lossy source, so higher bit depth adds no original studio detail."
                    : "Berasal dari file lossy yang di-decode ulang; bit tambahan tidak menambah detail asli.";
            }
            if (IsZeroPadded || Verdict == AuthenticityVerdict.ZeroPadded24Bit)
            {
                return en
                    ? $"Lowest 8 bits are empty ({LsbActivityPercentage:F1}% active, {LsbEntropy:F2}/8 bit entropy); dynamic range is 16-bit."
                    : $"8 bit terbawah kosong ({LsbActivityPercentage:F1}% aktif, entropi {LsbEntropy:F2}/8 bit); kualitas dinamika setara 16-bit.";
            }
            if (IsDitherDetected && SourceSpec.BitDepth >= BitDepth.Bit24)
            {
                return en
                    ? "Lower bits contain 16-bit TPDF dither noise (~-131 dBFS floor) rather than 24-bit music signal."
                    : "Bit bawah hanya berisi derau dither 16-bit (~-131 dBFS), bukan sinyal rekaman 24-bit.";
            }
            if (SourceSpec.BitDepth == BitDepth.Bit16)
            {
                return en
                    ? $"16th bit is active {LsbActivityPercentage:F1}% (entropy {LsbEntropy:F2}/8 bit); standard ~96 dB CD dynamic range."
                    : $"Bit ke-16 aktif {LsbActivityPercentage:F1}% (entropi {LsbEntropy:F2}/8 bit), rentang dinamis standar CD (~96 dB).";
            }
            if (IsTrue24Bit)
            {
                return en
                    ? $"Lowest 24-bit byte is active {LsbActivityPercentage:F1}% (entropy {LsbEntropy:F2}/8 bit); up to ~144 dB theoretical range."
                    : $"Bit ke-24 aktif {LsbActivityPercentage:F1}% (entropi {LsbEntropy:F2}/8 bit), data audio studio utuh.";
            }
            return en
                ? $"Lower-bit activity {LsbActivityPercentage:F1}% (entropy {LsbEntropy:F2}/8 bit)."
                : $"Aktivitas bit terbawah {LsbActivityPercentage:F1}% (entropi {LsbEntropy:F2}/8 bit).";
        }
    }

    public string SpecOutputValue
    {
        get
        {
            bool en = IsEnglish;
            if (!HasActiveHardwarePath)
            {
                return en ? "Not Currently Playing" : "Lagu Sedang Tidak Diputar";
            }
            if (IsBitPerfect)
            {
                return en ? "Bit-Perfect Direct" : "Bit-Perfect Langsung ke DAC";
            }
            if (IsHardwareExclusive)
            {
                return en ? "Exclusive Mode (Format Adapted)" : "Exclusive (Format Disesuaikan)";
            }
            return en ? "Shared Mixer / Resampling" : "Lewat Mixer Windows / Resampling";
        }
    }

    public string SpecOutputMeaning
    {
        get
        {
            bool en = IsEnglish;
            string driverLabel = ConfiguredDriverType switch
            {
                AudioDriverType.WASAPI_Exclusive => "WASAPI Exclusive",
                AudioDriverType.WASAPI_Shared => "WASAPI Shared",
                _ => ConfiguredDriverType.ToString()
            };

            if (!HasActiveHardwarePath)
            {
                return en
                    ? $"Configured driver: {driverLabel}. Play this track to lock the active output format."
                    : $"Mode driver terpilih: {driverLabel}. Putar lagu ini untuk melihat jalur perangkat aktif.";
            }

            string hwFmt = $"{(int)HardwareSpec.BitDepth}-bit / {HardwareSpec.SampleRateHz / 1000.0:F1} kHz";
            if (IsBitPerfect)
            {
                return en
                    ? $"Locked {hwFmt} directly to hardware, bypassing the Windows audio mixer."
                    : $"Terkunci {hwFmt} langsung ke DAC tanpa melalui mixer atau resampling Windows.";
            }

            if (IsHardwareExclusive)
            {
                return en
                    ? $"Direct hardware output at {hwFmt} (adapted from {SourceSpec.SampleRateHz / 1000.0:F1} kHz source)."
                    : $"Jalur eksklusif di {hwFmt} (menyesuaikan kemampuan perangkat dari sumber {SourceSpec.SampleRateHz / 1000.0:F1} kHz).";
            }

            return en
                ? $"Audio passes through the Windows mixer at {hwFmt} (shared mode)."
                : $"Suara melewati mixer Windows di {hwFmt} karena mode Shared atau eksklusif ditolak driver.";
        }
    }

    private string BuildEnglishExplanation()
    {
        if (SourceSpec.IsDsd)
        {
            return "1-bit DSD stream cannot be verified directly from PCM spectral bins without multi-stage decimation.";
        }

        const string aacOpusCaveat = " Note: high-bitrate AAC/Opus without a hard lowpass cannot be distinguished by spectral analysis alone.";
        uint reachHz = UpperEnvelopeReachHz ?? DetectedCutoffHz;
        double dropVal = DropAcrossKneeDb ?? 0.0;
        double cliffVal = CliffDropDb ?? 0.0;

        return Verdict switch
        {
            AuthenticityVerdict.VerifiedLossless =>
                $"No lossy cutoff or CD upsampling artifacts detected for {SourceSpec.FormattedBadge}; measured energy extends up to ~{NyquistLimitHz / 1000.0:F1} kHz.{aacOpusCaveat}",
            AuthenticityVerdict.AcousticNaturalRollOff =>
                $"No artificial brickwall filter artifacts detected: harmonics taper smoothly (reach ~{reachHz / 1000.0:F1} kHz), consistent with soft acoustic material.{aacOpusCaveat}",
            AuthenticityVerdict.HybridMaster =>
                $"Spectrum is consistent with a hybrid studio production: 44.1 kHz band-limited elements combined with active ultrasonic energy above 35 kHz.{aacOpusCaveat}",
            AuthenticityVerdict.UpscaledFromCd =>
                (dropVal < 10.0 && cliffVal < 15.0)
                    ? $"Likely consistent with upsampling from a 44.1 kHz CD master: container is {SourceSpec.SampleRateHz / 1000.0:F1} kHz, with a transition near 22.05 kHz (drop {dropVal:F1} dB)."
                    : $"Consistent with upsampling from a 44.1 kHz CD master: container is {SourceSpec.SampleRateHz / 1000.0:F1} kHz, but measured energy drops sharply near 22.05 kHz.",
            AuthenticityVerdict.UpscaledHiRes =>
                $"Consistent with upsampling from a ~{DetectedCutoffHz * 2 / 1000.0:F1} kHz master: container is {SourceSpec.SampleRateHz / 1000.0:F1} kHz, but measured energy drops sharply near the {DetectedCutoffHz / 1000.0:F1} kHz Nyquist boundary with a silent upper band.",
            AuthenticityVerdict.Dithered16BitUpscale =>
                $"Consistent with an export from a 16-bit TPDF-dithered master: container is 24-bit, while upper bands show a flat 16-bit dither floor ({UltrasonicNoiseFloorDbFS:F1} dBFS).",
            AuthenticityVerdict.ZeroPadded24Bit =>
                $"Consistent with a zero-padded 16-bit source in a 24-bit container: lowest 8 bits are empty ({LsbActivityPercentage:F1}% active, {LsbEntropy:F2}/8 bit entropy).",
            AuthenticityVerdict.SuspectedUpscaledLossy =>
                $"Consistent with a lossy transcode (MP3/AAC) or bandwidth-limited source on {SourceSpec.SampleRateHz / 1000.0:F1} kHz container: sharp frequency cutoff detected at ~{DetectedCutoffHz / 1000.0:F1} kHz.",
            AuthenticityVerdict.StandardLossy =>
                $"Standard lossy file ({SourceSpec.Codec}). Measured high-frequency limit is ~{DetectedCutoffHz / 1000.0:F1} kHz from perceptual compression.",
            AuthenticityVerdict.Inconclusive =>
                $"Inconclusive result on {SourceSpec.SampleRateHz / 1000.0:F1} kHz file: spectral transition around ~{DetectedCutoffHz / 1000.0:F1} kHz or potential resampler imaging requires manual inspection.",
            _ => Explanation
        };
    }

    public double[]? SpectrumDbBins { get; init; }

    public double BinResolutionHz => SpectrumDbBins != null && SpectrumDbBins.Length > 0
        ? (double)SourceSpec.SampleRateHz / (SpectrumDbBins.Length * 2)
        : 0.0;

    public AudioIntegrityReport(
        AudioStreamSpec sourceSpec,
        AudioStreamSpec hardwareSpec,
        AuthenticityVerdict verdict,
        uint detectedCutoffHz,
        string explanation,
        bool isTrue24Bit = true,
        double lsbActivityPercentage = 100.0,
        double[]? spectrumDbBins = null,
        double? spectralSlopeDbPerOctave = -12.0,
        double? ultrasonicNoiseFloorDbFS = -120.0,
        bool isDitherDetected = false,
        double lsbEntropy = 8.0,
        TimeSpan? analysisTimestamp = null,
        double analysisPositionPercentage = 40.0,
        double? powerAt22kDbFS = -120.0,
        double? powerAtNyquistDbFS = -120.0,
        double? ultrasonicNoiseStdDevDb = 0.0,
        bool isZeroPadded = false,
        bool isSyntheticSpectrum = false,
        uint? kneeHz = null,
        double? kneeSteepnessDbPerOct = null,
        double? dropAcrossKneeDb = null,
        double? relP16kDb = null,
        double? relP20kDb = null,
        double? imagingCorrelation = null,
        double? imagingAttenuationDb = null,
        double? imagingUpperMeanDb = null,
        uint? cliffHz = null,
        double? cliffDropDb = null,
        bool hasActiveHardwarePath = false,
        bool isHardwareExclusive = true,
        AudioDriverType configuredDriverType = AudioDriverType.WASAPI_Exclusive,
        uint? upperEnvelopeReachHz = null)
    {
        SourceSpec = sourceSpec ?? throw new ArgumentNullException(nameof(sourceSpec));
        HardwareSpec = hardwareSpec ?? throw new ArgumentNullException(nameof(hardwareSpec));
        Verdict = verdict;
        DetectedCutoffHz = detectedCutoffHz;
        Explanation = explanation ?? string.Empty;
        IsTrue24Bit = isTrue24Bit;
        LsbActivityPercentage = lsbActivityPercentage;
        SpectrumDbBins = spectrumDbBins;
        SpectralSlopeDbPerOctave = spectralSlopeDbPerOctave;
        UltrasonicNoiseFloorDbFS = ultrasonicNoiseFloorDbFS;
        IsDitherDetected = isDitherDetected;
        LsbEntropy = lsbEntropy;
        AnalysisTimestamp = analysisTimestamp ?? TimeSpan.Zero;
        AnalysisPositionPercentage = analysisPositionPercentage;
        PowerAt22kDbFS = powerAt22kDbFS;
        PowerAtNyquistDbFS = powerAtNyquistDbFS;
        UltrasonicNoiseStdDevDb = ultrasonicNoiseStdDevDb;
        IsZeroPadded = isZeroPadded;
        IsSyntheticSpectrum = isSyntheticSpectrum;
        KneeHz = kneeHz;
        KneeSteepnessDbPerOct = kneeSteepnessDbPerOct;
        DropAcrossKneeDb = dropAcrossKneeDb;
        RelP16kDb = relP16kDb;
        RelP20kDb = relP20kDb;
        ImagingCorrelation = imagingCorrelation;
        ImagingAttenuationDb = imagingAttenuationDb;
        ImagingUpperMeanDb = imagingUpperMeanDb;
        CliffHz = cliffHz;
        CliffDropDb = cliffDropDb;
        HasActiveHardwarePath = hasActiveHardwarePath;
        IsHardwareExclusive = isHardwareExclusive;
        ConfiguredDriverType = configuredDriverType;
        UpperEnvelopeReachHz = upperEnvelopeReachHz;
    }
}
