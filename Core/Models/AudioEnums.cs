namespace AuraAudio.Core.Models;

/// <summary>
/// Format kompresi dan kontainer audio yang didukung oleh AuraAudio.
/// Mencakup format Lossless standar studio, DSD murni, dan lossy umum.
/// </summary>
public enum AudioCodec
{
    // Lossless PCM High-Resolution Formats
    FLAC,
    WAV,
    ALAC,
    AIFF,

    // 1-Bit Direct Stream Digital (Audiophile SACD Formats)
    DSD_DSF,
    DSD_DFF,

    // Lossy Formats (untuk kompatibilitas)
    MP3,
    AAC,
    OGG_VORBIS,
    OPUS
}

/// <summary>
/// Kedalaman bit (Bit Depth) per sampel audio.
/// Menentukan rentang dinamis (Dynamic Range) dan tingkat detail kuantisasi.
/// </summary>
public enum BitDepth
{
    /// <summary>Kualitas standar CD audio (16-bit PCM, rentang dinamis ~96 dB)</summary>
    Bit16 = 16,

    /// <summary>Kualitas studio master (24-bit PCM, rentang dinamis ~144 dB)</summary>
    Bit24 = 24,

    /// <summary>Kualitas pemrosesan audio floating-point (32-bit float)</summary>
    Bit32Float = 32,

    /// <summary>Kualitas 1-bit Delta-Sigma murni pada frekuensi mega-hertz (DSD)</summary>
    DSD1Bit = 1
}

/// <summary>
/// Status operasional mesin audio saat ini.
/// </summary>
public enum PlaybackStatus
{
    Stopped,
    Playing,
    Paused,
    Buffering
}

/// <summary>
/// Tipe driver audio yang digunakan untuk berkomunikasi dengan soundcard/DAC.
/// </summary>
public enum AudioDriverType
{
    /// <summary>Mode Bit-Perfect eksklusif Windows (Bypass mixer OS)</summary>
    WASAPI_Exclusive,

    /// <summary>Mode standar bersama Windows (Melewati Audio Engine mixer OS)</summary>
    WASAPI_Shared,

    /// <summary>Audio Stream Input/Output driver untuk latency rendah dan DSD native</summary>
    ASIO
}

/// <summary>
/// Mode pemutaran untuk berkas 1-bit Direct Stream Digital (DSD / SACD).
/// </summary>
public enum DsdPlaybackMode
{
    /// <summary>
    /// Konversi desimasi FIR presisi tinggi ke 24-bit / 88.2 kHz PCM.
    /// Memungkinkan file DSD diputar di semua soundcard, headphone, dan speaker laptop dengan kejernihan maksimal.
    /// </summary>
    PcmDecimation,

    /// <summary>
    /// Transmisi bit-perfect DoP (DSD over PCM) v1.1 pada 176.4 kHz ke DAC audiophile eksternal.
    /// </summary>
    DoP
}

/// <summary>
/// Mode perulangan antrean pemutaran lagu (Repeat Mode).
/// </summary>
public enum PlaybackRepeatMode
{
    /// <summary>Tidak mengulang lagu (berhenti saat antrean selesai)</summary>
    Off,

    /// <summary>Mengulang seluruh antrean playlist dari awal</summary>
    RepeatAll,

    /// <summary>Mengulang satu lagu yang sama terus menerus</summary>
    RepeatOne
}
