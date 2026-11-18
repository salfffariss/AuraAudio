using System;
using System.Collections.Generic;
using System.Linq;
using AuraAudio.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AuraAudio.Audio;

/// <summary>
/// Modul enumerasi dan pendeteksi kapabilitas perangkat keras audio di Windows (Soundcard &amp; USB DAC).
/// Berkomunikasi langsung dengan Windows Core Audio API (WASAPI) untuk menguji format
/// yang didukung hardware dalam mode Bit-Perfect Exclusive.
/// </summary>
public class DeviceEnumerator : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private bool _isDisposed;

    /// <summary>
    /// Daftar frekuensi sampling standar dan audiophile Hi-Res yang akan diuji ke hardware DAC.
    /// </summary>
    private static readonly uint[] CandidateSampleRates =
    {
        44_100,     // CD Audio / MP3 Standar
        48_000,     // Standar Video / Windows Mixer Default
        88_200,     // 2x CD Hi-Res / SACD DSD downsample
        96_000,     // 2x 48k Studio Master
        176_400,    // 4x CD Hi-Res
        192_000,    // 4x 48k Studio Master
        352_800,    // DXD (Digital eXtreme Definition)
        384_000,    // Ultra Hi-Res PCM
        768_000     // Flagship DAC Master
    };

    public DeviceEnumerator()
    {
        _enumerator = new MMDeviceEnumerator();
    }

    /// <summary>
    /// Mengambil seluruh perangkat audio aktif (Output / Speakers / Headphones / USB DACs)
    /// yang terpasang di sistem operasi Windows saat ini.
    /// </summary>
    public IReadOnlyList<AudioDevice> GetActiveRenderDevices()
    {
        var resultList = new List<AudioDevice>();

        MMDevice? defaultDevice = null;
        try
        {
            defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            // Abaikan jika tidak ada default audio endpoint
        }

        var collection = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

        foreach (var mmDevice in collection)
        {
            bool isDefault = defaultDevice != null && mmDevice.ID == defaultDevice.ID;
            var deviceModel = ProbeDeviceCapabilities(mmDevice, isDefault);
            resultList.Add(deviceModel);
        }

        return resultList;
    }

    /// <summary>
    /// Mengambil perangkat output default Windows saat ini.
    /// </summary>
    public AudioDevice? GetDefaultRenderDevice()
    {
        try
        {
            var defaultDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return ProbeDeviceCapabilities(defaultDevice, isDefault: true);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Menguji kapabilitas fisik hardware soundcard / DAC dalam mode WASAPI Exclusive:
    /// Menguji frekuensi sample rate mana saja yang diterima secara native tanpa resampling.
    /// </summary>
    private static AudioDevice ProbeDeviceCapabilities(MMDevice mmDevice, bool isDefault)
    {
        string id = mmDevice.ID;
        string name = mmDevice.FriendlyName;
        var supportedRates = new List<uint>();
        BitDepth maxBitDepth = BitDepth.Bit16;

        try
        {
            using var client = mmDevice.CreateAudioClient();

            // Uji kedalaman bit dan frekuensi dalam mode AUDCLNT_SHAREMODE_EXCLUSIVE
            foreach (uint rate in CandidateSampleRates)
            {
                // Cek dukungan 24-bit PCM (Format Studio Audiophile paling umum)
                var waveFormat24 = new WaveFormatExtensible(
                    (int)rate,
                    24,
                    2
                );

                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, waveFormat24))
                {
                    supportedRates.Add(rate);
                    if (maxBitDepth < BitDepth.Bit24) maxBitDepth = BitDepth.Bit24;
                    continue;
                }

                // Cek dukungan 32-bit Float
                var waveFormat32 = WaveFormat.CreateIeeeFloatWaveFormat((int)rate, 2);
                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, waveFormat32))
                {
                    supportedRates.Add(rate);
                    maxBitDepth = BitDepth.Bit32Float;
                    continue;
                }

                // Cek dukungan 16-bit PCM (Standar CD)
                var waveFormat16 = new WaveFormat((int)rate, 16, 2);
                if (client.IsFormatSupported(AudioClientShareMode.Exclusive, waveFormat16))
                {
                    supportedRates.Add(rate);
                }
            }

            // Jika mode eksklusif tidak merespons pengujian di atas, gunakan MixFormat bawaan driver
            if (supportedRates.Count == 0 && client.MixFormat != null)
            {
                supportedRates.Add((uint)client.MixFormat.SampleRate);
                maxBitDepth = client.MixFormat.BitsPerSample switch
                {
                    >= 32 => BitDepth.Bit32Float,
                    >= 24 => BitDepth.Bit24,
                    _     => BitDepth.Bit16
                };
            }
        }
        catch
        {
            // Fallback dasar jika hardware menolak query eksklusif
            supportedRates.Add(44_100);
            supportedRates.Add(48_000);
        }

        return new AudioDevice(
            id: id,
            name: name,
            driverType: AudioDriverType.WASAPI_Exclusive,
            supportedSampleRates: supportedRates.Distinct().OrderBy(r => r),
            maxBitDepth: maxBitDepth,
            isDefault: isDefault
        );
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }
}
