using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace AuraAudio.Audio.Flac;

/// <summary>
/// Pure-managed NAudio <see cref="WaveStream"/> wrapper around <see cref="SafeFlacDecoder"/>.
/// Provides bit-exact PCM output, sample-accurate seeking (SEEKTABLE + sparse frame index),
/// gapless-safe Read semantics, and complete protection against unsafe memory corruption.
/// </summary>
public sealed class SafeFlacWaveStream : WaveStream
{
    private const int SparseIndexFrameStride = 16;
    private const long UnknownLengthScanLimitBytes = 16L * 1024 * 1024;
    private const int UnknownLengthScanMaxFrames = 65_536;
    private const long UnknownLengthScanMaxMs = 2_000;

    private readonly object _syncRoot = new();
    private readonly SafeFlacDecoder _decoder;
    private readonly WaveFormat _waveFormat;
    private readonly int _outputBytesPerSample;
    private readonly int _bitShiftLeft;
    private readonly List<SafeFlacDecoder.SeekPoint> _runtimeIndex = new();

    private byte[] _framePcmBytes;
    private int _framePcmByteLength;
    private int _framePcmByteOffset;
    private long _positionBytes;
    private long _totalSamples;
    private int _framesDecodedSinceAnchor;
    private bool _eofReached;
    private bool _isDisposed;

    /// <summary>True source bit depth declared in FLAC STREAMINFO (4..32).</summary>
    public int SourceBitsPerSample => _decoder.BitsPerSample;

    public override WaveFormat WaveFormat => _waveFormat;

    public override long Length => _totalSamples * _waveFormat.BlockAlign;

    public override long Position
    {
        get
        {
            lock (_syncRoot)
            {
                return _positionBytes;
            }
        }
        set
        {
            lock (_syncRoot)
            {
                SeekToBytePosition(value);
            }
        }
    }

    public SafeFlacWaveStream(string filePath)
        : this(new SafeFlacDecoder(filePath))
    {
    }

    public SafeFlacWaveStream(Stream input, bool leaveOpen = false)
        : this(new SafeFlacDecoder(input, leaveOpen))
    {
    }

    private SafeFlacWaveStream(SafeFlacDecoder decoder)
    {
        _decoder = decoder;

        int srcBits = _decoder.BitsPerSample;
        int outBits;
        if (srcBits <= 16)
        {
            // Scale 4..16-bit (including 8, 12, 15, 16-bit) MSB-aligned into standard 16-bit signed PCM
            outBits = 16;
            _bitShiftLeft = 16 - srcBits;
        }
        else if (srcBits <= 24)
        {
            // Scale 17..24-bit (including 20, 24-bit) MSB-aligned into standard 24-bit signed PCM
            outBits = 24;
            _bitShiftLeft = 24 - srcBits;
        }
        else if (srcBits <= 32)
        {
            // Scale 25..32-bit (including 32-bit) MSB-aligned into 32-bit signed integer PCM
            outBits = 32;
            _bitShiftLeft = 32 - srcBits;
        }
        else
        {
            _decoder.Dispose();
            throw new NotSupportedException($"Unsupported FLAC bit depth: {srcBits}-bit.");
        }

        _outputBytesPerSample = outBits / 8;
        _waveFormat = new WaveFormat(_decoder.SampleRate, outBits, _decoder.ChannelCount);
        _framePcmBytes = new byte[_decoder.MaxSamplesPerFrame * _waveFormat.BlockAlign];

        // Seed runtime index with frame 0 and any SEEKTABLE entries
        _runtimeIndex.Add(new SafeFlacDecoder.SeekPoint(0, 0, 0));
        foreach (var sp in _decoder.SeekPoints)
        {
            if (sp.SampleNumber > 0)
            {
                _runtimeIndex.Add(sp);
            }
        }

        try
        {
            if (_decoder.StreamSampleCount.HasValue && _decoder.StreamSampleCount.Value > 0)
            {
                _totalSamples = _decoder.StreamSampleCount.Value;
            }
            else
            {
                // Unknown total sample count in STREAMINFO (e.g. IETF subset/45): determine length safely
                _totalSamples = ProbeUnknownTotalSamples();
                _decoder.SeekToFrameOffset(_decoder.FirstFrameOffset, 0);
            }

            // Eagerly decode frame 0 so truncated/corrupt first frames fail fast during OpenStream
            // (and frame 0 is immediately ready in _framePcmBytes for the first Read() at zero extra cost).
            if (!DecodeNextFrameIntoBuffer())
            {
                _eofReached = true;
            }
        }
        catch
        {
            _decoder.Dispose();
            throw;
        }
    }

    private long ProbeUnknownTotalSamples()
    {
        long total = 0;
        int frames = 0;
        var sw = Stopwatch.StartNew();
        while (_decoder.DecodeFrame())
        {
            if (frames % SparseIndexFrameStride == 0 && total > 0)
            {
                RecordRuntimeAnchor(total, _decoder.CurrentFrameByteOffset - _decoder.FirstFrameOffset, _decoder.BufferSampleCount);
            }
            total += _decoder.BufferSampleCount;
            frames++;
            if (frames >= UnknownLengthScanMaxFrames ||
                sw.ElapsedMilliseconds >= UnknownLengthScanMaxMs ||
                (_decoder.NextFrameByteOffset - _decoder.FirstFrameOffset > UnknownLengthScanLimitBytes && frames > 4096))
            {
                break;
            }
        }
        return total;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        if (count == 0) return 0;

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            int totalRead = 0;
            while (totalRead < count)
            {
                int avail = _framePcmByteLength - _framePcmByteOffset;
                if (avail > 0)
                {
                    int toCopy = Math.Min(avail, count - totalRead);
                    Buffer.BlockCopy(_framePcmBytes, _framePcmByteOffset, buffer, offset + totalRead, toCopy);
                    _framePcmByteOffset += toCopy;
                    _positionBytes += toCopy;
                    totalRead += toCopy;
                    continue;
                }

                if (_eofReached)
                {
                    break;
                }

                if (!DecodeNextFrameIntoBuffer())
                {
                    _eofReached = true;
                    break;
                }
            }

            return totalRead;
        }
    }

    private bool DecodeNextFrameIntoBuffer()
    {
        long sampleBefore = _decoder.RunningSampleCount;
        if (!_decoder.DecodeFrame())
        {
            _framePcmByteLength = 0;
            _framePcmByteOffset = 0;
            return false;
        }

        if (_framesDecodedSinceAnchor >= SparseIndexFrameStride)
        {
            RecordRuntimeAnchor(sampleBefore, _decoder.CurrentFrameByteOffset - _decoder.FirstFrameOffset, _decoder.BufferSampleCount);
            _framesDecodedSinceAnchor = 0;
        }
        else
        {
            _framesDecodedSinceAnchor++;
        }

        int sampleCount = _decoder.BufferSampleCount;
        int blockAlign = _waveFormat.BlockAlign;
        int byteCount = sampleCount * blockAlign;
        if (_framePcmBytes.Length < byteCount)
        {
            _framePcmBytes = new byte[byteCount];
        }

        PackSamplesToPcm(_decoder.BufferSamples, sampleCount, _decoder.ChannelCount, _outputBytesPerSample, _bitShiftLeft, _framePcmBytes);
        _framePcmByteOffset = 0;
        _framePcmByteLength = byteCount;

        if (_decoder.RunningSampleCount > _totalSamples)
        {
            _totalSamples = _decoder.RunningSampleCount;
        }

        return true;
    }

    private static void PackSamplesToPcm(
        long[][] samples,
        int sampleCount,
        int channels,
        int outputBytesPerSample,
        int shiftLeft,
        byte[] dest)
    {
        int blockAlign = channels * outputBytesPerSample;

        if (outputBytesPerSample == 2)
        {
            Span<byte> span = dest;
            for (int ch = 0; ch < channels; ch++)
            {
                long[] src = samples[ch];
                int off = ch * 2;
                if (shiftLeft == 0)
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(off, 2), (short)src[i]);
                        off += blockAlign;
                    }
                }
                else
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(off, 2), (short)(src[i] << shiftLeft));
                        off += blockAlign;
                    }
                }
            }
        }
        else if (outputBytesPerSample == 3)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                long[] src = samples[ch];
                int off = ch * 3;
                if (shiftLeft == 0)
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        long s = src[i];
                        dest[off] = (byte)s;
                        dest[off + 1] = (byte)(s >> 8);
                        dest[off + 2] = (byte)(s >> 16);
                        off += blockAlign;
                    }
                }
                else
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        long s = src[i] << shiftLeft;
                        dest[off] = (byte)s;
                        dest[off + 1] = (byte)(s >> 8);
                        dest[off + 2] = (byte)(s >> 16);
                        off += blockAlign;
                    }
                }
            }
        }
        else if (outputBytesPerSample == 4)
        {
            Span<byte> span = dest;
            for (int ch = 0; ch < channels; ch++)
            {
                long[] src = samples[ch];
                int off = ch * 4;
                if (shiftLeft == 0)
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(off, 4), (int)src[i]);
                        off += blockAlign;
                    }
                }
                else
                {
                    for (int i = 0; i < sampleCount; i++)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(off, 4), (int)(src[i] << shiftLeft));
                        off += blockAlign;
                    }
                }
            }
        }
    }

    private void SeekToBytePosition(long targetBytePosition)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        int blockAlign = _waveFormat.BlockAlign;
        long maxBytes = Math.Max(0, Length);
        long clampedBytes = Math.Clamp(targetBytePosition, 0, maxBytes);
        long targetSample = clampedBytes / blockAlign;

        // Fast path: already positioned inside the currently decoded frame buffer
        long currentFrameEndSample = _decoder.RunningSampleCount;
        long currentFrameStartSample = currentFrameEndSample - _decoder.BufferSampleCount;
        if (_framePcmByteLength > 0 && targetSample >= currentFrameStartSample && targetSample < currentFrameEndSample)
        {
            int relSample = (int)(targetSample - currentFrameStartSample);
            _framePcmByteOffset = relSample * blockAlign;
            _positionBytes = targetSample * blockAlign;
            _eofReached = false;
            return;
        }

        // Find highest known anchor (from SEEKTABLE or runtime sparse index) with SampleNumber <= targetSample
        SafeFlacDecoder.SeekPoint anchor = FindBestAnchor(targetSample);

        // If seeking forward from our current position and our current position is closer than the anchor, stay put
        if (!_eofReached && _decoder.RunningSampleCount <= targetSample && _decoder.RunningSampleCount >= anchor.SampleNumber)
        {
            _framePcmByteOffset = _framePcmByteLength;
        }
        else
        {
            _decoder.SeekToFrameOffset(_decoder.FirstFrameOffset + anchor.StreamOffset, anchor.SampleNumber);
            _framePcmByteOffset = 0;
            _framePcmByteLength = 0;
            _framesDecodedSinceAnchor = 0;
            _eofReached = false;
        }

        // Decode forward frame-by-frame until we reach targetSample
        while (_decoder.RunningSampleCount <= targetSample)
        {
            long frameStartSample = _decoder.RunningSampleCount;
            if (!DecodeNextFrameIntoBuffer())
            {
                _eofReached = true;
                _positionBytes = _decoder.RunningSampleCount * blockAlign;
                return;
            }

            if (targetSample >= frameStartSample && targetSample < _decoder.RunningSampleCount)
            {
                int relSample = (int)(targetSample - frameStartSample);
                _framePcmByteOffset = relSample * blockAlign;
                _positionBytes = targetSample * blockAlign;
                return;
            }
        }

        _positionBytes = targetSample * blockAlign;
    }

    private SafeFlacDecoder.SeekPoint FindBestAnchor(long targetSample)
    {
        int lo = 0;
        int hi = _runtimeIndex.Count - 1;
        int best = 0;

        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_runtimeIndex[mid].SampleNumber <= targetSample)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return _runtimeIndex[best];
    }

    private void RecordRuntimeAnchor(long sampleNumber, long relByteOffset, int frameSamples)
    {
        if (sampleNumber <= 0 || relByteOffset < 0) return;
        if (_runtimeIndex.Count > 50_000) return;

        var last = _runtimeIndex[^1];
        if (sampleNumber > last.SampleNumber)
        {
            _runtimeIndex.Add(new SafeFlacDecoder.SeekPoint(sampleNumber, relByteOffset, frameSamples));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            if (disposing)
            {
                _decoder.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
