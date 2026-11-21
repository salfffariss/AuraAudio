using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AuraAudio.Audio;
using AuraAudio.Audio.Authenticity;
using NAudio.Wave;

namespace AuraAudio.BenchmarkWorker;

internal static class Program
{
    private sealed record StreamInfoHeader(
        int SampleRate,
        int Channels,
        int BitsPerSample,
        long TotalSamples,
        string? Md5Hex,
        bool HasId3Prefix);

    public static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: AuraAudio.BenchmarkWorker <audioFilePath>");
            return 2;
        }

        string filePath = args[0];
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return 3;
        }

        long fileBytes = new FileInfo(filePath).Length;
        bool isFlac = Path.GetExtension(filePath).Equals(".flac", StringComparison.OrdinalIgnoreCase);
        StreamInfoHeader? si = isFlac ? TryReadFlacStreamInfo(filePath) : null;

        var totalSw = Stopwatch.StartNew();
        long decodeMs = 0;
        long analyzeMs = 0;
        long decodedPcmBytes = 0;
        string? decodedPcmMd5 = null;
        bool? md5Match = null;
        int? waveBitsPerSample = null;
        int? waveBlockAlign = null;
        string? waveEncoding = null;

        try
        {
            // Phase 1: Full-stream decode via AudioDecoderFactory + canonical PCM MD5 verification
            var decSw = Stopwatch.StartNew();
            using (var stream = AudioDecoderFactory.OpenStream(filePath))
            {
                var wf = stream.WaveFormat;
                waveBitsPerSample = wf.BitsPerSample;
                waveBlockAlign = wf.BlockAlign;
                waveEncoding = wf.Encoding.ToString();

                int channels = Math.Max(1, wf.Channels);
                int bytesPerSample = wf.BlockAlign > 0 ? wf.BlockAlign / channels : Math.Max(1, wf.BitsPerSample / 8);
                int srcBits = si is not null ? si.BitsPerSample : wf.BitsPerSample;
                int expectedMd5BytesPerSample = (srcBits + 7) / 8;
                int shiftRight = Math.Max(0, wf.BitsPerSample - srcBits);

                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                byte[] buf = new byte[Math.Max(65536, wf.BlockAlign * 4096)];
                if (wf.BlockAlign > 0)
                {
                    buf = new byte[(buf.Length / wf.BlockAlign) * wf.BlockAlign];
                }

                // Temporary buffer when un-shifting MSB-aligned non-standard bit depths (e.g. 8/12/15/20-bit)
                // back to RFC 9639 canonical ceil(srcBits/8)-byte signed little-endian PCM for STREAMINFO MD5 checks
                byte[]? packed = null;
                int read;
                while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    decodedPcmBytes += read;
                    if (isFlac && si is not null && wf.Encoding == WaveFormatEncoding.Pcm)
                    {
                        if (bytesPerSample == expectedMd5BytesPerSample && shiftRight == 0)
                        {
                            md5.AppendData(buf, 0, read);
                        }
                        else if (bytesPerSample is 2 or 3 or 4 && expectedMd5BytesPerSample is >= 1 and <= 4)
                        {
                            int sampleCount = read / bytesPerSample;
                            int need = sampleCount * expectedMd5BytesPerSample;
                            if (packed is null || packed.Length < need) packed = new byte[need];
                            int inIdx = 0;
                            int outIdx = 0;
                            for (int i = 0; i < sampleCount; i++)
                            {
                                int s = bytesPerSample switch
                                {
                                    2 => (short)(buf[inIdx] | (buf[inIdx + 1] << 8)),
                                    3 => ((buf[inIdx] | (buf[inIdx + 1] << 8) | (buf[inIdx + 2] << 16)) << 8) >> 8,
                                    _ => buf[inIdx] | (buf[inIdx + 1] << 8) | (buf[inIdx + 2] << 16) | (buf[inIdx + 3] << 24)
                                };
                                inIdx += bytesPerSample;
                                s >>= shiftRight;
                                for (int b = 0; b < expectedMd5BytesPerSample; b++)
                                {
                                    packed[outIdx++] = (byte)((s >> (b * 8)) & 0xFF);
                                }
                            }
                            md5.AppendData(packed, 0, need);
                        }
                        else
                        {
                            md5.AppendData(buf, 0, read);
                        }
                    }
                }

                if (isFlac && si is not null && wf.Encoding == WaveFormatEncoding.Pcm)
                {
                    decodedPcmMd5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
                    if (!string.IsNullOrEmpty(si.Md5Hex))
                    {
                        md5Match = string.Equals(decodedPcmMd5, si.Md5Hex, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            decSw.Stop();
            decodeMs = decSw.ElapsedMilliseconds;

            // Phase 2: Forensic authenticity analysis via NyquistSpectrumAnalyzer
            var anSw = Stopwatch.StartNew();
            var r = NyquistSpectrumAnalyzer.Analyze(filePath);
            anSw.Stop();
            analyzeMs = anSw.ElapsedMilliseconds;
            totalSw.Stop();

            var payload = new
            {
                status = "OK",
                bytes = fileBytes,
                decodeMs,
                analyzeMs,
                totalMs = totalSw.ElapsedMilliseconds,
                decodedPcmBytes,
                waveBitsPerSample,
                waveBlockAlign,
                waveEncoding,
                streaminfoSampleRate = si?.SampleRate,
                streaminfoChannels = si?.Channels,
                streaminfoBitsPerSample = si?.BitsPerSample,
                streaminfoTotalSamples = si?.TotalSamples,
                streaminfoHasId3Prefix = si?.HasId3Prefix,
                streaminfoMd5 = si?.Md5Hex,
                decodedPcmMd5,
                md5Match,
                verdict = r.Verdict.ToString(),
                category = r.VerdictCategory.ToString(),
                codec = r.SourceSpec.Codec.ToString(),
                bits = r.SourceSpec.BitDepth.ToString(),
                sr = r.SourceSpec.SampleRateHz,
                ch = r.SourceSpec.Channels,
                cutoff = r.DetectedCutoffHz,
                knee = r.KneeHz,
                envReach = r.UpperEnvelopeReachHz,
                slopeCd = r.SpectralSlopeDbPerOctave,
                kneeSteep = r.KneeSteepnessDbPerOct,
                dropKnee = r.DropAcrossKneeDb,
                cliffHz = r.CliffHz,
                cliffDrop = r.CliffDropDb,
                noise = r.UltrasonicNoiseFloorDbFS,
                noiseStd = r.UltrasonicNoiseStdDevDb,
                dither = r.IsDitherDetected,
                lsbPct = r.LsbActivityPercentage,
                lsbH = r.LsbEntropy,
                true24 = r.IsTrue24Bit,
                zeroPad = r.IsZeroPadded,
                imgR = r.ImagingCorrelation,
                imgAtt = r.ImagingAttenuationDb,
                imgUpper = r.ImagingUpperMeanDb,
                relP16k = r.RelP16kDb,
                relP20k = r.RelP20kDb,
                analysisTsSec = Math.Round(r.AnalysisTimestamp.TotalSeconds, 2),
                analysisPosPct = r.AnalysisPositionPercentage,
                expl = r.Explanation
            };

            Console.WriteLine(JsonSerializer.Serialize(payload));
            return 0;
        }
        catch (Exception ex)
        {
            totalSw.Stop();
            var payload = new
            {
                status = "MANAGED_EXCEPTION",
                bytes = fileBytes,
                decodeMs,
                analyzeMs,
                totalMs = totalSw.ElapsedMilliseconds,
                decodedPcmBytes,
                waveBitsPerSample,
                waveBlockAlign,
                waveEncoding,
                streaminfoSampleRate = si?.SampleRate,
                streaminfoChannels = si?.Channels,
                streaminfoBitsPerSample = si?.BitsPerSample,
                streaminfoTotalSamples = si?.TotalSamples,
                streaminfoHasId3Prefix = si?.HasId3Prefix,
                streaminfoMd5 = si?.Md5Hex,
                decodedPcmMd5,
                md5Match,
                errorType = ex.GetType().Name,
                errorMessage = ex.Message,
                innerErrorType = ex.InnerException?.GetType().Name,
                innerErrorMessage = ex.InnerException?.Message
            };
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return 0;
        }
    }

    private static StreamInfoHeader? TryReadFlacStreamInfo(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] head = new byte[10];
            if (fs.Read(head, 0, 4) < 4) return null;

            bool hasId3 = false;
            if (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
            {
                hasId3 = true;
                if (fs.Read(head, 4, 6) < 6) return null;
                int id3Size = ((head[6] & 0x7F) << 21) | ((head[7] & 0x7F) << 14) | ((head[8] & 0x7F) << 7) | (head[9] & 0x7F);
                int footer = (head[5] & 0x10) != 0 ? 10 : 0;
                long nextPos = 10L + id3Size + footer;
                if (nextPos >= fs.Length) return null;
                fs.Position = nextPos;
                // Skip optional zero padding bytes between ID3v2 and fLaC
                int b;
                while ((b = fs.ReadByte()) == 0) { }
                if (b < 0) return null;
                fs.Position--;
                if (fs.Read(head, 0, 4) < 4) return null;
            }

            if (head[0] != (byte)'f' || head[1] != (byte)'L' || head[2] != (byte)'a' || head[3] != (byte)'C')
            {
                return null;
            }

            byte[] blockHdr = new byte[4];
            if (fs.Read(blockHdr, 0, 4) < 4) return null;
            int blockType = blockHdr[0] & 0x7F;
            int blockLen = (blockHdr[1] << 16) | (blockHdr[2] << 8) | blockHdr[3];
            if (blockType != 0 || blockLen < 34) return null;

            byte[] si = new byte[34];
            if (fs.Read(si, 0, 34) < 34) return null;

            int sr = (si[10] << 12) | (si[11] << 4) | (si[12] >> 4);
            int ch = ((si[12] >> 1) & 0x07) + 1;
            int bps = (((si[12] & 0x01) << 4) | (si[13] >> 4)) + 1;
            long totalSamples = ((long)(si[13] & 0x0F) << 32)
                                | ((long)si[14] << 24)
                                | ((long)si[15] << 16)
                                | ((long)si[16] << 8)
                                | si[17];

            bool allZeroMd5 = true;
            for (int i = 18; i < 34; i++)
            {
                if (si[i] != 0)
                {
                    allZeroMd5 = false;
                    break;
                }
            }

            string? md5Hex = allZeroMd5 ? null : Convert.ToHexString(si, 18, 16).ToLowerInvariant();
            return new StreamInfoHeader(sr, ch, bps, totalSamples, md5Hex, hasId3);
        }
        catch
        {
            return null;
        }
    }
}
