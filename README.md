# AuraAudio (v2.1.0)

Bit-perfect lossless desktop audio player and Nyquist FFT spectral authenticity inspector for Windows 10 and 11, built with .NET 9 and WPF in a strict pure-monochrome design system with an artwork-derived immersive playback backdrop.

## Key Features

- **Bit-Perfect Audio Playback & Safe Managed FLAC Decoding:** WASAPI Exclusive mode (bypassing the Windows Audio Engine mixer) with WASAPI Shared fallback, sample-exact gapless playback, anti-pop micro-fades, click-free PCM digital volume scaling (bit-exact passthrough at 100%), native AIFF/WAV/ALAC/DSD (DoP & DSD-to-PCM) decoding, and a pure-managed zero-`unsafe` RFC 9639 FLAC decoder (`SafeFlacDecoder` / `SafeFlacWaveStream`, MIT-derived from `SimpleFlac` / Project Nayuki) with live CRC-8/16 frame verification, ID3v2 prefix support, `SEEKTABLE` sample-accurate seeking, and `4–32-bit` integer PCM decoding (including `8/12/15/20/32-bit` and Rice escape code `0`).
- **Pure-Monochrome Multi-Surface UI:** Strictly neutral monochrome token system (`R = G = B` in `UI/Tokens/NeutralTokens.xaml`) with WCAG 2.1 verified contrast (`>= 4.5:1` text, `>= 3.0:1` interactive boundaries), app-scope `SystemColors` overrides, collapsible navigation rail (`248px <-> 56px`), floating bottom dock capsule, and responsive dual-purpose right split pane (`Queue`, `History`, `Lyrics`). Only the Full-Screen and Mini-Player backdrop layer uses runtime colors derived from the active track's artwork; all text, icons, controls, and every other screen remain pure monochrome.
- **Local Library Views:** Home shelves (*Recently Added*, *Jump Back In*, *Hi-Res & Lossless Masters*, *Your Artists*), virtualized Songs `DataGrid` with 3-column Column Browser (*Format*, *Artist*, *Album*), virtualized frameless Albums grid, master-detail Artists view, Album & Artist detail views, drag-and-drop Playlists, and dedicated scoped Search.
- **Full-Screen Stage & Mini-Player with Immersive Backdrop:** 5-column Full-Screen playback stage (`Ctrl+Shift+F`) with Centered `<->` Split Lyrics morph and an always-on-top Mini-Player (`Ctrl+Shift+M`) with Compact (`350x160`), Album View (`350x350`), and expandable Queue/Lyrics tray. Both surfaces feature an OKLCH artwork-derived soft animated backdrop (`ImmersiveBackdropControl`) with 650 ms track cross-fades, a spatially varying pure-black readability scrim (`AppleMusicSpatial` default), pure-neutral fallback for grayscale/missing art, a `Settings > Accessibility` **Animated backdrop** toggle, Battery Saver / pause / minimize / Reduced Motion freezing, and automatic fallback to static if frame times rise.
- **Cardless Synced Lyrics & Persistent History:** `.lrc` sidecar, embedded LRC, ID3v2 `SYLT`, and plain-text fallback rendered in a cardless, zero-layout pooled viewport (`SyncedLyricsViewport`) shared across Full-Screen Split, the 360 px Right Pane, and the Mini-Player tray, with constant-size Bold display typography, GPU-composited distance opacity/scale falloff, display-clock 3-dot progress interlude indicators (`>= 5 s` gaps), click-to-seek, keyboard navigation, hover/focus `.lrc` import, and persistent local SQLite `play_history` (capped at 500 entries).
- **Forensic Audio Authenticity Inspector:** Full-page Audio Inspector and tabbed `Get Info` (`Ctrl+I`) sheet combining a 4096-bin per-channel multi-window Nyquist FFT spectrogram (pure monochrome), 5-bin smoothed knee detection, short-clip (`< 10 s`) 8-segment spread sampling and apples-to-apples multi-segment P90 consistency checks, OLS spectral slope regression, TPDF dither shelf and container-aware dead-zero brickwall detection, Hi-Res-to-Hi-Res Nyquist-boundary upsampling detection (`UpscaledHiRes`), Nyquist-relative low sample-rate analysis (`< 44.1 kHz`), 48 kHz+ resampler mirror correlation, and 24-bit LSB Shannon entropy audit. Verdicts are computed on demand and never persisted in the library database.
- **Unified Settings & Fluid Motion:** Breadcrumb-driven Settings (*General*, *Playback & DAC*, *Library & Files*, *Accessibility*, *About*) and `RenderingTime`-deduplicated critically damped `SpringAnimator` motion with Reduced Motion support.

## Requirements & Installation

- **Operating System:** Windows 10 (build 19041+) or Windows 11, **64-bit (`win-x64`)**. No separate .NET runtime installation is required.
- **Windows Setup Wizard (Recommended):** Download `AuraAudio-v2.1.0-Setup-win-x64.exe` from the Releases page and run the installer. Installs per-user by default (`%LocalAppData%\Programs\AuraAudio`, no UAC prompt) with optional all-users install (`C:\Program Files\AuraAudio`), Start Menu & Desktop shortcuts, Windows Installed Apps registration, `Win+R` -> `auraaudio` support, and clean in-place upgrades preserving `%LocalAppData%\AuraAudio\library.db`.
- **Portable Standalone ZIP:** Download `AuraAudio-v2.1.0-win-x64-standalone.zip`, extract it to any folder, and run `AuraAudio.exe`. User library, thumbnails, and logs are stored under `%LocalAppData%\AuraAudio\`.
- **Audio Hardware:** Any Windows audio endpoint (WASAPI Exclusive DAC recommended; automatic WASAPI Shared fallback supported).

## Build & Test From Source

Requires the [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) on Windows x64 (plus [Inno Setup 6](https://jrsoftware.org/isinfo.php) via `winget install JRSoftware.InnoSetup` to compile the Windows Setup wizard).

```powershell
# 1. Build Release
dotnet build AuraAudio.csproj -c Release

# 2. Run unit & STA test suite
dotnet test tests/AuraAudio.Tests/AuraAudio.Tests.csproj -c Release

# 3. Run application
dotnet run --project AuraAudio.csproj -c Release

# 4. Run end-to-end UI Automation smoke test (after Release build)
powershell -NoProfile -ExecutionPolicy Bypass -File tests/ui/smoke_ui.ps1

# 5. Build Windows Setup Wizard EXE + Portable ZIP + SHA-256 checksums
powershell -NoProfile -ExecutionPolicy Bypass -File tools/release/build_release.ps1
```

## Project Architecture

- `Audio/`: WASAPI Exclusive/Shared output engine (`WasapiExclusivePlayer`), PCM digital volume control, `AudioDecoderFactory`, pure-managed RFC 9639 FLAC decoder (`Audio/Flac/SafeFlacDecoder.cs`, `SafeFlacWaveStream.cs`), `GaplessStreamQueue`, `AntiPopFader`, DSD (`DsfReader`, `DopStreamProvider`, `DsdToPcmStreamProvider`), `NyquistSpectrumAnalyzer`, `SignalPathAuditor`, and `AudiophilePlaybackController`.
- `Core/`: Models (`Track`, `Album`, `Artist`, `Playlist`, `AudioIntegrityReport`, `SignalPathSummary`), enums, and `LocalizationManager`.
- `Database/`: SQLite WAL schema (including `play_history`) and `DatabaseManager`.
- `Metadata/`: `AudioMetadataReader` (TagLibSharp), `LyricsParser` (`.lrc`/SYLT/plain), and `ThumbnailCacheManager`.
- `Scanner/`: High-throughput local `LibraryScanner` (skips and logs unreadable files without aborting scans).
- `UI/`: Pure-monochrome tokens (`UI/Tokens/`), control templates (`UI/Styles/`), custom controls (`SyncedLyricsViewport`, `ImmersiveBackdropControl`, `SpekSpectrumControl`), views & modal sheets (`UI/Views/`), ViewModels (`UI/ViewModels/`), windows (`MainWindow`, `MiniPlayerWindow`), and helpers (`SpringAnimator`, `ArtworkPaletteExtractor`, `PowerStatusHelper`, `FluidBehaviors`, `DwmBackdropHelper`).
- `Assets/`: Pure-monochrome multi-resolution application icons (`AuraAudio.ico`, `AuraAudio.png`).
- `installer/`: Inno Setup 6 Windows Setup Wizard script (`AuraAudio.iss`).
- `.github/workflows/`: Automated Windows release build & GitHub Release publishing (`release.yml`).
- `docs/`: Forensic DSP documentation (`METODE_ANALISIS_KEASLIAN_AUDIO.md`) and versioned release notes (`docs/releases/`).
- `tests/`: xUnit + STA test suite (`tests/AuraAudio.Tests/`) and UIA smoke test (`tests/ui/smoke_ui.ps1`).
- `tools/`: Crash-isolated FLAC & forensic benchmark harness (`tools/benchmark/`) and unified release packager (`tools/release/build_release.ps1`), excluded from application builds and published binaries.

## Known Limitations

- **FLAC Decoder Conformance:** `SafeFlacDecoder` enforces strict RFC 9639 CRC-8/16 and header checks, rejecting corrupted streams cleanly instead of playing invalid PCM. Non-subset FLAC files that change sample rate, channel count, or bit depth mid-stream are not supported (`NotSupportedException`), and a CRC error occurring mid-track during playback currently performs a clean hard stop.
- **Forensic Spectral Physical Limits:**
  - High-bitrate lossy codecs that preserve upper-band energy without a hard lowpass brickwall — including `MP3 V0`, `AAC` (`>= 192 kbps`), `Opus` (`64–192 kbps`, fullband `20–24 kHz`), and `Vorbis q5` — cannot be reliably separated from lossless masters by magnitude-only FFT.
  - `MP3 320 kbps` cutoffs near `20.1–20.2 kHz` overlap genuine `20 kHz` studio-lowpassed masters and are intentionally held as `Inconclusive` (manual check needed) rather than risking false accusations on authentic recordings.
  - Quiet solo-piano 16-bit MP3 transcodes whose source had negligible harmonic energy above `14 kHz` before lossy encoding can remain `AcousticNaturalRollOff` under magnitude-only FFT.
  - Linear-interpolated upsampling of uncorrelated broadband random noise (e.g., pure pink noise) yields low Pearson mirror correlation (`r ≈ 0.12–0.17`) because random noise has no deterministic mirror contour across `22.05 kHz`.
  - Window-position sensitivity: on tracks `>= 10 s`, shifting the main FFT window by `0.10–0.50 s` produced a `22%` any-perturbation verdict flip rate on the DEV benchmark set (`21/22` flips on tracks `>= 10 s`; `2.6%` on `< 10 s` short clips).
  - 1-bit DSD files report `Inconclusive` with a representative curve (`IsSyntheticSpectrum = true`).
- **UI & Display:** `App.xaml` defaults to `Strings.id.xaml` until English is selected in `Settings > General`; secondary detail views render in outer `ScrollViewer`s while primary lists are virtualized; 32-bit integer PCM streams currently display a `32-bit float` format badge; the Full-Screen and Mini-Player backdrop is intentionally conservative behind text and controls to preserve WCAG contrast across all cover types.
- See [docs/releases/v2.1.0.md](docs/releases/v2.1.0.md) for v2.1.0 lyrics & backdrop measurements, [docs/releases/v2.0.3.md](docs/releases/v2.0.3.md) for installer/storage details, and [docs/releases/v2.0.2.md](docs/releases/v2.0.2.md) for the full FLAC/forensic benchmark tables.

## Documentation & License

- Release Notes: [docs/releases/v2.1.0.md](docs/releases/v2.1.0.md) (previous: [docs/releases/v2.0.3.md](docs/releases/v2.0.3.md), [docs/releases/v2.0.2.md](docs/releases/v2.0.2.md), [docs/releases/v2.0.1.md](docs/releases/v2.0.1.md), [docs/releases/v2.0.0.md](docs/releases/v2.0.0.md))
- Changelog: [CHANGELOG.md](CHANGELOG.md)
- Forensic Audio Authenticity Methodology (ID): [docs/METODE_ANALISIS_KEASLIAN_AUDIO.md](docs/METODE_ANALISIS_KEASLIAN_AUDIO.md)
- Third-Party Notices: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
- License: [MIT License](LICENSE)
