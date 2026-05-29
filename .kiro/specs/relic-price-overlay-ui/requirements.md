# Requirements Document

## Introduction

The Relic Price Overlay UI feature delivers the visible presentation layer of WarframeRelicOverlay together with the application startup wiring needed to run the existing detection-and-pricing pipeline end-to-end.

The overlay is hidden by default and only becomes visible in two situations:

1. The Warframe reward screen at the end of a relic mission is detected (Overlay_State is `Pricing` or `Displaying`), in which case price labels are rendered below each detected reward card.
2. The user presses the configured global hotkey (`AppSettings.ToggleHotkey`, default `Shift+F9`), which forces the overlay to appear regardless of the pipeline state and stays visible until the hotkey is pressed again.

In every other situation — `Idle`, `Tracking`, `Detecting`, alt-tabbed away from Warframe, or with an invalid window snapshot — the overlay is fully hidden so it never appears during normal gameplay or in menus.

The codebase already implements detection (`LogFileDetector`, `OcrFallbackDetector`), screen capture, OCR, layout detection, fuzzy matching, market client, price cache, the reward pricing pipeline, the overlay state machine, the coordinator, and settings. What does not yet exist is:

1. A concrete implementation of `WarframeRelicOverlay.Core.IOverlayOutput`.
2. A WPF overlay window that renders one transparent price label per detected reward card directly below each card in the live Warframe game window.
3. The composition root in `App.xaml.cs` that wires every component together and starts the coordinator.

This feature delivers all three. The central technical challenge is screen-space alignment: every label position MUST be derived from `CardResult.BoundsInWindow` (physical pixels relative to the captured window bitmap) and `PipelineResult.Window` (the `WindowSnapshot` carrying `ClientX`, `ClientY`, `DpiScaleX`, `DpiScaleY`). No screen coordinate, offset, or resolution constant may be hardcoded.

## Glossary

- **Overlay_Window**: The single transparent, click-through, topmost WPF `Window` that hosts all price labels and the loading spinner. Implemented by this feature.
- **Price_Label**: One on-screen UI element rendered inside the Overlay_Window for a single `Card_Result`, displaying `Card_Result.DisplayText`.
- **Loading_Spinner**: A single transient indicator displayed inside the Overlay_Window while the pipeline is executing.
- **Overlay_Output**: The concrete implementation of `WarframeRelicOverlay.Core.IOverlayOutput` delivered by this feature; bridges the Overlay_Coordinator to the Overlay_Window.
- **Overlay_Coordinator**: The existing `WarframeRelicOverlay.Core.OverlayCoordinator` class that drives the state machine and invokes `IOverlayOutput` methods from background threads.
- **Composition_Root**: The application-startup code in `App.xaml.cs` (specifically the `OnStartup` override) that constructs the dependency graph using `Microsoft.Extensions.DependencyInjection` and starts the application.
- **UI_Dispatcher**: The `System.Windows.Threading.Dispatcher` associated with the WPF UI thread.
- **Pipeline_Result**: An instance of `WarframeRelicOverlay.OverlayApp.Pipeline.PipelineResult`.
- **Card_Result**: An instance of `WarframeRelicOverlay.OverlayApp.Pipeline.CardResult`.
- **Window_Snapshot**: An instance of `WarframeRelicOverlay.Infrastructure.Platform.WindowSnapshot`.
- **Client_Area**: The renderable surface inside the Warframe window described by `Window_Snapshot.ClientX`, `ClientY`, `ClientWidth`, `ClientHeight` in physical pixels.
- **DIP**: WPF device-independent pixel, equal to `physical_pixel / DpiScale`.
- **Overlay_State**: A value of `WarframeRelicOverlay.OverlayApp.StateMachine.OverlayState` (`Idle`, `Tracking`, `Detecting`, `Pricing`, `Displaying`).
- **Foreground_State**: The boolean returned by `IWindowTracker.IsForeground(handle)` for the tracked Warframe window handle, indicating whether the Warframe window currently has focus.
- **Manual_Shown_State**: An internal boolean owned by the Overlay_Output that toggles when the user presses the configured hotkey; when `true` the Overlay_Window is shown regardless of Overlay_State (subject to Foreground_State and a valid Window_Snapshot). When `false` the Overlay_Window's visibility is governed solely by Overlay_State, Foreground_State, and Window_Snapshot validity.
- **Hotkey**: The key combination parsed from `AppSettings.ToggleHotkey` (default `Shift+F9`) registered as a system-wide global hotkey.
- **Detection_Mode**: The value of `AppSettings.DetectionMode` (`EELog`, `OCR`, or `Manual`).

## Requirements

### Requirement 1: Overlay Window Visual Behaviour

**User Story:** As a Warframe player, I want the price overlay to float on top of the game without intercepting my mouse, so that I can see prices while continuing to interact with the game normally.

#### Acceptance Criteria

1. THE Overlay_Window SHALL be created with a fully transparent background.
2. THE Overlay_Window SHALL be configured as a topmost window so it renders above the Warframe client area.
3. THE Overlay_Window SHALL be configured as click-through such that all mouse input is passed to the window beneath it.
4. THE Overlay_Window SHALL be excluded from the Windows taskbar and from the Alt-Tab task switcher.
5. THE Overlay_Window SHALL render without a title bar, resize border, or minimize, maximize, or close chrome.
6. THE Overlay_Window SHALL apply the value of `AppSettings.OverlayOpacity` to the visible content layer such that a value of 1.0 renders fully opaque labels and a value of 0.5 renders labels at half opacity.

### Requirement 2: Overlay Window Geometry Alignment

**User Story:** As a Warframe player, I want the overlay surface to cover exactly the Warframe client area, so that price labels can be positioned in coordinates relative to that area without offset bookkeeping.

#### Acceptance Criteria

1. WHEN the Overlay_Output positions the Overlay_Window, THE Overlay_Output SHALL place the top-left corner of the Overlay_Window at screen-space pixel coordinates `(Window_Snapshot.ClientX, Window_Snapshot.ClientY)`.
2. WHEN the Overlay_Output sizes the Overlay_Window, THE Overlay_Output SHALL set the Overlay_Window width and height in DIPs to `Window_Snapshot.LogicalWidth` and `Window_Snapshot.LogicalHeight` respectively.
3. WHEN consecutive Pipeline_Results carry different `Window_Snapshot` values, THE Overlay_Output SHALL update the position and size of the Overlay_Window to match the most recently received Window_Snapshot.
4. IF the received Window_Snapshot has `IsValid` equal to `false`, THEN THE Overlay_Output SHALL hide the Overlay_Window and SHALL NOT render any Price_Label.

### Requirement 3: Multi-Card Price Label Rendering

**User Story:** As a Warframe player, I want one price label per reward card the pipeline detects, so that I can read each item's value without ambiguity about which card it refers to.

#### Acceptance Criteria

1. WHEN `IOverlayOutput.ShowPrices` is invoked with a Pipeline_Result containing N `Card_Result` items, THE Overlay_Output SHALL render exactly N Price_Labels inside the Overlay_Window, one per Card_Result.
2. THE Overlay_Output SHALL render each Price_Label using the `Card_Result.DisplayText` property of its corresponding Card_Result verbatim, supporting at minimum the values `"Np"` for an integer N, `"Untradeable"`, `"N/A"`, and `"?"`.
3. WHEN `IOverlayOutput.ShowPrices` is invoked with a Pipeline_Result whose `Cards` list is empty, THE Overlay_Output SHALL remove every previously rendered Price_Label and SHALL display zero Price_Labels.
4. IF a Card_Result has a `BoundsInWindow` width or height less than or equal to zero pixels, THEN THE Overlay_Output SHALL skip rendering the Price_Label for that Card_Result.

### Requirement 4: Price Label Positioning Derived From Card Bounds

**User Story:** As a Warframe player, I want each price label to appear directly below its specific reward card and to follow the card if the game window moves or the resolution changes, so that the overlay stays accurate without manual reconfiguration.

#### Acceptance Criteria

1. THE Overlay_Output SHALL position each Price_Label horizontally centered on its Card_Result such that the Price_Label center X in DIPs, measured from the Overlay_Window left edge, equals `(Card_Result.BoundsInWindow.X + Card_Result.BoundsInWindow.Width / 2) / Window_Snapshot.DpiScaleX`.
2. THE Overlay_Output SHALL position each Price_Label vertically directly below its Card_Result such that the Price_Label top edge in DIPs, measured from the Overlay_Window top edge, equals `(Card_Result.BoundsInWindow.Y + Card_Result.BoundsInWindow.Height) / Window_Snapshot.DpiScaleY` plus a fixed vertical gap of 4 DIPs.
3. IF the computed Price_Label bottom edge would exceed the Overlay_Window logical height, THEN THE Overlay_Output SHALL position the Price_Label directly above its Card_Result instead, with the Price_Label bottom edge in DIPs equal to `Card_Result.BoundsInWindow.Y / Window_Snapshot.DpiScaleY` minus 4 DIPs.
4. THE Overlay_Output SHALL NOT use any hardcoded screen-space pixel coordinate, screen resolution constant, or per-resolution offset table when positioning Price_Labels.
5. THE Overlay_Output SHALL derive every Price_Label position exclusively from `Card_Result.BoundsInWindow`, `Window_Snapshot.ClientX`, `Window_Snapshot.ClientY`, `Window_Snapshot.DpiScaleX`, and `Window_Snapshot.DpiScaleY`.
6. WHEN any of `Window_Snapshot.ClientX`, `Window_Snapshot.ClientY`, `Window_Snapshot.DpiScaleX`, or `Window_Snapshot.DpiScaleY` changes between consecutive renderings, THE Overlay_Output SHALL recompute the position of every visible Price_Label using the new values within 100 milliseconds of the change.
7. IF `Window_Snapshot.DpiScaleX` is less than or equal to zero or `Window_Snapshot.DpiScaleY` is less than or equal to zero, THEN THE Overlay_Output SHALL hide every Price_Label and SHALL NOT throw.

### Requirement 5: DPI And Multi-Monitor Correctness

**User Story:** As a Warframe player on a high-DPI or multi-monitor setup, I want price labels to align correctly regardless of system scaling or which monitor Warframe is on, so that the overlay works on every supported configuration.

#### Acceptance Criteria

1. THE Overlay_Window SHALL declare per-monitor DPI awareness so that WPF reports physical-pixel-accurate dimensions when the window is moved between monitors with different DPI settings.
2. THE Overlay_Output SHALL convert physical-pixel quantities sourced from `Card_Result.BoundsInWindow` and `Window_Snapshot` into DIPs by dividing by `Window_Snapshot.DpiScaleX` for X-axis values and by `Window_Snapshot.DpiScaleY` for Y-axis values.
3. WHEN Warframe is positioned on a non-primary monitor, THE Overlay_Window SHALL be positioned at the screen coordinates `(Window_Snapshot.ClientX, Window_Snapshot.ClientY)` interpreted in the Windows virtual-screen coordinate system.
4. FOR ALL Pipeline_Results identical except for `Window_Snapshot.DpiScaleX` and `DpiScaleY`, THE Overlay_Output SHALL produce Price_Labels whose screen-space pixel positions are equal within a tolerance of one physical pixel.
5. IF `Window_Snapshot.DpiScaleX` or `Window_Snapshot.DpiScaleY` is less than or equal to zero, THEN THE Overlay_Output SHALL skip rendering for that invocation and SHALL NOT throw.

### Requirement 6: State-Driven Visibility

**User Story:** As a Warframe player, I want the overlay to remain completely hidden whenever I am not at the reward screen, so that price labels never appear during normal gameplay or in the UI menus.

#### Acceptance Criteria

1. WHILE the Overlay_State is `Idle`, `Tracking`, or `Detecting` and the Manual_Shown_State is `false`, THE Overlay_Output SHALL ensure the Overlay_Window is hidden, contains zero visible Price_Label instances, and the Loading_Spinner is hidden.
2. WHILE the Overlay_State is `Pricing` and the Manual_Shown_State is `false`, THE Overlay_Output SHALL display the Overlay_Window and the Loading_Spinner within 100 ms of entering the state and SHALL ensure zero Price_Label instances are visible.
3. WHILE the Overlay_State is `Displaying` and the Manual_Shown_State is `false`, THE Overlay_Output SHALL display the Overlay_Window and one Price_Label per Card_Result contained in the most recently received Pipeline_Result within 100 ms of entering the state and SHALL hide the Loading_Spinner.
4. WHEN the Overlay_State transitions from `Displaying` to any state other than `Pricing` and the Manual_Shown_State is `false`, THE Overlay_Output SHALL remove every Price_Label from the Overlay_Window and hide the Overlay_Window within 100 ms of the transition and before any new Price_Label or Loading_Spinner is rendered.
5. WHEN the Overlay_State transitions from `Pricing` to any state other than `Displaying` and the Manual_Shown_State is `false`, THE Overlay_Output SHALL hide the Loading_Spinner and hide the Overlay_Window within 100 ms of the transition and before any new Price_Label is rendered.
6. WHILE the Manual_Shown_State is `true` and the Foreground_State is `true` and the most recently received Window_Snapshot has `IsValid` equal to `true`, THE Overlay_Output SHALL display the Overlay_Window regardless of the Overlay_State, displaying Price_Labels when a Pipeline_Result is available and otherwise displaying the Overlay_Window with zero Price_Label instances and the Loading_Spinner hidden.

### Requirement 7: Loading Spinner Placement And Appearance

**User Story:** As a Warframe player, I want a clear loading indicator over the reward area while prices are being fetched, so that I know the overlay is working and not stalled.

#### Acceptance Criteria

1. WHEN `IOverlayOutput.ShowLoading` is invoked, THE Overlay_Output SHALL display the Loading_Spinner inside the Overlay_Window.
2. WHEN `IOverlayOutput.HideLoading` is invoked, THE Overlay_Output SHALL remove the Loading_Spinner from the Overlay_Window.
3. WHEN `IOverlayOutput.HideLoading` is invoked while the Loading_Spinner is not displayed, THE Overlay_Output SHALL leave the Overlay_Window contents unchanged and SHALL NOT throw.
4. THE Overlay_Output SHALL position the Loading_Spinner horizontally at the center of the Overlay_Window and vertically over the reward-card area, defined as the vertical band between 30 percent and 70 percent of `Window_Snapshot.LogicalHeight` from the Overlay_Window top edge.
5. THE Overlay_Output SHALL render the Loading_Spinner as an animated indicator whose bounding box is no larger than 96 DIPs in width and 96 DIPs in height.

### Requirement 8: Foreground Gating

**User Story:** As a Warframe player, I want the overlay to disappear when I alt-tab away from the game, so that price labels do not appear over my browser, editor, or other foreground applications.

#### Acceptance Criteria

1. WHILE `IWindowTracker.IsForeground` returns `false` for the tracked Warframe window handle, THE Overlay_Window SHALL be hidden from the screen regardless of the Overlay_State.
2. WHEN `IWindowTracker.IsForeground` transitions from `false` to `true` for the tracked Warframe window handle, THE Overlay_Output SHALL restore the Overlay_Window content appropriate to the current Overlay_State.
3. THE Overlay_Output SHALL re-evaluate the Foreground_State at a polling interval no longer than 250 milliseconds while the Overlay_State is not `Idle`.
4. WHEN the foreground window changes while the Overlay_State is `Displaying`, THE Overlay_Output SHALL preserve the most recently received Pipeline_Result so that returning focus to Warframe restores the same Price_Labels.

### Requirement 9: Manual Hotkey Toggle

**User Story:** As a Warframe player, I want a configurable hotkey to manually force the overlay to appear outside the reward screen, so that I can preview labels or position-test the overlay without waiting for a real reward screen.

#### Acceptance Criteria

1. WHEN the application starts, THE Composition_Root SHALL register a global hotkey using the value of `AppSettings.ToggleHotkey`, defaulting to `Shift+F9` when the value is null, empty, or whitespace; valid hotkey strings consist of zero or more modifiers from the set `{Ctrl, Shift, Alt, Win}` plus exactly one non-modifier key, joined by `+`, parsed case-insensitively.
2. WHEN the registered Hotkey is pressed and the Manual_Shown_State is `false`, THE Overlay_Output SHALL set the Manual_Shown_State to `true` and SHALL update the Overlay_Window visibility within 200 milliseconds of the keypress to match the visibility specified by Requirement 6 for the current Overlay_State, Foreground_State, and Manual_Shown_State.
3. WHEN the registered Hotkey is pressed and the Manual_Shown_State is `true`, THE Overlay_Output SHALL set the Manual_Shown_State to `false` and SHALL update the Overlay_Window visibility within 200 milliseconds of the keypress to match the visibility specified by Requirement 6 for the current Overlay_State, Foreground_State, and Manual_Shown_State.
4. IF the value of `AppSettings.ToggleHotkey` cannot be parsed into a valid key combination per the grammar in criterion 1, THEN THE Composition_Root SHALL log a warning identifying the failure cause, fall back to `Shift+F9`, and continue startup.
5. IF registration of the Hotkey fails because the key combination is already owned by another process, THEN THE Composition_Root SHALL log a warning, leave the Manual_Shown_State at its initial value of `false`, leave the global hotkey unregistered for the remainder of the process lifetime, and continue startup.

### Requirement 10: Window Move And Resize Tracking

**User Story:** As a Warframe player, I want the price labels to follow the game when I move or resize the window, so that the overlay stays aligned without requiring a new pipeline cycle.

#### Acceptance Criteria

1. WHILE the Overlay_State is `Displaying`, THE Overlay_Output SHALL invoke `IWindowTracker.TryGetBounds` at a polling interval between 100 and 250 milliseconds inclusive.
2. WHEN the polled `WindowSnapshot` differs from the snapshot used for the most recent rendering by more than one physical pixel in any of `ClientX`, `ClientY`, `ClientWidth`, or `ClientHeight`, THE Overlay_Output SHALL reposition and resize the Overlay_Window using the new snapshot within 250 milliseconds of the polling result.
3. WHEN the polled `WindowSnapshot` differs from the snapshot used for the most recent rendering by more than one physical pixel in any of `ClientX`, `ClientY`, `ClientWidth`, or `ClientHeight`, THE Overlay_Output SHALL reposition every Price_Label using the new snapshot's DPI and offset values combined with the existing `Card_Result.BoundsInWindow` values, preserving each label's existing text and visibility state.
4. IF `IWindowTracker.TryGetBounds` returns `null` while the Overlay_State is `Displaying`, THEN THE Overlay_Output SHALL hide the Overlay_Window within 250 milliseconds and SHALL retain the existing Card_Result and Price_Label data unchanged.
5. WHEN `IWindowTracker.TryGetBounds` returns a non-null `WindowSnapshot` after a prior `null` result while the Overlay_State is `Displaying`, THE Overlay_Output SHALL show the Overlay_Window and reposition the Overlay_Window and every Price_Label using the new snapshot within 250 milliseconds.

### Requirement 11: Display Mode Support

**User Story:** As a Warframe player, I want the overlay to work whether I run Warframe windowed, borderless, or fullscreen, so that I do not have to change game settings to use the overlay.

#### Acceptance Criteria

1. WHILE Warframe runs in windowed display mode and the Window_Snapshot has `IsValid` equal to `true`, THE Overlay_Output SHALL apply the Overlay_Window positioning logic specified in Requirement 2 and the Price_Label positioning logic specified in Requirement 4, and SHALL NOT apply any display-mode-specific offset, scaling, branch, or fallback.
2. WHILE Warframe runs in borderless display mode and the Window_Snapshot has `IsValid` equal to `true`, THE Overlay_Output SHALL apply the Overlay_Window positioning logic specified in Requirement 2 and the Price_Label positioning logic specified in Requirement 4, and SHALL NOT apply any display-mode-specific offset, scaling, branch, or fallback.
3. WHILE Warframe runs in exclusive fullscreen display mode and the Window_Snapshot has `IsValid` equal to `true`, THE Overlay_Output SHALL invoke the same Overlay_Window and Price_Label positioning logic specified in Requirements 2 and 4 and SHALL NOT throw, terminate, or enter an error state, even when the Windows desktop compositor prevents the Overlay_Window from rendering above the Warframe surface.
4. THE Overlay_Output SHALL be delivered together with a section in the project README that (a) states Price_Labels may not appear while Warframe runs in exclusive fullscreen display mode because the Windows desktop compositor can prevent WPF from rendering above an exclusive fullscreen surface, and (b) instructs the player to switch Warframe to borderless display mode to restore Price_Label visibility.

### Requirement 12: IOverlayOutput Method Contract

**User Story:** As the Overlay_Coordinator, I want every method on `IOverlayOutput` to behave predictably, so that state transitions translate into a correct visible UI without coordinator-side bookkeeping.

#### Acceptance Criteria

1. WHEN `IOverlayOutput.ShowPrices` is invoked, THE Overlay_Output SHALL replace any previously displayed Price_Labels with the labels derived from the supplied Pipeline_Result.
2. WHEN `IOverlayOutput.ClearPrices` is invoked, THE Overlay_Output SHALL remove every Price_Label from the Overlay_Window.
3. WHEN `IOverlayOutput.ClearPrices` is invoked while no Price_Label is currently displayed, THE Overlay_Output SHALL leave the Overlay_Window contents unchanged and SHALL NOT throw.
4. WHEN multiple `IOverlayOutput.ShowPrices` invocations arrive in rapid succession, THE Overlay_Output SHALL render only the Price_Labels corresponding to the most recently received Pipeline_Result once dispatcher work has drained.
5. WHEN `IOverlayOutput.ShowLoading` is invoked while the Loading_Spinner is already visible, THE Overlay_Output SHALL leave the Overlay_Window contents unchanged and SHALL NOT throw.

### Requirement 13: Thread Marshalling

**User Story:** As the Overlay_Coordinator, I want to call `IOverlayOutput` methods from any thread without crashing the UI, so that I do not need to manage WPF dispatcher concerns in the coordinator.

#### Acceptance Criteria

1. WHEN any `IOverlayOutput` method is invoked from a thread other than the UI thread, THE Overlay_Output SHALL marshal the work onto the UI_Dispatcher before touching any WPF visual element.
2. WHEN any `IOverlayOutput` method is invoked from the UI thread, THE Overlay_Output SHALL execute the work without re-marshalling.
3. IF the UI_Dispatcher has been shut down at the time an `IOverlayOutput` method is invoked, THEN THE Overlay_Output SHALL discard the call and SHALL NOT throw.
4. THE Overlay_Output SHALL preserve the relative ordering of `IOverlayOutput` invocations made from a single thread when dispatching them to the UI_Dispatcher.

### Requirement 14: Price Label Visual Style And Font Sizing

**User Story:** As a Warframe player, I want price labels to be readable against any in-game background, so that I can read them on bright loot screens and dark void backgrounds alike.

#### Acceptance Criteria

1. THE Overlay_Output SHALL render each Price_Label with a dark background fill and light foreground text such that the contrast ratio between foreground and background colors is at least 4.5 to 1.
2. THE Overlay_Output SHALL render each Price_Label with internal padding of at least 4 DIPs on every side around its text.
3. THE Overlay_Output SHALL size each Price_Label to fit its `DisplayText` content without text truncation at the configured font size.
4. WHERE `AppSettings.PriceFontSizeOverride` is greater than zero, THE Overlay_Output SHALL render every Price_Label at a font size in DIPs equal to that value.
5. WHERE `AppSettings.PriceFontSizeOverride` equals zero, THE Overlay_Output SHALL derive the Price_Label font size in DIPs as a deterministic function of `Window_Snapshot.LogicalHeight` such that doubling `LogicalHeight` doubles the resulting font size within rounding tolerance.

### Requirement 15: Composition Root And Application Startup

**User Story:** As a developer running the application, I want a single entry point that wires up every component and starts the coordinator, so that launching the executable produces a working overlay end-to-end.

#### Acceptance Criteria

1. WHEN the application starts, THE Composition_Root SHALL execute inside the `OnStartup` override of the WPF `Application` class declared in `App.xaml.cs`.
2. WHEN the application starts, THE Composition_Root SHALL build a `Microsoft.Extensions.DependencyInjection` service collection and resolve the dependency graph from a single `IServiceProvider`.
3. WHEN the application starts, THE Composition_Root SHALL load `AppSettings` from the file `data/settings.json` using `AppSettings.Load`, log every validation warning returned by the settings load, and continue startup using the clamped values.
4. WHEN the application starts, THE Composition_Root SHALL register and resolve the following components: `AppSettings`, `JsonRewardRepository`, `FuzzyRewardMatcher`, `WarframeProcessTracker`, `WarframeWindowTracker`, `GdiScreenCapturer`, `TesseractOcrEngine`, `WarframeMarketClient` configured with a single shared `HttpClient`, `RewardPriceCache`, `IntensityProfileDetector`, `RewardPricingPipeline`, the `IRewardDetector` selected by `AppSettings.DetectionMode`, `OverlayStateMachine`, the Overlay_Output, the Overlay_Window, and the Overlay_Coordinator.
5. WHEN the value of `AppSettings.DetectionMode` is `EELog`, THE Composition_Root SHALL register `LogFileDetector` as the `IRewardDetector`.
6. WHEN the value of `AppSettings.DetectionMode` is `OCR`, THE Composition_Root SHALL register an adapter that exposes `OcrFallbackDetector` as `IRewardDetector`.
7. IF the value of `AppSettings.DetectionMode` is `Manual`, THEN THE Composition_Root SHALL log a warning, fall back to registering `LogFileDetector` as the `IRewardDetector`, and continue startup.
8. WHEN the dependency graph has been resolved, THE Composition_Root SHALL invoke `IProcessTracker.Start` and `OverlayCoordinator.Start` exactly once each.
9. THE Composition_Root SHALL pass the Overlay_Output instance to the Overlay_Coordinator constructor as the `IOverlayOutput` argument such that both refer to the same object.
10. IF resolving the dependency graph throws an exception, THEN THE Composition_Root SHALL log the exception, display a single error dialog with the exception message, and shut down the application without leaking partially constructed resources.

### Requirement 16: Application Shutdown And Lifecycle

**User Story:** As a developer running the application, I want every long-lived resource released cleanly on exit, so that closing the application does not leak Tesseract engines, HTTP connections, or process handles.

#### Acceptance Criteria

1. WHEN the application is shutting down, THE Composition_Root SHALL invoke `OverlayCoordinator.Dispose` exactly once before any other tracked disposable is released, and SHALL complete that invocation within 5 seconds.
2. WHEN the application is shutting down, THE Composition_Root SHALL dispose `TesseractOcrEngine`, `WarframeMarketClient`, `WarframeProcessTracker`, and any registered `IRewardDetector` exactly once each, in reverse order of their registration, after `OverlayCoordinator.Dispose` has returned.
3. WHEN the Overlay_Window is closed by the user or by the operating system, THE Composition_Root SHALL initiate application shutdown so that all coordinator and infrastructure resources are released within 5 seconds of the close event.
4. WHEN the application is shutting down, THE Composition_Root SHALL release the global hotkey registration exactly once before the process exits.
5. IF disposing any tracked resource throws an exception, THEN THE Composition_Root SHALL log the failure with the resource identifier, continue disposing the remaining tracked resources, and exit the process with a non-zero exit code.

### Requirement 17: Graceful Degradation On Missing Or Invalid Inputs

**User Story:** As a Warframe player, I want the application to start and remain responsive even when external data is missing or the window cannot be measured, so that a transient problem does not require restarting the application.

#### Acceptance Criteria

1. IF the file `data/items.json` is missing, cannot be opened, contains invalid JSON, or does not conform to the expected reward-set schema at startup, THEN THE Composition_Root SHALL log a warning indicating the failure cause, register a `FuzzyRewardMatcher` over an empty reward set, and continue startup without terminating the process.
2. WHILE the `FuzzyRewardMatcher` operates over an empty reward set, the resulting Pipeline_Results SHALL render Price_Labels whose text equals the value of `Card_Result.DisplayText`, which equals `"?"` for unmatched cards.
3. IF `IWindowTracker.TryGetBounds` returns `null` at the moment the Overlay_Output is asked to render, THEN THE Overlay_Output SHALL hide the Overlay_Window, SHALL NOT render any Price_Label or Loading_Spinner, and SHALL NOT throw.
4. IF the Pipeline_Result references a `Window_Snapshot` whose `IsValid` is `false`, THEN THE Overlay_Output SHALL hide the Overlay_Window and SHALL NOT throw.
5. IF the file `data/settings.json` is missing, cannot be opened, contains invalid JSON, or does not conform to the expected `AppSettings` schema at startup, THEN THE Composition_Root SHALL log a warning indicating the failure cause, fall back to the defaults defined by `AppSettings`, and continue startup without terminating the process.

### Requirement 18: Coding Conventions

**User Story:** As a developer maintaining this codebase, I want the new files to match the established conventions documented in `convention.txt` and visible in the existing code, so that the new code reads as a natural extension of the existing engine.

#### Acceptance Criteria

1. THE Overlay_Output, Overlay_Window code-behind, and Composition_Root source files SHALL declare a file-scoped namespace using the `namespace X;` form.
2. THE Overlay_Output, Overlay_Window code-behind, and Composition_Root source files SHALL place every `using` directive inside the namespace block, matching the prevailing style in `OverlayCoordinator.cs` and `RewardPricingPipeline.cs`.
3. THE Overlay_Output, Overlay_Window code-behind, and Composition_Root source files SHALL use PascalCase for classes, structs, enums, interfaces, methods, properties, public fields, and constants, and camelCase for local variables and parameters.
4. THE Overlay_Output, Overlay_Window code-behind, and Composition_Root source files SHALL prefix every private instance field with a single underscore followed by camelCase, for example `_dispatcher`, `_settings`, `_overlayWindow`.
5. THE Overlay_Output, Overlay_Window code-behind, and Composition_Root source files SHALL place an XML documentation comment beginning with `/// <summary>` above every public type, public method, and public property, following the style of `OverlayCoordinator.cs` (use of `<para>`, `<list>`, `<see cref=...>`, `<b>`, and `<code>` blocks where they aid clarity).
6. THE Overlay_Output, Overlay_Window code-behind, and Composition_Root source files SHALL use region-style banner comments of the form `// ── Section ──` to separate logical sections, matching the style of `OverlayStateMachine.cs` and `OcrFallbackDetector.cs`.
7. THE Overlay_Output, Overlay_Window code-behind, and Composition_Root source files SHALL declare the type `sealed` where the type is not designed for inheritance.
8. THE Overlay_Output, Overlay_Window code-behind, and Composition_Root source files SHALL use `nint` for native window handle values and `System.Drawing` types from `System.Drawing.Common` where the types `Rectangle`, `Point`, or `Size` are required.

### Requirement 19: Correctness Properties For Property-Based Testing

**User Story:** As a developer writing tests for this feature, I want a stable set of correctness properties expressed against existing types so that property-based tests can drive randomized Pipeline_Result inputs through the Overlay_Output and verify each invariant.

#### Acceptance Criteria

1. FOR ALL Pipeline_Results with `Window_Snapshot.IsValid` equal to `true`, every rendered Price_Label center X in physical pixels measured from the screen origin SHALL fall within the inclusive screen-space X range `[Window_Snapshot.ClientX + Card_Result.BoundsInWindow.X, Window_Snapshot.ClientX + Card_Result.BoundsInWindow.X + Card_Result.BoundsInWindow.Width]` for its corresponding Card_Result. (PositionDerivedFromCardX)
2. FOR ALL Pipeline_Results with `Window_Snapshot.IsValid` equal to `true`, every rendered Price_Label top edge in physical pixels measured from the screen origin SHALL fall within the inclusive screen-space Y range `[Window_Snapshot.ClientY + Card_Result.BoundsInWindow.Y - L, Window_Snapshot.ClientY + Card_Result.BoundsInWindow.Y + Card_Result.BoundsInWindow.Height + Window_Snapshot.DpiScaleY * 4 + L]` for its corresponding Card_Result, where `L` is the rendered Price_Label height in physical pixels. (PositionDerivedFromCardY)
3. FOR ALL Pipeline_Results with `Window_Snapshot.IsValid` equal to `true`, the number of rendered Price_Labels SHALL equal the count of Card_Results in the Pipeline_Result whose `BoundsInWindow` width and height are both strictly positive. (OneLabelPerCard)
4. FOR ALL Pipeline_Results with `Window_Snapshot.IsValid` equal to `false`, the number of rendered Price_Labels SHALL equal zero. (NoLabelsWhenWindowInvalid)
5. FOR ALL Pipeline_Results, each rendered Price_Label SHALL display text byte-equal to the `Card_Result.DisplayText` of its corresponding Card_Result, with no truncation, padding, case change, or character substitution. (DisplayTextMatchesCard)
6. FOR ALL combinations of Overlay_State, Foreground_State, Manual_Shown_State, and Window_Snapshot validity, the effective screen-space visibility of the Overlay_Window SHALL be a deterministic function of those four inputs alone, where the function returns `true` if and only if the Window_Snapshot is valid AND the Foreground_State is `true` AND either (Manual_Shown_State is `true`) or (Overlay_State is `Pricing` or `Displaying`); the function SHALL return `false` for every other combination. (StateVisibility)
7. FOR ALL combinations of Overlay_State, Foreground_State, Manual_Shown_State, and Window_Snapshot validity in which the Foreground_State is `false`, the effective screen-space visibility of the Overlay_Window SHALL be `false`. (HiddenWhenUnfocused)

### Requirement 20: Out Of Scope

**User Story:** As a developer scoping this feature, I want explicit non-goals documented, so that reviewers do not expect features that belong to a later iteration.

#### Acceptance Criteria

1. THE Composition_Root SHALL NOT register a manual hotkey detector that toggles the `RewardConfirmed` trigger on demand, even when `AppSettings.DetectionMode` is `Manual`.
2. THE Overlay_Output SHALL NOT expose a reward log or history view in the Overlay_Window.
3. THE Composition_Root SHALL NOT expose a settings editor user interface in this feature.
4. THE Composition_Root SHALL NOT auto-update the file `data/items.json` from any external source in this feature.
5. THE Overlay_Output SHALL NOT implement click-to-copy or other interactive behaviors on Price_Labels in this feature.
6. THE Composition_Root SHALL NOT register a tray icon or system menu in this feature.
