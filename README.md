# AuraAudio

Bit-perfect desktop lossless audio player and spectral authenticity inspector built with .NET 9 and WPF.

## Ringkasan Fitur

- Playback bit-perfect melalui WASAPI Exclusive mode (bypasses Windows Audio Engine DSP dan mixer).
- Inspeksi forensik spektrum 4096-bin FFT untuk membedakan Studio Master asli vs fake upscale (CD / MP3 upsampled).
- Audit integritas bit-depth LSB (Least Significant Bit) dan pengukuran entropi bit.
- Klasifikasi spektral adaptif: VerifiedLossless, AcousticNaturalRollOff, UpscaledFromCd, CompressedCutoff, DarkAcousticMaster, dan Inconclusive.
- Antarmuka Studio Audio Precision dengan palet warna kontras tinggi (sesuai WCAG AA 4.5:1), telemetri 2x2, serta dukungan dwi-bahasa (ID / EN).
- Pengindeksan metadata lokal cepat berbasis SQLite dengan WAL mode.

## Arsitektur Proyek

- **AuraAudio** (.NET 9 WPF)
  - `Audio/`: Engine WASAPI Exclusive, buffer playback, dan integrasi output hardware.
  - `Scanner/`: NyquistSpectrumAnalyzer (analisis FFT 4096-bin, window Blackman-Harris, deteksi cutoff & slope, audit LSB).
  - `Core/`: Model data, enum status klasifikasi spektrum, state management, dan localization manager.
  - `UI/`: View XAML (MainWindow, AuthenticityView, MusicLibraryView, DACConfigView, PlaylistView) dan ViewModel.
  - `Database/`: Skema SQLite, repositori track, album, artis, dan caching hasil FFT.
- **tests/AuraAudio.Tests**: Unit test komprehensif (28 skenario pengujian spektrum sintetis dan file master audio nyata).

## Prasyarat Sistem

- Windows 10 / 11 (64-bit)
- .NET 9.0 Desktop Runtime atau .NET 9.0 SDK
- Perangkat audio yang mendukung WASAPI Exclusive (opsional, fallback WASAPI Shared tersedia)

## Panduan Build dan Menjalankan

### 1. Build Proyek

```powershell
dotnet build -c Release
```

### 2. Menjalankan Unit Test

```powershell
dotnet test tests/AuraAudio.Tests/AuraAudio.Tests.csproj -c Release
```

### 3. Menjalankan Aplikasi

```powershell
dotnet run --project AuraAudio.csproj -c Release
```

### 4. Publish Mandiri (Self-Contained / Framework-Dependent)

```powershell
dotnet publish AuraAudio.csproj -c Release -r win-x64 --self-contained false -o ./publish
```

## Lisensi

Proyek ini dirilis di bawah lisensi MIT.
