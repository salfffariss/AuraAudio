using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AuraAudio.Audio.Dsd;
using AuraAudio.Core.Models;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Auditor Rantai Sinyal Audio (Live Signal Path Auditor & Quality Badge Resolver).
/// Mengadopsi standar Roon & USB Audio Player PRO (UAPP) untuk mengaudit setiap tahap
/// perjalanan bit audio dari berkas harddisk hingga ke chip konverter DAC fisik.
/// Menghasilkan status transparansi absolut: Bit-Perfect (Gold/Ungu), Enhanced (Biru), atau Resampled (Oranye).
/// </summary>
public static class SignalPathAuditor
{
    /// <summary>
    /// Melakukan audit langsung terhadap aliran audio dan kondisi perangkat keras output saat ini.
    /// </summary>
    public static SignalPathSummary Audit(
        IAudioStream stream,
        IAudioOutputEngine outputEngine,
        AudioDriverType driverType = AudioDriverType.WASAPI_Exclusive)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(outputEngine);

        var steps = new List<SignalStep>();

        // Mengurai stream jika dibungkus oleh GaplessStreamQueue
        IAudioStream activeStream = stream;
        if (stream is GaplessStreamQueue queue)
        {
            activeStream = queue.CurrentStream;
        }

        var sourceSpec = activeStream.Spec;
        string fileName = Path.GetFileName(activeStream.FilePath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "Audio Stream";

        // =========================================================================================
        // TAHAP 1: BERKAS SUMBER ASLI (SOURCE FILE)
        // =========================================================================================
        string sourceDesc = sourceSpec.IsDsd
            ? $"{sourceSpec.FormattedBadge} (~{sourceSpec.BitrateKbps} kbps)"
            : $"{(int)sourceSpec.BitDepth}-bit / {sourceSpec.SampleRateHz / 1000.0:F1} kHz {sourceSpec.Codec} (Stereo, ~{sourceSpec.BitrateKbps} kbps)";

        steps.Add(new SignalStep(
            SignalStepType.SourceFile,
            $"Sumber: {fileName}",
            sourceDesc,
            SignalPathQuality.BitPerfect
        ));

        // =========================================================================================
        // TAHAP 2: DEKODER & PEMROSESAN SINYAL (DECODER & DSP)
        // =========================================================================================
        if (activeStream is DopStreamProvider)
        {
            steps.Add(new SignalStep(
                SignalStepType.Decoder,
                "DoP v1.1 Bitstream Encapsulation",
                "Aliran 1-bit DSD dikemas murni ke dalam frame 24-bit / 176.4 kHz DoP dengan penanda 0x05/0xFA. Bit audio 100% utuh tanpa modifikasi.",
                SignalPathQuality.BitPerfect
            ));
        }
        else if (activeStream is DsdToPcmStreamProvider)
        {
            steps.Add(new SignalStep(
                SignalStepType.DspProcessing,
                "128-Tap Windowed-Sinc FIR Decimation",
                "Filter desimasi Blackman-Harris berpresisi tinggi mengonversi 2.8224 MHz DSD menjadi 24-bit / 88.2 kHz PCM (+6.02 dB level SACD terkalibrasi).",
                SignalPathQuality.Enhanced
            ));
        }
        else if (sourceSpec.Codec is AudioCodec.FLAC or AudioCodec.WAV or AudioCodec.ALAC or AudioCodec.AIFF)
        {
            steps.Add(new SignalStep(
                SignalStepType.Decoder,
                $"{sourceSpec.Codec} Bit-Exact Decompression",
                $"Dekompresi lossless bit-exact ke PCM murni ({(int)sourceSpec.BitDepth}-bit / {sourceSpec.SampleRateHz / 1000.0:F1} kHz) tanpa perubahan bit.",
                SignalPathQuality.BitPerfect
            ));
        }
        else // MP3 / AAC / Lossy
        {
            steps.Add(new SignalStep(
                SignalStepType.Decoder,
                $"{sourceSpec.Codec} Perceptual Decoder",
                "Dekompresi lossy ke PCM. Keterbatasan kompresi psikoakustik melekat pada berkas sumber asal.",
                SignalPathQuality.Enhanced
            ));
        }

        // =========================================================================================
        // TAHAP 3: DRIVER & SUBSISTEM OPERATING SYSTEM
        // =========================================================================================
        bool isExclusive = outputEngine.IsExclusiveLocked && driverType == AudioDriverType.WASAPI_Exclusive;

        if (isExclusive)
        {
            steps.Add(new SignalStep(
                SignalStepType.VolumeControl,
                "Bit-Perfect Direct Hardware Bypass",
                "Windows Audio Engine (AudioSrv mixer), konverter sample rate OS, dan software volume slider sepenuhnya di-bypass.",
                SignalPathQuality.BitPerfect
            ));
        }
        else
        {
            steps.Add(new SignalStep(
                SignalStepType.OperatingSystemMixer,
                "Windows Audio Engine (Shared Mixer)",
                "Aliran audio melewati mixer Windows AudioSrv dan berpotensi mengalami resampling atau pencampuran suara sistem.",
                SignalPathQuality.ResampledOrDegraded
            ));
        }

        // =========================================================================================
        // TAHAP 4: ENDPOINT PERANGKAT KERAS DAC (HARDWARE ENDPOINT)
        // =========================================================================================
        var currentDevice = outputEngine.CurrentDevice;
        var hardwareFormat = outputEngine.CurrentFormat;
        string deviceName = currentDevice?.Name ?? "Default Audio Endpoint";

        bool formatMatches = hardwareFormat != null &&
                             hardwareFormat.SampleRate == activeStream.WaveFormat.SampleRate &&
                             hardwareFormat.BitsPerSample == activeStream.WaveFormat.BitsPerSample;

        if (formatMatches && isExclusive)
        {
            steps.Add(new SignalStep(
                SignalStepType.HardwareEndpoint,
                $"{deviceName} [TERKUNCI BIT-PERFECT]",
                $"DAC menerima sinyal bit-exact pada {hardwareFormat?.BitsPerSample}-bit / {hardwareFormat?.SampleRate / 1000.0:F1} kHz. Nol resampling hardware.",
                SignalPathQuality.BitPerfect
            ));
        }
        else if (!formatMatches)
        {
            steps.Add(new SignalStep(
                SignalStepType.HardwareEndpoint,
                $"{deviceName} [RESAMPLED]",
                $"Perbedaan format: Aliran audio adalah {activeStream.WaveFormat.SampleRate / 1000.0:F1} kHz, namun DAC dinegosiasikan pada {hardwareFormat?.SampleRate / 1000.0:F1} kHz.",
                SignalPathQuality.ResampledOrDegraded
            ));
        }
        else
        {
            steps.Add(new SignalStep(
                SignalStepType.HardwareEndpoint,
                deviceName,
                $"Output ke {hardwareFormat?.BitsPerSample}-bit / {hardwareFormat?.SampleRate / 1000.0:F1} kHz dalam mode shared.",
                SignalPathQuality.ResampledOrDegraded
            ));
        }

        // =========================================================================================
        // EVALUASI KUALITAS TOTAL (OVERALL QUALITY)
        // =========================================================================================
        SignalPathQuality overallQuality;
        if (steps.Any(s => s.Quality == SignalPathQuality.ResampledOrDegraded))
        {
            overallQuality = SignalPathQuality.ResampledOrDegraded;
        }
        else if (steps.Any(s => s.Quality == SignalPathQuality.Enhanced))
        {
            overallQuality = SignalPathQuality.Enhanced;
        }
        else
        {
            overallQuality = SignalPathQuality.BitPerfect;
        }

        return new SignalPathSummary(steps, overallQuality, deviceName);
    }

    /// <summary>
    /// Menghasilkan format diagram alur teks (Text Tree Visualizer) yang siap dicetak ke konsol atau UI Modal.
    /// </summary>
    public static string RenderDiagram(SignalPathSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"┌── STATUS JALUR SINYAL: [{summary.BadgeText}] ───────────────────────────────");
        sb.AppendLine($"│ Perangkat Aktif : {summary.ActiveDeviceName}");
        sb.AppendLine($"│ Kualitas Utama  : {summary.OverallQuality}");
        sb.AppendLine("├── Rantai Pemrosesan Audio:");

        for (int i = 0; i < summary.Steps.Count; i++)
        {
            var step = summary.Steps[i];
            bool isLast = (i == summary.Steps.Count - 1);
            string branch = isLast ? "└──" : "├──";
            string qualityIcon = step.Quality switch
            {
                SignalPathQuality.BitPerfect          => "● [BIT-PERFECT]",
                SignalPathQuality.Enhanced            => "▲ [ENHANCED]",
                SignalPathQuality.ResampledOrDegraded => "■ [RESAMPLED]",
                _                                     => "○"
            };

            sb.AppendLine($"│   {branch} {step.Name} {qualityIcon}");
            sb.AppendLine($"│       └─ {step.Description}");
        }

        sb.AppendLine("└─────────────────────────────────────────────────────────────────────────");
        return sb.ToString();
    }
}
