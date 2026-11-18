# AuraAudio Design Direction (Option A: Studio Audio Presisi)

Design Read: Reading this as: desktop lossless music player and spectral authenticity analyzer for music collectors, in a warm hi-fi instrument language, dial ENERGY 2 / RHYTHM 2 / MOTION 1.

## Liveliness Dials
- **ENERGY 2**: Calm, disciplined instrument frame with one deliberate warm amber accent and clear status cues.
- **RHYTHM 2**: Visual composition shifts smoothly between airy artwork-forward collection views (Albums, Artists, Library) and focused precision readout views (Authenticity Inspector, DAC Configuration).
- **MOTION 1**: Crisp 120ms hover color transitions and play-state-only waveform bar movement (`SystemParameters.ClientAreaAnimation` respected). Zero perpetual decorative pulses.

## Verified WCAG AA Color Palette (R-25, R-29)
- **Surfaces (Warm Charcoal Core)**:
  - Window Canvas (`Brush.Background.Window`): `#111114`
  - Chrome / Sidebar / Player Bar (`Brush.Background.Sidebar`, `Brush.Background.PlayerBar`): `#141418`
  - Primary Surface Card (`Brush.Background.Card`): `#17171C`
  - Elevated / Hover Surface (`Brush.Background.CardHover`): `#1E1E25`
  - Input Field Surface (`Brush.Background.Input`): `#15151A`
- **Borders**:
  - Structural Hairline (`Brush.Border.Subtle`): `#282832` (1px subtle framing between sections)
  - Interactive Control Border (`Brush.Border.Light`): `#6E6C7A` (3.23:1 on `#1E1E25`, passes WCAG 1.4.11 3:1 non-text contrast)
- **Text Hierarchy (All >= 4.5:1 AA on `#17171C` & `#1E1E25`)**:
  - Primary Text (`Brush.Text.Primary`): `#F5F2EB` (Warm Ivory, 15.98:1 on `#17171C`, 16.86:1 on `#111114`)
  - Secondary Text (`Brush.Text.Secondary`): `#B8B4AA` (Warm Silver, 8.63:1 on `#17171C`, 9.11:1 on `#111114`)
  - Muted / Label Text (`Brush.Text.Tertiary`): `#949087` (Warm Stone, 5.61:1 on `#17171C`, 5.21:1 on `#1E1E25`)
- **Single Deliberate Accent (R-01)**:
  - Warm Amber (`Brush.Accent.Gold`): `#E5A23C` (8.14:1 on `#17171C`, `#111114` text on `#E5A23C` = 8.59:1)
  - Hover Amber (`Brush.Accent.GoldHover`): `#F0B24F`
  - Pressed Amber: `#C98A2B`
  - Used strictly for primary Play/Action CTAs, active navigation state, playback progress track, and keyboard focus rings.
- **Semantic Status Colors (Always Paired With Text & Distinct Vector Icon)**:
  - Genuine / Bit-Perfect (`Brush.Accent.Green`): `#34D399` (9.29:1 on `#17171C`)
  - Caution / Upscaled / Padded / Shared (`Brush.Accent.Warning`): `#FBBF24` (10.70:1 on `#17171C`)
  - Fake / Transcoded Lossy / Destructive (`Brush.Accent.Red`): `#F87171` (6.46:1 on `#17171C`)
  - Acoustic / Hybrid / Info (`Brush.Accent.Cyan`): `#93C5FD` (9.90:1 on `#17171C`)

## Typography & Hierarchy (R-06)
- **Primary UI Font**: `Segoe UI Variable Text, Segoe UI, sans-serif` in natural sentence case for clear scanning without shouted uppercase labels.
- **Tabular Instrument Readout Font**: `Cascadia Mono, Consolas, monospace` strictly for numeric telemetry (kHz, dBFS, kbps, percentages, timestamps) so numbers align cleanly like a desktop DAC front panel.

## Identity Motif & One-Line Decision Reasons (R-31)
- **Why warm charcoal dark theme**: Spectral power curves from 0 dBFS down to -145 dBFS and subtle waveform markers maintain legibility and avoid eye glare over dark studio backgrounds (R-21).
- **Why single warm amber accent**: Concentrates visual weight on playback transport, active view selection, and keyboard focus without competing with semantic authenticity verdicts.
- **Why 1.5px geometric `Path` icons instead of emojis**: Uniform crisp monochrome vector geometry gives AuraAudio a cohesive desktop instrument look rather than mixed OS emoji glyphs (R-04).
- **Why 2-layer progressive disclosure in Authenticity Inspector**: Leading with one unified Verdict + Spectrum Graph focal block followed by a 2x2 plain-language Specification grid lets collectors understand file quality at a glance, while keeping deep DSP numbers tucked inside a collapsible `Expander`.
- **Why varied rhythm across views**: Library, Albums, and Artists prioritize artwork and track readability, whereas Authenticity Inspector and DAC Configuration shift into structured diagnostic and hardware control panels (R-05).
