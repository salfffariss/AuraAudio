namespace AuraAudio.Core.Models;

/// <summary>
/// Representasi perangkat keras audio (Soundcard / USB DAC eksternal) yang terdeteksi di Windows.
/// Digunakan oleh engine audio untuk menegosiasikan mode Bit-Perfect WASAPI Exclusive.
/// </summary>
public record AudioDevice
{
    /// <summary>ID unik endpoint audio di Windows (MMDevice ID)</summary>
    public string Id { get; init; }

    /// <summary>Nama ramah perangkat (contoh: "FiiO K7 DAC" atau "Realtek(R) Audio")</summary>
    public string Name { get; init; }

    /// <summary>Tipe driver komunikasi audio</summary>
    public AudioDriverType DriverType { get; init; }

    /// <summary>Daftar frekuensi sampling native yang didukung oleh chip DAC</summary>
    public IReadOnlyList<uint> SupportedSampleRates { get; init; }

    /// <summary>Kedalaman bit maksimal yang didukung hardware</summary>
    public BitDepth MaxBitDepth { get; init; }

    /// <summary>Menandakan apakah perangkat ini adalah output default Windows</summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Menentukan apakah perangkat terdeteksi sebagai DAC eksternal audiophile (USB/DAC/Hi-Fi).
    /// </summary>
    public bool IsDedicatedDac =>
        Name.Contains("DAC", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("USB Audio", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("FiiO", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("iFi", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Topping", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("Chord", StringComparison.OrdinalIgnoreCase);

    public AudioDevice(
        string id,
        string name,
        AudioDriverType driverType,
        IEnumerable<uint> supportedSampleRates,
        BitDepth maxBitDepth,
        bool isDefault = false)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        DriverType = driverType;
        SupportedSampleRates = supportedSampleRates?.ToList() ?? new List<uint>();
        MaxBitDepth = maxBitDepth;
        IsDefault = isDefault;
    }

    /// <summary>
    /// Memeriksa apakah perangkat ini mampu memutar file audio tanpa memerlukan resampling sama sekali.
    /// </summary>
    public bool CanPlayNative(uint sampleRateHz) =>
        SupportedSampleRates.Contains(sampleRateHz);
}
