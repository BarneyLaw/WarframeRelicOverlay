# Warframe Relic Overlay

A C# / .NET (WPF) desktop overlay that detects Warframe's relic reward
selection screen and shows live Warframe Market prices over each reward
card, so you can pick the most valuable drop without alt-tabbing.

It is designed to run on integrated graphics: no GPU-direct frame
reading, no vision-model inference. The whole pipeline is plain CPU
work, log tailing, a thin GDI capture, a sub-millisecond pixel scan,
pooled Tesseract OCR, fuzzy matching, and a cached HTTP lookup.

---

## Motivation

When a Void Fissure cracks a relic you get a few seconds to choose one
of up to four rewards, and the "right" choice is usually the one worth
the most platinum on the market. Checking that manually means
memorising prices or tabbing out mid-mission, which is exactly when you
don't have time.

Existing community tools (WFInfo, AlecaFrame) solve this but lean on
fragile assumptions. WFInfo's layout detection brute-forces ~50 UI-scale
candidates and scores them against ~18 hard-coded theme colour arrays,
which breaks under HDR, Reshade, colourblind filters, or a theme update
from the developers; both tools require the user to copy their in-game UI
scale into a config. This project started as a CS portfolio piece, 
initially in another repository called `warframe-relic-overlay` and 
grew into an attempt to do the hard parts more robustly:

- **Resolution / aspect-ratio / UI-scale independence** without asking
  the user to configure anything.
- **Theme independence**: work regardless of the player's UI colour
  scheme or any post-processing.
- **A real-time, low-compute budget** that holds on integrated GPUs or
  warframe's minimum system requirements.
- **Graceful degradation**: when something is uncertain, show nothing
  rather than confidently show the wrong price.

The guiding principle throughout is **fail silent over fail confidently
wrong**. OCR is probabilistic, the game UI is a moving target, and the
rendering environment is uncontrolled, so the design assumes no single
stage is trustworthy on its own. 


What this project does not offer is a battle-tested, edge-case tested 
implementation that WFInfo or AlecaFrame can offer. We hope to build 
that experience and knowledge through trials within the game itself, 
and through user feedback. So feel free to use the app, and do contact 
us if you have issues to report or ideas to contribute.


---

## Features

**Zero-latency detection via EE.log tailing.** The primary detector
(`LogFileDetector`) tails Warframe's debug log
(`%LOCALAPPDATA%\Warframe\EE.log`) and fires the instant the game writes
a reward-screen line, it matches several phrases (e.g.
`OpenVoidProjectionRewardScreen`, `ProjectionRewardChoice.swf`,
`Got rewards`) so it survives log-format drift. This costs effectively
no CPU and triggers before the card animation even starts. It composes
with a reusable `FileTriggerWatcher` (FileSystemWatcher + short safety-net
poll + truncation handling) rather than reimplementing file tailing.

**OCR fallback detection.** If EE.log is unavailable (non-standard
install, permissions, network drive), `OcrFallbackDetector` polls a thin
header strip for the "REWARDS" text on a configurable interval and streak
count. The detector reports each poll as a *hint*; confirmation is the
caller's job via a streak, so the two detection modes share one state
machine.

**Resolution- and theme-independent card detection.** Instead of guessing
positions from UI-scale math, `WarmTextRowDetector` reads the actual
screenshot. It builds a mask of Warframe's warm/amber item-name text
(keyed on hue, `R >= G > B` with a wide R-B gap, not brightness, so it
survives a bright game scene bleeding through), finds rows that contain
2 - 4 evenly-spaced text runs, and validates them by the coefficient of
variation of their centre-to-centre spacing. Cards are rigidly evenly
spaced; stray warm text is not. This yields both the reward count *and*
the exact card boundaries in one pass, with no configuration and no theme
colour tables. A column-projection step recovers cards whose names wrap to
a second line (e.g. "Silva & Aegis Prime Blade").

**Settled-layout gating.** The card slide/zoom-in animation can briefly
present a plausible-but-wrong layout, so the pipeline captures, waits for
the text to settle, recaptures, and only proceeds when the two frames
agree on card count and aligned centres. This stops it cropping a frame
mid-animation.

**Fast, single-pass OCR.** `ImagePreprocessor` does one LockBits pass:
luminance &rarr; Otsu threshold &rarr; binarise, auto-inverting to dark-text-on-
white. (The old pipeline ran three Tesseract passes per card over a
GetPixel/SetPixel threshold.) OCR runs through `TesseractOcrEngine`, which
holds a pool of four engines so all four cards are recognised in true
parallel since Tesseract engines are not thread-safe, and a pool fixes the
prior shared-engine race. The character whitelist is set once at engine
creation, and `PageSegMode.SingleBlock` handles both single and two-line
names.

**Corrected fuzzy matching.** `FuzzyRewardMatcher` normalises OCR text
(lowercase, strip punctuation, fold "blue print" &rarr; "blueprint", drop
quantity noise like leading `2x`) and scores every pool item with the
max of FuzzySharp's `Ratio` and `PartialRatio`, accepting only at score
>= 70. The partial-ratio component means leading OCR garbage no longer
blocks a match the way the old "sequences from index 0 only" matcher did.
A recognised quantity prefix (e.g. "2 X ") is re-attached to the matched
name for display.

**Externalised reward pool.** The reward list lives in `data/items.json`
(~570 items) and is loaded at startup, so new Primes are added by editing
a file, no recompile. Untradeable items (e.g. Forma Blueprint) carry an
`untradeable` flag and skip the market call entirely.

**Cached pricing.** `RewardPriceCache` decorates the Warframe Market v2
client with a configurable TTL (default 5 min), so repeated rolls of the
same relic don't re-hit the API or risk rate-limiting.

**Event-driven state machine.** `OverlayStateMachine`
(Idle → Tracking → Detecting → Pricing → Displaying) is a small, locked,
table-driven machine with no polling loop. `OverlayCoordinator` wires
detection, process, and window-focus events to triggers; runs all OCR and
network work on the thread pool (the UI thread is never blocked); hides
the overlay when Warframe isn't focused; cancels the pipeline cleanly on
game exit; and has a 15-second display-timeout safety net so prices always
clear even if no exit event arrives.

**DPI-aware overlay.** The transparent, click-through, topmost WPF window
tracks the Warframe client area in physical pixels (Per-Monitor-V2),
positioning each price label directly over its detected card.

**Reward history.** Each priced run is written to
`data/reward-history.json` on screen exit (best-effort, offloaded).

**Debug mode.** Launching with `--debug` (or `DebugMode` in settings)
skips all real infrastructure and attaches a simulator so the overlay
visuals can be exercised with keyboard shortcuts. `SaveDebugImages` dumps
every capture/crop/preprocessed bitmap to `debug-images/` for diagnosis.

**Tested.** ~286 xUnit tests across Domain, Infrastructure, OverlayApp,
Core, and Integration projects, including an end-to-end pipeline test that
wires the real components together.

---

## Architecture

Layered, with cross-layer communication through interfaces and a
`Microsoft.Extensions.DependencyInjection` composition root in `App.xaml.cs`:

- **Domain**: pure logic: models, `FuzzyRewardMatcher`,
  `OcrTextNormalizer`, price-cache decorator. No WPF/Win32.
- **Infrastructure**: adapters to the outside world: pooled Tesseract,
  GDI capture, Warframe Market client, JSON reward repository, Win32
  process/window tracking, file logging, reward history.
- **OverlayApp (Application)**: orchestration: detectors, layout
  detector, the reward-pricing pipeline, the state machine.
- **Presentation**: the WPF overlay window and view model.
- **Core**: entry point, DI wiring, settings, the coordinator.

Pipeline per reward screen:
`EE.log trigger → (focus check) → capture → settled-layout gate →
WarmTextRowDetector → crop → preprocess + OCR (x4 parallel) →
fuzzy match → cached price → display`.

---

## Getting started

Requirements: .NET 10 SDK, Windows (WPF + Win32 interop), Warframe on PC.
The repo bundles `tessdata/eng.traineddata`.

```bash
git clone https://github.com/BarneyLaw/WarframeRelicOverlay
cd WarframeRelicOverlay
dotnet build
dotnet test            # run the suite
dotnet run --project . # launch the overlay
dotnet run --project . -- --debug   # overlay visuals without the game
```

Settings live in `data/settings.json` and are clamped/validated on load
(a corrupt file is renamed `.bak` and defaults are used). Key fields:
`DetectionMode` (`EELog` / `OCR` / `Manual`), `EeLogPathOverride`,
`DetectionIntervalMs`, `DetectionStreak`, `StabilizationDelayMs`,
`PriceCacheTtlMinutes`, `OverlayOpacity`, `PriceFontSizeOverride`,
`ToggleHotkey`, `DebugMode`, `SaveDebugImages`.

---

## Coming additions

- **In-app settings UI.** Settings are currently file-driven; the
  intended end state is a Steam-style two-layer overlay with a toggled
  menu (Shift+F9) and tabs for Settings, About, and a live Log view, plus
  a keybind picker and sliders. The settings model, persistence, and
  hotkey field already exist; the menu layer and its bindings are the
  remaining work.
- **Auto-updating item pool.** An `ApiRewardRepository` that pulls the
  relic-reward item list from the Warframe Market `/v2/items` endpoint at
  startup and merges it with the local JSON, so new Primes appear without
  editing a file.
- **Debug visualisation overlay.** Render the detected card boxes and the
  warm-text intensity profile on screen when Debug Mode is on, to make
  layout-detection failures obvious at a glance.
- **Async/buffered logger.** The file logger currently writes
  synchronously; a channel-backed background writer would remove disk I/O
  from any hot path.
- **Richer pricing.** Beyond lowest sell price, e.g. recent-trade volume
  or median, to flag thinly-traded items whose "price" is unreliable.

---

## Failure modes

The system is built to fail toward showing nothing rather than showing a
wrong price. Known failure modes and how they're handled:

- **No reward cards detected.** If `WarmTextRowDetector` returns zero
  boxes (wrong colours, heavy post-processing, an unexpected layout), the
  pipeline returns empty and the state machine fires `PricingFailed`,
  dropping back to Tracking with nothing displayed.
- **Layout never settles.** If capture/recapture never agree within the
  readiness timeout (~12 s), the pipeline gives up and returns a final
  diagnostic capture rather than cropping a mid-animation frame.
- **OCR garbage.** If the recognised text scores below 70 against every
  pool item, that card shows no match, better a blank than a confident
  mislabel.
- **Item not in pool.** A reward that isn't in `items.json` can't be
  matched; it shows blank until the pool is updated.
- **Market lookup fails / item untradeable.** A failed or skipped lookup
  yields a card with a name but no price; the rest of the cards still
  price normally.
- **EE.log missing or non-standard install.** Primary detection silently
  yields nothing; the user switches to `OCR` mode (or sets
  `EeLogPathOverride`).
- **Alt-tabbed away.** Detection can still fire in the background, but the
  coordinator checks window focus and skips pricing when Warframe isn't
  foreground, so the overlay never paints over another app.
- **Game closes mid-pipeline.** `WarframeStopped` cancels the running
  pipeline via its `CancellationToken`; a post-pipeline cancellation check
  prevents pushing stale prices after the move to Idle.
- **Missed startup event.** If Warframe was already running before the
  coordinator subscribed, it checks `IsRunning` and fires `WarframeStarted`
  manually so it doesn't sit idle.
- **No exit signal.** A 15-second display timeout clears prices even if no
  `RewardScreenExited` event ever arrives (e.g. Manual mode).

---

## Limitations worth investigating

These are the open weak points, places where the current approach is
"good enough" but could be made meaningfully better.

- **Tesseract on Warframe's font.** OCR currently uses stock English
  `eng.traineddata`. The design stance is that fuzzy matching absorbs most
  OCR error, so fine-tuning is only justified once measured pipeline
  failure rates show OCR output is too corrupt for fuzzy recovery, or that
  genuinely ambiguous item pairs are misfiring. The right next step is to
  *measure* per-card match/price success on real captures before investing
  in training a Warframe-font model, collect failures via `SaveDebugImages`
  and quantify them.
- **Layout detection robustness.** `WarmTextRowDetector` assumes the item
  name is warm/amber and that cards are evenly spaced. Both assumptions are
  strong but not guaranteed where a future UI restyle, an aggressive colour
  filter that desaturates the warm hue, or a very narrow aspect ratio could
  defeat the warm-colour mask or the spacing CV check. Worth exploring: an
  edge/contrast-based fallback detector; using the "REWARDS" header as a
  vertical anchor to constrain the search band; or a small validation pass
  that cross-checks detected card count against the EE.log reward count.
- **Vertical search band is heuristic.** The name row is searched across a
  fixed fraction of window height (25–58%). Extreme UI scales could push the
  text outside that band. Anchoring on the header text would remove the
  guess.
- **Hue-threshold constants are tuned, not learned.** The warm-mask
  thresholds and the various fractional tunables were hand-calibrated. A
  small labelled dataset of reward screenshots across resolutions/themes
  would let these be fit empirically (and regression-tested).
- **Header readiness uses a hardcoded region.** The reward-header readiness
  check (and the OCR fallback strip) still use fixed proportional
  rectangles, which is the same brittleness the layout detector was built
  to avoid; ideally these would key off detected content too.
- **Market data quality.** Lowest sell price ignores listing age and
  volume, so a single stale lowball listing can misrepresent an item's real
  value. Incorporating volume/median would make the "most valuable" call
  more honest.
- **Slug conversion edge cases.** `MarketSlugConverter` handles `&` &rarr; `and`
  and a hardcoded `2x Forma Blueprint`, but the broader component-suffix
  handling described in the design doc (stripping " blueprint" only for
  component items) isn't fully generalised yet; unusual item names may slug
  incorrectly and miss a price.
- **No guarantees are possible.** This is fundamental, not a bug:
  probabilistic OCR + a moving-target UI + an uncontrolled rendering
  environment mean correctness can only ever be high, never certain. The
  architecture leans into that by degrading gracefully rather than promising
  accuracy.