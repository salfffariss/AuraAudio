# Changelog

All notable changes to AuraAudio are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.1] - 2026-11-20

### Added
- **Dead-zero lossy brickwall scanner (`ComputeDeadZeroLossyBrickwall`):** Detects MP3-to-FLAC transcodes (`15.0 - 19.5 kHz` sharp drop into `-152 dBFS` digital silence) even on soft acoustic material (such as solo piano) whose harmonics fall below `-48 dB` before `16 kHz`.
- **Dead ultrasonic silence & broadband mirror guards:** Added `isDeadUltrasonicSilence` (`<= -152 dBFS` floor above `22.5 kHz` on `>= 48 kHz` containers) to block quiet CD upsamples from passing as natural acoustic roll-off, and `isFlatBroadbandMirror` (`r >= 0.30`, attenuation `<= 3.0 dB`, upper band `> -85 dBFS`, knee near Nyquist) to route broadband non-ideal resampler mirrors to `Inconclusive`.
- **Forensic regression & benchmark test suite (`tests/AuraAudio.Tests/ForensicHardeningTests.cs`):** Added 7 deterministic regression tests plus an opt-in 8-file SoniqTools CC0 real-audio benchmark (`AURA_SONIQTOOLS_DIR`), raising total suite coverage to 83 tests (`81 Passed, 0 Failed, 2 Skipped` with benchmark enabled; `80 Passed, 0 Failed, 3 Skipped` in default offline mode).

### Changed
- **Forensic engine calibration & band-averaged lookups (`Audio/Authenticity/NyquistSpectrumAnalyzer.cs`):** Replaced all remaining single-bin FFT lookups (`16k`, `20k`, `35k`, `40k`, and lossy path) with linear-power band averages (`GetBandPowerDb`), extracted magic numbers into named calibration constants, documented the `-6.02 dB` Hann coherent-gain internal scale offset, tightened `hasSmoothHiResContinuation`, widened the CD cliff window to `21.0 - 22.7 kHz`, and expanded `Detect24In32Alignment` probing from `100` frames to `4,096` frames with sign-extension verification on active frames.
- **Evidence-based verdict phrasing & universal AAC/Opus caveat (`NyquistSpectrumAnalyzer.cs`, `Core/Models/AudioIntegrityReport.cs`):** Reframed Indonesian and English verdict messages to evidence-based language, added the moderate-evidence hedge (`drop < 10 dB`) on `UpscaledFromCd`, and attached the high-bitrate AAC/Opus spectral limitation caveat across all `VerifiedLossless`, `AcousticNaturalRollOff`, and `HybridMaster` branches (`44.1 kHz`, `48 kHz`, and Hi-Res).
- **Documentation synchronization (`docs/METODE_ANALISIS_KEASLIAN_AUDIO.md`):** Rewrote the forensic methodology reference to match `v2.0.1` code (per-channel FFT power averaging, P90 multi-segment envelope, named constants, updated 44.1k/48k/Hi-Res decision tables, honest DSD synthetic curve disclosure, removal of an old local drive path, and the 8/8 SoniqTools CC0 benchmark table).
- **Version metadata:** Updated application version metadata to `2.0.1` across `AuraAudio.csproj`, `MainWindow`, `MiniPlayerWindow`, `SettingsView`, `SettingsViewModel`, `RuntimePixelPurityTests`, and `tests/ui/smoke_ui.ps1`.

### Fixed
- **Native AIFF decoding (`Audio/AudioDecoderFactory.cs`):** Switched `.aiff`/`.aif` files to `NAudio.Wave.AiffFileReader` (retaining `MediaFoundationReader` only as fallback for compressed AIFC), fixing playback and forensic analysis failures on uncompressed AIFF files.
- **Quiet acoustic 16-bit / 44.1 kHz false `Inconclusive`:** Updated `isSmoothAcousticRollOff` harmonic reach to `Math.Max(upperEnvelopeReachHz, detectedKneeHz) >= 9,000 Hz`, correctly classifying genuine soft acoustic 16/44.1 masters as `AcousticNaturalRollOff`.
- **Non-seekable stream timestamps:** `FindLoudestSectionTime` and `Analyze` now track seek failures explicitly and report `00:00` (`0.0%`) with a transparent note instead of a misleading timestamp.

## [2.0.0] - 2026-11-20

### Added
- **Pure-Monochrome Token System:** Centralized `UI/Tokens/NeutralTokens.xaml` (`R = G = B` across all 30 color tokens), app-scope `SystemOverrides.xaml`, `Metrics.xaml`, `Typography.xaml`, `Motion.xaml`, and pure-monochrome `Controls.xaml` templates with WCAG 2.1 contrast (`>= 4.5:1` text, `>= 3.0:1` interactive controls).
- **New Multi-Surface Shell:** 48px Top Bar with navigation history and search hints popup, collapsible Left Rail (`248px <-> 56px`) with virtualized drag-target playlists, Floating Bottom Dock capsule (`72px`), and responsive `360px` dual-purpose Right Split Pane (inline `>= 1440 DIPs`, overlay `< 1440 DIPs`).
- **New Library & Playback Views:** `HomeView` (4 real local SQLite shelves), `SongsView` (virtualized `DataGrid` + 3-column `ColumnBrowser`), `AlbumsView` (`VirtualizingUniformGrid`), `ArtistsView` (master-detail split), `ArtistDetailView`, `PlaylistsOverviewView`, `PlaylistDetailView`, `SearchView` (5 scopes + local browse cards), `FullScreenPlayerView` (Centered `<->` Split Lyrics), and standalone `MiniPlayerWindow` (Compact `<->` Album View + Queue/Lyrics tray).
- **Synchronized Lyrics & Import:** `LyricsParser` supporting `.lrc` sidecars, embedded LRC timestamps, ID3v2 `SYLT` frames, plain-text fallback, click-to-seek, auto-scroll pause, 3-dot instrumental gap indicators, and in-app `.lrc` import.
- **Persistent Play History:** Additive SQLite `play_history` table (capped at 500 entries) with foreign-key enforcement (`ForeignKeys = true`), powering Home *Jump Back In* and Queue *History* (with reset).
- **Unified Settings & Forensic Workstation:** Breadcrumb-driven `SettingsView` (*General*, *Playback & DAC*, *Library & Files*, *Accessibility*, *About*), full-page `InspectorView`, tabbed `InfoInspectorSheet` (`Ctrl+I`), `SignalPathFlyout`, and pure-monochrome `SpekSpectrumControl`.
- **Fluid Motion System:** Shared-progress critically damped `SpringAnimator` transitions (`damping = 1.0`, zero per-frame allocations) and Reduced Motion fallback.
- **Automated Verification Suite:** 73 active unit/STA tests (including `MotionContinuityInvariantTests`, `RuntimePixelPurityTests`, and `VolumeControlTests`) plus `tests/ui/smoke_ui.ps1` using language-independent `AutomationId`s.

### Changed
- Rebuilt 28 of 31 XAML files from zero; removed 13 legacy v1.3.0 views/modal dialog windows and `ModernTheme.xaml`.
- Updated application version metadata to `2.0.0` (`AuraAudio.csproj`, window titles, and `Settings > About`).
- Replaced the legacy amber application icon (`Assets/AuraAudio.ico`, `Assets/AuraAudio.png`) with a pure-monochrome multi-resolution spectrum glyph.

### Fixed
- Wired `AudiophilePlaybackController.Volume` through `IAudioOutputEngine.Volume` into smooth click-free PCM gain scaling (16/24/32-bit int & 32-bit float) across both WASAPI Exclusive and Shared modes, keeping 100% unity gain bit-exact and DoP framing untouched.
- Fixed motion continuity jumps/gaps during Right Split Pane, Left Rail, Full-Screen split morph, modal sheet close, Mini-Player resize, and `ShelfCarousel` paging transitions.
- Fixed `HI-RES` badge bottom-border clipping at 125% DPI in `HomeView` and `AlbumsView`.

## [1.0.0] - 2026-10-16

### Added
- Initial release of AuraAudio bit-perfect WASAPI Exclusive desktop audio player, SQLite library scanner, and 4096-bin Nyquist FFT / LSB spectral authenticity analyzer.
