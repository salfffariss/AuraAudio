using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using AuraAudio.Audio;
using AuraAudio.Audio.Authenticity;
using AuraAudio.Audio.Dsd;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.Metadata;
using NAudio.Wave;
using Xunit;

namespace AuraAudio.Tests;

/// <summary>
/// Regression tests ensuring a single source of truth for audio source format metadata
/// (codec, bit depth, sample rate, channels, actual file average bitrate, Hi-Res classification,
/// and consistent number formatting) across MetadataReader, Info Sheet, Library/SQLite,
/// AudioDecoderFactory, SignalPathAuditor, and NyquistSpectrumAnalyzer.
/// </summary>
public class SourceFormatConsistencyTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly Lazy<bool> HasFfmpeg = new(CheckFfmpegAvailable);

    public SourceFormatConsistencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "AuraAudio_FormatConsistency_" + Guid.NewGuid().ToString("N")[..8]);
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
        catch { }
    }

    [SkippableTheory]
    [InlineData(16, 44_100, 2, false, false)]
    [InlineData(16, 48_000, 2, false, false)]
    [InlineData(24, 44_100, 2, true, false)]
    [InlineData(24, 48_000, 2, true, false)]
    [InlineData(24, 48_000, 2, true, true)]   // Exact ISO 14496-12 / Apple Music repro: AudioSampleEntry.samplesize=16, ALACSpecificConfig.bitDepth=24
    [InlineData(24, 96_000, 2, true, true)]   // Hi-Res ALAC: AudioSampleEntry.samplerate=48000, ALACSpecificConfig.sampleRate=96000
    [InlineData(16, 22_050, 1, false, false)] // Low-SR mono ALAC
    public void Alac_AllCodePathsAndUiModels_ReportIdenticalBitDepthSampleRateBitrateAndHiRes(
        int bitsPerSample,
        int sampleRateHz,
        int channels,
        bool expectedHiRes,
        bool forceIsoSampleSize16)
    {
        Skip.IfNot(HasFfmpeg.Value, "ffmpeg is not installed on PATH; skipping generated ALAC consistency test.");

        string sampleFmt = bitsPerSample >= 24 ? "s32p" : "s16p";
        string filePath = Path.Combine(_tempDir, $"alac_{bitsPerSample}b_{sampleRateHz}hz_{channels}ch_{(forceIsoSampleSize16 ? "iso16" : "std")}.m4a");
        RunFfmpeg($"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r={sampleRateHz}:a=0.25\" -ac {channels} -c:a alac -sample_fmt {sampleFmt} \"{filePath}\"");

        if (forceIsoSampleSize16)
        {
            OverwriteMp4AudioSampleEntrySampleSize(filePath, 16);
        }

        // Verify MP4 ALACSpecificConfig directly
        Assert.True(AudioSourceFormatResolver.TryParseMp4AudioConfig(filePath, out var mp4Cfg));
        Assert.Equal(AudioCodec.ALAC, mp4Cfg.Codec);
        Assert.Equal(bitsPerSample, mp4Cfg.BitsPerSample);
        Assert.Equal((uint)sampleRateHz, mp4Cfg.SampleRateHz);
        Assert.Equal((ushort)channels, mp4Cfg.Channels);

        // 1. MetadataReader (Library scan / SongsView / Badges)
        var reader = new AudioMetadataReader();
        var meta = reader.ReadFile(filePath);
        var spec = meta.Track.StreamSpec;

        BitDepth expectedBitDepth = bitsPerSample >= 24 ? BitDepth.Bit24 : BitDepth.Bit16;
        Assert.Equal(AudioCodec.ALAC, spec.Codec);
        Assert.Equal(expectedBitDepth, spec.BitDepth);
        Assert.Equal((uint)sampleRateHz, spec.SampleRateHz);
        Assert.Equal((ushort)channels, spec.Channels);
        Assert.Equal(expectedHiRes, spec.IsHiRes);
        Assert.True(spec.BitrateKbps > 0, "ALAC file average bitrate must never be 0.");

        uint expectedUncompressedPcmKbps = AudioSourceFormatResolver.ComputeUncompressedPcmBitrateKbps(sampleRateHz, bitsPerSample, channels);
        Assert.True(spec.BitrateKbps < expectedUncompressedPcmKbps,
            $"Compressed ALAC average bitrate ({spec.BitrateKbps} kbps) should be below uncompressed PCM ({expectedUncompressedPcmKbps} kbps).");

        // 2. Info Sheet (both fresh track AND pre-existing stale DB track claiming 16-bit / 0 kbps)
        AudioMetadataReader.InvalidateExtendedDetailsCache(filePath);
        var staleTrack = meta.Track with
        {
            StreamSpec = new AudioStreamSpec(AudioCodec.ALAC, 48_000, BitDepth.Bit16, 2, 0)
        };
        var infoDetails = AudioMetadataReader.ReadExtendedDetails(staleTrack);
        string expectedRes = $"{bitsPerSample}-bit / {AudioStreamSpec.FormatSampleRateKhz((uint)sampleRateHz)} • {AudioStreamSpec.FormatChannelLabel((ushort)channels, verbose: true)}";
        string expectedBitrateText = AudioStreamSpec.FormatBitrateKbps(spec.BitrateKbps);
        Assert.Equal(expectedRes, infoDetails.SampleRateAndBitDepth);
        Assert.Equal(expectedBitrateText, infoDetails.BitrateText);

        // 3. Decoder & Signal Chain Auditor
        using var stream = AudioDecoderFactory.OpenStream(filePath);
        Assert.Equal(spec, stream.Spec);

        using var mockEngine = new StubBitPerfectOutputEngine(stream.WaveFormat);
        var signalPath = SignalPathAuditor.Audit(stream, mockEngine, AudioDriverType.WASAPI_Exclusive);
        var sourceStep = signalPath.Steps.First(s => s.StepType == SignalStepType.SourceFile);
        var decoderStep = signalPath.Steps.First(s => s.StepType == SignalStepType.Decoder);

        string expectedSourceDesc = $"{spec.FormattedResolution} ALAC ({AudioStreamSpec.FormatChannelLabel((ushort)channels)}, {expectedBitrateText})";
        Assert.Equal(expectedSourceDesc, sourceStep.Description);
        Assert.Contains($"PCM {AudioStreamSpec.FormatBitrateKbps(expectedUncompressedPcmKbps)}", decoderStep.Description);
        Assert.DoesNotContain(",", AudioStreamSpec.FormatSampleRateKhz((uint)sampleRateHz));

        // 4. Authenticity Analyzer / Inspector
        var report = NyquistSpectrumAnalyzer.Analyze(filePath);
        Assert.Equal(spec, report.SourceSpec);
        Assert.Contains(expectedBitrateText, report.SpecContainerValue);
        Assert.Contains(AudioStreamSpec.FormatSampleRateKhz((uint)sampleRateHz), report.SpecFrequencyValue);

        // 5. SQLite Database round-trip & IsHiRes SQL consistency
        string dbPath = Path.Combine(_tempDir, $"db_{bitsPerSample}_{sampleRateHz}_{channels}_{(forceIsoSampleSize16 ? 1 : 0)}.db");
        using var db = new DatabaseManager(dbPath);
        db.Initialize();
        db.InsertOrUpdateTrack(meta.Track);

        var loadedTracks = db.GetSortedTracks(TrackSortOption.TitleAscending, limit: 50);
        Assert.Single(loadedTracks);
        Assert.Equal(spec, loadedTracks[0].StreamSpec);

        var albums = db.GetAlbums();
        Assert.Single(albums);
        Assert.Equal(expectedHiRes, albums[0].IsHiRes);

        var hiResAlbums = db.GetHiResAlbums();
        Assert.Equal(expectedHiRes ? 1 : 0, hiResAlbums.Count);
    }

    [SkippableFact]
    public void Flac_LowSampleRateMonoAndHiRes_MatchAcrossAllCodePaths()
    {
        Skip.IfNot(HasFfmpeg.Value, "ffmpeg is not installed on PATH; skipping generated FLAC consistency test.");

        var cases = new[]
        {
            (bits: 16, sr: 44_100, ch: 2, hiRes: false, fmt: "s16"),
            (bits: 24, sr: 48_000, ch: 2, hiRes: true,  fmt: "s32"),
            (bits: 16, sr: 22_050, ch: 1, hiRes: false, fmt: "s16"),
        };

        var reader = new AudioMetadataReader();
        foreach (var c in cases)
        {
            string filePath = Path.Combine(_tempDir, $"flac_{c.bits}b_{c.sr}hz_{c.ch}ch.flac");
            string bitsArg = c.bits == 24 ? " -bits_per_raw_sample 24" : "";
            RunFfmpeg($"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r={c.sr}:a=0.25\" -ac {c.ch} -c:a flac -sample_fmt {c.fmt}{bitsArg} \"{filePath}\"");

            var meta = reader.ReadFile(filePath);
            var info = AudioMetadataReader.ReadExtendedDetails(meta.Track);
            using var stream = AudioDecoderFactory.OpenStream(filePath);
            using var mockEngine = new StubBitPerfectOutputEngine(stream.WaveFormat);
            var path = SignalPathAuditor.Audit(stream, mockEngine, AudioDriverType.WASAPI_Exclusive);
            var report = NyquistSpectrumAnalyzer.Analyze(filePath);

            Assert.Equal(AudioCodec.FLAC, meta.Track.StreamSpec.Codec);
            Assert.Equal((uint)c.sr, meta.Track.StreamSpec.SampleRateHz);
            Assert.Equal((ushort)c.ch, meta.Track.StreamSpec.Channels);
            Assert.Equal(c.hiRes, meta.Track.StreamSpec.IsHiRes);
            Assert.Equal(meta.Track.StreamSpec, stream.Spec);
            Assert.Equal(meta.Track.StreamSpec, report.SourceSpec);
            Assert.Equal(meta.Track.StreamSpec.FormattedBitrate, info.BitrateText);
            Assert.Contains(AudioStreamSpec.FormatChannelLabel((ushort)c.ch), path.Steps[0].Description);
            Assert.Contains(meta.Track.StreamSpec.FormattedBitrate, path.Steps[0].Description);
            Assert.Contains("PCM ", path.Steps[1].Description);
        }
    }

    [Fact]
    public void WavAndAiff_PureManagedGeneratedFiles_MatchAcrossAllCodePaths()
    {
        string wav24 = Path.Combine(_tempDir, "wav_24_48.wav");
        string wavMono22 = Path.Combine(_tempDir, "wav_16_22k_mono.wav");
        string aiff24 = Path.Combine(_tempDir, "aiff_24_48.aiff");

        WriteSyntheticWav(wav24, 48_000, 24, 2, durationSec: 1.0);
        WriteSyntheticWav(wavMono22, 22_050, 16, 1, durationSec: 1.0);
        WriteSyntheticAiff(aiff24, 48_000, 24, 2, durationSec: 1.0);

        var reader = new AudioMetadataReader();
        foreach (var (filePath, expectedCodec, expectedBits, expectedSr, expectedCh, expectedHiRes) in new[]
        {
            (wav24, AudioCodec.WAV, BitDepth.Bit24, 48_000u, (ushort)2, true),
            (wavMono22, AudioCodec.WAV, BitDepth.Bit16, 22_050u, (ushort)1, false),
            (aiff24, AudioCodec.AIFF, BitDepth.Bit24, 48_000u, (ushort)2, true)
        })
        {
            var meta = reader.ReadFile(filePath);
            var info = AudioMetadataReader.ReadExtendedDetails(meta.Track);
            using var stream = AudioDecoderFactory.OpenStream(filePath);
            using var mockEngine = new StubBitPerfectOutputEngine(stream.WaveFormat);
            var signalPath = SignalPathAuditor.Audit(stream, mockEngine, AudioDriverType.WASAPI_Exclusive);
            var report = NyquistSpectrumAnalyzer.Analyze(filePath);

            Assert.Equal(expectedCodec, meta.Track.StreamSpec.Codec);
            Assert.Equal(expectedBits, meta.Track.StreamSpec.BitDepth);
            Assert.Equal(expectedSr, meta.Track.StreamSpec.SampleRateHz);
            Assert.Equal(expectedCh, meta.Track.StreamSpec.Channels);
            Assert.Equal(expectedHiRes, meta.Track.StreamSpec.IsHiRes);
            Assert.Equal(meta.Track.StreamSpec, stream.Spec);
            Assert.Equal(meta.Track.StreamSpec, report.SourceSpec);
            Assert.Equal(meta.Track.StreamSpec.FormattedBitrate, info.BitrateText);
            Assert.Contains(meta.Track.StreamSpec.FormattedResolution, info.SampleRateAndBitDepth);
            Assert.Contains(meta.Track.StreamSpec.FormattedResolution, signalPath.Steps[0].Description);
            Assert.Contains(meta.Track.StreamSpec.FormattedBitrate, signalPath.Steps[0].Description);
        }
    }

    [SkippableFact]
    public void LossyFormats_Mp3AacOggOpus_ReportFileBitrate_PreserveMonoAndHighSr_AndAreNeverHiRes()
    {
        Skip.IfNot(HasFfmpeg.Value, "ffmpeg is not installed on PATH; skipping generated lossy consistency test.");

        string mp3Path = Path.Combine(_tempDir, "mp3_320_441.mp3");
        string mp3MonoPath = Path.Combine(_tempDir, "mp3_128_22k_mono.mp3");
        string aac48Path = Path.Combine(_tempDir, "aac_256_48.m4a");
        string aac96Path = Path.Combine(_tempDir, "aac_256_96.m4a");
        string oggPath = Path.Combine(_tempDir, "vorbis_q6_48.ogg");
        string opusPath = Path.Combine(_tempDir, "opus_128_48.opus");

        RunFfmpeg($"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r=44100:a=0.25\" -ac 2 -c:a libmp3lame -b:a 320k \"{mp3Path}\"");
        RunFfmpeg($"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r=22050:a=0.25\" -ac 1 -c:a libmp3lame -b:a 128k \"{mp3MonoPath}\"");
        RunFfmpeg($"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r=48000:a=0.25\" -ac 2 -c:a aac -b:a 256k \"{aac48Path}\"");
        RunFfmpeg($"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r=96000:a=0.25\" -ac 2 -c:a aac -b:a 256k \"{aac96Path}\"");
        RunFfmpeg($"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r=48000:a=0.25\" -ac 2 -c:a libvorbis -q:a 6 \"{oggPath}\"");
        RunFfmpeg($"-y -hide_banner -loglevel error -f lavfi -i \"anoisesrc=d=2:c=pink:r=48000:a=0.25\" -ac 2 -c:a libopus -b:a 128k \"{opusPath}\"");

        var reader = new AudioMetadataReader();
        string dbPath = Path.Combine(_tempDir, "lossy_audit.db");
        using var db = new DatabaseManager(dbPath);
        db.Initialize();

        // Decodable lossy formats (MP3, AAC) must match end-to-end including Signal Chain and Inspector
        foreach (var (filePath, expectedCodec, expectedSr, expectedCh) in new[]
        {
            (mp3Path, AudioCodec.MP3, 44_100u, (ushort)2),
            (mp3MonoPath, AudioCodec.MP3, 22_050u, (ushort)1),
            (aac48Path, AudioCodec.AAC, 48_000u, (ushort)2),
            (aac96Path, AudioCodec.AAC, 96_000u, (ushort)2)
        })
        {
            var meta = reader.ReadFile(filePath);
            var info = AudioMetadataReader.ReadExtendedDetails(meta.Track);
            using var stream = AudioDecoderFactory.OpenStream(filePath);
            using var mockEngine = new StubBitPerfectOutputEngine(stream.WaveFormat);
            var signalPath = SignalPathAuditor.Audit(stream, mockEngine, AudioDriverType.WASAPI_Exclusive);
            var report = NyquistSpectrumAnalyzer.Analyze(filePath);

            Assert.Equal(expectedCodec, meta.Track.StreamSpec.Codec);
            Assert.Equal(expectedSr, meta.Track.StreamSpec.SampleRateHz);
            Assert.Equal(expectedCh, meta.Track.StreamSpec.Channels);
            Assert.False(meta.Track.StreamSpec.IsHiRes, $"{expectedCodec} must never be marked Hi-Res Lossless.");
            Assert.Equal(meta.Track.StreamSpec, stream.Spec);
            Assert.Equal(meta.Track.StreamSpec, report.SourceSpec);
            Assert.True(meta.Track.StreamSpec.BitrateKbps < 600,
                $"Lossy {expectedCodec} must report actual file bitrate ({meta.Track.StreamSpec.BitrateKbps} kbps), not uncompressed PCM.");
            Assert.Equal(meta.Track.StreamSpec.FormattedBitrate, info.BitrateText);
            Assert.Contains(meta.Track.StreamSpec.FormattedBitrate, signalPath.Steps[0].Description);
            Assert.Contains("PCM ", signalPath.Steps[1].Description);
            Assert.Contains(meta.Track.StreamSpec.FormattedBitrate, report.SpecContainerValue);

            db.InsertOrUpdateTrack(meta.Track);
        }

        // OGG & Opus metadata consistency (non-zero Opus bitrate, never Hi-Res in SQLite)
        var oggMeta = reader.ReadFile(oggPath);
        var opusMeta = reader.ReadFile(opusPath);
        Assert.Equal(AudioCodec.OGG_VORBIS, oggMeta.Track.StreamSpec.Codec);
        Assert.Equal(AudioCodec.OPUS, opusMeta.Track.StreamSpec.Codec);
        Assert.True(oggMeta.Track.StreamSpec.BitrateKbps > 0);
        Assert.True(opusMeta.Track.StreamSpec.BitrateKbps > 0, "Opus file average bitrate must not be 0.");
        Assert.False(oggMeta.Track.StreamSpec.IsHiRes);
        Assert.False(opusMeta.Track.StreamSpec.IsHiRes);
        Assert.Equal(opusMeta.Track.StreamSpec.FormattedBitrate, AudioMetadataReader.ReadExtendedDetails(opusMeta.Track).BitrateText);

        db.InsertOrUpdateTrack(oggMeta.Track);
        db.InsertOrUpdateTrack(opusMeta.Track);

        // SQLite IsHiRes must be false for ALL lossy albums (including 48 kHz and 96 kHz AAC/OGG/Opus)
        Assert.All(db.GetAlbums(), album => Assert.False(album.IsHiRes));
        Assert.Empty(db.GetHiResAlbums());
    }

    [Fact]
    public void DsdDsf_MetadataDecoderSignalPathAndInspector_AgreeExactly()
    {
        string dsfPath = Path.Combine(_tempDir, "dsd64_stereo.dsf");
        WriteSyntheticDsd64Dsf(dsfPath, blocksPerChannel: 16);

        var reader = new AudioMetadataReader();
        var meta = reader.ReadFile(dsfPath);
        var info = AudioMetadataReader.ReadExtendedDetails(meta.Track);

        using var pcmStream = AudioDecoderFactory.OpenStream(dsfPath, DsdPlaybackMode.PcmDecimation);
        using var dopStream = AudioDecoderFactory.OpenStream(dsfPath, DsdPlaybackMode.DoP);
        using var mockEngine = new StubBitPerfectOutputEngine(pcmStream.WaveFormat);
        var signalPath = SignalPathAuditor.Audit(pcmStream, mockEngine, AudioDriverType.WASAPI_Exclusive);
        var report = NyquistSpectrumAnalyzer.Analyze(dsfPath);

        Assert.Equal(AudioCodec.DSD_DSF, meta.Track.StreamSpec.Codec);
        Assert.Equal(BitDepth.DSD1Bit, meta.Track.StreamSpec.BitDepth);
        Assert.Equal(2_822_400u, meta.Track.StreamSpec.SampleRateHz);
        Assert.True(meta.Track.StreamSpec.IsHiRes);
        Assert.Equal(meta.Track.StreamSpec, pcmStream.Spec);
        Assert.Equal(meta.Track.StreamSpec, dopStream.Spec);
        Assert.Equal(meta.Track.StreamSpec, report.SourceSpec);
        Assert.Equal("DSD64 2.82 MHz 1-bit • 2 Ch (Stereo)", info.SampleRateAndBitDepth);
        Assert.Equal(meta.Track.StreamSpec.FormattedBitrate, info.BitrateText);
        Assert.Contains("DSD64 2.82 MHz 1-bit", signalPath.Steps[0].Description);
        Assert.Contains(meta.Track.StreamSpec.FormattedBitrate, signalPath.Steps[0].Description);
    }

    [Theory]
    [InlineData("id-ID")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public void NumberFormatting_IsIdenticalAcrossLocalesInInfoSheetSignalChainAndBadges(string cultureName)
    {
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            var spec = new AudioStreamSpec(AudioCodec.ALAC, 48_000, BitDepth.Bit24, 2, 2_004);
            var track = new Track(
                id: 1,
                filePath: Path.Combine(_tempDir, "rollerblade.m4a"),
                title: "rollerblade",
                artist: "no na",
                sortArtist: "no na",
                album: "rollerblade - Single",
                albumArtist: "no na",
                year: 2025,
                trackNumber: 1,
                discNumber: 1,
                duration: TimeSpan.FromSeconds(114),
                streamSpec: spec,
                fileSizeBytes: 28_573_696,
                dateAdded: DateTime.UtcNow);

            var details = TrackExtendedDetails.FromTrackFallback(track);
            Assert.Equal("24-bit / 48.0 kHz • 2 Ch (Stereo)", details.SampleRateAndBitDepth);
            Assert.Equal("2,004 kbps", details.BitrateText);
            Assert.Equal("27.25 MB", details.FileSizeText);
            Assert.Equal("24-bit / 48.0 kHz ALAC", spec.FormattedBadge);
            Assert.Equal("2,004 kbps", spec.FormattedBitrate);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    internal static bool CheckFfmpegAvailable()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void RunFfmpeg(string args)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to launch ffmpeg.");

        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15_000);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed (exit {proc.ExitCode}): {stderr}");
        }
    }

    internal static void OverwriteMp4AudioSampleEntrySampleSize(string filePath, ushort sampleSize)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        ReadOnlySpan<byte> stsd = "stsd"u8;
        int idx = bytes.AsSpan().IndexOf(stsd);
        Assert.True(idx >= 4, "Expected 'stsd' box in generated MP4/ALAC file.");
        int sampleEntryStart = idx + 12; // 4 ('stsd') + 4 (ver/flags) + 4 (entryCount)
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(sampleEntryStart + 26, 2), sampleSize);
        File.WriteAllBytes(filePath, bytes);
    }

    private static void WriteSyntheticWav(string path, int sampleRate, int bitsPerSample, int channels, double durationSec)
    {
        int totalFrames = (int)(sampleRate * durationSec);
        int bytesPerSample = bitsPerSample / 8;
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, bitsPerSample, channels));
        byte[] frameBuf = new byte[bytesPerSample * channels];
        double scale = bitsPerSample == 24 ? 8_388_607.0 : 32_767.0;

        for (int i = 0; i < totalFrames; i++)
        {
            double s = 0.25 * Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate);
            int q = (int)Math.Round(s * scale);
            for (int ch = 0; ch < channels; ch++)
            {
                int off = ch * bytesPerSample;
                if (bytesPerSample == 3)
                {
                    frameBuf[off] = (byte)(q & 0xFF);
                    frameBuf[off + 1] = (byte)((q >> 8) & 0xFF);
                    frameBuf[off + 2] = (byte)((q >> 16) & 0xFF);
                }
                else
                {
                    frameBuf[off] = (byte)(q & 0xFF);
                    frameBuf[off + 1] = (byte)((q >> 8) & 0xFF);
                }
            }
            writer.Write(frameBuf, 0, frameBuf.Length);
        }
    }

    private static void WriteSyntheticAiff(string path, int sampleRate, int bitsPerSample, int channels, double durationSec)
    {
        uint totalFrames = (uint)(sampleRate * durationSec);
        int bytesPerSample = bitsPerSample / 8;
        int pcmBytes = (int)totalFrames * channels * bytesPerSample;
        int ssndSize = 8 + pcmBytes;
        int formSize = 4 + (8 + 18) + (8 + ssndSize);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(Encoding.ASCII.GetBytes("FORM"));
        WriteBe32(bw, (uint)formSize);
        bw.Write(Encoding.ASCII.GetBytes("AIFF"));

        bw.Write(Encoding.ASCII.GetBytes("COMM"));
        WriteBe32(bw, 18);
        WriteBe16(bw, (ushort)channels);
        WriteBe32(bw, totalFrames);
        WriteBe16(bw, (ushort)bitsPerSample);
        WriteIeeeExtended80(bw, sampleRate);

        bw.Write(Encoding.ASCII.GetBytes("SSND"));
        WriteBe32(bw, (uint)ssndSize);
        WriteBe32(bw, 0); // offset
        WriteBe32(bw, 0); // blockSize

        double scale = bitsPerSample == 24 ? 8_388_607.0 : 32_767.0;
        for (int i = 0; i < totalFrames; i++)
        {
            double s = 0.25 * Math.Sin(2.0 * Math.PI * 1000.0 * i / sampleRate);
            int q = (int)Math.Round(s * scale);
            for (int ch = 0; ch < channels; ch++)
            {
                if (bytesPerSample == 3)
                {
                    bw.Write((byte)((q >> 16) & 0xFF));
                    bw.Write((byte)((q >> 8) & 0xFF));
                    bw.Write((byte)(q & 0xFF));
                }
                else
                {
                    bw.Write((byte)((q >> 8) & 0xFF));
                    bw.Write((byte)(q & 0xFF));
                }
            }
        }
    }

    private static void WriteSyntheticDsd64Dsf(string path, int blocksPerChannel)
    {
        const uint sr = 2_822_400;
        const uint ch = 2;
        const uint blockSize = 4096;
        ulong sampleCount = (ulong)blocksPerChannel * blockSize * 8;
        byte[] payload = new byte[blocksPerChannel * blockSize * ch];
        Array.Fill(payload, (byte)0x69);
        ulong dataChunkSize = 12 + (ulong)payload.Length;
        ulong totalSize = 28 + 52 + dataChunkSize;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(Encoding.ASCII.GetBytes("DSD "));
        bw.Write(28UL);
        bw.Write(totalSize);
        bw.Write(0UL);

        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(52UL);
        bw.Write(1u); // version
        bw.Write(0u); // format id
        bw.Write(2u); // stereo
        bw.Write(ch);
        bw.Write(sr);
        bw.Write(1u); // 1-bit
        bw.Write(sampleCount);
        bw.Write(blockSize);
        bw.Write(0u);

        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataChunkSize);
        bw.Write(payload);
    }

    private static void WriteBe16(BinaryWriter bw, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        bw.Write(buf);
    }

    private static void WriteBe32(BinaryWriter bw, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        bw.Write(buf);
    }

    private static void WriteIeeeExtended80(BinaryWriter bw, double value)
    {
        int exp = 16383 + 63;
        double v = value;
        while (v < (1UL << 63))
        {
            v *= 2.0;
            exp--;
        }
        ulong mantissa = (ulong)Math.Round(v);
        Span<byte> buf = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(buf[..2], (ushort)exp);
        BinaryPrimitives.WriteUInt64BigEndian(buf[2..10], mantissa);
        bw.Write(buf);
    }

    internal sealed class StubBitPerfectOutputEngine : IAudioOutputEngine
    {
        public AudioDevice? CurrentDevice { get; }
        public WaveFormat? CurrentFormat { get; }
        public PlaybackStatus Status => PlaybackStatus.Playing;
        public bool IsExclusiveLocked => true;
        public float Volume { get; set; } = 1.0f;
        public event EventHandler<PlaybackStatus>? StatusChanged { add { } remove { } }

        public StubBitPerfectOutputEngine(WaveFormat format)
        {
            CurrentFormat = format;
            CurrentDevice = new AudioDevice(
                "test-dac",
                "Reference DAC",
                AudioDriverType.WASAPI_Exclusive,
                new[] { (uint)format.SampleRate },
                BitDepth.Bit32Float,
                isDefault: true);
        }

        public void Initialize(AudioDevice device, IWaveProvider waveProvider, int latencyMs = 50, AudioDriverType driverType = AudioDriverType.WASAPI_Exclusive) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
