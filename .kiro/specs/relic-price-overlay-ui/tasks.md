# Implementation Plan: Relic Price Overlay UI

## Overview

Convert the feature design into a series of prompts for a code-generation LLM that will implement each step with incremental progress. Make sure that each prompt builds on the previous prompts, and ends with wiring things together. There should be no hanging or orphaned code that isn't integrated into a previous step. Focus ONLY on tasks that involve writing, modifying, or testing code.

This plan delivers the WPF presentation layer and the composition root for the existing engine (.NET 10, WPF, C#). The engine layer (state machine, pipeline, detectors, trackers, market client, OCR) is already implemented; this feature wires it together and adds the visible overlay window, the global hotkey, the `IOverlayOutput` implementation, and the `App.xaml.cs` startup graph.

Implementation language: **C# 13 / .NET 10 (`net10.0-windows`, WPF)**, matching `Directory.Build.props`.

Coding-convention reminders (mirror `OverlayCoordinator.cs`, `OverlayStateMachine.cs`, `OcrFallbackDetector.cs`):
- File-scoped namespace: `namespace X;`
- `using` directives placed inside the namespace block
- `_camelCase` for private instance fields, PascalCase for everything public
- `/// <summary>` XML doc above every public type and member
- `// ── Section ──` region banner comments to separate logical sections
- `sealed` on classes not designed for inheritance
- `nint` for native window handles, `System.Drawing` for `Rectangle`/`Point`/`Size`
- Single shared `HttpClient` registered via DI (R15.4)

## Tasks

- [ ] 1. Foundation: pure helpers and new test project
  - [ ] 1.1 Create `src/Presentation/OverlayLayout.cs`
    - Add a `static class OverlayLayout` in namespace `WarframeRelicOverlay.Presentation`.
    - Expose a single pure method `public static (double Left, double Top) ComputeLabelPosition(CardResult card, WindowSnapshot window, double labelWidth, double labelHeight, double windowLogicalHeight)` that returns the top-left position of a price label in DIPs relative to the OverlayWindow.
    - Below-card placement (R4.1, R4.2): `centerX = (card.BoundsInWindow.X + card.BoundsInWindow.Width / 2.0) / window.DpiScaleX`, `top = (card.BoundsInWindow.Y + card.BoundsInWindow.Height) / window.DpiScaleY + 4.0`, `left = centerX - labelWidth / 2.0`.
    - Above-card flip (R4.3): if `top + labelHeight > windowLogicalHeight`, set `top = card.BoundsInWindow.Y / window.DpiScaleY - 4.0 - labelHeight`.
    - Do NOT clamp to the window edges; the property test verifies the formula directly.
    - File-scoped namespace, `using` inside the namespace, `///` doc on the method, `// ── Sections ──` for "Below-card placement" and "Above-card flip".
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 18.1, 18.2, 18.5, 18.6_

  - [ ] 1.2 Create `src/Presentation/VisibilityDecision.cs`
    - Add a `static class VisibilityDecision` in namespace `WarframeRelicOverlay.Presentation`.
    - Expose a single pure method `public static bool IsOverlayVisible(OverlayState state, bool foreground, bool manualShown, bool snapshotValid)` returning `snapshotValid && foreground && (manualShown || state is OverlayState.Pricing or OverlayState.Displaying)` (R19.6).
    - `///` doc explaining the function, `// ── Section ──` banner. Pure, no mutable state.
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 8.1, 19.6, 19.7, 18.1, 18.2, 18.5, 18.6_

  - [ ] 1.3 Add `FsCheck.Xunit` to `Directory.Packages.props`
    - Add `<PackageVersion Include="FsCheck.Xunit" Version="3.1.0" />` (or current 3.x release) under the Test packages ItemGroup.
    - Do not add it to the existing test project files yet. Only `Presentation.Tests` will reference it.
    - _Requirements: 19.1, 19.2, 19.3, 19.4, 19.5, 19.6, 19.7_

  - [ ] 1.4 Create `tests/Presentation.Tests/Presentation.Tests.csproj`
    - Mirror `tests/Core.Tests/Core.Tests.csproj`: `OutputType=Library`, `IsPackable=false`, `AssemblyName=Presentation.Tests`, `RootNamespace=WarframeRelicOverlay.Tests.Presentation`, `ProjectReference` to `..\..\WarframeRelicOverlay.csproj`.
    - Reference packages: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`, `FluentAssertions`, `System.Drawing.Common`, `FsCheck.Xunit`.
    - _Requirements: 19.1, 19.2, 19.3, 19.4, 19.5, 19.6, 19.7_

  - [ ]* 1.5 Write unit tests for `VisibilityDecision.IsOverlayVisible`
    - Create `tests/Presentation.Tests/VisibilityDecisionTests.cs`.
    - Use `[Theory]` + `[InlineData]` to enumerate all 5 states × 2 foreground × 2 manualShown × 2 snapshotValid = 40 combinations.
    - Assert the function returns `true` exclusively for the rows where `snapshotValid && foreground && (manualShown || state is Pricing or Displaying)`.
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 8.1, 19.6, 19.7_

  - [ ]* 1.6 Write property test `PositionDerivedFromCardX` for `OverlayLayout`
    - Create `tests/Presentation.Tests/OverlayLayoutPropertyTests.cs` using FsCheck.Xunit.
    - **Property 1 (R19.1): PositionDerivedFromCardX** — for any valid `WindowSnapshot` and `CardResult` with positive `BoundsInWindow`, the rendered label center X in physical pixels (computed as `(left + labelWidth/2) * window.DpiScaleX + window.ClientX` after passing through `ComputeLabelPosition`) lies in the inclusive screen-space X range `[ClientX + Card.X, ClientX + Card.X + Card.Width]`.
    - **Validates: Requirements 4.1, 4.5, 19.1**
    - _Requirements: 4.1, 4.5, 19.1_

  - [ ]* 1.7 Write property test `PositionDerivedFromCardY` for `OverlayLayout`
    - Add to `tests/Presentation.Tests/OverlayLayoutPropertyTests.cs`.
    - **Property 2 (R19.2): PositionDerivedFromCardY** — for any valid `WindowSnapshot` and `CardResult`, the rendered label top edge in physical pixels (computed as `top * window.DpiScaleY + window.ClientY`) lies in the inclusive screen-space Y range `[ClientY + Card.Y - L, ClientY + Card.Y + Card.Height + window.DpiScaleY * 4 + L]` where `L` is the label height in physical pixels (`labelHeight * window.DpiScaleY`).
    - Cover both branches (below-card and above-card flip) by parameterizing with `windowLogicalHeight`.
    - **Validates: Requirements 4.2, 4.3, 4.5, 19.2**
    - _Requirements: 4.2, 4.3, 4.5, 19.2_

- [ ] 2. Win32 hotkey interop and global hotkey manager
  - [ ] 2.1 Extend `src/Infrastructure/Platform/Win32Interop.cs` with hotkey P/Invokes
    - Add `WS_EX_TOOLWINDOW = 0x00000080` and `GWL_EXSTYLE` already present — reuse it.
    - Add the hotkey API:
      ```
      [LibraryImport("user32.dll", SetLastError = true)]
      [return: MarshalAs(UnmanagedType.Bool)]
      internal static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

      [LibraryImport("user32.dll", SetLastError = true)]
      [return: MarshalAs(UnmanagedType.Bool)]
      internal static partial bool UnregisterHotKey(nint hWnd, int id);
      ```
    - Add modifier constants: `MOD_ALT = 0x0001`, `MOD_CONTROL = 0x0002`, `MOD_SHIFT = 0x0004`, `MOD_WIN = 0x0008`, `MOD_NOREPEAT = 0x4000`.
    - Add `WM_HOTKEY = 0x0312`.
    - Keep the file `internal static partial class`, file-scoped namespace, `// ── Section ──` banners.
    - _Requirements: 9.1, 18.1, 18.2, 18.6, 18.8_

  - [ ] 2.2 Create `src/Infrastructure/Platform/GlobalHotkey.cs`
    - File-scoped namespace `WarframeRelicOverlay.Infrastructure.Platform`, `using` directives inside namespace block.
    - `public sealed class GlobalHotkey : IDisposable` with banner sections for "Parsing", "Registration", "Message hook", "Disposal".
    - Constructor: `public GlobalHotkey(string hotkey, ILogger logger)`. Parse the hotkey with the static helper `TryParse` below.
    - Static parser `public static bool TryParse(string input, out uint modifiers, out uint virtualKey, out string? error)`:
      - Split on `+`, trim, compare case-insensitively.
      - Modifiers from `{Ctrl, Shift, Alt, Win}` map to `MOD_CONTROL | MOD_SHIFT | MOD_ALT | MOD_WIN`. Reject duplicates and bare empty tokens.
      - Exactly one non-modifier token required. Map letter `A-Z` to `(uint)char`, `F1..F24` to `0x70..0x87`, digit `0-9` to `(uint)char`, plus a small map for `Escape`, `Space`, `Enter`, `Tab`. Anything else → `error = "unknown key"`.
    - Method `public void Register(nint windowHandle, int id = 0xB0B0)` calls `Win32Interop.RegisterHotKey(windowHandle, id, modifiers | MOD_NOREPEAT, virtualKey)`. On `false`, logs a warning via `ILogger` and sets `_registered = false` (R9.5).
    - Method `public void HandleMessage(int msg, nint wParam)` raises the `Pressed` event when `msg == WM_HOTKEY && (int)wParam == _id`.
    - `public event Action? Pressed;`
    - `Dispose()` calls `UnregisterHotKey` if `_registered` and is idempotent.
    - All public members `///` documented.
    - _Requirements: 9.1, 9.4, 9.5, 16.4, 18.1, 18.2, 18.4, 18.5, 18.6, 18.7, 18.8_

  - [ ]* 2.3 Write unit tests for `GlobalHotkey.TryParse`
    - Create `tests/Presentation.Tests/GlobalHotkeyParserTests.cs`.
    - `[Theory]` cases for: `"Shift+F9"`, `"Ctrl+Alt+F1"`, `"shift+f9"` (case-insensitive), `"  Shift  +  F9  "` (whitespace), `"Win+A"`, `"F12"` (no modifier), `"A"`.
    - `[Theory]` rejection cases for: `""`, `"Shift+"`, `"+F9"`, `"Shift+Ctrl"` (no non-modifier), `"Shift+F9+A"` (two non-modifiers), `"Shift+Bogus"`, `"Shift Shift+F9"` (bad grammar), `null`.
    - Assert returned `modifiers` and `virtualKey` values for the success cases.
    - _Requirements: 9.1, 9.4_

- [ ] 3. OverlayWindow XAML and code-behind

  - [ ] 3.1 Create `src/Presentation/OverlayWindow.xaml`
    - `<Window x:Class="WarframeRelicOverlay.Presentation.OverlayWindow" ...>` with attributes: `WindowStyle="None"`, `AllowsTransparency="True"`, `Background="Transparent"`, `Topmost="True"`, `ShowInTaskbar="False"`, `ResizeMode="NoResize"`, `Focusable="False"`, `Title="Warframe Relic Overlay"`, default `Height="100" Width="100"` (will be replaced at runtime).
    - Root content is a `<Canvas x:Name="LabelCanvas"/>` so labels can be placed via `Canvas.SetLeft/SetTop` in DIPs (R3.1, R4.1, R4.2).
    - Add a `ProgressBar x:Name="LoadingSpinner" IsIndeterminate="True" Width="96" Height="6" Visibility="Collapsed"` inside the canvas (R7.5).
    - Register the Page in the project: see task 7.2.
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 7.5_

  - [ ] 3.2 Create `src/Presentation/OverlayWindow.xaml.cs`
    - File-scoped namespace `WarframeRelicOverlay.Presentation`, `using` directives inside the namespace block.
    - `public sealed partial class OverlayWindow : Window` with `// ── Lifecycle ──`, `// ── Click-through ──`, `// ── Spinner positioning ──` sections.
    - In the constructor, call `InitializeComponent()` and subscribe to `SourceInitialized` to apply extended window styles `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW` via `Win32Interop.SetWindowLongPtr(hWnd, GWL_EXSTYLE, current | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW)` (R1.3, R1.4).
    - Expose `public Canvas LabelCanvas => this.LabelCanvas;` and `public ProgressBar LoadingSpinner => this.LoadingSpinner;` (alias the auto-generated fields).
    - Expose `public new nint Handle { get; private set; }` set during `SourceInitialized`.
    - Expose `public void PositionLoadingSpinner(double windowLogicalWidth, double windowLogicalHeight)`: place the spinner horizontally centered (`Canvas.SetLeft(LoadingSpinner, (windowLogicalWidth - LoadingSpinner.Width) / 2)`) and vertically at 50% of `windowLogicalHeight` (R7.4).
    - `///` doc on every public type/method, `_camelCase` for any private fields.
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 5.1, 7.4, 7.5, 18.1–18.8_

- [ ] 4. OverlayOutput implementation

  - [ ] 4.1 Create `src/Presentation/PriceLabel.cs`
    - File-scoped namespace `WarframeRelicOverlay.Presentation`.
    - `internal sealed class PriceLabel : Border` containing a `TextBlock` child.
    - Constructor takes `(string displayText, double fontSize, double opacity)`.
    - Visual style (R14.1, R14.2, R14.3):
      - `Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0x00, 0x00))`
      - `CornerRadius = new CornerRadius(4)`
      - `Padding = new Thickness(6, 3, 6, 3)`  (≥ 4 DIPs satisfies R14.2; horizontal 6 for visual balance)
      - Inner `TextBlock` with `Foreground = Brushes.White`, `FontWeight = FontWeights.SemiBold`, `FontSize = fontSize`, `Text = displayText` verbatim (R3.2, R14.3, R19.5)
      - `Border.Opacity = opacity` (applied per-label so `AppSettings.OverlayOpacity` flows through, R1.6)
    - Override `MeasureOverride` is not needed; `Border` auto-sizes to content.
    - `///` doc, sections, sealed.
    - _Requirements: 1.6, 3.2, 14.1, 14.2, 14.3, 19.5, 18.1–18.8_

  - [ ] 4.2 Create `src/Presentation/OverlayOutput.cs` skeleton implementing `IOverlayOutput`
    - File-scoped namespace `WarframeRelicOverlay.Presentation`, `using` inside the namespace block.
    - `public sealed class OverlayOutput : IOverlayOutput, IDisposable` implementing the four `IOverlayOutput` methods.
    - Section banners: `// ── Construction ──`, `// ── IOverlayOutput surface ──`, `// ── Polling timer ──`, `// ── Hotkey ──`, `// ── Rendering ──`, `// ── Visibility ──`, `// ── Dispose ──`.
    - Constructor signature: `public OverlayOutput(OverlayWindow window, AppSettings settings, IWindowTracker windowTracker, IProcessTracker processTracker, OverlayStateMachine stateMachine, GlobalHotkey hotkey, ILogger logger)`.
    - Private fields (all `_camelCase`): `_window`, `_dispatcher`, `_settings`, `_windowTracker`, `_processTracker`, `_stateMachine`, `_hotkey`, `_logger`, `_pollTimer`, `_lastSnapshot` (`WindowSnapshot?`), `_lastResult` (`PipelineResult?`), `_manualShown` (bool), `_currentLabels` (`List<PriceLabel>`), `_loadingVisible` (bool), `_disposed` (bool).
    - Subscribe to `_stateMachine.StateChanged` and `_hotkey.Pressed` in the constructor.
    - Hook up `_window.Closed` so a user-driven close raises an event the composition root subscribes to (R16.3).
    - `Dispose` unsubscribes everything, stops the polling timer, disposes `_hotkey`.
    - _Requirements: 13.1, 13.2, 13.3, 13.4, 16.3, 18.1–18.8_

  - [ ] 4.3 Implement dispatcher marshalling on `OverlayOutput`
    - Add a private helper `private void OnUi(Action action)` that:
      - Returns immediately if `_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished` (R13.3).
      - Calls `action()` synchronously when `_dispatcher.CheckAccess()` is true (R13.2).
      - Otherwise calls `_dispatcher.BeginInvoke(action, DispatcherPriority.Render)` so calls from the same thread preserve order (R13.1, R13.4).
    - Wire `ShowPrices`, `ClearPrices`, `ShowLoading`, `HideLoading` through `OnUi(...)`.
    - _Requirements: 13.1, 13.2, 13.3, 13.4_

  - [ ] 4.4 Implement `ShowLoading` and `HideLoading`
    - `ShowLoading` (R7.1, R7.4, R7.5, R12.5): set `_loadingVisible = true`, ensure the OverlayWindow is shown if `VisibilityDecision.IsOverlayVisible(...)` returns true, set `_window.LoadingSpinner.Visibility = Visibility.Visible`, call `_window.PositionLoadingSpinner(...)` using the most recent snapshot's `LogicalWidth`/`LogicalHeight`. If the loading spinner is already visible, no-op.
    - `HideLoading` (R7.2, R7.3): set `_loadingVisible = false`, set the spinner `Visibility = Visibility.Collapsed`. If already hidden, no-op and do not throw.
    - _Requirements: 7.1, 7.2, 7.3, 7.4, 7.5, 12.5_

  - [ ] 4.5 Implement `ShowPrices` and `ClearPrices` rendering
    - `ShowPrices(PipelineResult result)` (R3.1, R3.2, R3.3, R3.4, R12.1, R12.4):
      - Cache the result in `_lastResult` and update `_lastSnapshot` with `result.Window`.
      - Validate snapshot (R2.4, R5.5, R17.4): if `!result.Window.IsValid || result.Window.DpiScaleX <= 0 || result.Window.DpiScaleY <= 0` → call `HideOverlayWindow()` and return.
      - Clear all existing labels from `_window.LabelCanvas` (do NOT clear the spinner). Empty `_currentLabels`.
      - Compute font size (R14.4, R14.5): `int fontSize = _settings.PriceFontSizeOverride > 0 ? _settings.PriceFontSizeOverride : (int)Math.Round(Math.Clamp(result.Window.LogicalHeight * 0.018, 12, 28))`.
      - For each `CardResult` with strictly positive `BoundsInWindow.Width` and `Height` (R3.4, R19.3), create a `PriceLabel` with `card.DisplayText`, `fontSize`, `_settings.OverlayOpacity`. Add it to `_window.LabelCanvas.Children` and to `_currentLabels`. Measure the label (`label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity))`) to get `DesiredSize`, then call `OverlayLayout.ComputeLabelPosition(card, result.Window, label.DesiredSize.Width, label.DesiredSize.Height, result.Window.LogicalHeight)` and `Canvas.SetLeft/SetTop(label, ...)`.
      - Re-evaluate visibility through `ApplyVisibility()` (defined in 4.7).
    - `ClearPrices()` (R12.2, R12.3): remove every `PriceLabel` from the canvas, clear `_currentLabels`. If there were no labels, no-op without throwing.
    - For rapid-fire `ShowPrices` invocations the dispatcher's render priority + most-recent-result caching produce the latest labels once work drains (R12.4).
    - _Requirements: 2.4, 3.1, 3.2, 3.3, 3.4, 5.5, 12.1, 12.2, 12.3, 12.4, 14.4, 14.5, 17.4, 19.3, 19.5_

  - [ ] 4.6 Implement window position / size / opacity application
    - Add `private void ApplyWindowGeometry(WindowSnapshot snapshot)`:
      - Compute logical position by dividing physical pixels by the snapshot's DPI scale: `_window.Left = snapshot.ClientX / snapshot.DpiScaleX`, `_window.Top = snapshot.ClientY / snapshot.DpiScaleY` (R2.1, R5.2, R5.3).
      - Set `_window.Width = snapshot.LogicalWidth`, `_window.Height = snapshot.LogicalHeight` (R2.2).
    - Add `private void ReflowLabels(WindowSnapshot snapshot)`:
      - For each label/card pair currently rendered, recompute position via `OverlayLayout.ComputeLabelPosition(...)` using the new snapshot. Preserve the existing label text and visibility (R10.3).
    - Apply `_settings.OverlayOpacity` once at startup to `_window.LabelCanvas.Opacity` (R1.6) so per-label opacity is not re-applied after the fact.
    - _Requirements: 1.6, 2.1, 2.2, 2.3, 4.6, 5.2, 5.3, 5.4, 10.3_

  - [ ] 4.7 Implement `ApplyVisibility` decision and overlay show/hide
    - `private void ApplyVisibility()`:
      - Compute `bool foreground = _processTracker.MainWindowHandle != nint.Zero && _windowTracker.IsForeground(_processTracker.MainWindowHandle)` (R8.1, R8.2).
      - Compute `bool snapshotValid = _lastSnapshot is { } s && s.IsValid && s.DpiScaleX > 0 && s.DpiScaleY > 0` (R2.4, R5.5, R17.3, R17.4).
      - `bool visible = VisibilityDecision.IsOverlayVisible(_stateMachine.Current, foreground, _manualShown, snapshotValid)` (R6.*, R19.6, R19.7).
      - When `visible` and the window is not shown, call `_window.Show()`. When not visible, call `_window.Hide()` and clear labels + hide spinner (R6.1, R6.4, R6.5, R8.1, R10.4).
      - When the state is `Pricing` (and the manual flag did not promote it to label rendering), ensure the spinner is visible and labels are cleared (R6.2).
      - When the state is `Displaying` or `_manualShown` with a `_lastResult`, ensure labels are present (re-render from `_lastResult` if labels list is empty) and spinner hidden (R6.3, R6.6).
    - Call `ApplyVisibility()` at the end of every `OnUi`-marshalled mutation (state changed, hotkey pressed, polling tick, ShowPrices, ClearPrices, ShowLoading, HideLoading).
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 8.1, 8.2, 10.4, 17.3, 17.4, 19.6, 19.7_

  - [ ] 4.8 Implement the 150 ms polling timer (move/resize/foreground tracking)
    - Use a `DispatcherTimer` with `Interval = TimeSpan.FromMilliseconds(150)` (within the 100–250 ms band of R10.1 and below the 250 ms ceiling of R8.3).
    - On each tick (UI thread already): query `_windowTracker.TryGetBounds(_processTracker.MainWindowHandle)`.
      - If `null` (R10.4, R17.3): hide the OverlayWindow but retain `_lastResult` and `_currentLabels` so they restore on recovery.
      - If non-null and `!IsValid`: same as null path.
      - If non-null and valid: compare with `_lastSnapshot`. If any of `ClientX/Y/Width/Height` differ by more than 1 physical pixel (R10.2): apply `ApplyWindowGeometry` and `ReflowLabels` and update `_lastSnapshot`.
    - Always finish with `ApplyVisibility()` so foreground/state changes propagate within 250 ms (R8.3).
    - Start the timer in the constructor; stop and dispose it in `Dispose`.
    - _Requirements: 4.6, 8.3, 10.1, 10.2, 10.3, 10.4, 10.5, 17.3_

  - [ ] 4.9 Implement hotkey wiring and `_manualShown` toggle
    - In the constructor, after the OverlayWindow's `SourceInitialized` fires, install a `HwndSource.FromHwnd(_window.Handle)!.AddHook(WndProc)` hook and call `_hotkey.Register(_window.Handle)`.
    - `private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)` forwards `msg/wParam` to `_hotkey.HandleMessage(msg, wParam)` and returns `nint.Zero`.
    - In the `Pressed` event handler (marshal via `OnUi`): toggle `_manualShown` and call `ApplyVisibility()`. The 150 ms polling guarantees the state propagates within 200 ms (R9.2, R9.3).
    - On `Dispose`, remove the hook and dispose `_hotkey` to release the global registration (R16.4).
    - _Requirements: 9.2, 9.3, 16.4_

  - [ ] 4.10 Subscribe to `OverlayStateMachine.StateChanged`
    - Handler marshals via `OnUi` and calls `ApplyVisibility()`. No state-specific branching here — `ApplyVisibility` already centralizes the rules from R6.
    - When the new state is `Tracking` or `Idle` (i.e. exit from `Displaying`/`Pricing`), additionally call `ClearPrices()` and `HideLoading()` to satisfy R6.4 and R6.5 within 100 ms.
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5_

  - [ ]* 4.11 Write property test `OneLabelPerCard`
    - Add to `tests/Presentation.Tests/OverlayLayoutPropertyTests.cs` (or a new file `OverlayOutputPropertyTests.cs`) using a lightweight in-memory test harness that drives `OverlayOutput` on the WPF dispatcher (`Dispatcher.Run`) or — preferred — a pure helper `LabelPlanner.PlanLabels(PipelineResult, AppSettings)` extracted from `OverlayOutput.ShowPrices` that returns the planned positions without touching WPF. Extract that helper if needed to keep the test pure.
    - **Property 3 (R19.3): OneLabelPerCard** — for any valid `PipelineResult` the planned label count equals the number of `CardResult`s with both `BoundsInWindow.Width > 0` and `BoundsInWindow.Height > 0`.
    - **Validates: Requirements 3.1, 3.4, 19.3**
    - _Requirements: 3.1, 3.4, 19.3_

  - [ ]* 4.12 Write property test `NoLabelsWhenWindowInvalid`
    - **Property 4 (R19.4): NoLabelsWhenWindowInvalid** — for any `PipelineResult` whose `Window.IsValid` is `false`, the planned label count equals zero (use the same `LabelPlanner` helper from 4.11).
    - **Validates: Requirements 2.4, 17.4, 19.4**
    - _Requirements: 2.4, 17.4, 19.4_

  - [ ]* 4.13 Write property test `DisplayTextMatchesCard`
    - **Property 5 (R19.5): DisplayTextMatchesCard** — for any valid `PipelineResult`, every planned label's text is byte-equal to its corresponding `CardResult.DisplayText` (no truncation, padding, case change, or substitution).
    - **Validates: Requirements 3.2, 19.5**
    - _Requirements: 3.2, 19.5_

  - [ ]* 4.14 Write property test `StateVisibility`
    - **Property 6 (R19.6): StateVisibility** — invoke `VisibilityDecision.IsOverlayVisible(state, fg, manual, valid)` over arbitrary inputs and assert the result equals `valid && fg && (manual || state is Pricing or Displaying)`.
    - **Validates: Requirements 6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 19.6**
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6, 19.6_

  - [ ]* 4.15 Write property test `HiddenWhenUnfocused`
    - **Property 7 (R19.7): HiddenWhenUnfocused** — `IsOverlayVisible(state, foreground=false, manual, valid)` always returns `false` regardless of the other inputs.
    - **Validates: Requirements 8.1, 19.7**
    - _Requirements: 8.1, 19.7_

- [ ] 5. OcrFallbackDetector adapter

  - [ ] 5.1 Create `src/OverlayApp/Detection/OcrFallbackDetectorAdapter.cs`
    - File-scoped namespace `WarframeRelicOverlay.OverlayApp.Detection`, `using` inside the namespace block.
    - `public sealed class OcrFallbackDetectorAdapter : IRewardDetector`.
    - Mirror the `LogDetectorAdapter` pattern in `tests/Integration.Tests/EndToEndPipelineTests.cs`: take an `OcrFallbackDetector` (which implements `IRewardScreenDetector`) in the constructor and re-emit `RewardScreenDetected → RewardDetected`, `RewardScreenExited → RewardScreenExited`, plus a synthetic `RewardLost` event whenever a positive→negative transition occurs (use the existing `RewardScreenExited` event for that since that's exactly what `OcrFallbackDetector` already raises; `RewardLost` should be left unused unless the adapter can derive it without polling).
    - Implement `Start()`, `Stop()`, `Dispose()` by delegating to the inner detector.
    - `///` doc, sections, sealed.
    - _Requirements: 15.6, 18.1–18.8_

- [ ] 6. Composition root in `App.xaml.cs`

  - [ ] 6.1 Add a console-aware `ILogger` for startup
    - Reuse the existing `WarframeRelicOverlay.Infrastructure.Logging.ILogger`/`FileLogger` types (already present). The composition root resolves the same instance for both the warning logs in R9.4/R9.5/R15.7/R17.* and the `GlobalHotkey` constructor.
    - No new file; the task is to register `FileLogger` as `ILogger` in 6.4.
    - _Requirements: 9.4, 9.5, 15.7, 17.1, 17.5_

  - [ ] 6.2 Replace `src/Core/App.xaml.cs` with the composition root
    - File-scoped namespace `WarframeRelicOverlay`, `using` inside the namespace block (matches `App.xaml`'s `x:Class="WarframeRelicOverlay.App"`).
    - `public partial class App : Application` with section banners: `// ── Startup ──`, `// ── Service registration ──`, `// ── Hotkey ──`, `// ── Shutdown ──`.
    - Override `OnStartup(StartupEventArgs e)`:
      1. Call `Win32Interop.SetProcessDpiAwareness(PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE)` before any window is created (R5.1).
      2. Build the service collection (see 6.3).
      3. Build the `IServiceProvider` and store it in `_serviceProvider` (R15.2).
      4. Resolve `AppSettings`, log every warning returned by `Validate()` via `ILogger` (R15.3).
      5. Resolve `OverlayCoordinator`, `IProcessTracker`, `OverlayWindow`, `OverlayOutput`, `GlobalHotkey`.
      6. Call `_processTracker.Start()` then `_coordinator.Start()` then `_overlayWindow.Show()` (kept hidden via `Hide()` after `Show()` if the state machine is `Idle`, but a clean `Show()` lets the dispatcher attach the source — `OverlayOutput` already controls visibility post-show).
      7. Catch any exception from the resolution chain: log it, show a single `MessageBox.Show` with the exception message, call `Shutdown(1)` (R15.10).
    - `OnExit` performs disposal in reverse order (see 6.5).
    - _Requirements: 5.1, 15.1, 15.2, 15.3, 15.8, 15.9, 15.10, 18.1–18.8_

  - [ ] 6.3 Implement service registration in `BuildServices`
    - Extract a private `IServiceCollection BuildServices(string settingsPath)` method.
    - Register (R15.4) using `Microsoft.Extensions.DependencyInjection`:
      - `services.AddSingleton<ILogger>(sp => new FileLogger(...))`
      - `services.AddSingleton(sp => AppSettings.Load(settingsPath))` — this also handles R17.5 (defaults on missing/invalid file).
      - `services.AddSingleton<IRewardRepository>(sp => new JsonRewardRepository("data/items.json"))` and `services.AddSingleton<FuzzyRewardMatcher>()`. The `JsonRewardRepository` already returns an empty list on missing/corrupt input (R17.1, R17.2) — no extra try/catch needed.
      - `services.AddSingleton<IProcessTracker, WarframeProcessTracker>()`
      - `services.AddSingleton<IWindowTracker, WarframeWindowTracker>()`
      - `services.AddSingleton<IScreenCapturer, GdiScreenCapturer>()`
      - `services.AddSingleton<IOcrEngine>(sp => new TesseractOcrEngine(Path.Combine(AppContext.BaseDirectory, "tessdata")))`
      - `services.AddSingleton(sp => new HttpClient { BaseAddress = new Uri("https://api.warframe.market/v2/"), Timeout = TimeSpan.FromSeconds(10) })` — single shared instance with the standard headers from `EndToEndPipelineTests`.
      - `services.AddSingleton<IWarframeMarketAPI, WarframeMarketClient>()`
      - `services.AddSingleton(sp => new RewardPriceCache(sp.GetRequiredService<IWarframeMarketAPI>(), TimeSpan.FromMinutes(sp.GetRequiredService<AppSettings>().PriceCacheTtlMinutes)))`
      - `services.AddSingleton<IRewardLayoutDetector, IntensityProfileDetector>()`
      - `services.AddSingleton<IRewardPipeline, RewardPricingPipeline>()`
      - Detector selection (R15.5, R15.6, R15.7):
        ```
        services.AddSingleton<IRewardDetector>(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var logger = sp.GetRequiredService<ILogger>();
            switch (settings.DetectionMode)
            {
                case "EELog": return new LogFileDetector(settings);
                case "OCR":   return new OcrFallbackDetectorAdapter(
                    new OcrFallbackDetector(
                        sp.GetRequiredService<IScreenCapturer>(),
                        sp.GetRequiredService<IOcrEngine>(),
                        sp.GetRequiredService<IProcessTracker>(),
                        sp.GetRequiredService<IWindowTracker>(),
                        settings));
                default:
                    logger.Warn($"DetectionMode '{settings.DetectionMode}' not supported as standalone detector; falling back to EELog.");
                    return new LogFileDetector(settings);
            }
        });
        ```
      - `services.AddSingleton<OverlayStateMachine>()`
      - `services.AddSingleton<OverlayWindow>()`
      - `services.AddSingleton<GlobalHotkey>(sp => new GlobalHotkey(sp.GetRequiredService<AppSettings>().ToggleHotkey, sp.GetRequiredService<ILogger>()))`
      - `services.AddSingleton<IOverlayOutput, OverlayOutput>()`
      - `services.AddSingleton<OverlayCoordinator>()` — its constructor receives `IOverlayOutput` so it shares the same instance as `OverlayOutput` (R15.9).
    - _Requirements: 15.4, 15.5, 15.6, 15.7, 15.9, 17.1, 17.2, 17.5, 20.1, 20.3, 20.4, 20.6_

  - [ ] 6.4 Wire hotkey-parse fallback (R9.4)
    - Inside `GlobalHotkey`'s constructor (task 2.2) the parser already falls back; ensure the composition root logs a single warning if `TryParse` returns false on `AppSettings.ToggleHotkey` and re-parses with `"Shift+F9"`. Implementation lives in `GlobalHotkey` itself; the App-level task is just to verify the warning is surfaced through the registered `ILogger`.
    - _Requirements: 9.1, 9.4_

  - [ ] 6.5 Implement `OnExit` shutdown sequence
    - Override `OnExit(ExitEventArgs e)`:
      1. Resolve `OverlayCoordinator` and `Dispose` it first within 5 seconds (R16.1).
      2. After it returns, dispose the rest in reverse registration order: `IRewardDetector`, `WarframeMarketClient` (via `HttpClient`), `TesseractOcrEngine`, `WarframeProcessTracker` (R16.2). Use a small `try/catch` around each so a single failure does not skip the others (R16.5).
      3. Dispose `GlobalHotkey` last so the registration is released exactly once (R16.4).
      4. If any disposal threw, call `Environment.Exit(non-zero)` per R16.5.
    - Subscribe to `_overlayWindow.Closed` in `OnStartup` and call `Shutdown()` from the handler so closing the window initiates application shutdown within 5 seconds (R16.3).
    - _Requirements: 16.1, 16.2, 16.3, 16.4, 16.5_

- [ ] 7. Remove MainWindow and update WPF project wiring

  - [ ] 7.1 Delete `src/Presentation/MainWindow.xaml` and `src/Presentation/MainWindow.xaml.cs`
    - Use the file deletion tool. The OverlayWindow becomes the sole window in the application.
    - _Requirements: 1.1, 15.1_

  - [ ] 7.2 Update `src/Core/App.xaml`
    - Remove the `StartupUri="Presentation/MainWindow.xaml"` attribute from the `<Application>` element. Startup is driven by `OnStartup` in `App.xaml.cs`.
    - Keep the `x:Class="WarframeRelicOverlay.App"` attribute and `Application.Resources` block.
    - _Requirements: 15.1_

  - [ ] 7.3 Update `WarframeRelicOverlay.csproj` to compile the new XAML
    - Add an `<ItemGroup>` that includes `<Page Update="src/Presentation/OverlayWindow.xaml"><Generator>MSBuild:Compile</Generator><SubType>Designer</SubType></Page>`. The default WPF SDK glob auto-includes .xaml under the project, but be explicit to mirror the existing `ApplicationDefinition` pattern.
    - Confirm the existing `<Compile Remove="tests/**" />` glob does not accidentally exclude any of the new `src/Presentation/*.cs` files.
    - _Requirements: 1.1_

- [ ] 8. README documentation

  - [ ] 8.1 Append the exclusive-fullscreen limitation section to `README.md`
    - Section heading `## Display Modes` followed by two paragraphs:
      1. Note that price labels may not appear while Warframe runs in **exclusive fullscreen** because the Windows desktop compositor can prevent WPF from rendering above an exclusive fullscreen surface.
      2. Instruct the user to switch Warframe to **borderless** display mode to restore overlay visibility.
    - _Requirements: 11.4_

- [ ] 9. Final build verification

  - [ ] 9.1 Run `dotnet build` and resolve any warnings
    - From the workspace root run `dotnet build WarframeRelicOverlay.sln` (or the .csproj). The build must succeed with zero warnings.
    - Run `dotnet test tests/Presentation.Tests/Presentation.Tests.csproj --no-build` to ensure the new property tests, parser tests, and visibility tests pass (only when the `*` sub-tasks above are implemented).
    - Ensure all tests pass, ask the user if questions arise.
    - _Requirements: all_

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP. Property tests in 1.6, 1.7, 4.11–4.15 and unit tests in 1.5, 2.3 are optional but strongly encouraged because they validate the seven correctness properties from R19.
- Each task references specific requirements clauses (granular, not just user stories) for traceability.
- Checkpoints are folded into task 9 (build verification). The intentional rendering pipeline boundary is task 4.7 (`ApplyVisibility`) — every mutation routes through it.
- Property tests validate the seven universal correctness properties from R19 (PositionDerivedFromCardX/Y, OneLabelPerCard, NoLabelsWhenWindowInvalid, DisplayTextMatchesCard, StateVisibility, HiddenWhenUnfocused).
- Unit tests validate parser edge cases (`GlobalHotkey.TryParse`) and the exhaustive 40-row truth table for `VisibilityDecision`.
- The composition root assembles the same `OverlayCoordinator` consumer-of-`IOverlayOutput` that ships in the engine; no engine code is modified.

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "1.2", "1.3", "2.1", "5.1", "7.1", "7.2", "8.1"] },
    { "id": 1, "tasks": ["1.4", "2.2", "3.1", "4.1", "7.3"] },
    { "id": 2, "tasks": ["1.5", "1.6", "1.7", "2.3", "3.2"] },
    { "id": 3, "tasks": ["4.2"] },
    { "id": 4, "tasks": ["4.3", "4.6"] },
    { "id": 5, "tasks": ["4.4", "4.5", "4.7"] },
    { "id": 6, "tasks": ["4.8", "4.9", "4.10"] },
    { "id": 7, "tasks": ["4.11", "4.12", "4.13", "4.14", "4.15", "6.1"] },
    { "id": 8, "tasks": ["6.3", "6.4"] },
    { "id": 9, "tasks": ["6.2", "6.5"] },
    { "id": 10, "tasks": ["9.1"] }
  ]
}
```
