using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using AuraAudio.Audio;
using AuraAudio.Audio.Controller;
using AuraAudio.Core.Models;
using AuraAudio.Database;
using AuraAudio.UI.Helpers;

namespace AuraAudio.UI.ViewModels;

/// <summary>
/// ViewModel untuk Modal Dialog Konfigurasi Hardware DAC, Latensi Buffer, dan Driver Mode.
/// Menyediakan enumerasi perangkat real-time dan live apply tanpa restart aplikasi.
/// </summary>
public class HardwareConfigViewModel : ViewModelBase
{
    private readonly AudiophilePlaybackController _controller;
    private readonly DatabaseManager _db;

    private AudioDevice? _selectedDevice;
    private bool _isExclusiveMode = true;
    private int _latencyMs = 50;
    private bool _isDsdDoP = false;
    private string _statusMessage = string.Empty;

    public ObservableCollection<AudioDevice> AvailableDevices { get; } = new();

    public AudioDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnPropertyChanged(nameof(DeviceDetailsText));
                OnPropertyChanged(nameof(IsDedicatedDac));
            }
        }
    }

    public bool IsDedicatedDac => SelectedDevice?.IsDedicatedDac ?? false;

    public bool IsExclusiveMode
    {
        get => _isExclusiveMode;
        set
        {
            if (SetProperty(ref _isExclusiveMode, value))
            {
                OnPropertyChanged(nameof(IsSharedMode));
                OnPropertyChanged(nameof(DriverTypeDescription));
            }
        }
    }

    public bool IsSharedMode
    {
        get => !_isExclusiveMode;
        set
        {
            if (value)
            {
                IsExclusiveMode = false;
            }
        }
    }

    public int LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (SetProperty(ref _latencyMs, value))
            {
                OnPropertyChanged(nameof(LatencyDescription));
                OnPropertyChanged(nameof(LatencyColorHex));
                OnPropertyChanged(nameof(BufferSafetyBadge));
            }
        }
    }

    public bool IsDsdDoP
    {
        get => _isDsdDoP;
        set
        {
            if (SetProperty(ref _isDsdDoP, value))
            {
                OnPropertyChanged(nameof(IsDsdDecimation));
                OnPropertyChanged(nameof(DsdModeDescription));
            }
        }
    }

    public bool IsDsdDecimation
    {
        get => !_isDsdDoP;
        set
        {
            if (value)
            {
                IsDsdDoP = false;
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string LatencyDescription
    {
        get
        {
            bool isEn = LocalizationManager.CurrentLanguage == "en";
            return LatencyMs switch
            {
                < 35 => isEn
                    ? $"{LatencyMs} ms • Ultra-Low Latency (Instant response, high CPU load)"
                    : $"{LatencyMs} ms • Latensi Ekstrem Rendah (Respon instan, beban CPU tinggi)",
                <= 75 => isEn
                    ? $"{LatencyMs} ms • Audiophile Optimal (Perfect balance & glitch-free)"
                    : $"{LatencyMs} ms • Optimal Audiophile (Keseimbangan sempurna & aman)",
                <= 150 => isEn
                    ? $"{LatencyMs} ms • High Glitch Protection (Safe against buffer underrun)"
                    : $"{LatencyMs} ms • Proteksi Glitch Tinggi (Sangat aman dari buffer underrun)",
                _ => isEn
                    ? $"{LatencyMs} ms • Conservative Buffer (Maximum stability)"
                    : $"{LatencyMs} ms • Buffer Konservatif (Stabilitas maksimum)"
            };
        }
    }

    public string LatencyColorHex => LatencyMs switch
    {
        < 35 => "#06B6D4",   // Cyan
        <= 75 => "#10B981",  // Emerald Green (Optimal)
        <= 150 => "#F59E0B", // Gold / Amber
        _ => "#94A3B8"       // Slate
    };

    public string BufferSafetyBadge
    {
        get
        {
            bool isEn = LocalizationManager.CurrentLanguage == "en";
            return LatencyMs switch
            {
                < 35 => isEn ? "ULTRA-LOW LATENCY" : "LATENSI EKSTREM RENDAH",
                <= 75 => isEn ? "OPTIMAL (RECOMMENDED)" : "OPTIMAL (DIREKOMENDASIKAN)",
                <= 150 => isEn ? "HIGH BUFFER SAFETY" : "PROTEKSI BUFFER TINGGI",
                _ => isEn ? "MAXIMUM STABILITY" : "STABILITAS MAKSIMUM"
            };
        }
    }

    public string DriverTypeDescription
    {
        get
        {
            bool isEn = LocalizationManager.CurrentLanguage == "en";
            return IsExclusiveMode
                ? (isEn
                    ? "WASAPI Exclusive: Locks soundcard hardware directly (Bit-Perfect Direct, Bypass Windows Mixer)."
                    : "WASAPI Exclusive: Mengunci chip soundcard secara langsung (Bit-Perfect Direct, Bypass Windows Mixer).")
                : (isEn
                    ? "WASAPI Shared: Share output with other apps (browser, discord) via Windows Audio Engine Mixer."
                    : "WASAPI Shared: Berbagi output dengan aplikasi lain (browser, discord) via Windows Audio Engine Mixer.");
        }
    }

    public string DsdModeDescription
    {
        get
        {
            bool isEn = LocalizationManager.CurrentLanguage == "en";
            return IsDsdDoP
                ? (isEn
                    ? "DoP 1.0 (DSD over PCM): Raw DSD bitstream packed into 176.4/352.8 kHz PCM frames with 0x05/0xFA markers for DSD-capable DACs."
                    : "DoP 1.0 (DSD over PCM): Bitstream DSD dibungkus ke frame PCM 176.4/352.8 kHz dengan marker 0x05/0xFA untuk DAC berfitur DSD.")
                : (isEn
                    ? "PCM Decimation: Converted to 24-bit / 176.4 kHz PCM via linear-phase 64-bit FIR Low-Pass Filter (Compatible with all standard soundcards)."
                    : "PCM Decimation: Dikonversi ke 24-bit / 176.4 kHz PCM melalui 64-bit FIR Low-Pass Filter (Kompatibel dengan semua soundcard).");
        }
    }

    public string DeviceDetailsText
    {
        get
        {
            bool isEn = LocalizationManager.CurrentLanguage == "en";
            if (SelectedDevice == null) return isEn ? "No device selected" : "Tidak ada perangkat dipilih";
            var rates = string.Join(", ", SelectedDevice.SupportedSampleRates.Select(r => $"{r / 1000.0:F1}k"));
            return isEn
                ? $"ID: {SelectedDevice.Id}\nMax Bit-Depth: {SelectedDevice.MaxBitDepth}\nNative Sample Rates: {rates}"
                : $"ID: {SelectedDevice.Id}\nKedalaman Bit Maks: {SelectedDevice.MaxBitDepth}\nFrekuensi Native: {rates}";
        }
    }

    public ICommand RefreshDevicesCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand CancelCommand { get; }

    public event Action? RequestClose;

    public HardwareConfigViewModel(AudiophilePlaybackController controller, DatabaseManager db)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _db = db ?? throw new ArgumentNullException(nameof(db));

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        ApplyCommand = new RelayCommand(ApplySettings);
        CancelCommand = new RelayCommand(() =>
        {
            LocalizationManager.LanguageChanged -= OnLanguageChanged;
            RequestClose?.Invoke();
        });

        LocalizationManager.LanguageChanged += OnLanguageChanged;

        LoadCurrentSettings();
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(DeviceDetailsText));
        OnPropertyChanged(nameof(LatencyDescription));
        OnPropertyChanged(nameof(BufferSafetyBadge));
        OnPropertyChanged(nameof(DriverTypeDescription));
        OnPropertyChanged(nameof(DsdModeDescription));
    }

    private void LoadCurrentSettings()
    {
        RefreshDevices();

        // Muat nilai saat ini dari controller
        _isExclusiveMode = _controller.DriverType == AudioDriverType.WASAPI_Exclusive;
        _latencyMs = _controller.LatencyMs;
        _isDsdDoP = _controller.DsdMode == DsdPlaybackMode.DoP;

        if (_controller.CurrentDevice != null)
        {
            SelectedDevice = AvailableDevices.FirstOrDefault(d => d.Id == _controller.CurrentDevice.Id)
                             ?? AvailableDevices.FirstOrDefault();
        }
        else
        {
            SelectedDevice = AvailableDevices.FirstOrDefault();
        }

        OnPropertyChanged(nameof(IsExclusiveMode));
        OnPropertyChanged(nameof(IsSharedMode));
        OnPropertyChanged(nameof(LatencyMs));
        OnPropertyChanged(nameof(LatencyDescription));
        OnPropertyChanged(nameof(LatencyColorHex));
        OnPropertyChanged(nameof(BufferSafetyBadge));
        OnPropertyChanged(nameof(IsDsdDoP));
        OnPropertyChanged(nameof(IsDsdDecimation));
        OnPropertyChanged(nameof(DriverTypeDescription));
        OnPropertyChanged(nameof(DsdModeDescription));
    }

    public void RefreshDevices()
    {
        var currentSelectedId = SelectedDevice?.Id;
        var devices = _controller.DeviceEnumerator.GetActiveRenderDevices();

        AvailableDevices.Clear();
        foreach (var dev in devices)
        {
            AvailableDevices.Add(dev);
        }

        if (currentSelectedId != null)
        {
            SelectedDevice = AvailableDevices.FirstOrDefault(d => d.Id == currentSelectedId);
        }

        SelectedDevice ??= AvailableDevices.FirstOrDefault(d => d.IsDefault) ?? AvailableDevices.FirstOrDefault();
    }

    private void ApplySettings()
    {
        try
        {
            if (SelectedDevice != null)
            {
                _controller.SetDevice(SelectedDevice);
                _db.SetSetting("audio_device_id", SelectedDevice.Id);
            }

            var driverType = IsExclusiveMode ? AudioDriverType.WASAPI_Exclusive : AudioDriverType.WASAPI_Shared;
            _controller.SetDriverType(driverType);
            _db.SetSetting("audio_driver_type", driverType.ToString());

            _controller.SetLatency(LatencyMs);
            _db.SetSetting("latency_ms", LatencyMs.ToString());

            var dsdMode = IsDsdDoP ? DsdPlaybackMode.DoP : DsdPlaybackMode.PcmDecimation;
            _controller.SetDsdMode(dsdMode);
            _db.SetSetting("dsd_mode", dsdMode.ToString());

            StatusMessage = "Pengaturan berhasil diterapkan secara langsung ke hardware!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Peringatan: {ex.Message}";
        }
        finally
        {
            LocalizationManager.LanguageChanged -= OnLanguageChanged;
            RequestClose?.Invoke();
        }
    }
}
