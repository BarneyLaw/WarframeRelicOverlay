# Warframe Relic Price Overlay — Architectural Overhaul

## Executive Summary

After a full audit of every source file in the repository, this document catalogues the structural problems in the current codebase and prescribes a ground-up redesign that meets four hard requirements: sub-4-second reward pricing cycles, a proper two-layer Steam-style overlay with a settings tab, sound OOP/SWE principles, and full correctness in OCR-to-price accuracy.

The overhaul touches every module. Nothing in the current codebase is beyond saving, but almost everything needs to move, split, or be rewritten.

---

## Part 1 — Audit of the Current Codebase

### 1.1 God-Class AppController

`AppController.cs` is ~250 lines of interleaved concerns: process tracking, window positioning, DPI calculation, state machine transitions, OCR scheduling, reward capture coordination, price fetching, and overlay rendering. It runs everything from a single `DispatcherTimer.Tick` handler on the **UI thread**.

**Consequences:**

- Every OCR call (`CheckForRewardScreen.TryDetectRewardScreen`) blocks the dispatcher. During the detection phase (750ms poll interval × 4 consecutive hits required), the UI thread is frozen for the duration of each Tesseract invocation.
- `captureStableReward()` returns `Task` but is called with implicit `async void` semantics from the tick handler — exceptions are silently swallowed and the method interleaves `await` with UI thread dispatcher work in unpredictable ways.
- `DoEvents()` uses `Dispatcher.PushFrame`, a well-known WPF anti-pattern that causes re-entrant message pumping, leading to subtle race conditions.

### 1.2 Static Mutable Global State

Three classes serve as ambient mutable singletons:

| Class | Problem |
|---|---|
| `GlobalState.ProcessID` | Mutable static `int?` read/written from both event handlers and the timer tick without synchronization |
| `WarframeWindowInfo` | 12 mutable static properties written every tick, read by OCR and UI code on potentially different threads |
| `TesseractObject.tessEngine` | Single shared `TesseractEngine` instance — **not thread-safe** yet used from parallel `Task.WhenAll` in `captureStableReward` |

The `TesseractEngine` thread-safety issue is a **live crash bug**. When 4 rewards are captured in parallel, four threads call `tessEngine.Process()` concurrently on a non-thread-safe object.

### 1.3 Painfully Slow OCR Pipeline

The current pipeline for a single reward box:

1. `ScreenCaptureRow.captureRegion` — GDI `CopyFromScreen` (fine)
2. `ImageToText.multiPassOCR` — runs **3 sequential** Tesseract passes (raw, grayscale, grayscale+threshold)
3. `ScreenCaptureRow.Threshold` — uses `GetPixel`/`SetPixel` per-pixel loop (orders of magnitude slower than `LockBits`)

For 4 rewards, steps 2-3 mean **12 Tesseract invocations** (3 passes × 4 boxes). Each Tesseract call on a small bitmap takes ~150-300ms. That alone is 1.8-3.6 seconds — and the pre-detection phase has already burned 4 × 750ms = 3 seconds waiting for streak confirmation, plus a 1-second stabilization delay. Total worst-case: **~7 seconds** from reward screen appearance to price display.

### 1.4 Broken Async Pattern

```csharp
// In OnTick (sync void handler):
captureStableReward();  // Returns Task — fire-and-forget, no await
```

The `async Task captureStableReward()` method is called without `await` from a synchronous tick handler. This means:
- The calling code continues immediately — `_hasCapturedStableReward = true` runs before any OCR completes.
- Any exception inside the task is never observed.
- The `_overlayRenderer.Hud.HideLoadingIndicator()` call executes before prices are fetched.

### 1.5 Hardcoded Reward Pool (~600 lines)

`RewardPool.cs` is a massive handwritten list. Problems:
- Cannot be updated without recompilation.
- Already contains duplicates (Vectis Prime and Velox Prime are listed twice).
- Contains data entry errors ("Carrier Prime Cerebum" — should be "Cerebrum"; "girp" in `ComponentKeywords` should be "grip").
- Missing many items (no Warframes after Yareli Prime, no newer weapon primes).
- The `MatchPattern` field is always just `CanonicalName.ToLower()` — the field is redundant.

### 1.6 Fuzzy Matching Accuracy Issues

`RewardMatcher.matchSingle` only tries word sequences **starting from index 0**. If OCR produces garbage characters before the item name, the matcher will try "junk ash prime chassis" but never "ash prime chassis" alone. The threshold of 20 is also extremely low — `Fuzz.TokenSetRatio` on unrelated strings can easily exceed 20, producing false positives.

### 1.7 No Price Caching

`Cache.cs` is an empty class. Every reward cycle hits the Warframe Market API for every item, even if the same item was priced 30 seconds ago. This adds network latency to every cycle and risks rate-limiting.

### 1.8 Bare-Bones UI

The menu is a hardcoded StackPanel with three lines of static text. There is no settings tab, no adjustment capability, no way to change the hotkey, detection parameters, or overlay appearance. The HUD just draws gold `TextBlock`s with `RemoveAfterDelayAsync(1500)` — prices disappear after 1.5 seconds even if the reward screen is still showing.

### 1.9 Naming Convention Chaos

The codebase mixes `PascalCase`, `camelCase`, and inconsistent patterns:
- `checkIfRunning`, `captureStableReward`, `startLoop` (should be Pascal for methods)
- `hudRenderer` class (should be `HudRenderer`)
- `singleBoxOCR`, `multiPassOCR`, `saveDebugImage` (should be Pascal)
- `isComponent`, `toGrayScale` (same)

### 1.10 Miscellaneous Issues

- `WarframeMarketNaming.ComponentKeywords` includes "girp" (typo for "grip") and "forma", but misses "chassis", "systems", "neuroptics".
- `Logger` does synchronous `File.AppendAllText` on every call — disk I/O on the UI thread.
- Debug images are saved unconditionally on every OCR call via `saveDebugImage` — fills disk and costs time.
- `SetVariable("tessedit_char_whitelist", ...)` is called on every single OCR invocation, reconfiguring the engine unnecessarily.

### 1.11 Hardcoded Layout Assumptions Break Across Configurations

`ScreenCaptureRow` uses fixed proportional constants (e.g., `0.379 * HeightPx`, `0.125 * WidthPx`) to calculate reward box positions. These assume a specific aspect ratio and 100% UI scale. In reality, **Warframe's reward screen layout varies across three independent axes:**

1. **Aspect ratio** — Warframe supports 4:3, 16:10, 16:9, 21:9, 32:9, and Auto. The reward cards are always horizontally centered, but the proportional x-positions shift depending on ratio. On 21:9 ultrawide, the cards occupy a smaller fraction of total width. On 4:3, they're packed tighter.

2. **In-game UI scaling** — Warframe has a UI scale setting (Display settings). The reward screen scales with it. Reference measurements from WFInfo's codebase show the baseline is 1920×1080 at 100% scale: reward area is 968px wide, 235px tall, offset 316px from vertical center. Everything is multiplied by `ScreenScaling × uiScaling`.

3. **Windows DPI scaling** — A 4K monitor at 200% DPI means the Warframe window is 3840×2160 pixels but may report as 1920×1080 to DPI-unaware apps. Warframe's "Scaled" vs "Native" display mode interacts with this further.

Additionally, `CountRewards()` assumes a 4-slot layout when probing for reward count, then uses text quality scores to count how many slots "look real." If there are only 2 or 3 rewards, the 4-slot box positions are **wrong** because Warframe centers the cards differently depending on count — so you're OCR-ing the wrong screen regions and then guessing from garbage output.

The `GetRewardBoxPx(int index, int totalRewards)` method does account for variable reward counts in its centering math, but `CountRewards()` has a chicken-and-egg problem: it needs to know the count to position the boxes correctly, but it's using incorrectly-positioned boxes to determine the count.

---

## Part 2 — Target Architecture

### 2.1 Solution Structure

```
WarframeRelicOverlay/
├── WarframeRelicOverlay.sln
│
├── src/
│   ├── Core/                          # Application entry, DI composition root
│   │   ├── App.xaml / App.xaml.cs
│   │   ├── CompositionRoot.cs         # Microsoft.Extensions.DI container setup
│   │   └── AppSettings.cs             # Serializable settings model (JSON)
│   │
│   ├── Domain/                        # Pure domain logic (no WPF/Win32 deps)
│   │   ├── Models/
│   │   │   ├── RewardItem.cs          # Immutable record
│   │   │   ├── PricedReward.cs        # RewardItem + price + timestamp
│   │   │   └── DetectionResult.cs     # Value object for OCR output
│   │   ├── Matching/
│   │   │   ├── IRewardMatcher.cs
│   │   │   └── FuzzyRewardMatcher.cs  # Corrected algorithm
│   │   ├── Normalization/
│   │   │   └── OcrTextNormalizer.cs
│   │   └── Pricing/
│   │       ├── IPriceProvider.cs
│   │       └── CachedPriceProvider.cs  # Decorator over WarframeMarketClient
│   │
│   ├── Infrastructure/                 # External world adapters
│   │   ├── OCR/
│   │   │   ├── IOcrEngine.cs
│   │   │   ├── TesseractOcrEngine.cs  # Pooled engines, one per thread
│   │   │   └── ImagePreprocessor.cs   # LockBits threshold, single-pass
│   │   ├── ScreenCapture/
│   │   │   ├── IScreenCapturer.cs
│   │   │   └── GdiScreenCapturer.cs
│   │   ├── Market/
│   │   │   ├── IWarframeMarketApi.cs
│   │   │   ├── WarframeMarketClient.cs
│   │   │   └── MarketDtos.cs
│   │   ├── RewardData/
│   │   │   ├── IRewardRepository.cs
│   │   │   ├── JsonRewardRepository.cs # Loads from items.json on disk
│   │   │   └── ApiRewardRepository.cs  # Fetches from WF Market items endpoint
│   │   └── Platform/
│   │       ├── IProcessTracker.cs
│   │       ├── WarframeProcessTracker.cs
│   │       ├── IWindowTracker.cs
│   │       ├── WarframeWindowTracker.cs
│   │       └── Win32Interop.cs
│   │
│   ├── Application/                    # Orchestration layer
│   │   ├── Pipeline/
│   │   │   ├── IRewardPipeline.cs
│   │   │   └── RewardPricingPipeline.cs  # Capture → OCR → Match → Price
│   │   ├── StateMachine/
│   │   │   ├── OverlayState.cs         # Enum: Idle, Tracking, Detecting, Pricing, Displaying
│   │   │   └── OverlayStateMachine.cs  # Clean state transitions, event-driven
│   │   ├── Detection/
│   │   │   ├── IRewardScreenDetector.cs
│   │   │   ├── LogFileDetector.cs      # Tails EE.log for "Got rewards"
│   │   │   └── OcrFallbackDetector.cs  # OCR streak fallback if log unavailable
│   │   └── Layout/
│   │       ├── IRewardLayoutDetector.cs
│   │       └── IntensityProfileDetector.cs # Horizontal scan for card boundaries
│   │
│   └── Presentation/                   # WPF UI layer
│       ├── Overlay/
│       │   ├── OverlayWindow.xaml/.cs  # Transparent topmost window
│       │   ├── OverlayViewModel.cs     # Bindable state for HUD
│       │   └── OverlayPositioner.cs    # DPI-aware window placement
│       ├── HUD/
│       │   ├── HudLayer.xaml/.cs       # Price display UserControl
│       │   ├── StatusIndicator.xaml/.cs # "Active" indicator
│       │   └── LoadingSpinner.xaml/.cs
│       ├── Menu/
│       │   ├── MenuLayer.xaml/.cs      # Steam-style overlay menu
│       │   ├── MenuViewModel.cs
│       │   ├── Tabs/
│       │   │   ├── SettingsTab.xaml/.cs
│       │   │   ├── SettingsViewModel.cs
│       │   │   ├── AboutTab.xaml/.cs
│       │   │   └── LogTab.xaml/.cs
│       │   └── Controls/
│       │       ├── SliderSetting.xaml/.cs
│       │       ├── KeybindPicker.xaml/.cs
│       │       └── ToggleSetting.xaml/.cs
│       ├── Shell/
│       │   ├── ShellController.cs      # Click-through toggle, hotkey management
│       │   └── HotkeyService.cs
│       └── Converters/
│           └── PriceToDisplayConverter.cs
│
├── data/
│   ├── items.json                      # Reward pool (editable without recompilation)
│   └── settings.json                   # User settings (persisted)
│
└── tests/
    ├── Domain.Tests/
    │   ├── FuzzyRewardMatcherTests.cs
    │   ├── OcrTextNormalizerTests.cs
    │   └── CachedPriceProviderTests.cs
    └── Integration.Tests/
        └── RewardPricingPipelineTests.cs
```

### 2.2 Design Principles Applied

**Single Responsibility:** Every class has one reason to change. `OverlayStateMachine` manages transitions. `RewardPricingPipeline` coordinates the capture→price pipeline. `TesseractOcrEngine` wraps Tesseract. No more AppController doing everything.

**Dependency Inversion:** All cross-layer communication is through interfaces (`IOcrEngine`, `IPriceProvider`, `IRewardMatcher`, `IScreenCapturer`, `IProcessTracker`). The composition root wires them. This makes every component independently testable.

**Open/Closed:** The reward pool is loaded from `items.json` at startup — new primes can be added by editing a file. The `IRewardRepository` interface allows swapping to the Warframe Market items API endpoint if desired.

**Interface Segregation:** `IProcessTracker` exposes only `IsRunning`, `ProcessId`, and start/stop events. `IWindowTracker` exposes only `TryGetBounds`. No leaking of concerns.

---

## Part 3 — Critical Fixes for Performance (Sub-4-Second Target)

### 3.1 Detection Phase: 3s → ~0s (EE.log Tailing)

**Current:** 4 consecutive OCR detections at 750ms intervals = 3 seconds minimum, plus unreliable because OCR is overkill for detecting a single word.

**Primary fix — tail Warframe's debug log:** Warframe writes `"Got rewards"` to its debug log file (`%LOCALAPPDATA%\Warframe\EE.log`) the instant the reward screen appears. This is how WFInfo's auto-mode works. Tailing the log file gives a **zero-latency, 100% reliable** trigger with no OCR cost.

```csharp
public sealed class LogFileDetector : IRewardScreenDetector, IDisposable
{
    private readonly string _logPath;
    private readonly Timer _timer;
    private long _lastPosition;

    public event Action? RewardScreenDetected;
    public event Action? RewardScreenExited;

    public LogFileDetector()
    {
        _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Warframe", "EE.log");
    }

    public void Start()
    {
        if (!File.Exists(_logPath)) return;
        _lastPosition = new FileInfo(_logPath).Length;

        // Poll every 100ms — reading a few bytes is trivial
        _timer = new Timer(CheckLog, null, 0, 100);
    }

    private void CheckLog(object? _)
    {
        try
        {
            using var fs = new FileStream(_logPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);

            if (fs.Length <= _lastPosition) return;

            fs.Seek(_lastPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string newContent = reader.ReadToEnd();
            _lastPosition = fs.Length;

            if (newContent.Contains("Got rewards"))
                RewardScreenDetected?.Invoke();
        }
        catch { /* file locked by Warframe — retry next tick */ }
    }

    public void Dispose() => _timer?.Dispose();
}
```

**Fallback — OCR streak (reduced):** If the EE.log is unavailable (e.g., Warframe installed in a non-standard location), fall back to OCR detection with a 250ms interval and 2-hit streak (0.5s worst-case). This is exposed as a settings toggle.

```
EE.log mode:  Warframe writes log → 100ms poll detects it → trigger (0.1s)
OCR fallback: [250ms] detect → [250ms] confirm → trigger (0.5s)
Current:      [750ms] × 4 streak hits → trigger (3.0s)
```

### 3.2 Stabilization Delay: 1s → 250ms (configurable)

**Current:** After detection confirmation, waits an additional 1000ms before capturing reward text.

**Fix:** With EE.log tailing, the trigger fires at the moment Warframe creates the reward screen internally — the UI animation may still be playing. A 250ms delay (configurable in settings, range 0–2000ms) is sufficient for the text to render. The current 1000ms was over-compensating for the 3s OCR detection streak already consuming most of the animation time.

### 3.3 OCR Passes: 12 → 4

**Current:** `multiPassOCR` runs 3 passes per box (raw, grayscale, grayscale+threshold), picking the best by a heuristic score. For 4 rewards: 12 Tesseract invocations.

**Fix:** Use a single optimized preprocessing pipeline:

```csharp
public sealed class ImagePreprocessor
{
    public Bitmap PrepareForOcr(Bitmap source)
    {
        // Single pass: convert to grayscale + threshold using LockBits
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format8bppIndexed);
        // ... fast pointer-based grayscale + Otsu threshold in one pass

        source.UnlockBits(data);
        return result;
    }
}
```

**Why this works:** The multi-pass approach was a brute-force attempt to handle varying contrast. A single adaptive threshold (Otsu's method) with `LockBits` achieves better results in 1/10th the time. The `GetPixel`/`SetPixel` loop in the current `Threshold()` is approximately 100x slower than `LockBits` for a typical reward box region.

### 3.4 Tesseract Thread Safety: Pool of Engines

**Current:** Single shared `TesseractEngine` — concurrent access is undefined behavior.

**Fix:** Use `ObjectPool<TesseractEngine>` (from `Microsoft.Extensions.ObjectPool`):

```csharp
public sealed class TesseractOcrEngine : IOcrEngine, IDisposable
{
    private readonly ObjectPool<TesseractEngine> _pool;

    public TesseractOcrEngine(string tessDataPath, int poolSize = 4)
    {
        var policy = new TesseractEnginePoolPolicy(tessDataPath);
        _pool = new DefaultObjectPool<TesseractEngine>(policy, poolSize);
    }

    public string Recognize(Bitmap image)
    {
        var engine = _pool.Get();
        try
        {
            using var pix = PixConverter.ToPix(image);
            using var page = engine.Process(pix);
            return page.GetText().Trim();
        }
        finally
        {
            _pool.Return(engine);
        }
    }
}
```

Four pooled engines allow truly parallel OCR for 4 reward boxes without contention.

### 3.5 Price Caching: Network → Memory

```csharp
public sealed class CachedPriceProvider : IPriceProvider
{
    private readonly IWarframeMarketApi _api;
    private readonly ConcurrentDictionary<string, (int? Price, DateTime FetchedAt)> _cache = new();
    private readonly TimeSpan _ttl;

    public CachedPriceProvider(IWarframeMarketApi api, TimeSpan? ttl = null)
    {
        _api = api;
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
    }

    public async Task<int?> GetPriceAsync(string slug)
    {
        if (_cache.TryGetValue(slug, out var cached) &&
            DateTime.UtcNow - cached.FetchedAt < _ttl)
        {
            return cached.Price;
        }

        int? price = await _api.GetLowestSellPriceAsync(slug);
        _cache[slug] = (price, DateTime.UtcNow);
        return price;
    }
}
```

Cache TTL is exposed in settings (default 5 minutes). For Forma Blueprint (always untradeable), a hardcoded exclusion avoids the API call entirely.

### 3.6 Projected Timeline

| Phase | Current | Proposed |
|---|---|---|
| Detection trigger | 3000ms (OCR streak) | ~100ms (EE.log tail) |
| Stabilization delay | 1000ms | 250ms (configurable) |
| Layout detection (card count + boundaries) | ~600ms (OCR 4 boxes + score) | <1ms (intensity profile) |
| Screen capture (per-card) | ~20ms | ~20ms |
| Image preprocessing (4 cards) | ~400ms (GetPixel) | ~10ms (LockBits) |
| OCR (4 cards × 3 passes) | 1800–3600ms | 600–1200ms (4 cards × 1 pass, parallel) |
| Fuzzy matching | ~50ms | ~20ms |
| API calls (4 items) | 400–800ms | 0–800ms (cached or parallel) |
| **Total** | **6.7–8.9s** | **1.0–2.3s** |

---

## Part 4 — Corrected Fuzzy Matching

### 4.1 Current Algorithm's Flaws

The `matchSingle` method only builds word sequences starting from position 0:

```csharp
// Current: only tries words[..1], words[..2], words[..3], etc.
for (int length = 1; length <= 5 && length <= words.Length; length++)
{
    string segment = string.Join(' ', words[..length]);
    // ...
}
```

If OCR produces `"1 ash prime chassis blueprint"`, the matcher tries:
- `"1"` → low scores
- `"1 ash"` → low scores
- `"1 ash prime"` → moderate score but wrong match
- `"1 ash prime chassis"` → might match but penalized by leading "1"

It should try **every** starting position.

### 4.2 Corrected Algorithm

```csharp
public sealed class FuzzyRewardMatcher : IRewardMatcher
{
    private readonly IReadOnlyList<RewardItem> _pool;
    private const int MatchThreshold = 75;  // Raised from 20

    public RewardItem? MatchSingle(string ocrText)
    {
        string normalized = OcrTextNormalizer.Normalize(ocrText);
        string[] words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        RewardItem? bestMatch = null;
        int bestScore = 0;

        // Try every starting position AND every length
        for (int start = 0; start < words.Length; start++)
        {
            for (int len = 1; len <= 5 && start + len <= words.Length; len++)
            {
                string segment = string.Join(' ', words[start..(start + len)]);

                foreach (var item in _pool)
                {
                    int score = Fuzz.TokenSetRatio(segment, item.CanonicalName);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMatch = item;
                    }
                }
            }
        }

        return bestScore >= MatchThreshold ? bestMatch : null;
    }
}
```

Key changes:
- **Sliding window** over all starting positions, not just position 0.
- **Threshold raised to 75** (from 20) to eliminate false positives. `TokenSetRatio` returns 100 for perfect matches; 75 allows ~25% character error while rejecting garbage.
- Pool items loaded from JSON, not a hardcoded list.

### 4.3 Performance Optimization: Pre-computed Index

For very large item pools, build a lookup index by common token:

```csharp
// At startup, build: "ash" → [Ash Prime Chassis BP, Ash Prime Neuro BP, ...]
private readonly Dictionary<string, List<RewardItem>> _tokenIndex;

// During matching, only score items that share at least one token with the OCR text
var candidates = words
    .Where(w => _tokenIndex.ContainsKey(w))
    .SelectMany(w => _tokenIndex[w])
    .Distinct()
    .ToList();
```

This reduces the inner loop from ~500 items to ~10-20 candidates per match.

---

## Part 5 — Two-Layer Steam-Style Overlay UI

### 5.1 Architecture

The overlay window has two layers rendered as overlapping `Canvas` or `Grid` panels:

**Layer 1 — HUD (always visible when Warframe focused):**
- Top-left status indicator ("Relic Overlay Active" + green dot)
- Price labels positioned over reward slots
- Loading spinner during pricing cycle
- Fully click-through (WS_EX_TRANSPARENT)

**Layer 2 — Menu (toggled with Shift+F9):**
- Semi-transparent dark backdrop (click captures)
- Centered panel with tabbed navigation
- Click-interactive (WS_EX_TRANSPARENT removed)

### 5.2 Menu Tabs

**Tab 1: Settings**

| Setting | Type | Default | Effect |
|---|---|---|---|
| Toggle Hotkey | Keybind picker | Shift+F9 | Changes the menu toggle |
| Detection Mode | Dropdown | EE.log Auto | "EE.log Auto" (tails game log), "OCR Polling" (fallback), "Manual Hotkey" |
| EE.log Path | File picker | Auto-detected | Override path to Warframe's EE.log if non-standard install |
| Detection Interval (OCR mode) | Slider (100–1000ms) | 250ms | Poll rate for OCR fallback detection |
| Detection Streak (OCR mode) | Slider (1–6) | 2 | Consecutive OCR hits to confirm (fallback only) |
| Stabilization Delay | Slider (0–2000ms) | 250ms | Wait after detection before capturing rewards |
| Price Cache TTL | Slider (1–30 min) | 5 min | How long cached prices are valid |
| Overlay Opacity | Slider (0.5–1.0) | 1.0 | Price label opacity |
| Price Font Size | Slider (12–32) | Auto | Override auto-scaled font size |
| Debug Mode | Toggle | Off | Shows detected card bounding boxes, intensity profile |
| Save Debug Images | Toggle | Off | Writes captures and profiles to disk |

**Tab 2: About**
- Version number, repo link, credits

**Tab 3: Log (Debug Mode only)**
- Scrolling live log view (ring buffer of last 200 entries)

### 5.3 Settings Persistence

Settings are serialized to `data/settings.json` using `System.Text.Json`:

```csharp
public sealed class AppSettings
{
    // Detection
    public string DetectionMode { get; set; } = "EELog";  // "EELog", "OCR", "Manual"
    public string? EeLogPathOverride { get; set; } = null; // null = auto-detect
    public int DetectionIntervalMs { get; set; } = 250;    // OCR fallback only
    public int DetectionStreak { get; set; } = 2;          // OCR fallback only
    public int StabilizationDelayMs { get; set; } = 250;

    // Pricing
    public int PriceCacheTtlMinutes { get; set; } = 5;

    // Overlay appearance
    public double OverlayOpacity { get; set; } = 1.0;
    public int PriceFontSizeOverride { get; set; } = 0;  // 0 = auto
    public string ToggleHotkey { get; set; } = "Shift+F9";

    // Debug
    public bool DebugMode { get; set; } = false;
    public bool SaveDebugImages { get; set; } = false;

    public static AppSettings Load(string path) { /* ... */ }
    public void Save(string path) { /* ... */ }
}
```

Changes take effect immediately (settings object is injected everywhere via DI).

### 5.4 XAML Structure for OverlayWindow

```xml
<Window AllowsTransparency="True" Background="Transparent"
        WindowStyle="None" Topmost="True" ShowInTaskbar="True">
    <Grid>
        <!-- Layer 1: HUD (always rendered) -->
        <Canvas x:Name="HudCanvas" />

        <!-- Layer 2: Menu (visibility bound to IsMenuOpen) -->
        <Grid x:Name="MenuLayer"
              Visibility="{Binding IsMenuOpen, Converter={StaticResource BoolToVis}}">

            <!-- Dim backdrop -->
            <Rectangle Fill="#8C000000" />

            <!-- Centered menu panel -->
            <Border HorizontalAlignment="Center" VerticalAlignment="Center"
                    Background="#E6141414" CornerRadius="12" Padding="24"
                    BorderBrush="#33FFFFFF" BorderThickness="1"
                    MinWidth="500" MaxWidth="700">
                <DockPanel>
                    <!-- Header -->
                    <TextBlock DockPanel.Dock="Top" Text="Relic Overlay"
                               FontSize="22" FontWeight="Bold" Foreground="White"
                               Margin="0,0,0,16" />

                    <!-- Tab bar -->
                    <TabControl Style="{StaticResource OverlayTabStyle}">
                        <TabItem Header="Settings">
                            <local:SettingsTab DataContext="{Binding Settings}" />
                        </TabItem>
                        <TabItem Header="About">
                            <local:AboutTab />
                        </TabItem>
                        <TabItem Header="Log" Visibility="{Binding DebugMode, ...}">
                            <local:LogTab />
                        </TabItem>
                    </TabControl>
                </DockPanel>
            </Border>
        </Grid>
    </Grid>
</Window>
```

---

## Part 6 — Warframe Layout Variability & Resolution-Independent Card Detection

### 6.1 The Problem: Warframe's Layout Is Not Fixed

The current codebase uses hardcoded proportional constants in `ScreenCaptureRow` to locate reward boxes (e.g., `0.379 * HeightPx`, `0.125 * WidthPx`). These were calibrated for a single resolution and break under different configurations.

**Three independent variables affect the reward screen layout:**

**Aspect ratio** — Warframe supports 4:3, 16:10, 16:9, 21:9, 32:9, and Auto. The reward cards are always horizontally centered relative to the window, but the proportional x-positions shift with aspect ratio. On 21:9 ultrawide, the cards occupy a smaller fraction of the total width and have more empty space on the sides. On 4:3, the cards are packed tighter and closer together. The number of horizontal pixels between card edges is not a stable fraction of window width across ratios.

**In-game UI scaling** — Warframe has a configurable UI scale (Options → Display). All UI elements, including reward cards, scale proportionally with this setting. WFInfo's source code reveals the reference measurements at 1920×1080 with 100% UI scale:

| Measurement | Baseline (1080p, 100%) |
|---|---|
| Reward area width | 968 px |
| Reward area height | 235 px |
| Y offset from vertical center | 316 px downward |
| Text line height | 48 px |

Everything is multiplied by `ScreenScaling × uiScaling`. The UI scale can range from approximately 50% to 150%+, making any single set of hardcoded proportions wrong for most users.

**Windows DPI scaling** — A 4K monitor at 200% DPI means the Warframe window is 3840×2160 physical pixels but may report as 1920×1080 logical pixels to DPI-unaware apps. Warframe has "Scaled" vs "Native" display modes that interact with Windows DPI in different ways. Screen capture (`CopyFromScreen`) always works in physical pixels, but `GetWindowRect` may return logical coordinates depending on the DPI awareness of the calling process.

### 6.2 How Existing Tools Handle This

**WFInfo** (the most mature tool, ~3000 lines of OCR code) takes a brute-force approach: it tries 50 UI scaling values (50%–100% in 1% increments), samples pixel colors at the expected card-edge positions for each candidate scale, scores each one against the active Warframe UI theme's known color palette, and picks the best match. This is why WFInfo requires the user to configure their UI scaling in settings — it narrows the search space. It also explains their ~18 theme color arrays (Vitruvian, Stalker, Corpus, Grineer, Lotus, etc.) and the HDR/Reshade compatibility issues.

**AlecaFrame** (Overwolf-based) similarly requires the user to copy their in-game scaling settings into the app configuration.

Both approaches are fragile: they break when DE updates theme colors, when users run color correction (HDR, Reshade, colorblind filters), or when the UI scale/DPI combination falls outside the pre-calibrated range.

### 6.3 Recommended Approach: Horizontal Intensity Projection

Instead of guessing positions from scaling math, **detect the actual card boundaries from the screenshot itself**. This approach requires zero configuration, works on any resolution/aspect ratio/UI scale combination, and costs less than 1ms.

**How it works:** Warframe's reward cards are bright UI panels on a darker game background. Capture a thin horizontal strip across where the reward row approximately sits, sum pixel brightness column-by-column, and the resulting 1D intensity profile shows each card as a plateau and each gap as a valley:

```
4 rewards:  ___/‾‾‾‾\__/‾‾‾‾\__/‾‾‾‾\__/‾‾‾‾\___
3 rewards:  _____/‾‾‾‾\__/‾‾‾‾\__/‾‾‾‾\_____
2 rewards:  ________/‾‾‾‾\__/‾‾‾‾\________
```

Count the plateaus → reward count. Plateau edges → exact x-boundaries for each card. No OCR, no scaling math, no theme detection.

```csharp
public sealed class IntensityProfileDetector : IRewardLayoutDetector
{
    /// <summary>
    /// Detects reward card boundaries from a full-window screenshot.
    /// Returns one Rectangle per detected card (2–4), or empty if no reward screen.
    /// </summary>
    public List<Rectangle> DetectCardBoundaries(
        Bitmap windowScreenshot, int windowWidth, int windowHeight)
    {
        // The vertical position of the reward text row is the ONE dimension
        // that's proportionally stable: it sits near the vertical center.
        // Scan a few candidate Y-positions to handle UI scale variation.
        double[] candidateYPercents = { 0.38, 0.40, 0.42, 0.44 };

        foreach (double yPercent in candidateYPercents)
        {
            int stripY = (int)(yPercent * windowHeight);
            int stripH = 8;  // thin strip, averaged for noise resistance

            var strip = CropHorizontalStrip(windowScreenshot, stripY, stripH);
            double[] profile = BuildColumnIntensityProfile(strip);
            var cardRanges = FindCardPeaks(profile, windowWidth);

            // Valid result: 1–4 roughly evenly-spaced peaks
            if (cardRanges.Count >= 1 && cardRanges.Count <= 4 &&
                AreRoughlyEvenlySpaced(cardRanges))
            {
                // Convert to rectangles with vertical extent for OCR
                int textHeight = (int)(0.055 * windowHeight);
                int textTop = stripY - textHeight / 3;

                return cardRanges.Select(r => new Rectangle(
                    r.Start, textTop, r.End - r.Start, textHeight
                )).ToList();
            }
        }

        return new List<Rectangle>();  // no reward screen detected at any Y
    }

    private static double[] BuildColumnIntensityProfile(Bitmap strip)
    {
        var rect = new Rectangle(0, 0, strip.Width, strip.Height);
        var data = strip.LockBits(rect, ImageLockMode.ReadOnly,
                                   PixelFormat.Format24bppRgb);
        var profile = new double[strip.Width];

        unsafe
        {
            byte* ptr = (byte*)data.Scan0;
            for (int x = 0; x < strip.Width; x++)
            {
                double colSum = 0;
                for (int y = 0; y < strip.Height; y++)
                {
                    byte* pixel = ptr + y * data.Stride + x * 3;
                    colSum += 0.299 * pixel[2] + 0.587 * pixel[1] + 0.114 * pixel[0];
                }
                profile[x] = colSum / strip.Height;
            }
        }

        strip.UnlockBits(data);
        return profile;
    }

    private static List<(int Start, int End)> FindCardPeaks(
        double[] profile, int windowWidth)
    {
        double threshold = OtsuThreshold(profile);
        bool[] isCard = profile.Select(v => v > threshold).ToArray();

        var ranges = new List<(int Start, int End)>();
        int i = 0;
        while (i < isCard.Length)
        {
            if (!isCard[i]) { i++; continue; }

            int start = i;
            while (i < isCard.Length && isCard[i]) i++;
            int end = i;

            // Real cards are at least ~4% of window width; filter noise
            // Real cards are at most ~20% of window width; filter false merges
            int width = end - start;
            if (width > windowWidth * 0.04 && width < windowWidth * 0.20)
                ranges.Add((start, end));
        }

        return ranges;
    }

    private static bool AreRoughlyEvenlySpaced(List<(int Start, int End)> ranges)
    {
        if (ranges.Count <= 1) return true;

        // Check that card widths are similar (within 20% of each other)
        var widths = ranges.Select(r => r.End - r.Start).ToList();
        double avgWidth = widths.Average();
        return widths.All(w => Math.Abs(w - avgWidth) < avgWidth * 0.2);
    }
}
```

### 6.4 Why This Beats All Alternatives

| Approach | Resolution independent? | UI-scale independent? | Theme independent? | Cost | Configuration needed? |
|---|---|---|---|---|---|
| **Hardcoded percentages** (current) | No | No | N/A | ~0ms | None (just wrong) |
| **Brute-force scaling** (WFInfo) | Yes | Yes | No (18 theme arrays) | ~50ms | UI scale setting |
| **Horizontal intensity projection** | Yes | Yes | Yes | <1ms | None |

The intensity projection also eliminates the chicken-and-egg problem in `CountRewards()`: you get both the count AND the exact card boundaries in a single sub-millisecond pass, from what's actually on screen rather than what you assume should be there.

### 6.5 Handling the Vertical Position Uncertainty

The vertical position of the reward row scales with UI scale, so you can't know it exactly without either knowing the scale or searching for it. Two strategies:

**Strategy A (used above): Multi-Y scan.** Test 3–4 candidate Y-positions (38%, 40%, 42%, 44% of window height). The correct one will show 2–4 evenly-spaced bright peaks; the wrong ones will show noise or a flat profile. Total cost: ~3ms instead of ~1ms.

**Strategy B: Use the "REWARDS" header text as a vertical anchor.** The word "REWARDS" appears above the cards in a predictable location. A single narrow OCR pass on a small horizontal band near the top of the screen can find the text, giving you the exact Y-coordinate. The card row is at a fixed offset below it. This costs one small OCR call (~50ms) but gives you a precise anchor.

**Recommendation:** Use Strategy A for the common case (fast, zero OCR) and reserve Strategy B for a debug/calibration mode if users report issues.

### 6.6 Integration with the Pipeline

The detection and layout steps slot into the reward pricing pipeline as follows:

```
1. EE.log watcher fires "Got rewards"            (~0ms, event-driven)
2. Wait stabilization delay                       (250ms, configurable)
3. Capture full-window screenshot                  (~5ms, GDI CopyFromScreen)
4. IntensityProfileDetector.DetectCardBoundaries   (<1ms, no OCR)
   → Returns List<Rectangle> (2–4 card rects)
   → Also gives reward count (rectangles.Count)
5. Crop each card rect from the screenshot         (~1ms)
6. Preprocess + OCR each card in parallel          (600–1200ms, pooled Tesseract)
7. Fuzzy match + price lookup in parallel          (0–800ms, cached)
8. Display prices over the detected card positions
```

The card rectangles from step 4 are used for both OCR cropping (step 5) AND overlay positioning (step 8) — the prices appear directly above each detected card, regardless of resolution or UI scale.

---

## Part 7 — Externalized Reward Pool

### 7.1 JSON Format

Replace the 600-line hardcoded `RewardPool.cs` with `data/items.json`:

```json
{
  "version": "2025-06-01",
  "items": [
    { "name": "Ash Prime Chassis Blueprint" },
    { "name": "Ash Prime Neuroptics Blueprint" },
    { "name": "Acceltra Prime Barrel" },
    { "name": "Forma Blueprint", "untradeable": true }
  ]
}
```

The `MatchPattern` field is eliminated — it was always `name.ToLower()` and is computed at load time. The `untradeable` flag replaces the special-case `if (slug == "forma")` check.

### 7.2 Auto-Updating from Warframe Market API

Implement `ApiRewardRepository` that hits `GET https://api.warframe.market/v2/items` at startup, extracts all items tagged as relic rewards, and merges with the local JSON. This ensures new primes are picked up automatically without user intervention.

---

## Part 8 — Corrected Warframe Market Naming

### 8.1 Current Bugs

`WarframeMarketNaming.ComponentKeywords` has:
- `"girp"` — should be `"grip"`
- Missing: `"chassis"`, `"systems"`, `"neuroptics"`, `"harness"`, `"wings"`, `"disc"`, `"ornament"`, `"gauntlet"`, `"head"`, `"stars"`, `"pouch"`, `"string"`, `"limb"`, `"hilt"`, `"guard"`, `"boot"`, `"chain"`, `"band"`, `"buckle"`

The regex only strips " blueprint" after specific component keywords, but the Warframe Market URL format is consistent: just lowercase + underscores, with " blueprint" **kept** for non-component items (e.g., `ash_prime_blueprint`) and **stripped** for component items (e.g., `ash_prime_chassis`, not `ash_prime_chassis_blueprint`).

### 8.2 Corrected Implementation

```csharp
public static class MarketSlugConverter
{
    // Items where the market listing is just the component name without "blueprint"
    private static readonly HashSet<string> ComponentSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "chassis", "systems", "neuroptics",
        "stock", "blade", "handle", "receiver", "barrel",
        "grip", "link", "cerebrum", "carapace",
        "hilt", "guard", "gauntlet", "ornament",
        "disc", "head", "boot", "chain", "band", "buckle",
        "pouch", "stars", "string",
        "upper limb", "lower limb"
    };

    public static string ToSlug(string canonicalName)
    {
        string lower = canonicalName.Trim().ToLowerInvariant();

        // Strip trailing " blueprint" if the item ends with a known component suffix + " blueprint"
        if (lower.EndsWith(" blueprint"))
        {
            string withoutBp = lower[..^" blueprint".Length];
            string lastToken = withoutBp.Split(' ')[^1];

            if (ComponentSuffixes.Contains(lastToken) ||
                ComponentSuffixes.Contains(string.Join(' ', withoutBp.Split(' ')[^2..])))
            {
                lower = withoutBp;
            }
        }

        return lower.Replace(" ", "_").Replace("&", "and");
    }
}
```

This also handles the `&` character in items like "Cobra & Crane Prime" → `cobra_and_crane_prime_blade`.

---

## Part 9 — Async Architecture

### 9.1 No More DispatcherTimer + Sync OCR

Replace the `DispatcherTimer` with a background task loop:

```csharp
public sealed class OverlayStateMachine : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IRewardScreenDetector _detector;
    private readonly IRewardPipeline _pipeline;
    private readonly OverlayViewModel _viewModel;  // Thread-safe via Dispatcher.Invoke
    private readonly AppSettings _settings;

    public async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            switch (_state)
            {
                case OverlayState.Tracking:
                    await DetectRewardScreenAsync();
                    break;

                case OverlayState.Pricing:
                    var prices = await _pipeline.ExecuteAsync(_cts.Token);
                    _viewModel.UpdatePrices(prices);  // Dispatches to UI thread
                    _state = OverlayState.Displaying;
                    break;

                case OverlayState.Displaying:
                    await MonitorRewardScreenExitAsync();
                    break;
            }
        }
    }

    private async Task DetectRewardScreenAsync()
    {
        await Task.Delay(_settings.DetectionIntervalMs, _cts.Token);

        // Run OCR on thread pool, not UI thread
        bool detected = await Task.Run(() => _detector.IsRewardScreenVisible());

        if (detected)
        {
            _streakCount++;
            if (_streakCount >= _settings.DetectionStreak)
            {
                _state = OverlayState.Pricing;
                _streakCount = 0;
            }
        }
        else
        {
            _streakCount = 0;
        }
    }
}
```

**Key insight:** All OCR and network work runs on the thread pool via `Task.Run` and `async/await`. The UI thread is **never** blocked. Updates to the ViewModel dispatch back to the UI thread via `Dispatcher.Invoke`.

### 9.2 Pipeline Orchestration

```csharp
public sealed class RewardPricingPipeline : IRewardPipeline
{
    private readonly IScreenCapturer _capturer;
    private readonly IRewardLayoutDetector _layoutDetector;
    private readonly IOcrEngine _ocr;
    private readonly IRewardMatcher _matcher;
    private readonly IPriceProvider _pricer;

    public async Task<List<PricedReward>> ExecuteAsync(CancellationToken ct)
    {
        // Step 1: Capture full window screenshot (one GDI call)
        var (screenshot, windowWidth, windowHeight) = _capturer.CaptureFullWindow();

        // Step 2: Detect card boundaries via intensity profile (<1ms, no OCR)
        var cardRects = _layoutDetector.DetectCardBoundaries(
            screenshot, windowWidth, windowHeight);

        if (cardRects.Count == 0)
            return new List<PricedReward>();

        // Step 3: For each detected card, crop + OCR + match + price (all parallel)
        var tasks = cardRects.Select(async (rect, i) =>
        {
            var cardBitmap = CropRegion(screenshot, rect);
            var preprocessed = ImagePreprocessor.Prepare(cardBitmap);
            string text = _ocr.Recognize(preprocessed);
            var match = _matcher.MatchSingle(text);

            if (match == null)
                return new PricedReward(i, rect, null, null, text);

            string slug = MarketSlugConverter.ToSlug(match.CanonicalName);
            int? price = match.IsUntradeable ? null : await _pricer.GetPriceAsync(slug);

            return new PricedReward(i, rect, match, price, text);
        });

        return (await Task.WhenAll(tasks)).ToList();
    }
}
```

Note that `PricedReward` now carries the detected `Rectangle` so the overlay can position prices directly above each card without any hardcoded offset math. Each reward box is processed independently and concurrently. With 4 pooled Tesseract engines, all 4 OCR calls run in true parallel.

---

## Part 10 — Logger Fix

Replace synchronous `File.AppendAllText` with an async buffered logger:

```csharp
public sealed class AsyncLogger : ILogger, IDisposable
{
    private readonly Channel<string> _channel = Channel.CreateBounded<string>(1000);
    private readonly Task _writerTask;

    public AsyncLogger(string logPath)
    {
        _writerTask = Task.Run(() => WriteLoop(logPath));
    }

    public void Log(string message)
    {
        string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        _channel.Writer.TryWrite(entry);  // Non-blocking, drops if full
    }

    private async Task WriteLoop(string path)
    {
        using var writer = new StreamWriter(path, append: true) { AutoFlush = false };
        await foreach (var line in _channel.Reader.ReadAllAsync())
        {
            await writer.WriteLineAsync(line);
            if (_channel.Reader.Count == 0)
                await writer.FlushAsync();
        }
    }

    public void Dispose()
    {
        _channel.Writer.Complete();
        _writerTask.Wait(TimeSpan.FromSeconds(2));
    }
}
```

Zero UI thread impact. Batches writes. Auto-flushes when the queue drains.

---

## Part 11 — Dependency Injection Composition Root

```csharp
public static class CompositionRoot
{
    public static IServiceProvider Build(AppSettings settings)
    {
        var services = new ServiceCollection();

        // Settings
        services.AddSingleton(settings);

        // Infrastructure
        services.AddSingleton<IOcrEngine>(sp =>
            new TesseractOcrEngine("./tessdata", poolSize: 4));
        services.AddSingleton<IScreenCapturer, GdiScreenCapturer>();
        services.AddSingleton<IWarframeMarketApi, WarframeMarketClient>();
        services.AddSingleton<IProcessTracker, WarframeProcessTracker>();
        services.AddSingleton<IWindowTracker, WarframeWindowTracker>();
        services.AddSingleton<ILogger, AsyncLogger>();

        // Detection & Layout
        services.AddSingleton<IRewardScreenDetector>(sp => settings.DetectionMode switch
        {
            "EELog" => new LogFileDetector(settings.EeLogPathOverride),
            "OCR"   => new OcrFallbackDetector(
                           sp.GetRequiredService<IOcrEngine>(),
                           sp.GetRequiredService<IScreenCapturer>(), settings),
            _       => new ManualHotkeyDetector()  // "Manual" — user presses hotkey
        });
        services.AddSingleton<IRewardLayoutDetector, IntensityProfileDetector>();

        // Domain
        services.AddSingleton<IRewardRepository, JsonRewardRepository>();
        services.AddSingleton<IRewardMatcher, FuzzyRewardMatcher>();
        services.AddSingleton<IPriceProvider>(sp =>
            new CachedPriceProvider(
                sp.GetRequiredService<IWarframeMarketApi>(),
                TimeSpan.FromMinutes(settings.PriceCacheTtlMinutes)));

        // Application
        services.AddSingleton<IRewardPipeline, RewardPricingPipeline>();
        services.AddSingleton<OverlayStateMachine>();

        // Presentation
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<MenuViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellController>();

        return services.BuildServiceProvider();
    }
}
```

---

## Part 12 — Migration Roadmap

The overhaul should be executed in phases, where each phase produces a working (if incomplete) application.

### Phase 1: Foundation (non-breaking)
1. Introduce `AppSettings` with JSON persistence.
2. Externalize reward pool to `items.json`.
3. Fix `WarframeMarketNaming` bugs.
4. Fix naming conventions across the codebase.
5. Add `CachedPriceProvider` decorator.

### Phase 2: Detection Overhaul
1. Implement `LogFileDetector` — tail `EE.log` for "Got rewards" trigger.
2. Implement `OcrFallbackDetector` with reduced 250ms/2-streak parameters.
3. Add detection mode setting (EE.log / OCR / Manual) to `AppSettings`.
4. Auto-detect `EE.log` path from `%LOCALAPPDATA%\Warframe\EE.log`.

### Phase 3: Resolution-Independent Layout Detection
1. Implement `IntensityProfileDetector` with `LockBits`-based column intensity scan.
2. Implement multi-Y-position scanning (38%–44% of window height).
3. Implement `AreRoughlyEvenlySpaced` validation for peak filtering.
4. Remove all hardcoded box position constants from `ScreenCaptureRow`.
5. Use detected card rectangles for both OCR cropping and overlay price positioning.

### Phase 4: OCR Performance
1. Replace `GetPixel`/`SetPixel` threshold with `LockBits` + Otsu in `ImagePreprocessor`.
2. Implement `TesseractOcrEngine` with object pool (4 engines).
3. Remove `multiPassOCR` — single preprocessed pass per card.
4. Configure Tesseract whitelist once at pool creation, not per-call.
5. Make `saveDebugImage` conditional on `AppSettings.SaveDebugImages`.

### Phase 5: Async Architecture
1. Extract interfaces (`IOcrEngine`, `IScreenCapturer`, `IPriceProvider`, `IRewardMatcher`, `IRewardLayoutDetector`).
2. Build `RewardPricingPipeline` with intensity detection → OCR → match → price flow.
3. Build `OverlayStateMachine` with background task loop.
4. Replace `DispatcherTimer` tick loop.
5. Remove `DoEvents` anti-pattern.
6. Replace `GlobalState` with injected services.

### Phase 6: UI Overhaul
1. Implement `SettingsTab` with detection mode, EE.log path, and all configurable parameters.
2. Build tabbed `MenuLayer` with proper XAML/MVVM.
3. Wire `OverlayViewModel` with data binding.
4. Implement `OverlayPositioner` using detected card boundaries for price placement.
5. Add loading spinner animation.
6. Add debug visualization mode showing detected card boundaries and intensity profile.

### Phase 7: Correctness & Polish
1. Fix `FuzzyRewardMatcher` to use sliding window.
2. Raise match threshold to 75.
3. Build token index for performance.
4. Add `ApiRewardRepository` for auto-updating item pool.
5. Write unit tests for matcher, normalizer, slug converter, intensity detector.
6. Integration test for full pipeline with sample screenshots at various resolutions.

---

## Part 13 — Summary of All Bugs Found

| # | File | Bug | Severity |
|---|---|---|---|
| 1 | `TesseractObject.cs` | Shared static engine used from parallel tasks — race condition / crash | **Critical** |
| 2 | `AppController.cs` | `captureStableReward()` called without await — fire-and-forget async void | **High** |
| 3 | `AppController.cs` | `DoEvents()` re-entrant dispatch anti-pattern | **High** |
| 4 | `ScreenCaptureRow.cs` | `Threshold()` uses GetPixel/SetPixel — ~100x slower than LockBits | **High** |
| 5 | `ScreenCaptureRow.cs` | Hardcoded layout percentages only work for one resolution/aspect ratio/UI scale | **High** |
| 6 | `CheckForRewardScreen.cs` | `CountRewards()` uses 4-slot positions to probe — wrong regions when <4 rewards | **High** |
| 7 | `WarframeMarketNaming.cs` | `"girp"` typo in ComponentKeywords (should be "grip") | **Medium** |
| 8 | `WarframeMarketNaming.cs` | Missing component keywords (chassis, systems, neuroptics, 20+ others) | **Medium** |
| 9 | `RewardMatcher.cs` | `matchSingle` only tries sequences from index 0 — misses prefixed OCR output | **Medium** |
| 10 | `RewardMatcher.cs` | Match threshold of 20 is far too low — false positive prone | **Medium** |
| 11 | `RewardPool.cs` | Duplicate entries (Vectis Prime, Velox Prime listed twice) | **Low** |
| 12 | `RewardPool.cs` | Typo: "Carrier Prime Cerebum" should be "Cerebrum" | **Low** |
| 13 | `ImageToText.cs` | `SetVariable` called on every OCR invocation — unnecessary overhead | **Low** |
| 14 | `ImageToText.cs` | `saveDebugImage` called unconditionally — disk I/O waste | **Low** |
| 15 | `Logger.cs` | Synchronous `File.AppendAllText` on UI thread | **Low** |
| 16 | `RewardPool.cs` | Alternox Prime section has wrong names (says "Akvasto Prime" entries) | **Medium** |
| 17 | `GlobalState.cs` | Mutable static read/written without synchronization | **Medium** |
| 18 | `AppController.cs` | Uses OCR to detect "REWARDS" text when EE.log tailing is free and instant | **Medium** |
