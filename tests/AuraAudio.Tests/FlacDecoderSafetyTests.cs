using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AuraAudio.Audio;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Audio.Controller;
using AuraAudio.Audio.Flac;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Scanner;
using NAudio.Wave;
using Xunit;

namespace AuraAudio.Tests;

/// <summary>
/// Unit and opt-in corpus regression tests for pure-managed RFC 9639 FLAC decoding
/// (CRITICAL #1 crash safety, bounds/time guards, MD5 correctness, seeking, gapless,
/// scanner resilience, Inspector error handling, and controller failure reporting).
/// All synthetic FLAC bitstreams are generated in-memory with zero third-party audio committed.
/// </summary>
public sealed class FlacDecoderSafetyTests : IDisposable
{
    private readonly string _tempDir;

    public FlacDecoderSafetyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_FlacSafety_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void TruncatedAndMutatedFrames_ThrowManagedException_NeverCrash()
    {
        // Build a valid multi-frame 16-bit/44.1kHz stereo FLAC, then systematically truncate & corrupt it
        // to reproduce the unsafe pointer-overrun paths that caused AccessViolationException in NAudio.Flac.
        int[] samplesL = Enumerable.Range(0, 1024).Select(i => (int)(Math.Sin(i * 0.13) * 20_000)).ToArray();
        int[] samplesR = Enumerable.Range(0, 1024).Select(i => (int)(Math.Cos(i * 0.17) * 18_000)).ToArray();
        byte[] validFlac = SyntheticFlacBuilder.BuildVerbatimFlac(
            sampleRate: 44_100,
            bitsPerSample: 16,
            channelSamples: [samplesL, samplesR],
            frameSizes: [256, 256, 256, 256]);

        // 1. Truncate right inside frame 0 and inside frame 2
        int[] cutLengths = [4, 20, 42, 48, 80, validFlac.Length / 2, validFlac.Length - 1, validFlac.Length - 2];
        foreach (int len in cutLengths)
        {
            byte[] truncated = validFlac[..len];
            Exception? ex = Record.Exception(() =>
            {
                using var ms = new MemoryStream(truncated, writable: false);
                using var ws = new SafeFlacWaveStream(ms);
                DrainAllBytes(ws);
            });
            Assert.True(ex is InvalidDataException or EndOfStreamException,
                $"Expected InvalidDataException or EndOfStreamException at cut {len}, got {ex?.GetType().Name ?? "none"}.");
        }

        // 2. Flip bits in frame payload -> must fail CRC-16 with InvalidDataException
        byte[] badCrc16 = (byte[])validFlac.Clone();
        badCrc16[badCrc16.Length - 10] ^= 0x5A;
        Assert.Throws<InvalidDataException>(() =>
        {
            using var ms = new MemoryStream(badCrc16, writable: false);
            using var ws = new SafeFlacWaveStream(ms);
            DrainAllBytes(ws);
        });

        // 3. Corrupt frame header byte -> must fail CRC-8 with InvalidDataException
        byte[] badCrc8 = (byte[])validFlac.Clone();
        int frameStart = 4 + 4 + 34; // "fLaC" + metadata header + STREAMINFO
        badCrc8[frameStart + 2] ^= 0x01;
        Assert.Throws<InvalidDataException>(() =>
        {
            using var ms = new MemoryStream(badCrc8, writable: false);
            using var ws = new SafeFlacWaveStream(ms);
            DrainAllBytes(ws);
        });
    }

    [Fact]
    public void CorruptedRiceResidual_ThrowsManagedException_WithinBoundedTime()
    {
        // Construct a frame with a FIXED(order=0) Rice subframe filled with 0x00 bytes (pathological unary zeros)
        // followed by EOF, ensuring the decoder aborts cleanly with a managed IOException instead of hanging or AV.
        byte[] raw = SyntheticFlacBuilder.BuildCorruptedRiceZeroFloodFlac(payloadZeroBytes: 4096);
        var sw = Stopwatch.StartNew();

        Exception? ex = Record.Exception(() =>
        {
            using var ms = new MemoryStream(raw, writable: false);
            using var ws = new SafeFlacWaveStream(ms);
            DrainAllBytes(ws);
        });
        Assert.True(ex is InvalidDataException or EndOfStreamException,
            $"Expected InvalidDataException or EndOfStreamException, got {ex?.GetType().Name ?? "none"}.");

        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"Corrupted Rice stream took {sw.ElapsedMilliseconds}ms (expected <500ms).");
    }

    [Fact]
    public void ConsecutiveBlocksizeOneFrames_RejectedFast_NoStall()
    {
        // Reproduces IETF faulty/09 (repeated blocksize=1 frames that caused a 9.2s stall in NAudio.Flac).
        int[] mono = Enumerable.Range(0, 64).Select(i => i * 100).ToArray();
        byte[] badStream = SyntheticFlacBuilder.BuildVerbatimFlac(
            sampleRate: 44_100,
            bitsPerSample: 16,
            channelSamples: [mono],
            frameSizes: Enumerable.Repeat(1, 64).ToArray(),
            overrideMinBlockSize: 16,
            overrideMaxBlockSize: 64);

        var sw = Stopwatch.StartNew();
        Assert.Throws<InvalidDataException>(() =>
        {
            using var ms = new MemoryStream(badStream, writable: false);
            using var ws = new SafeFlacWaveStream(ms);
            DrainAllBytes(ws);
        });
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 250, $"Consecutive blocksize-1 rejection took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void VariableBlocksize_MonoPartialLastFrame_EmptySeekTable_MatchesExactMd5()
    {
        int[] mono = Enumerable.Range(0, 777).Select(i => (int)(Math.Sin(i * 0.09) * 30_000)).ToArray();
        byte[] flac = SyntheticFlacBuilder.BuildVerbatimFlac(
            sampleRate: 48_000,
            bitsPerSample: 16,
            channelSamples: [mono],
            frameSizes: [256, 192, 300, 29],
            includeEmptySeekTable: true,
            setUnknownTotalSamples: false);

        using var ms = new MemoryStream(flac, writable: false);
        using var ws = new SafeFlacWaveStream(ms);
        byte[] decoded = DrainAllBytes(ws);

        Assert.Equal(mono.Length * 2, decoded.Length);
        byte[] expectedPcm = SyntheticFlacBuilder.EncodeCanonicalPcm([mono], 16);
        Assert.Equal(expectedPcm, decoded);
        Assert.Equal(MD5.HashData(expectedPcm), MD5.HashData(decoded));
    }

    [Fact]
    public void UnknownTotalSamplesInStreamInfo_DecodesExactLengthAndSamples()
    {
        int[] left = Enumerable.Range(0, 512).Select(i => (int)(Math.Sin(i * 0.05) * 4_000_000)).ToArray();
        int[] right = Enumerable.Range(0, 512).Select(i => (int)(Math.Cos(i * 0.07) * 4_000_000)).ToArray();
        byte[] flac = SyntheticFlacBuilder.BuildVerbatimFlac(
            sampleRate: 96_000,
            bitsPerSample: 24,
            channelSamples: [left, right],
            frameSizes: [256, 256],
            setUnknownTotalSamples: true);

        using var ms = new MemoryStream(flac, writable: false);
        using var ws = new SafeFlacWaveStream(ms);

        Assert.Equal(512 * 2 * 3, ws.Length);
        byte[] decoded = DrainAllBytes(ws);
        byte[] expectedPcm = SyntheticFlacBuilder.EncodeCanonicalPcm([left, right], 24);
        Assert.Equal(expectedPcm, decoded);
    }

    [Fact]
    public void RiceEscapeCodeZero_DecodesZeroResidualsBitExactly()
    {
        // CONSTANT-value / FIXED-order-1 linear ramp has all residuals == 0, encoded with Rice escape code 0.
        int[] ramp = Enumerable.Range(0, 256).Select(i => 1000).ToArray();
        byte[] flac = SyntheticFlacBuilder.BuildRiceEscapeZeroFlac(sampleRate: 44_100, bitsPerSample: 16, samples: ramp);

        using var ms = new MemoryStream(flac, writable: false);
        using var ws = new SafeFlacWaveStream(ms);
        byte[] decoded = DrainAllBytes(ws);
        byte[] expectedPcm = SyntheticFlacBuilder.EncodeCanonicalPcm([ramp], 16);
        Assert.Equal(expectedPcm, decoded);
    }

    [Fact]
    public void SampleAccurateSeeking_MatchesSequentialDecode_ForwardBackwardAndBoundaries()
    {
        int totalSamples = 2048;
        int[] left = Enumerable.Range(0, totalSamples).Select(i => (int)(Math.Sin(i * 0.031) * 28_000)).ToArray();
        int[] right = Enumerable.Range(0, totalSamples).Select(i => (int)(Math.Cos(i * 0.047) * 26_000)).ToArray();
        byte[] flac = SyntheticFlacBuilder.BuildVerbatimFlac(
            sampleRate: 44_100,
            bitsPerSample: 16,
            channelSamples: [left, right],
            frameSizes: [256, 256, 256, 256, 256, 256, 256, 256]);

        byte[] fullPcm = SyntheticFlacBuilder.EncodeCanonicalPcm([left, right], 16);

        using var ms = new MemoryStream(flac, writable: false);
        using var ws = new SafeFlacWaveStream(ms);
        int blockAlign = ws.WaveFormat.BlockAlign;

        // Seek forward mid-frame, backward across multiple frames, to 0, and near EOF
        int[] seekSamples = [600, 1500, 123, 0, 1900, 256, 2047];
        foreach (int samplePos in seekSamples)
        {
            long bytePos = (long)samplePos * blockAlign;
            ws.Position = bytePos;
            Assert.Equal(bytePos, ws.Position);

            int readBytes = Math.Min(320 * blockAlign, fullPcm.Length - (int)bytePos);
            byte[] slice = new byte[readBytes];
            int got = ReadExact(ws, slice);
            Assert.Equal(readBytes, got);
            Assert.Equal(fullPcm.AsSpan((int)bytePos, readBytes).ToArray(), slice);
        }
    }

    [Fact]
    public void GaplessQueue_TwoFlacTracks_StitchesWithoutDropsOrGlitches()
    {
        int[] aL = Enumerable.Range(0, 512).Select(i => 1000 + i).ToArray();
        int[] aR = Enumerable.Range(0, 512).Select(i => -1000 - i).ToArray();
        int[] bL = Enumerable.Range(0, 512).Select(i => 5000 + i).ToArray();
        int[] bR = Enumerable.Range(0, 512).Select(i => -5000 - i).ToArray();

        string pathA = Path.Combine(_tempDir, "gapless_a.flac");
        string pathB = Path.Combine(_tempDir, "gapless_b.flac");
        File.WriteAllBytes(pathA, SyntheticFlacBuilder.BuildVerbatimFlac(44_100, 16, [aL, aR], [256, 256]));
        File.WriteAllBytes(pathB, SyntheticFlacBuilder.BuildVerbatimFlac(44_100, 16, [bL, bR], [256, 256]));

        byte[] expectedA = SyntheticFlacBuilder.EncodeCanonicalPcm([aL, aR], 16);
        byte[] expectedB = SyntheticFlacBuilder.EncodeCanonicalPcm([bL, bR], 16);

        using var streamA = AudioDecoderFactory.OpenStream(pathA);
        var streamB = AudioDecoderFactory.OpenStream(pathB);
        using var queue = new GaplessStreamQueue(streamA);
        queue.EnqueueNext(streamB);

        var combined = new MemoryStream();
        byte[] buf = new byte[512];
        int n;
        while ((n = queue.Read(buf, 0, buf.Length)) > 0)
        {
            combined.Write(buf, 0, n);
        }

        byte[] actual = combined.ToArray();
        Assert.Equal(expectedA.Length + expectedB.Length, actual.Length);
        // After the initial AntiPop fade-in on track A, track B starts seamlessly at exact byte offset expectedA.Length
        Assert.Equal(expectedB, actual.AsSpan(expectedA.Length, expectedB.Length).ToArray());
    }

    [Fact]
    public async Task LibraryScanner_SkipsCorruptFlac_LogsAndIndexesValidNeighbor()
    {
        string scanDir = Path.Combine(_tempDir, "scan");
        Directory.CreateDirectory(scanDir);

        int[] sL = Enumerable.Range(0, 512).Select(i => (int)(Math.Sin(i * 0.1) * 16_000)).ToArray();
        int[] sR = Enumerable.Range(0, 512).Select(i => (int)(Math.Cos(i * 0.1) * 16_000)).ToArray();
        string validPath = Path.Combine(scanDir, "01 - Valid Track.flac");
        string corruptPath = Path.Combine(scanDir, "02 - Corrupt Track.flac");

        File.WriteAllBytes(validPath, SyntheticFlacBuilder.BuildVerbatimFlac(44_100, 16, [sL, sR], [256, 256]));
        // Completely corrupt / invalid bytes with .flac extension
        File.WriteAllBytes(corruptPath, [0x00, 0xFF, 0x13, 0x37, 0xDE, 0xAD, 0xBE, 0xEF]);

        string dbPath = Path.Combine(_tempDir, "scanner_test.db");
        using var db = new DatabaseManager(dbPath);
        db.Initialize();
        var scanner = new LibraryScanner(db);

        var report = await scanner.ScanDirectoryAsync(scanDir);

        Assert.Equal(2, report.TotalFound);
        Assert.Equal(2, report.Processed);
        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.Failed);
        Assert.Single(db.GetExistingFileSignatures());
    }

    [Fact]
    public async Task InspectorAndController_HandleCorruptFlacGracefully_NoCrash()
    {
        string corruptPath = Path.Combine(_tempDir, "corrupt_frame.flac");
        int[] samples = Enumerable.Range(0, 512).Select(i => (int)(Math.Sin(i * 0.1) * 20_000)).ToArray();
        byte[] valid = SyntheticFlacBuilder.BuildVerbatimFlac(44_100, 16, [samples, samples], [256, 256]);
        // Keep valid STREAMINFO (42 bytes), truncate right in the middle of frame 0
        File.WriteAllBytes(corruptPath, valid[..60]);

        // 1. Inspector (NyquistSpectrumAnalyzer) must throw a catchable managed exception, never terminate the process
        Exception? inspectorEx = await Record.ExceptionAsync(() => NyquistSpectrumAnalyzer.AnalyzeAsync(corruptPath));
        Assert.True(inspectorEx is InvalidDataException or EndOfStreamException,
            $"Expected InvalidDataException or EndOfStreamException, got {inspectorEx?.GetType().Name ?? "none"}.");

        // 2. Playback controller must stop cleanly and fire PlaybackFailed with an informative message
        using var fakeEngine = new StubAudioOutputEngine();
        using var controller = new AudiophilePlaybackController(fakeEngine);
        PlaybackErrorEventArgs? capturedError = null;
        controller.PlaybackFailed += (_, args) => capturedError = args;

        var track = new Track(
            id: 1,
            filePath: corruptPath,
            title: "Corrupt Test Track",
            artist: "Test",
            sortArtist: "Test",
            album: "Test",
            albumArtist: "Test",
            year: 2026,
            trackNumber: 1,
            discNumber: 1,
            duration: TimeSpan.FromSeconds(1),
            streamSpec: new AudioStreamSpec(AudioCodec.FLAC, 44_100, BitDepth.Bit16, 2, 1411),
            fileSizeBytes: 60,
            dateAdded: DateTime.UtcNow,
            hasEmbeddedCover: false,
            coverArtCacheKey: null);

        await controller.PlayTrackAsync(track);

        Assert.NotNull(capturedError);
        Assert.Equal(corruptPath, capturedError!.Track.FilePath);
        Assert.False(string.IsNullOrWhiteSpace(capturedError.Message));
        Assert.Equal(PlaybackStatus.Stopped, controller.Status);
    }

    [SkippableFact]
    public void ExternalCorpus_Critical1_ZeroFatalCrashes_Faulty09Fast_SubsetMd5Match()
    {
        string? corpusRoot = Environment.GetEnvironmentVariable("AURA_BENCHMARK_CORPUS");
        Skip.If(string.IsNullOrWhiteSpace(corpusRoot) || !Directory.Exists(corpusRoot),
            "Set AURA_BENCHMARK_CORPUS to run external FLAC corpus regression tests.");

        // 1. IETF faulty/09 must fail cleanly in <500ms (was 9.2s stall in v2.0.1)
        string faulty09 = Path.Combine(corpusRoot!, "ietf-flac-test-files", "faulty", "09 - blocksize 1.flac");
        if (File.Exists(faulty09))
        {
            var sw = Stopwatch.StartNew();
            Assert.ThrowsAny<Exception>(() =>
            {
                using var ws = new SafeFlacWaveStream(faulty09);
                DrainAllBytes(ws);
            });
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 500, $"faulty/09 took {sw.ElapsedMilliseconds}ms.");
        }

        // 2. Every file in ietf/subset with 16/24/32-bit must decode cleanly with exact STREAMINFO MD5
        string subsetDir = Path.Combine(corpusRoot!, "ietf-flac-test-files", "subset");
        if (Directory.Exists(subsetDir))
        {
            foreach (string file in Directory.GetFiles(subsetDir, "*.flac").OrderBy(x => x))
            {
                using var dec = new SafeFlacDecoder(file);
                if (dec.BitsPerSample is not (16 or 24 or 32)) continue;
                byte[]? expectedMd5 = dec.ExpectedMd5;
                dec.Dispose();

                using var ws = new SafeFlacWaveStream(file);
                byte[] pcm = DrainAllBytes(ws);
                if (expectedMd5 != null)
                {
                    Assert.Equal(expectedMd5, MD5.HashData(pcm));
                }
            }
        }
    }

    private static byte[] DrainAllBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        byte[] buf = new byte[16_384];
        int read;
        while ((read = stream.Read(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    private static int ReadExact(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    private sealed class StubAudioOutputEngine : IAudioOutputEngine
    {
        public AudioDevice? CurrentDevice { get; private set; }
        public WaveFormat? CurrentFormat { get; private set; }
        public PlaybackStatus Status { get; private set; } = PlaybackStatus.Stopped;
        public bool IsExclusiveLocked => false;
        public float Volume { get; set; } = 1.0f;
        public event EventHandler<PlaybackStatus>? StatusChanged;

        public void Initialize(AudioDevice device, IWaveProvider waveProvider, int latencyMs = 50, AudioDriverType driverType = AudioDriverType.WASAPI_Exclusive)
        {
            CurrentDevice = device;
            CurrentFormat = waveProvider.WaveFormat;
            Status = PlaybackStatus.Stopped;
            StatusChanged?.Invoke(this, Status);
        }

        public void Play() { Status = PlaybackStatus.Playing; StatusChanged?.Invoke(this, Status); }
        public void Pause() { Status = PlaybackStatus.Paused; StatusChanged?.Invoke(this, Status); }
        public void Stop() { Status = PlaybackStatus.Stopped; StatusChanged?.Invoke(this, Status); }
        public void Dispose() { }
    }

    /// <summary>
    /// Minimal RFC 9639 FLAC bitstream generator for in-memory deterministic unit tests.
    /// Produces compliant STREAMINFO (with exact PCM MD5), optional SEEKTABLE, CRC-8 frame headers,
    /// verbatim / Rice-escape-0 subframes, and CRC-16 footers.
    /// </summary>
    internal static class SyntheticFlacBuilder
    {
        public static byte[] EncodeCanonicalPcm(int[][] channelSamples, int bitsPerSample)
        {
            int channels = channelSamples.Length;
            int totalSamples = channelSamples[0].Length;
            int bytesPerSample = (bitsPerSample + 7) / 8;
            byte[] pcm = new byte[totalSamples * channels * bytesPerSample];
            int idx = 0;
            for (int i = 0; i < totalSamples; i++)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    int v = channelSamples[ch][i];
                    for (int b = 0; b < bytesPerSample; b++)
                    {
                        pcm[idx++] = (byte)((v >> (b * 8)) & 0xFF);
                    }
                }
            }
            return pcm;
        }

        public static byte[] BuildVerbatimFlac(
            int sampleRate,
            int bitsPerSample,
            int[][] channelSamples,
            int[] frameSizes,
            bool includeEmptySeekTable = false,
            bool setUnknownTotalSamples = false,
            int? overrideMinBlockSize = null,
            int? overrideMaxBlockSize = null)
        {
            int channels = channelSamples.Length;
            int totalSamples = channelSamples[0].Length;
            byte[] md5 = MD5.HashData(EncodeCanonicalPcm(channelSamples, bitsPerSample));

            int minBlock = overrideMinBlockSize ?? frameSizes.Min();
            int maxBlock = overrideMaxBlockSize ?? frameSizes.Max();
            if (minBlock < 16 && overrideMinBlockSize == null) minBlock = 16;
            if (maxBlock < 16 && overrideMaxBlockSize == null) maxBlock = 16;

            using var ms = new MemoryStream();
            WriteAscii(ms, "fLaC");

            // Metadata block 0: STREAMINFO (34 bytes)
            bool streamInfoLast = !includeEmptySeekTable;
            WriteMetadataHeader(ms, isLast: streamInfoLast, blockType: 0, length: 34);
            WriteStreamInfo(ms, minBlock, maxBlock, sampleRate, channels, bitsPerSample, setUnknownTotalSamples ? 0 : totalSamples, md5);

            if (includeEmptySeekTable)
            {
                WriteMetadataHeader(ms, isLast: true, blockType: 3, length: 0);
            }

            bool variableBlockSize = frameSizes.Distinct().Count() > 1;
            int sampleOffset = 0;
            for (int f = 0; f < frameSizes.Length; f++)
            {
                int bs = frameSizes[f];
                byte[] frame = BuildVerbatimFrame(
                    sampleRate,
                    bitsPerSample,
                    channels,
                    channelSamples,
                    sampleOffset,
                    bs,
                    variableBlockSize,
                    codedNumber: variableBlockSize ? (ulong)sampleOffset : (ulong)f);
                ms.Write(frame, 0, frame.Length);
                sampleOffset += bs;
            }

            return ms.ToArray();
        }

        public static byte[] BuildRiceEscapeZeroFlac(int sampleRate, int bitsPerSample, int[] samples)
        {
            int totalSamples = samples.Length;
            byte[] md5 = MD5.HashData(EncodeCanonicalPcm([samples], bitsPerSample));

            using var ms = new MemoryStream();
            WriteAscii(ms, "fLaC");
            WriteMetadataHeader(ms, isLast: true, blockType: 0, length: 34);
            WriteStreamInfo(ms, totalSamples, totalSamples, sampleRate, 1, bitsPerSample, totalSamples, md5);

            var bw = new BitWriter();
            WriteFrameHeader(bw, sampleRate, bitsPerSample, channels: 1, blockSize: totalSamples, variableBlockSize: false, codedNumber: 0);

            // Subframe: FIXED order 1 (type = 8 + 1 = 9), no wasted bits
            bw.WriteBits(0, 1); // padding
            bw.WriteBits(9, 6); // FIXED order 1
            bw.WriteBits(0, 1); // wasted bits flag = 0
            bw.WriteSigned(samples[0], bitsPerSample); // warmup sample 0

            // Residual: method 0 (4-bit Rice param), partitionOrder 0 (1 partition), escape param = 15, numBits = 0 (escape code 0)
            bw.WriteBits(0, 2);  // RICE (4-bit param)
            bw.WriteBits(0, 4);  // partition order 0
            bw.WriteBits(15, 4); // escape code
            bw.WriteBits(0, 5);  // 0 bits -> all residuals are 0!

            bw.AlignToByte();
            byte[] body = bw.ToArray();
            ushort crc16 = ComputeCrc16(body);
            ms.Write(body, 0, body.Length);
            ms.WriteByte((byte)(crc16 >> 8));
            ms.WriteByte((byte)(crc16 & 0xFF));
            return ms.ToArray();
        }

        public static byte[] BuildCorruptedRiceZeroFloodFlac(int payloadZeroBytes)
        {
            using var ms = new MemoryStream();
            WriteAscii(ms, "fLaC");
            WriteMetadataHeader(ms, isLast: true, blockType: 0, length: 34);
            WriteStreamInfo(ms, 256, 256, 44_100, 1, 16, 256, new byte[16]);

            var bw = new BitWriter();
            WriteFrameHeader(bw, 44_100, 16, 1, 256, variableBlockSize: false, codedNumber: 0);
            bw.WriteBits(0, 1); // subframe pad
            bw.WriteBits(8, 6); // FIXED order 0
            bw.WriteBits(0, 1); // wasted bits = 0
            bw.WriteBits(0, 2); // RICE
            bw.WriteBits(0, 4); // partition order 0
            bw.WriteBits(0, 4); // k = 0
            byte[] headerBytes = bw.ToArray();
            ms.Write(headerBytes, 0, headerBytes.Length);
            ms.Write(new byte[payloadZeroBytes], 0, payloadZeroBytes); // unary zero flood + EOF
            return ms.ToArray();
        }

        private static byte[] BuildVerbatimFrame(
            int sampleRate,
            int bitsPerSample,
            int channels,
            int[][] channelSamples,
            int sampleOffset,
            int blockSize,
            bool variableBlockSize,
            ulong codedNumber)
        {
            var bw = new BitWriter();
            WriteFrameHeader(bw, sampleRate, bitsPerSample, channels, blockSize, variableBlockSize, codedNumber);

            for (int ch = 0; ch < channels; ch++)
            {
                bw.WriteBits(0, 1); // subframe pad
                bw.WriteBits(1, 6); // VERBATIM
                bw.WriteBits(0, 1); // wasted bits flag = 0
                for (int i = 0; i < blockSize; i++)
                {
                    bw.WriteSigned(channelSamples[ch][sampleOffset + i], bitsPerSample);
                }
            }

            bw.AlignToByte();
            byte[] frameWithoutCrc16 = bw.ToArray();
            ushort crc16 = ComputeCrc16(frameWithoutCrc16);

            byte[] full = new byte[frameWithoutCrc16.Length + 2];
            Buffer.BlockCopy(frameWithoutCrc16, 0, full, 0, frameWithoutCrc16.Length);
            full[^2] = (byte)(crc16 >> 8);
            full[^1] = (byte)(crc16 & 0xFF);
            return full;
        }

        private static void WriteFrameHeader(
            BitWriter bw,
            int sampleRate,
            int bitsPerSample,
            int channels,
            int blockSize,
            bool variableBlockSize,
            ulong codedNumber)
        {
            bw.WriteBits(0x3FFE, 14);
            bw.WriteBits(0, 1); // reserved
            bw.WriteBits(variableBlockSize ? 1UL : 0UL, 1);

            int bsCode = blockSize <= 256 ? 6 : 7;
            bw.WriteBits((ulong)bsCode, 4);

            int srCode = sampleRate switch
            {
                44_100 => 9,
                48_000 => 10,
                96_000 => 11,
                _ => 0 // from STREAMINFO
            };
            bw.WriteBits((ulong)srCode, 4);
            bw.WriteBits((ulong)(channels - 1), 4);

            int bpsCode = bitsPerSample switch
            {
                8 => 1,
                12 => 2,
                16 => 4,
                20 => 5,
                24 => 6,
                32 => 7,
                _ => 0 // from STREAMINFO (e.g. 15-bit)
            };
            bw.WriteBits((ulong)bpsCode, 3);
            bw.WriteBits(0, 1); // reserved

            WriteUtf8Number(bw, codedNumber);

            if (bsCode == 6) bw.WriteBits((ulong)(blockSize - 1), 8);
            else if (bsCode == 7) bw.WriteBits((ulong)(blockSize - 1), 16);

            byte crc8 = ComputeCrc8(bw.ToArray());
            bw.WriteBits(crc8, 8);
        }

        private static void WriteUtf8Number(BitWriter bw, ulong val)
        {
            if (val < 0x80)
            {
                bw.WriteBits(val, 8);
            }
            else if (val < 0x800)
            {
                bw.WriteBits(0xC0 | (val >> 6), 8);
                bw.WriteBits(0x80 | (val & 0x3F), 8);
            }
            else if (val < 0x10000)
            {
                bw.WriteBits(0xE0 | (val >> 12), 8);
                bw.WriteBits(0x80 | ((val >> 6) & 0x3F), 8);
                bw.WriteBits(0x80 | (val & 0x3F), 8);
            }
            else
            {
                bw.WriteBits(0xF0 | (val >> 18), 8);
                bw.WriteBits(0x80 | ((val >> 12) & 0x3F), 8);
                bw.WriteBits(0x80 | ((val >> 6) & 0x3F), 8);
                bw.WriteBits(0x80 | (val & 0x3F), 8);
            }
        }

        private static void WriteMetadataHeader(Stream s, bool isLast, int blockType, int length)
        {
            s.WriteByte((byte)((isLast ? 0x80 : 0) | (blockType & 0x7F)));
            s.WriteByte((byte)((length >> 16) & 0xFF));
            s.WriteByte((byte)((length >> 8) & 0xFF));
            s.WriteByte((byte)(length & 0xFF));
        }

        private static void WriteStreamInfo(
            Stream s,
            int minBlockSize,
            int maxBlockSize,
            int sampleRate,
            int channels,
            int bitsPerSample,
            long totalSamples,
            byte[] md5)
        {
            var bw = new BitWriter();
            bw.WriteBits((ulong)minBlockSize, 16);
            bw.WriteBits((ulong)maxBlockSize, 16);
            bw.WriteBits(0, 24); // min frame size unknown
            bw.WriteBits(0, 24); // max frame size unknown
            bw.WriteBits((ulong)sampleRate, 20);
            bw.WriteBits((ulong)(channels - 1), 3);
            bw.WriteBits((ulong)(bitsPerSample - 1), 5);
            bw.WriteBits((ulong)totalSamples, 36);
            byte[] head = bw.ToArray();
            s.Write(head, 0, head.Length);
            s.Write(md5, 0, 16);
        }

        private static void WriteAscii(Stream s, string text)
        {
            foreach (char c in text) s.WriteByte((byte)c);
        }

        private static byte ComputeCrc8(byte[] data)
        {
            int crc = 0;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x07) : (crc << 1);
                }
            }
            return (byte)(crc & 0xFF);
        }

        private static ushort ComputeCrc16(byte[] data)
        {
            int crc = 0;
            foreach (byte b in data)
            {
                crc ^= (b << 8);
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 0x8000) != 0 ? ((crc << 1) ^ 0x8005) : (crc << 1);
                }
            }
            return (ushort)(crc & 0xFFFF);
        }

        private sealed class BitWriter
        {
            private readonly List<byte> _bytes = new();
            private int _currentByte;
            private int _bitCount;

            public void WriteBits(ulong value, int count)
            {
                for (int i = count - 1; i >= 0; i--)
                {
                    _currentByte = (_currentByte << 1) | (int)((value >> i) & 1UL);
                    _bitCount++;
                    if (_bitCount == 8)
                    {
                        _bytes.Add((byte)_currentByte);
                        _currentByte = 0;
                        _bitCount = 0;
                    }
                }
            }

            public void WriteSigned(int value, int count)
            {
                ulong mask = count == 32 ? 0xFFFF_FFFFUL : ((1UL << count) - 1UL);
                WriteBits(((ulong)(long)value) & mask, count);
            }

            public void AlignToByte()
            {
                if (_bitCount > 0)
                {
                    _bytes.Add((byte)(_currentByte << (8 - _bitCount)));
                    _currentByte = 0;
                    _bitCount = 0;
                }
            }

            public byte[] ToArray()
            {
                AlignToByte();
                return _bytes.ToArray();
            }
        }
    }
}
