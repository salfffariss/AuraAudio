using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using AuraAudio.Audio.Flac;
using AuraAudio.Core.Models;
using AuraAudio.Metadata;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Single source of truth for resolving an audio file's physical source format
/// (codec, bit depth, sample rate, channel count, duration, and actual file average bitrate).
/// Used identically by library scanning, the Get Info sheet, decoders, the Signal Path auditor,
/// and the Authenticity Inspector so no two UI surfaces can disagree on source specs.
/// </summary>
public static class AudioSourceFormatResolver
{
    public readonly record struct ResolvedSourceFormat(
        AudioStreamSpec Spec,
        TimeSpan Duration,
        int SourceBitsPerSample);

    public readonly record struct Mp4AudioConfig(
        AudioCodec Codec,
        uint SampleRateHz,
        int BitsPerSample,
        ushort Channels);

    private const int MaxMp4RecursionDepth = 12;
    private const int MaxMp4BoxesVisited = 4096;

    // MPEG-4 AudioSpecificConfig sampling frequency table (ISO/IEC 14496-3 Table 1.18)
    private static readonly uint[] AacSamplingFrequencies =
    [
        96_000, 88_200, 64_000, 48_000, 44_100, 32_000,
        24_000, 22_050, 16_000, 12_000, 11_025, 8_000, 7_350
    ];

    /// <summary>
    /// Resolves authoritative source format metadata from the file's real codec configuration
    /// (ALACSpecificConfig, MP4 esds, FLAC STREAMINFO, DSF fmt chunk, decoder WaveFormat, or TagLib)
    /// and computes consistent actual file average bitrate (file size / duration).
    /// </summary>
    public static ResolvedSourceFormat ResolveFromFile(
        string filePath,
        TagLib.Properties? tagProperties = null,
        WaveFormat? decoderWaveFormat = null,
        TimeSpan? decoderDuration = null,
        AudioCodec? knownCodec = null,
        int? decoderSourceBitsPerSample = null)
    {
        string ext = string.IsNullOrEmpty(filePath) ? string.Empty : Path.GetExtension(filePath).ToLowerInvariant();
        long fileSizeBytes = 0;
        bool fileExists = !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);
        if (fileExists)
        {
            try { fileSizeBytes = new FileInfo(filePath).Length; } catch { fileSizeBytes = 0; }
        }

        Mp4AudioConfig? mp4Config = null;
        if (fileExists && ext is ".m4a" or ".mp4" or ".alac")
        {
            if (TryParseMp4AudioConfig(filePath, out var parsed))
            {
                mp4Config = parsed;
            }
        }

        AudioCodec codec = knownCodec
            ?? mp4Config?.Codec
            ?? AudioMetadataReader.DetectCodec(ext, tagProperties);

        // If MP4 container explicitly carries an ALAC or AAC audio sample entry, trust the container box.
        if (mp4Config.HasValue && ext is ".m4a" or ".mp4")
        {
            codec = mp4Config.Value.Codec;
        }

        uint sampleRateHz = 0;
        ushort channels = 0;
        int rawBits = 0;
        TimeSpan duration = decoderDuration.HasValue && decoderDuration.Value > TimeSpan.Zero
            ? decoderDuration.Value
            : (tagProperties != null && tagProperties.Duration > TimeSpan.Zero ? tagProperties.Duration : TimeSpan.Zero);

        // Align MP3 duration with TagLib# header duration when called from a decoder stream so
        // Library/InfoSheet and SignalChain/Inspector report the exact same file average bitrate
        // even on very short MP3 files where Xing/encoder-delay frames differ by ~25 ms.
        if (codec == AudioCodec.MP3 && tagProperties == null && fileExists)
        {
            try
            {
                using var mp3Tag = TagLib.File.Create(filePath);
                if (mp3Tag.Properties != null && mp3Tag.Properties.Duration > TimeSpan.Zero)
                {
                    duration = mp3Tag.Properties.Duration;
                }
            }
            catch { }
        }

        if (codec == AudioCodec.FLAC)
        {
            if (decoderWaveFormat != null)
            {
                sampleRateHz = (uint)Math.Max(0, decoderWaveFormat.SampleRate);
                channels = (ushort)Math.Max(0, decoderWaveFormat.Channels);
                rawBits = decoderSourceBitsPerSample ?? decoderWaveFormat.BitsPerSample;
            }
            else if (fileExists && SafeFlacDecoder.TryReadStreamInfo(filePath, out var flacInfo))
            {
                sampleRateHz = (uint)flacInfo.SampleRate;
                channels = (ushort)flacInfo.ChannelCount;
                rawBits = flacInfo.BitsPerSample;
                if (duration <= TimeSpan.Zero && flacInfo.TotalSamples > 0 && flacInfo.SampleRate > 0)
                {
                    duration = TimeSpan.FromSeconds((double)flacInfo.TotalSamples / flacInfo.SampleRate);
                }
            }
        }
        else if (codec == AudioCodec.ALAC)
        {
            // Never trust TagLib's generic ISO AudioSampleEntry.samplesize (often 16 for 24-bit ALAC)
            // or 16-bit samplerate (capped at 48 kHz for 96/192 kHz ALAC). Prefer ALACSpecificConfig,
            // then real decoder WaveFormat.
            if (mp4Config.HasValue && mp4Config.Value.Codec == AudioCodec.ALAC)
            {
                sampleRateHz = mp4Config.Value.SampleRateHz;
                channels = mp4Config.Value.Channels;
                rawBits = mp4Config.Value.BitsPerSample;
            }
            if (decoderWaveFormat != null)
            {
                if (sampleRateHz == 0 && decoderWaveFormat.SampleRate > 0) sampleRateHz = (uint)decoderWaveFormat.SampleRate;
                if (channels == 0 && decoderWaveFormat.Channels > 0) channels = (ushort)decoderWaveFormat.Channels;
                if (rawBits == 0 && decoderWaveFormat.BitsPerSample > 0) rawBits = decoderWaveFormat.BitsPerSample;
            }
        }
        else if (codec == AudioCodec.DSD_DSF)
        {
            rawBits = 1;
            if (fileExists && TryReadDsfHeader(filePath, out uint dsfSr, out ushort dsfCh, out TimeSpan dsfDur))
            {
                sampleRateHz = dsfSr;
                channels = dsfCh;
                if (duration <= TimeSpan.Zero && dsfDur > TimeSpan.Zero)
                {
                    duration = dsfDur;
                }
            }
        }
        else if (codec == AudioCodec.DSD_DFF)
        {
            rawBits = 1;
        }
        else
        {
            // WAV, AIFF, MP3, AAC, OGG_VORBIS, OPUS:
            // Prefer MP4 esds config for high-SR AAC (>48 kHz), then real decoder WaveFormat.
            if (mp4Config.HasValue)
            {
                sampleRateHz = mp4Config.Value.SampleRateHz;
                channels = mp4Config.Value.Channels;
                rawBits = mp4Config.Value.BitsPerSample;
            }
            if (decoderWaveFormat != null)
            {
                if (sampleRateHz == 0 && decoderWaveFormat.SampleRate > 0) sampleRateHz = (uint)decoderWaveFormat.SampleRate;
                if (channels == 0 && decoderWaveFormat.Channels > 0) channels = (ushort)decoderWaveFormat.Channels;
                if (rawBits == 0 && decoderWaveFormat.BitsPerSample > 0) rawBits = decoderWaveFormat.BitsPerSample;
            }
        }

        // Fill any remaining missing fields from TagLib without clamping valid low sample rates or mono channels.
        if (tagProperties != null)
        {
            if (sampleRateHz == 0 && tagProperties.AudioSampleRate > 0)
            {
                sampleRateHz = (uint)tagProperties.AudioSampleRate;
            }
            if (channels == 0 && tagProperties.AudioChannels > 0)
            {
                channels = (ushort)tagProperties.AudioChannels;
            }
            if (rawBits == 0 && tagProperties.BitsPerSample > 0)
            {
                rawBits = tagProperties.BitsPerSample;
            }
        }

        if (sampleRateHz == 0) sampleRateHz = 44_100;
        if (channels == 0) channels = 2;
        if (rawBits == 0) rawBits = codec is AudioCodec.DSD_DSF or AudioCodec.DSD_DFF ? 1 : 16;

        BitDepth bitDepth = MapBitDepth(rawBits, codec);

        // Actual average bitrate of the file (file size / duration) used consistently across all UI surfaces.
        int fallbackKbps = tagProperties != null && tagProperties.AudioBitrate > 0
            ? tagProperties.AudioBitrate
            : (decoderWaveFormat != null && !fileExists
                ? (int)ComputeUncompressedPcmBitrateKbps(decoderWaveFormat.SampleRate, decoderWaveFormat.BitsPerSample, decoderWaveFormat.Channels)
                : 0);

        uint bitrateKbps = ComputeFileAverageBitrateKbps(fileSizeBytes, duration, fallbackKbps);
        if (bitrateKbps == 0 && decoderWaveFormat != null)
        {
            bitrateKbps = ComputeUncompressedPcmBitrateKbps(
                decoderWaveFormat.SampleRate,
                decoderWaveFormat.BitsPerSample,
                decoderWaveFormat.Channels);
        }

        var spec = new AudioStreamSpec(codec, sampleRateHz, bitDepth, channels, bitrateKbps);
        TimeSpan finalDuration = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);
        return new ResolvedSourceFormat(spec, finalDuration, rawBits);
    }

    /// <summary>
    /// Fast codec detection for container extensions like .m4a/.mp4 that inspects the MP4 stsd box first
    /// and falls back to TagLib# only if needed.
    /// </summary>
    public static AudioCodec DetectCodecFromFile(string extension, string filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath) &&
            extension is ".m4a" or ".mp4" or ".alac" &&
            TryParseMp4AudioConfig(filePath, out var cfg))
        {
            return cfg.Codec;
        }

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

    /// <summary>
    /// Canonical mapping from source bits-per-sample to AuraAudio's <see cref="BitDepth"/> enum.
    /// </summary>
    public static BitDepth MapBitDepth(int bitsPerSample, AudioCodec codec)
    {
        if (codec is AudioCodec.DSD_DSF or AudioCodec.DSD_DFF)
        {
            return BitDepth.DSD1Bit;
        }

        if (codec is AudioCodec.MP3 or AudioCodec.AAC or AudioCodec.OGG_VORBIS or AudioCodec.OPUS)
        {
            return BitDepth.Bit16;
        }

        return bitsPerSample switch
        {
            > 24 => BitDepth.Bit32Float,
            > 16 => BitDepth.Bit24,
            _    => BitDepth.Bit16
        };
    }

    /// <summary>
    /// Computes the actual average file bitrate in kbps from file size and duration.
    /// </summary>
    public static uint ComputeFileAverageBitrateKbps(long fileSizeBytes, TimeSpan duration, int fallbackBitrateKbps = 0)
    {
        if (fileSizeBytes > 0 && duration.TotalSeconds > 0.05)
        {
            return (uint)Math.Max(1, Math.Round((fileSizeBytes * 8.0) / (duration.TotalSeconds * 1000.0)));
        }

        return fallbackBitrateKbps > 0 ? (uint)fallbackBitrateKbps : 0u;
    }

    /// <summary>
    /// Computes the uncompressed linear PCM stream bitrate in kbps (sampleRate * bitsPerSample * channels / 1000).
    /// Used exclusively where uncompressed PCM output rate is explicitly labeled (e.g., Signal Chain decompression).
    /// </summary>
    public static uint ComputeUncompressedPcmBitrateKbps(int sampleRate, int bitsPerSample, int channels)
    {
        if (sampleRate <= 0 || bitsPerSample <= 0 || channels <= 0)
        {
            return 0;
        }

        double bps = (double)sampleRate * bitsPerSample * channels;
        return (uint)Math.Max(1, Math.Round(bps / 1000.0));
    }

    /// <summary>
    /// Walks an ISO BMFF / QuickTime MP4 container using seek-based O(1) mdat skipping to extract
    /// true audio parameters from ALACSpecificConfig ('alac') or MPEG-4 AudioSpecificConfig ('esds').
    /// </summary>
    public static bool TryParseMp4AudioConfig(string filePath, out Mp4AudioConfig config)
    {
        config = default;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            int boxesVisited = 0;
            return WalkMp4Boxes(fs, 0, fs.Length, depth: 0, ref boxesVisited, out config);
        }
        catch
        {
            return false;
        }
    }

    private static bool WalkMp4Boxes(
        Stream stream,
        long start,
        long end,
        int depth,
        ref int boxesVisited,
        out Mp4AudioConfig config)
    {
        config = default;
        if (depth > MaxMp4RecursionDepth || start < 0 || end <= start || end > stream.Length)
        {
            return false;
        }

        long pos = start;
        Span<byte> header = stackalloc byte[8];
        Span<byte> largeSizeBuf = stackalloc byte[8];

        while (pos + 8 <= end && boxesVisited < MaxMp4BoxesVisited)
        {
            boxesVisited++;
            stream.Position = pos;
            if (!ReadExact(stream, header))
            {
                return false;
            }

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            uint boxType = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
            long headerSize = 8;
            long boxSize;

            if (size32 == 1)
            {
                if (pos + 16 > end || !ReadExact(stream, largeSizeBuf))
                {
                    return false;
                }
                headerSize = 16;
                ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(largeSizeBuf);
                if (size64 < 16 || size64 > (ulong)(end - pos))
                {
                    return false;
                }
                boxSize = (long)size64;
            }
            else if (size32 == 0)
            {
                boxSize = end - pos;
            }
            else
            {
                if (size32 < 8)
                {
                    return false;
                }
                boxSize = size32;
            }

            long boxEnd = pos + boxSize;
            if (boxEnd > end || boxEnd <= pos)
            {
                return false;
            }

            long payloadStart = pos + headerSize;

            if (boxType == FourCc("stsd"))
            {
                if (ParseStsdBox(stream, payloadStart, boxEnd, depth + 1, ref boxesVisited, out config))
                {
                    return true;
                }
            }
            else if (boxType == FourCc("moov") ||
                     boxType == FourCc("trak") ||
                     boxType == FourCc("mdia") ||
                     boxType == FourCc("minf") ||
                     boxType == FourCc("stbl"))
            {
                if (WalkMp4Boxes(stream, payloadStart, boxEnd, depth + 1, ref boxesVisited, out config))
                {
                    return true;
                }
            }

            pos = boxEnd;
        }

        return false;
    }

    private static bool ParseStsdBox(
        Stream stream,
        long payloadStart,
        long boxEnd,
        int depth,
        ref int boxesVisited,
        out Mp4AudioConfig config)
    {
        config = default;
        if (payloadStart + 8 > boxEnd)
        {
            return false;
        }

        Span<byte> stsdHdr = stackalloc byte[8];
        stream.Position = payloadStart;
        if (!ReadExact(stream, stsdHdr))
        {
            return false;
        }

        uint entryCount = Math.Min(BinaryPrimitives.ReadUInt32BigEndian(stsdHdr[4..8]), 32u);
        long pos = payloadStart + 8;
        Span<byte> entryHdr = stackalloc byte[8];

        for (uint i = 0; i < entryCount && pos + 8 <= boxEnd && boxesVisited < MaxMp4BoxesVisited; i++)
        {
            boxesVisited++;
            stream.Position = pos;
            if (!ReadExact(stream, entryHdr))
            {
                return false;
            }

            uint entrySize = BinaryPrimitives.ReadUInt32BigEndian(entryHdr[..4]);
            uint entryType = BinaryPrimitives.ReadUInt32BigEndian(entryHdr[4..8]);
            if (entrySize < 36 || pos + entrySize > boxEnd)
            {
                return false;
            }

            long entryEnd = pos + entrySize;

            if (entryType == FourCc("alac"))
            {
                if (ParseAlacSampleEntry(stream, pos, entryEnd, depth + 1, ref boxesVisited, out config))
                {
                    return true;
                }
            }
            else if (entryType == FourCc("mp4a"))
            {
                if (ParseMp4aSampleEntry(stream, pos, entryEnd, depth + 1, ref boxesVisited, out config))
                {
                    return true;
                }
            }

            pos = entryEnd;
        }

        return false;
    }

    private static bool ParseAlacSampleEntry(
        Stream stream,
        long entryStart,
        long entryEnd,
        int depth,
        ref int boxesVisited,
        out Mp4AudioConfig config)
    {
        config = default;
        if (entryStart + 36 > entryEnd)
        {
            return false;
        }

        Span<byte> entryFixed = stackalloc byte[28];
        stream.Position = entryStart + 8;
        if (!ReadExact(stream, entryFixed))
        {
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16BigEndian(entryFixed[8..10]);
        long childStart = entryStart + version switch
        {
            1 => 52, // QuickTime SoundDescription v1 (+16 bytes)
            2 => 72, // QuickTime SoundDescription v2 (+36 bytes)
            _ => 36  // ISO BMFF / QuickTime v0
        };

        if (childStart < entryEnd && FindAlacSpecificConfig(stream, childStart, entryEnd, depth, ref boxesVisited, out config))
        {
            return true;
        }

        // Legacy fallback if child 'alac' box offset differs in non-standard v1/v2 muxers
        if (childStart != entryStart + 36 && FindAlacSpecificConfig(stream, entryStart + 36, entryEnd, depth, ref boxesVisited, out config))
        {
            return true;
        }

        return false;
    }

    private static bool FindAlacSpecificConfig(
        Stream stream,
        long start,
        long end,
        int depth,
        ref int boxesVisited,
        out Mp4AudioConfig config)
    {
        config = default;
        if (depth > MaxMp4RecursionDepth || start + 8 > end)
        {
            return false;
        }

        long pos = start;
        Span<byte> hdr = stackalloc byte[8];
        Span<byte> cookieBuf = stackalloc byte[28];

        while (pos + 8 <= end && boxesVisited < MaxMp4BoxesVisited)
        {
            boxesVisited++;
            stream.Position = pos;
            if (!ReadExact(stream, hdr))
            {
                return false;
            }

            uint size = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..8]);
            if (size < 8 || pos + size > end)
            {
                return false;
            }

            long boxEnd = pos + size;
            long payloadLen = size - 8;

            if (type == FourCc("alac") && payloadLen >= 24)
            {
                int toRead = (int)Math.Min(payloadLen, 28);
                stream.Position = pos + 8;
                if (ReadExact(stream, cookieBuf[..toRead]))
                {
                    // Standard ISO FullBox: 4-byte version/flags (0) + 24-byte ALACSpecificConfig.
                    // Legacy QuickTime 'wave' box may contain the bare 24-byte ALACSpecificConfig directly.
                    int cfgOffset = (toRead >= 28 && BinaryPrimitives.ReadUInt32BigEndian(cookieBuf[..4]) == 0) ? 4 : 0;
                    if (toRead - cfgOffset >= 24)
                    {
                        ReadOnlySpan<byte> cfg = cookieBuf.Slice(cfgOffset, 24);
                        byte compatibleVersion = cfg[4];
                        byte bitDepth = cfg[5];
                        byte numChannels = cfg[9];
                        uint sampleRate = BinaryPrimitives.ReadUInt32BigEndian(cfg[20..24]);

                        if (compatibleVersion == 0 &&
                            bitDepth is >= 8 and <= 32 &&
                            numChannels is >= 1 and <= 16 &&
                            sampleRate is >= 1_000 and <= 768_000)
                        {
                            config = new Mp4AudioConfig(AudioCodec.ALAC, sampleRate, bitDepth, numChannels);
                            return true;
                        }
                    }
                }
            }
            else if (type == FourCc("wave"))
            {
                if (FindAlacSpecificConfig(stream, pos + 8, boxEnd, depth + 1, ref boxesVisited, out config))
                {
                    return true;
                }
            }

            pos = boxEnd;
        }

        return false;
    }

    private static bool ParseMp4aSampleEntry(
        Stream stream,
        long entryStart,
        long entryEnd,
        int depth,
        ref int boxesVisited,
        out Mp4AudioConfig config)
    {
        config = default;
        if (entryStart + 36 > entryEnd)
        {
            return false;
        }

        Span<byte> entryFixed = stackalloc byte[28];
        stream.Position = entryStart + 8;
        if (!ReadExact(stream, entryFixed))
        {
            return false;
        }

        ushort version = BinaryPrimitives.ReadUInt16BigEndian(entryFixed[8..10]);
        ushort entryChannels = BinaryPrimitives.ReadUInt16BigEndian(entryFixed[16..18]);
        uint entrySampleRate = BinaryPrimitives.ReadUInt32BigEndian(entryFixed[24..28]) >> 16;

        long childStart = entryStart + version switch
        {
            1 => 52,
            2 => 72,
            _ => 36
        };

        if (childStart < entryEnd && FindEsdsAudioConfig(stream, childStart, entryEnd, depth, ref boxesVisited, out uint esdsSr, out ushort esdsCh))
        {
            config = new Mp4AudioConfig(
                AudioCodec.AAC,
                esdsSr > 0 ? esdsSr : entrySampleRate,
                16,
                esdsCh > 0 ? esdsCh : (entryChannels > 0 ? entryChannels : (ushort)2));
            return true;
        }

        if (entrySampleRate > 0)
        {
            config = new Mp4AudioConfig(
                AudioCodec.AAC,
                entrySampleRate,
                16,
                entryChannels > 0 ? entryChannels : (ushort)2);
            return true;
        }

        return false;
    }

    private static bool FindEsdsAudioConfig(
        Stream stream,
        long start,
        long end,
        int depth,
        ref int boxesVisited,
        out uint sampleRateHz,
        out ushort channels)
    {
        sampleRateHz = 0;
        channels = 0;
        if (depth > MaxMp4RecursionDepth || start + 8 > end)
        {
            return false;
        }

        long pos = start;
        Span<byte> hdr = stackalloc byte[8];

        while (pos + 8 <= end && boxesVisited < MaxMp4BoxesVisited)
        {
            boxesVisited++;
            stream.Position = pos;
            if (!ReadExact(stream, hdr))
            {
                return false;
            }

            uint size = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(hdr[4..8]);
            if (size < 8 || pos + size > end)
            {
                return false;
            }

            long boxEnd = pos + size;
            if (type == FourCc("esds") && size >= 16 && size <= 4096)
            {
                int payloadLen = (int)(size - 12); // skip 8-byte box header + 4-byte FullBox version/flags
                byte[] buf = new byte[payloadLen];
                stream.Position = pos + 12;
                if (ReadExact(stream, buf) && TryParseEsdsPayload(buf, out sampleRateHz, out channels))
                {
                    return true;
                }
            }
            else if (type == FourCc("wave"))
            {
                if (FindEsdsAudioConfig(stream, pos + 8, boxEnd, depth + 1, ref boxesVisited, out sampleRateHz, out channels))
                {
                    return true;
                }
            }

            pos = boxEnd;
        }

        return false;
    }

    private static bool TryParseEsdsPayload(ReadOnlySpan<byte> data, out uint sampleRateHz, out ushort channels)
    {
        sampleRateHz = 0;
        channels = 0;
        int pos = 0;

        while (pos < data.Length)
        {
            byte tag = data[pos++];
            int len = ReadMp4DescriptorLength(data, ref pos);
            if (len < 0 || pos + len > data.Length)
            {
                return false;
            }

            if (tag == 0x03) // ES_DescrTag
            {
                if (len < 3) return false;
                int innerEnd = pos + len;
                pos += 2; // ES_ID
                byte flags = data[pos++];
                if ((flags & 0x80) != 0) pos += 2; // streamDependenceFlag
                if ((flags & 0x40) != 0 && pos < innerEnd) pos += 1 + data[pos]; // URL_Flag
                if ((flags & 0x20) != 0) pos += 2; // OCRstreamFlag
                if (pos > innerEnd) return false;
                continue;
            }

            if (tag == 0x04) // DecoderConfigDescrTag
            {
                if (len < 13) return false;
                pos += 13; // objectTypeIndication(1) + streamType(1) + bufferSizeDB(3) + maxBitrate(4) + avgBitrate(4)
                continue;
            }

            if (tag == 0x05 && len >= 2) // DecSpecificInfoTag (AudioSpecificConfig)
            {
                return TryParseAacAudioSpecificConfig(data.Slice(pos, len), out sampleRateHz, out channels);
            }

            pos += len;
        }

        return false;
    }

    private static bool TryParseAacAudioSpecificConfig(ReadOnlySpan<byte> asc, out uint sampleRateHz, out ushort channels)
    {
        sampleRateHz = 0;
        channels = 0;
        if (asc.Length < 2) return false;

        int bitPos = 0;
        int audioObjectType = ReadBits(asc, ref bitPos, 5);
        if (audioObjectType == 31)
        {
            if (bitPos + 6 > asc.Length * 8) return false;
            audioObjectType = 32 + ReadBits(asc, ref bitPos, 6);
        }

        if (bitPos + 4 > asc.Length * 8) return false;
        int freqIdx = ReadBits(asc, ref bitPos, 4);
        if (freqIdx == 15)
        {
            if (bitPos + 24 > asc.Length * 8) return false;
            sampleRateHz = (uint)ReadBits(asc, ref bitPos, 24);
        }
        else if (freqIdx < AacSamplingFrequencies.Length)
        {
            sampleRateHz = AacSamplingFrequencies[freqIdx];
        }

        if (bitPos + 4 > asc.Length * 8) return false;
        int chCfg = ReadBits(asc, ref bitPos, 4);
        channels = chCfg switch
        {
            >= 1 and <= 6 => (ushort)chCfg,
            7 => 8,
            _ => 0
        };

        // Explicit SBR / PS signaling carries the real output extension sample rate next
        if ((audioObjectType == 5 || audioObjectType == 29) && bitPos + 4 <= asc.Length * 8)
        {
            int extFreqIdx = ReadBits(asc, ref bitPos, 4);
            if (extFreqIdx == 15 && bitPos + 24 <= asc.Length * 8)
            {
                sampleRateHz = (uint)ReadBits(asc, ref bitPos, 24);
            }
            else if (extFreqIdx < AacSamplingFrequencies.Length)
            {
                sampleRateHz = AacSamplingFrequencies[extFreqIdx];
            }
        }

        return sampleRateHz > 0;
    }

    private static int ReadMp4DescriptorLength(ReadOnlySpan<byte> data, ref int pos)
    {
        int length = 0;
        for (int i = 0; i < 4; i++)
        {
            if (pos >= data.Length) return -1;
            byte b = data[pos++];
            length = (length << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return length;
            }
        }
        return length;
    }

    private static int ReadBits(ReadOnlySpan<byte> data, ref int bitPos, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++)
        {
            int byteIndex = bitPos >> 3;
            int bitOffset = 7 - (bitPos & 7);
            value = (value << 1) | ((data[byteIndex] >> bitOffset) & 1);
            bitPos++;
        }
        return value;
    }

    private static bool TryReadDsfHeader(string filePath, out uint sampleRateHz, out ushort channels, out TimeSpan duration)
    {
        sampleRateHz = 0;
        channels = 0;
        duration = TimeSpan.Zero;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128);
            Span<byte> buf = stackalloc byte[80];
            if (!ReadExact(fs, buf)) return false;

            if (Encoding.ASCII.GetString(buf[..4]) != "DSD " || Encoding.ASCII.GetString(buf.Slice(28, 4)) != "fmt ")
            {
                return false;
            }

            uint ch = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(52, 4));
            uint sr = BinaryPrimitives.ReadUInt32LittleEndian(buf.Slice(56, 4));
            ulong sampleCount = BinaryPrimitives.ReadUInt64LittleEndian(buf.Slice(64, 8));

            if (sr == 0 || ch == 0 || ch > 16) return false;

            sampleRateHz = sr;
            channels = (ushort)ch;
            if (sampleCount > 0)
            {
                duration = TimeSpan.FromSeconds((double)sampleCount / sr);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }

    private static uint FourCc(string code) =>
        ((uint)code[0] << 24) | ((uint)code[1] << 16) | ((uint)code[2] << 8) | code[3];
}
