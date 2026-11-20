using System;
using System.IO;
using AuraAudio.Audio.Dsd;
using AuraAudio.Core.Models;
using AuraAudio.Metadata;
using NAudio.Flac;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Pabrik decoder audio otomatis (Audio Decoder Factory).
/// Menganalisis ekstensi dan header file musik untuk memilih decoder lossless / DSD / lossy yang paling optimal.
/// </summary>
public static class AudioDecoderFactory
{
    /// <summary>
    /// Membuka file audio dari harddisk dan mengembalikan aliran IAudioStream bit-exact yang siap diputar.
    /// Mendukung PCM Lossless (FLAC, WAV, ALAC, AIFF), DSD murni (.dsf via DoP atau FIR Decimation), dan lossy (MP3).
    /// </summary>
    public static IAudioStream OpenStream(string filePath, DsdPlaybackMode dsdMode = DsdPlaybackMode.PcmDecimation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File audio tidak ditemukan di harddisk.", filePath);
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        // Rute khusus 1-bit DSD (Sony DSF)
        if (ext == ".dsf")
        {
            var dsfReader = new DsfReader(filePath);
            return dsdMode switch
            {
                DsdPlaybackMode.DoP => new DopStreamProvider(dsfReader),
                _                   => new DsdToPcmStreamProvider(dsfReader)
            };
        }

        WaveStream underlyingStream;
        AudioCodec codec;

        switch (ext)
        {
            case ".flac":
                codec = AudioCodec.FLAC;
                underlyingStream = new FlacReader(filePath);
                break;

            case ".wav":
                codec = AudioCodec.WAV;
                underlyingStream = new WaveFileReader(filePath);
                break;

            case ".mp3":
                codec = AudioCodec.MP3;
                underlyingStream = new MediaFoundationReader(filePath);
                break;

            case ".m4a" or ".mp4":
                codec = ProbeCodecWithTagLib(ext, filePath);
                underlyingStream = new MediaFoundationReader(filePath);
                break;

            case ".alac":
                codec = AudioCodec.ALAC;
                underlyingStream = new MediaFoundationReader(filePath);
                break;

            case ".aac":
                codec = AudioCodec.AAC;
                underlyingStream = new MediaFoundationReader(filePath);
                break;

            case ".ogg":
                codec = AudioCodec.OGG_VORBIS;
                underlyingStream = new MediaFoundationReader(filePath);
                break;

            case ".opus":
                codec = AudioCodec.OPUS;
                underlyingStream = new MediaFoundationReader(filePath);
                break;

            case ".aiff" or ".aif":
                codec = AudioCodec.AIFF;
                try
                {
                    underlyingStream = new AiffFileReader(filePath);
                }
                catch
                {
                    underlyingStream = new MediaFoundationReader(filePath);
                }
                break;

            default:
                // Fallback menggunakan Windows Media Foundation dan identifikasi header codec nyata
                codec = ProbeCodecWithTagLib(ext, filePath);
                underlyingStream = new MediaFoundationReader(filePath);
                break;
        }

        return new UniversalAudioStream(filePath, underlyingStream, codec);
    }

    private static AudioCodec ProbeCodecWithTagLib(string extension, string filePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            return AudioMetadataReader.DetectCodec(extension, tagFile.Properties);
        }
        catch
        {
            return AudioMetadataReader.DetectCodec(extension, null);
        }
    }
}
