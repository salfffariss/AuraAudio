using System;
using System.Globalization;

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
    /// Menentukan apakah codec ini merupakan format lossless PCM atau DSD murni.
    /// </summary>
    public bool IsLossless => Codec is AudioCodec.FLAC
        or AudioCodec.ALAC
        or AudioCodec.WAV
        or AudioCodec.AIFF
        or AudioCodec.DSD_DSF
        or AudioCodec.DSD_DFF;

    /// <summary>
    /// Menentukan apakah format ini tergolong ke dalam kategori High-Resolution Lossless Audio
    /// (Lossless dengan Sample rate > 48 kHz atau Bit depth > 16-bit, atau format DSD).
    /// </summary>
    public bool IsHiRes => IsLossless && (SampleRateHz > 48_000 || BitDepth > BitDepth.Bit16 || IsDsd);

    /// <summary>
    /// Menentukan apakah audio berformat 1-bit Direct Stream Digital.
    /// </summary>
    public bool IsDsd => Codec is AudioCodec.DSD_DSF or AudioCodec.DSD_DFF;

    /// <summary>
    /// Formats a PCM sample rate consistently across all UI surfaces using invariant audio notation
    /// (e.g., "44.1 kHz", "48.0 kHz", "96.0 kHz").
    /// </summary>
    public static string FormatSampleRateKhz(uint sampleRateHz) =>
        $"{(sampleRateHz / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)} kHz";

    /// <summary>
    /// Formats a bitrate consistently across all UI surfaces with invariant thousands separators
    /// (e.g., "2,004 kbps", "320 kbps", or "Variable").
    /// </summary>
    public static string FormatBitrateKbps(uint bitrateKbps) =>
        bitrateKbps > 0
            ? $"{bitrateKbps.ToString("N0", CultureInfo.InvariantCulture)} kbps"
            : "Variable";

    /// <summary>
    /// Formats a channel count label consistently across Info Sheet, Signal Chain, and Inspector.
    /// </summary>
    public static string FormatChannelLabel(ushort channels, bool verbose = false) => channels switch
    {
        1 => verbose ? "1 Ch (Mono)" : "Mono",
        2 => verbose ? "2 Ch (Stereo)" : "Stereo",
        6 => verbose ? "5.1 Ch (Surround)" : "5.1 Surround",
        8 => verbose ? "7.1 Ch (Surround)" : "7.1 Surround",
        _ => $"{channels} Ch"
    };

    /// <summary>
    /// Canonical bit-depth and sample-rate text (e.g., "24-bit / 48.0 kHz" or "DSD64 2.82 MHz 1-bit").
    /// </summary>
    public string FormattedResolution
    {
        get
        {
            if (IsDsd)
            {
                string mhz = (SampleRateHz / 1_000_000.0).ToString("0.00", CultureInfo.InvariantCulture);
                string dsdName = SampleRateHz switch
                {
                    >= 11_289_600 => "DSD256",
                    >= 5_644_800  => "DSD128",
                    _             => "DSD64"
                };
                return $"{dsdName} {mhz} MHz 1-bit";
            }

            return $"{(int)BitDepth}-bit / {FormatSampleRateKhz(SampleRateHz)}";
        }
    }

    /// <summary>
    /// Menghasilkan badge teks keterbacaan tinggi yang konsisten di seluruh UI.
    /// Contoh: "24-bit / 96.0 kHz FLAC" atau "DSD64 2.82 MHz 1-bit"
    /// </summary>
    public string FormattedBadge => IsDsd ? FormattedResolution : $"{FormattedResolution} {Codec}";

    /// <summary>
    /// Formatted actual average bitrate of the file (e.g., "2,004 kbps").
    /// </summary>
    public string FormattedBitrate => FormatBitrateKbps(BitrateKbps);

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
