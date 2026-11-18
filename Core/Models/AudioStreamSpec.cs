namespace AuraAudio.Core.Models;

/// <summary>
/// Spesifikasi teknis aliran audio (Audio Stream Specification).
/// Merupakan representasi fisik mutlak dari data biner audio.
/// Didesain immutable (record) untuk menjamin thread-safety saat dikirim antar-thread.
/// </summary>
public record AudioStreamSpec
{
    public AudioCodec Codec { get; init; }
    public uint SampleRateHz { get; init; }
    public BitDepth BitDepth { get; init; }
    public ushort Channels { get; init; }
    public uint BitrateKbps { get; init; }

    /// <summary>
    /// Menentukan apakah format ini tergolong ke dalam kategori High-Resolution Audio
    /// (Sample rate > 44.1/48 kHz atau Bit depth > 16-bit, atau format DSD).
    /// </summary>
    public bool IsHiRes => SampleRateHz > 48_000 || BitDepth > BitDepth.Bit16 || IsDsd;

    /// <summary>
    /// Menentukan apakah audio berformat 1-bit Direct Stream Digital.
    /// </summary>
    public bool IsDsd => Codec is AudioCodec.DSD_DSF or AudioCodec.DSD_DFF;

    /// <summary>
    /// Menghasilkan badge teks keterbacaan tinggi yang mirip dengan Apple Music & Spotify.
    /// Contoh: "24-bit / 96.0 kHz FLAC" atau "DSD64 2.82 MHz 1-bit"
    /// </summary>
    public string FormattedBadge
    {
        get
        {
            if (IsDsd)
            {
                double mhz = Math.Round(SampleRateHz / 1_000_000.0, 2);
                string dsdName = SampleRateHz switch
                {
                    >= 11_289_600 => "DSD256",
                    >= 5_644_800  => "DSD128",
                    _             => "DSD64"
                };
                return $"{dsdName} {mhz:F2} MHz 1-bit";
            }

            double khz = Math.Round(SampleRateHz / 1000.0, 1);
            int bits = (int)BitDepth;
            return $"{bits}-bit / {khz:F1} kHz {Codec}";
        }
    }

    /// <summary>
    /// Konstruktor standar untuk membuat spesifikasi audio lengkap.
    /// </summary>
    public AudioStreamSpec(AudioCodec codec, uint sampleRateHz, BitDepth bitDepth, ushort channels, uint bitrateKbps)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sampleRateHz, nameof(sampleRateHz));
        ArgumentOutOfRangeException.ThrowIfZero(channels, nameof(channels));

        Codec = codec;
        SampleRateHz = sampleRateHz;
        BitDepth = bitDepth;
        Channels = channels;
        BitrateKbps = bitrateKbps;
    }

    /// <summary>
    /// Factory helper untuk format standar CD Red Book (16-bit / 44.1 kHz PCM).
    /// </summary>
    public static AudioStreamSpec StandardCd(AudioCodec codec = AudioCodec.FLAC) =>
        new(codec, 44_100, BitDepth.Bit16, 2, 1411);

    /// <summary>
    /// Factory helper untuk format Studio Master Hi-Res (24-bit / 96.0 kHz PCM).
    /// </summary>
    public static AudioStreamSpec StudioMaster96k(AudioCodec codec = AudioCodec.FLAC) =>
        new(codec, 96_000, BitDepth.Bit24, 2, 4608);

    /// <summary>
    /// Factory helper untuk format DSD64 murni (SACD 1-bit / 2.8224 MHz).
    /// </summary>
    public static AudioStreamSpec Dsd64(AudioCodec codec = AudioCodec.DSD_DSF) =>
        new(codec, 2_822_400, BitDepth.DSD1Bit, 2, 5645);
}
