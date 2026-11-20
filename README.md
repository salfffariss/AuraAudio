# AuraAudio (v2.0.0)

Bit-perfect lossless desktop audio player and Nyquist FFT spectral authenticity inspector for Windows 10 and 11, built with .NET 9 and WPF in a strict pure-monochrome design system.

## Key Features

- **Bit-Perfect Audio Playback:** WASAPI Exclusive mode (bypassing the Windows Audio Engine mixer) with seamless WASAPI Shared fallback, sample-exact gapless playback, anti-pop micro-fades, DSD (DoP & DSD-to-PCM) decoding, and smooth click-free PCM digital volume scaling (bit-exact passthrough at 100%).
- **Pure-Monochrome Multi-Surface UI:** Strictly neutral monochrome token system (`R = G = B` in `UI/Tokens/NeutralTokens.xaml`) with WCAG 2.1 verified contrast (`>= 4.5:1` text, `>= 3.0:1` interactive boundaries), app-scope `SystemColors` overrides, collapsible navigation rail (`248px <-> 56px`), floating bottom dock capsule, and responsive dual-purpose right split pane (`Queue`, `History`, `Lyrics`).
- **Local Library Views:** Home shelves (*Recently Added*, *Jump Back In*, *Hi-Res & Lossless Masters*, *Your Artists*), virtualized Songs `DataGrid` with 3-column Column Browser (*Format*, *Artist*, *Album*), virtualized frameless Albums grid, master-detail Artists view, Album & Artist detail views, drag-and-drop Playlists, and dedicated scoped Search.
- **Full-Screen Stage & Mini-Player:** 5-column Full-Screen playback stage (`Ctrl+Shift+F`) with Centered `<->` Split Lyrics morph, plus an always-on-top Mini-Player (`Ctrl+Shift+M`) with Compact (`350x160`), Album View (`350x350`), and expandable Queue/Lyrics tray.
- **Synchronized Lyrics & Persistent History:** `.lrc` sidecar, embedded LRC, ID3v2 `SYLT`, and plain-text lyrics with click-to-seek, smooth auto-scroll, and in-app `.lrc` import; persistent local SQLite `play_history` (capped at 500 entries).
- **Forensic Audio Authenticity Inspector:** Full-page Audio Inspector and tabbed `Get Info` (`Ctrl+I`) sheet combining a 4096-bin multi-window Nyquist FFT spectrogram (pure monochrome), OLS spectral slope regression, dither shelf detection, and 24-bit LSB Shannon entropy audit.
- **Unified Settings & Fluid Motion:** Breadcrumb-driven Settings (*General*, *Playback & DAC*, *Library & Files*, *Accessibility*, *About*) and critically damped `SpringAnimator` motion with full Reduced Motion support.

## Requirements & Installation

- **Operating System:** Windows 10 (build 19041+) or Windows 11, **64-bit (`win-x64`)**.
- **Standalone Release (Recommended):** Download `AuraAudio-v2.0.0-win-x64-standalone.zip` from the Releases page, extract it to any folder, and run `AuraAudio.exe`. No separate .NET runtime installation is required.
- **Audio Hardware:** Any Windows audio endpoint (WASAPI Exclusive DAC recommended; automatic WASAPI Shared fallback supported).

## Build & Test From Source

Requires the [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) on Windows x64.

```powershell
# 1. Build Release
dotnet build AuraAudio.csproj -c Release

# 2. Run unit & STA test suite
dotnet test tests/AuraAudio.Tests/AuraAudio.Tests.csproj -c Release

# 3. Run application
dotnet run --project AuraAudio.csproj -c Release

# 4. Run end-to-end UI Automation smoke test (after Release build)
powershell -NoProfile -ExecutionPolicy Bypass -File tests/ui/smoke_ui.ps1

# 5. Publish self-contained win-x64 single-file executable
dotnet publish AuraAudio.csproj -c Release -r win-x64 -p:AuraStandalonePublish=true -o ./artifacts/publish/publish_v2_0
```

## Project Architecture

- `Audio/` — WASAPI Exclusive/Shared output engine (`WasapiExclusivePlayer`), smooth PCM volume control, `GaplessStreamQueue`, `AntiPopFader`, DSD (`DsfReader`, `DopStreamProvider`, `DsdToPcmStreamProvider`), `NyquistSpectrumAnalyzer`, `SignalPathAuditor`, and `AudiophilePlaybackController`.
- `Core/` — Models (`Track`, `Album`, `Artist`, `Playlist`, `AudioIntegrityReport`, `SignalPathSummary`), enums, and `LocalizationManager`.
- `Database/` — SQLite WAL schema (including `play_history`) and `DatabaseManager`.
- `Metadata/` — `AudioMetadataReader` (TagLibSharp), `LyricsParser` (`.lrc`/SYLT/plain), and `ThumbnailCacheManager`.
- `Scanner/` — High-throughput local `LibraryScanner`.
- `UI/` — Pure-monochrome tokens (`UI/Tokens/`), control templates (`UI/Styles/`), custom controls (`UI/Controls/`), views & modal sheets (`UI/Views/`), ViewModels (`UI/ViewModels/`), windows (`MainWindow`, `MiniPlayerWindow`), and helpers (`SpringAnimator`, `FluidBehaviors`, `DwmBackdropHelper`).
- `Assets/` — Pure-monochrome multi-resolution application icons (`AuraAudio.ico`, `AuraAudio.png`).
- `docs/` — Forensic DSP documentation (`METODE_ANALISIS_KEASLIAN_AUDIO.md`) and versioned release notes (`docs/releases/`).
- `tests/` — xUnit + STA test suite (`tests/AuraAudio.Tests/`) and UIA smoke test (`tests/ui/smoke_ui.ps1`).

## Known Limitations

- `App.xaml` defaults to `Strings.id.xaml`, so top-bar route titles and some status strings render in Indonesian while most new v2.0.0 XAML headings are English until English is selected in `Settings > General`.
- Secondary/detail views (`AlbumDetailView`, `PlaylistDetailView`, `ArtistDetailView`, `PlaylistsOverviewView`, `SearchView`) render inside outer `ScrollViewer`s without UI virtualization (primary large-library lists are virtualized with `Recycling`).
- See [docs/releases/v2.0.0.md](docs/releases/v2.0.0.md) for the complete verification summary and release checksums.

## Documentation & License

- Release Notes: [docs/releases/v2.0.0.md](docs/releases/v2.0.0.md)
- Changelog: [CHANGELOG.md](CHANGELOG.md)
- Forensic Audio Authenticity Methodology (ID): [docs/METODE_ANALISIS_KEASLIAN_AUDIO.md](docs/METODE_ANALISIS_KEASLIAN_AUDIO.md)
- Third-Party Notices: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
- License: [MIT License](LICENSE)
