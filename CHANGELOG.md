# Changelog

All notable changes to AuraAudio are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
