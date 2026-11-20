using System;
using AuraAudio.Audio;
using AuraAudio.Audio.Controller;
using AuraAudio.Core.Models;
using NAudio.Wave;
using Xunit;

namespace AuraAudio.Tests;

public class VolumeControlTests
{
    private sealed class ConstantPcmWaveProvider : IWaveProvider
    {
        private readonly byte[] _frameBytes;
        public WaveFormat WaveFormat { get; }

        public ConstantPcmWaveProvider(WaveFormat format, byte[] singleFrameBytes)
        {
            WaveFormat = format;
            _frameBytes = singleFrameBytes;
        }

        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public int Read(Span<byte> destination)
        {
            int written = 0;
            while (written + _frameBytes.Length <= destination.Length)
            {
                _frameBytes.CopyTo(destination.Slice(written, _frameBytes.Length));
                written += _frameBytes.Length;
            }
            return written;
        }
    }

    private sealed class FakeAudioOutputEngine : IAudioOutputEngine
    {
        public AudioDevice? CurrentDevice { get; private set; }
        public WaveFormat? CurrentFormat { get; private set; }
        public PlaybackStatus Status { get; private set; } = PlaybackStatus.Stopped;
        public bool IsExclusiveLocked => true;
        public float Volume { get; set; } = 1.0f;
        public event EventHandler<PlaybackStatus>? StatusChanged;

        public void Initialize(AudioDevice device, IWaveProvider waveProvider, int latencyMs = 50, AudioDriverType driverType = AudioDriverType.WASAPI_Exclusive)
        {
            CurrentDevice = device;
            CurrentFormat = waveProvider.WaveFormat;
        }

        public void Play() { Status = PlaybackStatus.Playing; StatusChanged?.Invoke(this, Status); }
        public void Pause() { Status = PlaybackStatus.Paused; StatusChanged?.Invoke(this, Status); }
        public void Stop() { Status = PlaybackStatus.Stopped; StatusChanged?.Invoke(this, Status); }
        public void Dispose() { }
    }

    [Fact]
    public void SilenceAwareWaveProvider_UnityVolume_IsBitExact()
    {
        var fmt = new WaveFormat(48000, 24, 2);
        byte[] frame = { 0x34, 0x12, 0x56, 0xAB, 0xCD, 0x7E };
        var provider = new SilenceAwareWaveProvider(new ConstantPcmWaveProvider(fmt, frame), initialVolume: 1.0f);

        byte[] buf = new byte[frame.Length * 64];
        int read = provider.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, read);

        for (int i = 0; i < buf.Length; i += frame.Length)
        {
            Assert.Equal(frame, buf.AsSpan(i, frame.Length).ToArray());
        }
    }

    [Fact]
    public void SilenceAwareWaveProvider_ZeroVolume_OutputsDigitalSilence()
    {
        var fmt = new WaveFormat(44100, 16, 2);
        byte[] frame = { 0x00, 0x40, 0x00, 0xC0 }; // +16384, -16384
        var provider = new SilenceAwareWaveProvider(new ConstantPcmWaveProvider(fmt, frame), initialVolume: 0.0f);

        byte[] buf = new byte[frame.Length * 64];
        int read = provider.Read(buf, 0, buf.Length);
        Assert.Equal(buf.Length, read);
        Assert.All(buf, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void SilenceAwareWaveProvider_Scales16Bit24Bit32BitIntAndFloatPcm()
    {
        // 16-bit PCM at volume 0.5 -> perceptual gain 0.25 -> 20000 * 0.25 = 5000
        var fmt16 = new WaveFormat(48000, 16, 1);
        byte[] f16 = BitConverter.GetBytes((short)20000);
        var p16 = new SilenceAwareWaveProvider(new ConstantPcmWaveProvider(fmt16, f16), initialVolume: 0.5f);
        byte[] out16 = new byte[2 * 16];
        p16.Read(out16, 0, out16.Length);
        short s16 = BitConverter.ToInt16(out16, out16.Length - 2);
        Assert.InRange(s16, (short)4998, (short)5002);

        // 24-bit PCM at volume 0.5 -> perceptual gain 0.25 -> 4,000,000 * 0.25 = 1,000,000
        var fmt24 = new WaveFormat(48000, 24, 1);
        int sample24 = 4_000_000;
        byte[] f24 = { (byte)(sample24 & 0xFF), (byte)((sample24 >> 8) & 0xFF), (byte)((sample24 >> 16) & 0xFF) };
        var p24 = new SilenceAwareWaveProvider(new ConstantPcmWaveProvider(fmt24, f24), initialVolume: 0.5f);
        byte[] out24 = new byte[3 * 16];
        p24.Read(out24, 0, out24.Length);
        int off = out24.Length - 3;
        int got24 = out24[off] | (out24[off + 1] << 8) | (out24[off + 2] << 16);
        if ((got24 & 0x800000) != 0) got24 |= unchecked((int)0xFF000000);
        Assert.InRange(got24, 999_990, 1_000_010);

        // 32-bit IEEE Float at volume 0.5 -> perceptual gain 0.25 -> 0.8f * 0.25 = 0.2f
        var fmtF = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
        byte[] fF = BitConverter.GetBytes(0.8f);
        var pF = new SilenceAwareWaveProvider(new ConstantPcmWaveProvider(fmtF, fF), initialVolume: 0.5f);
        byte[] outF = new byte[4 * 16];
        pF.Read(outF, 0, outF.Length);
        float gotF = BitConverter.ToSingle(outF, outF.Length - 4);
        Assert.InRange(gotF, 0.199f, 0.201f);

        // Dynamic live volume change (ramp down from 1.0 to 0.5 over ~25ms buffer)
        var pDyn = new SilenceAwareWaveProvider(new ConstantPcmWaveProvider(fmt16, f16), initialVolume: 1.0f);
        pDyn.Volume = 0.5f;
        byte[] rampBuf = new byte[2 * 2400]; // 50ms at 48kHz
        pDyn.Read(rampBuf, 0, rampBuf.Length);
        short firstSample = BitConverter.ToInt16(rampBuf, 0);
        short lastSample = BitConverter.ToInt16(rampBuf, rampBuf.Length - 2);
        Assert.True(firstSample > 18000, $"Expected smooth start near 20000, got {firstSample}");
        Assert.InRange(lastSample, (short)4995, (short)5015);
    }

    [Fact]
    public void Controller_VolumeSetter_PropagatesToOutputEngineImmediately()
    {
        var fakeEngine = new FakeAudioOutputEngine();
        using var controller = new AudiophilePlaybackController(fakeEngine);

        controller.Volume = 0.35f;
        Assert.Equal(0.35f, fakeEngine.Volume, precision: 4);

        controller.Volume = 0.0f;
        Assert.Equal(0.0f, fakeEngine.Volume, precision: 4);

        controller.Volume = 1.0f;
        Assert.Equal(1.0f, fakeEngine.Volume, precision: 4);
    }
}
