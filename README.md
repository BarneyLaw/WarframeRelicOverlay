# WarframeRelicOverlay

A click-through Windows overlay that shows Warframe Market platinum prices over each reward card during relic-cracking missions. The overlay is hidden during normal gameplay and only appears at the reward selection screen, or when the user presses the configured hotkey (`Shift+F9` by default).

## How detection works

The overlay supports two reward-screen detection strategies, selected via `data/settings.json`:

- `EELog` (default, recommended) — tails Warframe's debug log at `%LOCALAPPDATA%\Warframe\EE.log` for the `GotRewards` line. Zero-latency, zero CPU.
- `OCR` — periodically captures a small strip of the Warframe window and runs Tesseract for the word `REWARD`. Used when EE.log is inaccessible.

Once a reward screen is detected, the pipeline screenshots the client area, finds card boundaries via intensity-profile analysis, OCRs each card name, fuzzy-matches it to the local reward pool in `data/items.json`, and fetches the lowest-sell platinum price from `api.warframe.market`.

## Display Modes

Price labels may not appear while Warframe runs in **exclusive fullscreen** because the Windows desktop compositor can prevent WPF from rendering above an exclusive fullscreen surface.

To restore overlay visibility, switch Warframe to **borderless** display mode in the in-game Display options.

## Settings

`data/settings.json` controls runtime behaviour. Keys you might want to tweak:

- `DetectionMode` — `EELog` | `OCR`
- `ToggleHotkey` — global hotkey to force-show the overlay (default `Shift+F9`)
- `OverlayOpacity` — 0.5 - 1.0
- `PriceFontSizeOverride` — 0 (auto-size) or 12-32

The file is auto-validated on load; out-of-range values are clamped and the corrected file is used.
