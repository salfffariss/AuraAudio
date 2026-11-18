namespace AuraAudio.Core.Models;

/// <summary>
/// Kualitas fidelitas suara pada rantai pemrosesan audio (Signal Path).
/// Mengadopsi standar Roon / UAPP untuk transparansi kualitas absolut ke pengguna.
/// </summary>
public enum SignalPathQuality
{
    /// <summary>
    /// Kualitas Tertinggi: Bit audio 100% identik dengan file asal, 
    /// tidak ada perubahan volume software, tidak ada resampling, mode eksklusif aktif.
    /// </summary>
    BitPerfect,

    /// <summary>
    /// Kualitas Tinggi Terkendali: Ada pemrosesan matematis presisi tinggi 64-bit float
    /// yang disengaja (misal: konversi DSD-to-PCM FIR Filter atau Parametric EQ).
    /// </summary>
    Enhanced,

    /// <summary>
    /// Kualitas Terdegradasi: Audio dipaksa resample oleh Windows Audio Engine mixer,
    /// atau terjadi pembulatan bit depth (misal dari 24-bit dipaksa ke 16-bit).
    /// </summary>
    ResampledOrDegraded
}

/// <summary>
/// Jenis simpul tahapan pemrosesan audio dari file ke telinga pengguna.
/// </summary>
public enum SignalStepType
{
    SourceFile,
    Decoder,
    DspProcessing,
    VolumeControl,
    OperatingSystemMixer,
    HardwareEndpoint
}

/// <summary>
/// Satu simpul langkah di dalam rantai pemrosesan sinyal audio.
/// </summary>
public record SignalStep
{
    public SignalStepType StepType { get; init; }
    public string Name { get; init; }
    public string Description { get; init; }
    public SignalPathQuality Quality { get; init; }

    public SignalStep(SignalStepType stepType, string name, string description, SignalPathQuality quality)
    {
        StepType = stepType;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Quality = quality;
    }
}

/// <summary>
/// Ringkasan keseluruhan rantai pemrosesan audio saat ini.
/// Ditampilkan saat pengguna mengeklik "Pill Indikator Bit-Perfect" di Player Bar.
/// </summary>
public record SignalPathSummary
{
    public IReadOnlyList<SignalStep> Steps { get; init; }
    public SignalPathQuality OverallQuality { get; init; }
    public string ActiveDeviceName { get; init; }

    public SignalPathSummary(IEnumerable<SignalStep> steps, SignalPathQuality overallQuality, string activeDeviceName)
    {
        Steps = steps?.ToList() ?? new List<SignalStep>();
        OverallQuality = overallQuality;
        ActiveDeviceName = activeDeviceName ?? "Unknown Device";
    }

    /// <summary>
    /// Label badge teks yang akan muncul di Player Bar (ala Spotify & Apple Music).
    /// </summary>
    public string BadgeText => OverallQuality switch
    {
        SignalPathQuality.BitPerfect           => "BIT-PERFECT DIRECT",
        SignalPathQuality.Enhanced             => "ENHANCED (DSP/FIR)",
        SignalPathQuality.ResampledOrDegraded  => "RESAMPLED (OS MIXER)",
        _                                      => "UNKNOWN"
    };

    /// <summary>
    /// Kode warna UI untuk merepresentasikan kualitas fidelitas.
    /// Gold/Ungu = BitPerfect, Hijau/Biru = Enhanced, Oranye = Resampled.
    /// </summary>
    public string BadgeColorHex => OverallQuality switch
    {
        SignalPathQuality.BitPerfect           => "#E5A93C", // Gold Audiophile
        SignalPathQuality.Enhanced             => "#3B82F6", // Royal Blue
        SignalPathQuality.ResampledOrDegraded  => "#F97316", // Warning Orange
        _                                      => "#9CA3AF"  // Neutral Gray
    };
}
