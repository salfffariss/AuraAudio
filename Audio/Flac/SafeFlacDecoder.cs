// Derived from SimpleFlac (MIT License) with AuraAudio RFC 9639 safety, seeking, CRC, and performance extensions.
//
// License: MIT
// Copyright (c) J.D. Purcell (C# port and enhancements)
// Copyright (c) Project Nayuki (Simple FLAC decoder in Java)
// https://www.nayuki.io/page/simple-flac-implementation
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
// of the Software, and to permit persons to whom the Software is furnished to do
// so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace AuraAudio.Audio.Flac;

/// <summary>
/// Pure-managed, 100% memory-safe RFC 9639 FLAC bitstream decoder (zero unsafe code).
/// Features buffered 64 KB I/O, CRC-8 frame-header verification, CRC-16 frame integrity checks,
/// O(1) metadata skipping, SEEKTABLE parsing, 64-bit LPC accumulators (overflow-safe),
/// Rice escape code 0 support, and strict bounds/time guards against malformed or fuzzed inputs.
/// </summary>
public sealed class SafeFlacDecoder : IDisposable
{
    public readonly record struct SeekPoint(long SampleNumber, long StreamOffset, int FrameSamples);

    private const int MaxMetadataBlocks = 10_000;
    private const int MaxSeekPoints = 250_000;
    private const int MaxRiceFallbackUnaryZeros = 1_048_576;
    private const int MaxBlockSizeAllowed = 65_535;
    private const int MaxChannelsAllowed = 8;
    private const int MaxLeadingId3Tags = 16;
    private const int MaxId3PaddingBytes = 1_048_576;

    private readonly BufferedBitReader _reader;
    private readonly bool _leaveOpen;
    private readonly byte[] _expectedMd5 = new byte[16];
    private readonly bool _hasExpectedMd5;
    private bool _prevFrameWasBlockSize1;
    private bool _isDisposed;

    public int MinSamplesPerFrame { get; private set; }
    public int MaxSamplesPerFrame { get; private set; }
    public int SampleRate { get; private set; }
    public int ChannelCount { get; private set; }
    public int BitsPerSample { get; private set; }
    public long? StreamSampleCount { get; private set; }
    public long FirstFrameOffset { get; private set; }
    public SeekPoint[] SeekPoints { get; private set; } = Array.Empty<SeekPoint>();
    public byte[]? ExpectedMd5 => _hasExpectedMd5 ? (byte[])_expectedMd5.Clone() : null;

    public long[][] BufferSamples { get; private set; } = Array.Empty<long[]>();
    public int BufferSampleCount { get; private set; }
    public long RunningSampleCount { get; private set; }
    public long CurrentFrameByteOffset { get; private set; }
    public long NextFrameByteOffset => _reader.BytePosition;

    public SafeFlacDecoder(Stream input, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        _leaveOpen = leaveOpen;
        _reader = new BufferedBitReader(input, leaveOpen);

        try
        {
            ReadMetadata(out bool hasMd5);
            _hasExpectedMd5 = hasMd5;
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public SafeFlacDecoder(string filePath)
        : this(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.SequentialScan), leaveOpen: false)
    {
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _reader.Dispose();
    }

    /// <summary>
    /// Resets the decoder to a known frame boundary byte offset and sample offset (used for seeking).
    /// </summary>
    public void SeekToFrameOffset(long frameByteOffset, long sampleOffset)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (frameByteOffset < FirstFrameOffset)
        {
            frameByteOffset = FirstFrameOffset;
            sampleOffset = 0;
        }

        _reader.ResetToByteOffset(frameByteOffset);
        RunningSampleCount = sampleOffset;
        BufferSampleCount = 0;
        _prevFrameWasBlockSize1 = false;
    }

    private void ReadMetadata(out bool hasMd5)
    {
        hasMd5 = false;

        SkipId3v2Prefix();

        if (_reader.Read(32) != 0x664C6143UL) // "fLaC"
        {
            throw new InvalidDataException("Invalid FLAC file: \"fLaC\" stream marker not found.");
        }

        bool foundStreamInfo = false;
        bool isLastBlock;
        int blockIndex = 0;
        var seekList = new List<SeekPoint>();

        do
        {
            if (++blockIndex > MaxMetadataBlocks)
            {
                throw new InvalidDataException("Invalid FLAC file: excessive metadata blocks.");
            }

            isLastBlock = _reader.Read(1) != 0;
            int blockType = (int)_reader.Read(7);
            int blockLength = (int)_reader.Read(24);

            if (blockType == 127)
            {
                throw new InvalidDataException("Invalid FLAC metadata block type (127).");
            }

            long blockStart = _reader.BytePosition;
            if (_reader.StreamLength >= 0 && blockStart + blockLength > _reader.StreamLength)
            {
                throw new InvalidDataException("FLAC metadata block length exceeds file size.");
            }

            if (blockIndex == 1)
            {
                if (blockType != 0 || blockLength != 34)
                {
                    throw new InvalidDataException("Invalid FLAC file: first metadata block must be a 34-byte STREAMINFO block.");
                }
                hasMd5 = ReadStreamInfoBlock();
                foundStreamInfo = true;
            }
            else if (blockType == 0)
            {
                throw new InvalidDataException("Invalid FLAC file: duplicate STREAMINFO metadata block.");
            }
            else if (blockType == 3 && blockLength >= 18 && blockLength % 18 == 0)
            {
                int count = blockLength / 18;
                if (count <= MaxSeekPoints)
                {
                    ReadSeekTableBlock(count, seekList);
                }
                else
                {
                    _reader.SkipBytes(blockLength);
                }
            }
            else
            {
                _reader.SkipBytes(blockLength);
            }
        }
        while (!isLastBlock);

        if (!foundStreamInfo)
        {
            throw new InvalidDataException("FLAC STREAMINFO metadata block not found.");
        }

        FirstFrameOffset = _reader.BytePosition;
        if (seekList.Count > 0)
        {
            seekList.Sort((a, b) => a.SampleNumber.CompareTo(b.SampleNumber));
            SeekPoints = seekList.ToArray();
        }
    }

    private void SkipId3v2Prefix()
    {
        // Some real-world FLAC files carry one or more leading ID3v2 tags (optionally followed by
        // a 10-byte ID3v2.4 footer and/or zero padding) before the "fLaC" stream marker.
        for (int tagCount = 0; tagCount < MaxLeadingId3Tags; tagCount++)
        {
            if (_reader.HasReachedEnd || (_reader.Peek32() >> 8) != 0x494433UL) // "ID3"
            {
                break;
            }

            _reader.Skip(24); // "ID3"
            byte majorVer = (byte)_reader.Read(8);
            byte minorVer = (byte)_reader.Read(8);
            byte flags = (byte)_reader.Read(8);
            byte s0 = (byte)_reader.Read(8);
            byte s1 = (byte)_reader.Read(8);
            byte s2 = (byte)_reader.Read(8);
            byte s3 = (byte)_reader.Read(8);

            if (majorVer == 0xFF || minorVer == 0xFF || ((s0 | s1 | s2 | s3) & 0x80) != 0)
            {
                throw new InvalidDataException("Invalid ID3v2 header preceding FLAC stream.");
            }

            long tagBodySize = ((long)s0 << 21) | ((long)s1 << 14) | ((long)s2 << 7) | s3;
            if ((flags & 0x10) != 0)
            {
                tagBodySize += 10; // ID3v2.4 footer present
            }

            if (_reader.StreamLength >= 0 && _reader.BytePosition + tagBodySize > _reader.StreamLength)
            {
                throw new InvalidDataException("Truncated ID3v2 tag preceding FLAC stream.");
            }

            _reader.SkipBytes(tagBodySize);

            // Skip optional zero padding bytes between ID3v2 tag and "fLaC"
            int zeroSkipped = 0;
            while (!_reader.HasReachedEnd && (_reader.Peek32() >> 24) == 0)
            {
                _reader.Skip(8);
                if (++zeroSkipped > MaxId3PaddingBytes)
                {
                    throw new InvalidDataException("Excessive zero padding after ID3v2 tag before FLAC stream.");
                }
            }
        }
    }

    private bool ReadStreamInfoBlock()
    {
        MinSamplesPerFrame = (int)_reader.Read(16);
        MaxSamplesPerFrame = (int)_reader.Read(16);
        _reader.Skip(24); // min frame size
        _reader.Skip(24); // max frame size
        SampleRate = (int)_reader.Read(20);
        ChannelCount = (int)_reader.Read(3) + 1;
        BitsPerSample = (int)_reader.Read(5) + 1;
        long totalSamples = (long)_reader.Read(36);

        bool nonZeroMd5 = false;
        for (int i = 0; i < 16; i++)
        {
            byte b = (byte)_reader.Read(8);
            _expectedMd5[i] = b;
            if (b != 0) nonZeroMd5 = true;
        }

        if (SampleRate <= 0)
        {
            throw new InvalidDataException("Invalid FLAC STREAMINFO: sample rate is 0.");
        }
        if (ChannelCount < 1 || ChannelCount > MaxChannelsAllowed)
        {
            throw new InvalidDataException($"Invalid FLAC STREAMINFO: unsupported channel count {ChannelCount}.");
        }
        if (BitsPerSample < 4 || BitsPerSample > 32)
        {
            throw new InvalidDataException($"Invalid FLAC STREAMINFO: unsupported bit depth {BitsPerSample}.");
        }
        if (MaxSamplesPerFrame < 16 || MaxSamplesPerFrame > MaxBlockSizeAllowed)
        {
            throw new InvalidDataException($"Invalid FLAC STREAMINFO: maximum blocksize {MaxSamplesPerFrame} out of range (16..65535).");
        }
        if (MinSamplesPerFrame != 0 && MinSamplesPerFrame > MaxSamplesPerFrame)
        {
            throw new InvalidDataException("Invalid FLAC STREAMINFO: minimum blocksize exceeds maximum blocksize.");
        }

        StreamSampleCount = totalSamples > 0 ? totalSamples : null;
        BufferSamples = new long[ChannelCount][];
        for (int ch = 0; ch < ChannelCount; ch++)
        {
            BufferSamples[ch] = new long[MaxSamplesPerFrame];
        }

        return nonZeroMd5;
    }

    private void ReadSeekTableBlock(int pointCount, List<SeekPoint> seekList)
    {
        for (int i = 0; i < pointCount; i++)
        {
            ulong sampleNumHi = _reader.Read(32);
            ulong sampleNumLo = _reader.Read(32);
            ulong sampleNum = (sampleNumHi << 32) | sampleNumLo;

            ulong offsetHi = _reader.Read(32);
            ulong offsetLo = _reader.Read(32);
            ulong streamOffset = (offsetHi << 32) | offsetLo;

            int frameSamples = (int)_reader.Read(16);

            // Placeholder seekpoint = 0xFFFFFFFFFFFFFFFF
            if (sampleNum == ulong.MaxValue) continue;
            if (sampleNum > (ulong)long.MaxValue || streamOffset > (ulong)long.MaxValue) continue;
            if (frameSamples <= 0) continue;

            seekList.Add(new SeekPoint((long)sampleNum, (long)streamOffset, frameSamples));
        }
    }

    /// <summary>
    /// Decodes the next FLAC frame into <see cref="BufferSamples"/>.
    /// Returns false cleanly at EOF (or valid trailing ID3v1/APEv2 tag after all declared samples).
    /// Throws <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/> on malformed/unsupported streams.
    /// </summary>
    public bool DecodeFrame()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_reader.HasReachedEnd)
        {
            if (RunningSampleCount == 0 && StreamSampleCount.HasValue && StreamSampleCount.Value > 0)
            {
                throw new EndOfStreamException("Unexpected end of FLAC stream: metadata declares audio samples but no audio frames were found.");
            }
            BufferSampleCount = 0;
            return false;
        }

        // If all STREAMINFO samples have already been decoded, tolerate standard trailing ID3v1 ("TAG")
        // or APEv2 ("APETAGEX") tags appended after the final FLAC frame.
        if (StreamSampleCount.HasValue && RunningSampleCount >= StreamSampleCount.Value)
        {
            ulong peek32 = _reader.Peek32();
            if ((peek32 >> 8) == 0x544147UL || peek32 == 0x41504554UL) // "TAG" or "APET"
            {
                BufferSampleCount = 0;
                return false;
            }
        }

        CurrentFrameByteOffset = _reader.BytePosition;
        _reader.BeginFrameCrc();

        // Sync code (14 bits = 0x3FFE) + 1 reserved bit (0) => 15 bits = 0x7FFC
        if (_reader.Read(15) != 0x7FFCUL)
        {
            throw new InvalidDataException("Invalid FLAC frame sync code.");
        }

        _reader.Skip(1); // Blocking strategy (0 = fixed, 1 = variable)
        int blockSizeCode = (int)_reader.Read(4);
        int sampleRateCode = (int)_reader.Read(4);
        int channelLayout = (int)_reader.Read(4);
        int bitDepthCode = (int)_reader.Read(3);
        if (_reader.Read(1) != 0)
        {
            throw new InvalidDataException("Invalid FLAC frame header reserved bit.");
        }

        // UTF-8 coded frame or sample number (RFC 9639 Section 9.1.5)
        byte firstUtf8 = (byte)_reader.Read(8);
        int leadingOnes = BitOperations.LeadingZeroCount(~((ulong)firstUtf8 << 56));
        if (leadingOnes == 1 || leadingOnes > 7)
        {
            throw new InvalidDataException("Invalid UTF-8 coded frame/sample number in FLAC header.");
        }
        for (int i = 1; i < leadingOnes; i++)
        {
            byte cont = (byte)_reader.Read(8);
            if ((cont & 0xC0) != 0x80)
            {
                throw new InvalidDataException("Invalid UTF-8 continuation byte in FLAC frame header.");
            }
        }

        int frameSampleCount = blockSizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)_reader.Read(8) + 1,
            7 => (int)_reader.Read(16) + 1,
            >= 8 and <= 15 => 256 << (blockSizeCode - 8),
            _ => throw new InvalidDataException("Reserved FLAC blocksize code (0).")
        };

        int frameSampleRate = sampleRateCode switch
        {
            0 => SampleRate,
            >= 1 and <= 11 => SampleRateCodes[sampleRateCode],
            12 => (int)_reader.Read(8) * 1000,
            13 => (int)_reader.Read(16),
            14 => (int)_reader.Read(16) * 10,
            _ => throw new InvalidDataException("Reserved FLAC sample rate code (15).")
        };

        int frameBitsPerSample = bitDepthCode switch
        {
            0 => BitsPerSample,
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            6 => 24,
            7 => 32,
            _ => throw new InvalidDataException("Reserved FLAC bit-depth code (3).")
        };

        int frameChannelCount = channelLayout switch
        {
            >= 0 and <= 7 => channelLayout + 1,
            >= 8 and <= 10 => 2,
            _ => throw new InvalidDataException("Reserved FLAC channel assignment.")
        };

        // Verify RFC 9639 Frame Header CRC-8 before trusting parsed header fields
        byte computedCrc8 = _reader.FinishHeaderCrc8();
        byte expectedCrc8 = (byte)_reader.Read(8);
        if (computedCrc8 != expectedCrc8)
        {
            throw new InvalidDataException($"FLAC frame header CRC-8 mismatch (computed 0x{computedCrc8:X2}, expected 0x{expectedCrc8:X2}).");
        }

        // RFC 9639 Section 9.1.1: blocksize == 1 is only legal for the final frame of a stream.
        // Rejecting consecutive blocksize-1 frames immediately prevents pathological 9s+ decode stalls (e.g., IETF faulty/09).
        if (_prevFrameWasBlockSize1)
        {
            throw new InvalidDataException("Invalid FLAC stream: blocksize 1 is only permitted for the final frame.");
        }
        _prevFrameWasBlockSize1 = (frameSampleCount == 1);

        if (frameSampleCount <= 0 || frameSampleCount > MaxSamplesPerFrame)
        {
            throw new InvalidDataException($"FLAC frame sample count ({frameSampleCount}) exceeds STREAMINFO maximum ({MaxSamplesPerFrame}).");
        }

        if (frameSampleRate != SampleRate || frameBitsPerSample != BitsPerSample || frameChannelCount != ChannelCount)
        {
            throw new NotSupportedException(
                $"Unsupported mid-stream FLAC format change (frame: {frameBitsPerSample}b/{frameSampleRate}Hz/{frameChannelCount}ch, stream: {BitsPerSample}b/{SampleRate}Hz/{ChannelCount}ch).");
        }

        DecodeSubframes(_reader, BitsPerSample, channelLayout, BufferSamples, frameSampleCount);
        _reader.AlignToByte();

        // Verify RFC 9639 Whole-Frame CRC-16
        ushort computedCrc16 = _reader.FinishFrameCrc16();
        ushort expectedCrc16 = (ushort)_reader.Read(16);
        if (computedCrc16 != expectedCrc16)
        {
            throw new InvalidDataException($"FLAC frame CRC-16 mismatch (computed 0x{computedCrc16:X4}, expected 0x{expectedCrc16:X4}).");
        }

        BufferSampleCount = frameSampleCount;
        RunningSampleCount += frameSampleCount;
        return true;
    }

    private static void DecodeSubframes(BufferedBitReader reader, int bitsPerSample, int channelLayout, long[][] result, int blockSize)
    {
        if (channelLayout <= 7)
        {
            for (int ch = 0; ch < result.Length; ch++)
            {
                DecodeSubframe(reader, bitsPerSample, result[ch].AsSpan(0, blockSize));
            }
        }
        else if (channelLayout is >= 8 and <= 10)
        {
            DecodeSubframe(reader, bitsPerSample + (channelLayout == 9 ? 1 : 0), result[0].AsSpan(0, blockSize));
            DecodeSubframe(reader, bitsPerSample + (channelLayout == 9 ? 0 : 1), result[1].AsSpan(0, blockSize));

            long[] ch0 = result[0];
            long[] ch1 = result[1];
            if (channelLayout == 8) // Left-Side
            {
                for (int i = 0; i < blockSize; i++)
                {
                    ch1[i] = ch0[i] - ch1[i];
                }
            }
            else if (channelLayout == 9) // Right-Side
            {
                for (int i = 0; i < blockSize; i++)
                {
                    ch0[i] += ch1[i];
                }
            }
            else // Mid-Side (10)
            {
                for (int i = 0; i < blockSize; i++)
                {
                    long side = ch1[i];
                    long right = ch0[i] - (side >> 1);
                    ch1[i] = right;
                    ch0[i] = right + side;
                }
            }
        }
        else
        {
            throw new InvalidDataException("Invalid FLAC channel layout.");
        }
    }

    private static void DecodeSubframe(BufferedBitReader reader, int bitsPerSample, Span<long> result)
    {
        if (reader.Read(1) != 0)
        {
            throw new InvalidDataException("Invalid FLAC subframe padding bit.");
        }

        int type = (int)reader.Read(6);
        int shift = (int)reader.Read(1);
        if (shift == 1)
        {
            while (reader.Read(1) == 0)
            {
                shift++;
                if (shift >= bitsPerSample)
                {
                    throw new InvalidDataException("Invalid FLAC wasted-bits count.");
                }
            }
        }

        bitsPerSample -= shift;
        if (bitsPerSample <= 0 || bitsPerSample > 33)
        {
            throw new InvalidDataException($"Invalid FLAC effective subframe bit depth ({bitsPerSample}).");
        }

        if (type == 0) // CONSTANT
        {
            long v = reader.ReadSigned(bitsPerSample);
            result.Fill(v);
        }
        else if (type == 1) // VERBATIM
        {
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = reader.ReadSigned(bitsPerSample);
            }
        }
        else if (type is >= 8 and <= 12) // FIXED
        {
            DecodeFixedPredictionSubframe(reader, type - 8, bitsPerSample, result);
        }
        else if (type is >= 32 and <= 63) // LPC
        {
            DecodeLinearPredictiveCodingSubframe(reader, type - 31, bitsPerSample, result);
        }
        else
        {
            throw new InvalidDataException($"Reserved FLAC subframe type ({type}).");
        }

        if (shift != 0)
        {
            for (int i = 0; i < result.Length; i++)
            {
                result[i] <<= shift;
            }
        }
    }

    private static void DecodeFixedPredictionSubframe(BufferedBitReader reader, int predOrder, int bitsPerSample, Span<long> result)
    {
        if (predOrder > result.Length)
        {
            throw new InvalidDataException("FLAC fixed predictor order exceeds frame blocksize.");
        }

        for (int i = 0; i < predOrder; i++)
        {
            result[i] = reader.ReadSigned(bitsPerSample);
        }

        DecodeResiduals(reader, predOrder, result);

        if (predOrder == 1)
        {
            for (int i = 1; i < result.Length; i++)
                result[i] += result[i - 1];
        }
        else if (predOrder == 2)
        {
            for (int i = 2; i < result.Length; i++)
                result[i] += (result[i - 1] << 1) - result[i - 2];
        }
        else if (predOrder == 3)
        {
            for (int i = 3; i < result.Length; i++)
                result[i] += 3 * (result[i - 1] - result[i - 2]) + result[i - 3];
        }
        else if (predOrder == 4)
        {
            for (int i = 4; i < result.Length; i++)
                result[i] += ((result[i - 1] + result[i - 3]) << 2) - 6 * result[i - 2] - result[i - 4];
        }
    }

    private static void DecodeLinearPredictiveCodingSubframe(BufferedBitReader reader, int lpcOrder, int bitsPerSample, Span<long> result)
    {
        if (lpcOrder > result.Length)
        {
            throw new InvalidDataException("FLAC LPC order exceeds frame blocksize.");
        }

        for (int i = 0; i < lpcOrder; i++)
        {
            result[i] = reader.ReadSigned(bitsPerSample);
        }

        int precisionRaw = (int)reader.Read(4);
        if (precisionRaw == 15)
        {
            throw new InvalidDataException("Invalid FLAC LPC quantized precision (reserved value 15).");
        }
        int precision = precisionRaw + 1;

        int shift = (int)reader.ReadSigned(5);
        if (shift < 0)
        {
            throw new InvalidDataException($"Invalid negative FLAC LPC shift ({shift}).");
        }

        Span<long> coefs = stackalloc long[lpcOrder];
        for (int i = lpcOrder - 1; i >= 0; i--)
        {
            coefs[i] = reader.ReadSigned(precision);
        }

        DecodeResiduals(reader, lpcOrder, result);
        RestoreLinearPrediction(result, coefs, shift);
    }

    private static void DecodeResiduals(BufferedBitReader reader, int warmup, Span<long> result)
    {
        int method = (int)reader.Read(2);
        if (method >= 2)
        {
            throw new InvalidDataException("Reserved FLAC residual coding method.");
        }

        int paramBits = method == 0 ? 4 : 5;
        int escapeParam = method == 0 ? 15 : 31;

        int partitionOrder = (int)reader.Read(4);
        int numPartitions = 1 << partitionOrder;
        if (result.Length % numPartitions != 0)
        {
            throw new InvalidDataException("FLAC blocksize is not divisible by Rice partition count.");
        }

        int partitionSize = result.Length / numPartitions;
        if (warmup > partitionSize)
        {
            throw new InvalidDataException("FLAC predictor warmup exceeds Rice partition size.");
        }

        for (int i = 0; i < numPartitions; i++)
        {
            int start = i * partitionSize + (i == 0 ? warmup : 0);
            int end = (i + 1) * partitionSize;

            int param = (int)reader.Read(paramBits);
            if (param != escapeParam)
            {
                for (int j = start; j < end; j++)
                {
                    result[j] = DecodeRice(reader, param);
                }
            }
            else
            {
                int numBits = (int)reader.Read(5);
                if (numBits == 0)
                {
                    // RFC 9639 escape code 0: all residual values in this partition are 0
                    result.Slice(start, end - start).Clear();
                }
                else
                {
                    for (int j = start; j < end; j++)
                    {
                        result[j] = reader.ReadSigned(numBits);
                    }
                }
            }
        }
    }

    private static void RestoreLinearPrediction(Span<long> result, ReadOnlySpan<long> coefs, int shift)
    {
        int order = coefs.Length;
        int len = result.Length - order;

        // Specialized unrolled paths for the most common FLAC LPC orders (8 and 12) for peak managed throughput
        if (order == 8)
        {
            long c0 = coefs[0], c1 = coefs[1], c2 = coefs[2], c3 = coefs[3];
            long c4 = coefs[4], c5 = coefs[5], c6 = coefs[6], c7 = coefs[7];
            for (int i = 0; i < len; i++)
            {
                long sum = result[i] * c0 + result[i + 1] * c1 + result[i + 2] * c2 + result[i + 3] * c3
                         + result[i + 4] * c4 + result[i + 5] * c5 + result[i + 6] * c6 + result[i + 7] * c7;
                result[i + 8] += sum >> shift;
            }
            return;
        }
        if (order == 12)
        {
            long c0 = coefs[0], c1 = coefs[1], c2 = coefs[2], c3 = coefs[3];
            long c4 = coefs[4], c5 = coefs[5], c6 = coefs[6], c7 = coefs[7];
            long c8 = coefs[8], c9 = coefs[9], c10 = coefs[10], c11 = coefs[11];
            for (int i = 0; i < len; i++)
            {
                long sum = result[i] * c0 + result[i + 1] * c1 + result[i + 2] * c2 + result[i + 3] * c3
                         + result[i + 4] * c4 + result[i + 5] * c5 + result[i + 6] * c6 + result[i + 7] * c7
                         + result[i + 8] * c8 + result[i + 9] * c9 + result[i + 10] * c10 + result[i + 11] * c11;
                result[i + 12] += sum >> shift;
            }
            return;
        }

        for (int i = 0; i < len; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += result[i + j] * coefs[j];
            }
            result[i + order] += sum >> shift;
        }
    }

    private static long DecodeRice(BufferedBitReader reader, int k)
    {
        ulong data = reader.RawBuffer;
        int leadingZeros = BitOperations.LeadingZeroCount(data);
        int quotientBits = leadingZeros + 1;
        int fullBits = quotientBits + k;
        if (fullBits <= BufferedBitReader.BitsAvailableWorstCase)
        {
            ulong v = ((ulong)leadingZeros) << k;
            if (k != 0)
            {
                v |= (data << quotientBits) >> (64 - k);
            }
            reader.Skip(fullBits);
            return (long)(v >> 1) ^ -(long)(v & 1);
        }

        return DecodeRiceFallback(reader, k);
    }

    private static long DecodeRiceFallback(BufferedBitReader reader, int k)
    {
        long leadingZeros = 0;
        while (reader.Read(1) == 0)
        {
            leadingZeros++;
            if (leadingZeros > MaxRiceFallbackUnaryZeros)
            {
                throw new InvalidDataException("Invalid FLAC Rice residual: excessive unary zeros.");
            }
        }

        ulong v = ((ulong)leadingZeros) << k;
        if (k != 0)
        {
            v |= reader.Read(k);
        }
        return (long)(v >> 1) ^ -(long)(v & 1);
    }

    private static readonly int[] SampleRateCodes =
    [
        0, 88_200, 176_400, 192_000, 8_000, 16_000, 22_050, 24_000, 32_000, 44_100, 48_000, 96_000
    ];

    private static readonly byte[] Crc8Table = BuildCrc8Table();
    private static readonly ushort[] Crc16Table = BuildCrc16Table();

    private static byte[] BuildCrc8Table()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i;
            for (int b = 0; b < 8; b++)
            {
                c = (c & 0x80) != 0 ? ((c << 1) ^ 0x07) : (c << 1);
            }
            table[i] = (byte)(c & 0xFF);
        }
        return table;
    }

    private static ushort[] BuildCrc16Table()
    {
        var table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int b = 0; b < 8; b++)
            {
                c = (c & 0x8000) != 0 ? ((c << 1) ^ 0x8005) : (c << 1);
            }
            table[i] = (ushort)(c & 0xFFFF);
        }
        return table;
    }

    /// <summary>
    /// High-throughput buffered bit reader with O(1) seek, exact byte-position tracking,
    /// and zero-hot-path sliding-tail byte-wise CRC-8 / CRC-16 verification. Zero unsafe code.
    /// </summary>
    private sealed class BufferedBitReader : IDisposable
    {
        public const int BitsAvailableWorstCase = 57;
        private const int TailKeepBytes = 16;
        private const int IoReadChunkSize = 65_536;

        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly byte[] _ioBuf = new byte[TailKeepBytes + IoReadChunkSize];
        private int _ioPos;
        private int _ioLen;
        private long _ioBufStartPos;
        private long _streamReadPos;

        private ulong _buffer;
        private int _bufferDeficitBits;
        private int _streamOverreadBytes;
        private bool _streamEof;

        // Deferred byte-wise CRC state: computed directly over contiguous _ioBuf slices (zero work inside DecodeRice/Skip)
        private bool _crcActive;
        private bool _crc8Done;
        private long _crcHashedPos;
        private byte _crc8;
        private ushort _crc16;

        public long StreamLength { get; }

        public BufferedBitReader(Stream stream, bool leaveOpen)
        {
            _stream = stream;
            _leaveOpen = leaveOpen;
            StreamLength = stream.CanSeek ? stream.Length : -1;
            long startPos = stream.CanSeek ? stream.Position : 0;
            _ioBufStartPos = startPos;
            _streamReadPos = startPos;
            _bufferDeficitBits = 64;
            ReplenishBuffer();
        }

        public void Dispose()
        {
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }

        public bool HasReachedEnd => _streamOverreadBytes >= 8;

        public ulong RawBuffer => _buffer;

        /// <summary>
        /// Exact byte offset in the underlying stream of the next unread bit (valid when byte-aligned).
        /// </summary>
        public long BytePosition
        {
            get
            {
                if (_streamOverreadBytes >= 8)
                {
                    return _streamReadPos;
                }
                int bufferedValidBytes = ((64 - _bufferDeficitBits) >> 3) - _streamOverreadBytes;
                return _streamReadPos - bufferedValidBytes;
            }
        }

        public ulong Peek32() => _buffer >> 32;

        public void BeginFrameCrc()
        {
            _crcActive = true;
            _crc8Done = false;
            _crcHashedPos = BytePosition;
            _crc8 = 0;
            _crc16 = 0;
        }

        public byte FinishHeaderCrc8()
        {
            long endPos = BytePosition;
            UpdateCrcFromBuffer(_crcHashedPos, endPos, updateCrc8: true);
            _crcHashedPos = endPos;
            _crc8Done = true;
            return _crc8;
        }

        public ushort FinishFrameCrc16()
        {
            long endPos = BytePosition;
            UpdateCrcFromBuffer(_crcHashedPos, endPos, updateCrc8: false);
            _crcHashedPos = endPos;
            _crcActive = false;
            return _crc16;
        }

        private void UpdateCrcFromBuffer(long fromStreamPos, long toStreamPos, bool updateCrc8)
        {
            if (toStreamPos <= fromStreamPos) return;

            int fromIdx = (int)(fromStreamPos - _ioBufStartPos);
            int toIdx = (int)(toStreamPos - _ioBufStartPos);
            if (fromIdx < 0 || toIdx > _ioLen || fromIdx > toIdx)
            {
                throw new InvalidDataException("Internal FLAC CRC buffer bounds error.");
            }

            byte[] buf = _ioBuf;
            ushort crc16 = _crc16;
            ushort[] table16 = Crc16Table;

            if (updateCrc8)
            {
                byte crc8 = _crc8;
                byte[] table8 = Crc8Table;
                for (int i = fromIdx; i < toIdx; i++)
                {
                    byte b = buf[i];
                    crc8 = table8[crc8 ^ b];
                    crc16 = (ushort)((crc16 << 8) ^ table16[((crc16 >> 8) ^ b) & 0xFF]);
                }
                _crc8 = crc8;
            }
            else
            {
                for (int i = fromIdx; i < toIdx; i++)
                {
                    crc16 = (ushort)((crc16 << 8) ^ table16[((crc16 >> 8) ^ buf[i]) & 0xFF]);
                }
            }

            _crc16 = crc16;
        }

        public void ResetToByteOffset(long offset)
        {
            if (!_stream.CanSeek)
            {
                throw new NotSupportedException("Underlying FLAC stream does not support seeking.");
            }
            if (offset < 0 || (StreamLength >= 0 && offset > StreamLength))
            {
                throw new EndOfStreamException("Seek target exceeds FLAC stream bounds.");
            }

            _stream.Position = offset;
            _ioPos = 0;
            _ioLen = 0;
            _ioBufStartPos = offset;
            _streamReadPos = offset;
            _buffer = 0;
            _bufferDeficitBits = 64;
            _streamOverreadBytes = 0;
            _streamEof = false;
            _crcActive = false;
            ReplenishBuffer();
        }

        public void SkipBytes(long byteCount)
        {
            if (byteCount <= 0) return;
            if (_stream.CanSeek)
            {
                ResetToByteOffset(BytePosition + byteCount);
                return;
            }

            for (long i = 0; i < byteCount; i++)
            {
                Skip(8);
            }
        }

        private int ReadNextByte()
        {
            if (_ioPos >= _ioLen)
            {
                if (_streamEof || !RefillIoBuffer())
                {
                    return -1;
                }
            }
            _streamReadPos++;
            return _ioBuf[_ioPos++];
        }

        private bool RefillIoBuffer()
        {
            // Keep the trailing 16 bytes (exceeding the 8-byte bit-buffer lookahead) so BytePosition
            // is always guaranteed to reside inside _ioBuf while hashing everything older into CRC.
            int keep = Math.Min(TailKeepBytes, _ioLen);
            long flushEnd = _streamReadPos - keep;

            if (_crcActive && _crcHashedPos < flushEnd)
            {
                UpdateCrcFromBuffer(_crcHashedPos, flushEnd, updateCrc8: !_crc8Done);
                _crcHashedPos = flushEnd;
            }

            if (keep > 0)
            {
                Buffer.BlockCopy(_ioBuf, _ioLen - keep, _ioBuf, 0, keep);
            }

            _ioBufStartPos = flushEnd;
            int read = _stream.Read(_ioBuf, keep, IoReadChunkSize);
            _ioPos = keep;
            _ioLen = keep + read;
            if (read == 0)
            {
                _streamEof = true;
                return false;
            }
            return true;
        }

        private void ReplenishBuffer()
        {
            while (_bufferDeficitBits >= 8)
            {
                int b = ReadNextByte();
                if (b == -1)
                {
                    _streamOverreadBytes++;
                    if (_streamOverreadBytes >= 8)
                    {
                        if (_bufferDeficitBits == 8)
                        {
                            return;
                        }
                        throw new EndOfStreamException("Unexpected end of FLAC stream.");
                    }
                }
                else
                {
                    _buffer |= (ulong)b << (_bufferDeficitBits - 8);
                }
                _bufferDeficitBits -= 8;
            }
        }

        public void Skip(int numBits)
        {
            if (numBits < 1 || numBits > BitsAvailableWorstCase)
            {
                throw new ArgumentOutOfRangeException(nameof(numBits));
            }

            _buffer <<= numBits;
            _bufferDeficitBits += numBits;
            ReplenishBuffer();
        }

        public ulong Read(int numBits)
        {
            ulong x = _buffer >> (64 - numBits);
            Skip(numBits);
            return x;
        }

        public long ReadSigned(int numBits)
        {
            ulong x = Read(numBits);
            int shift = 64 - numBits;
            return (long)(x << shift) >> shift;
        }

        public void AlignToByte()
        {
            int rem = _bufferDeficitBits & 7;
            if (rem != 0)
            {
                Skip(8 - rem);
            }
        }
    }
}
