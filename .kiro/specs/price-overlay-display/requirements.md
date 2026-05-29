# Requirements Document

## Introduction

The Price Overlay Display feature is the visible presentation layer of the WarframeRelicOverlay application. When the existing detection and pricing pipeline produces a `PipelineResult`, this feature renders one small price card per detected reward card directly below (or above) the corresponding reward card in the Warframe game window, so the player can decide which relic reward to pick without alt-tabbing to a market site.

The pipeline, market client, layout detector, and state coordinator are already implemented. This feature delivers the transparent, click-through, always-on-top WPF overlay window, the `IOverlayOutput` implementation that the `OverlayCoordinator` calls into, and the composition root wiring that boots the application end-to-end. The central technical challenge is screen-space alignment: each price card's position must be derived from `CardResult.BoundsInWindow` (physical pixels relative to the captured bitmap) and `PipelineResult.Window` (the `WindowSnapshot` carrying the client-area offset and DPI scale factors), then translated into WPF logical units (DIPs) so the price card sits flush with its source reward card regardless of monitor DPI.

## Glossary

- **Overlay_Window**: The single transparent, click-through, topmost WPF `Window` that hosts all price cards and the loading indicator.
- **Price_Card**: One on-screen UI element rendered inside the Overlay_Window for a single `CardResult`, containing its `DisplayText`.
- **Loading_Indicator**: A single transient UI element shown inside the Overlay_Window while the pipeline is executing.
- **Overlay_Output**: The concrete implementation of `WarframeRelicOverlay.Core.IOverlayOutput` that this feature delivers.
- **Composition_Root**: The application-startup code (in `App.xaml.cs` or an equivalent class) that constructs the dependency graph (settings, trackers, detector, pipeline, state machine, Overlay_Output, Overlay_Coordinator) and starts it.
- **Overlay_Coordinator**: The existing `WarframeRelicOverlay.Core.OverlayCoordinator` class which calls the Overlay_Output methods from background threads.
- **UI_Dispatcher**: The `System.Windows.Threading.Dispatcher` associated with the WPF UI thread.
- **Pipeline_Result**: An instance of `WarframeRelicOverlay.OverlayApp.Pipeline.PipelineResult`.
- **Card_Result**: An instance of `WarframeRelicOverlay.OverlayApp.Pipeline.CardResult`.
- **Window_Snapshot**: An instance of `WarframeRelicOverlay.Infrastructure.Platform.WindowSnapshot`.
- **Client_Area**: The renderable surface inside the Warframe window described by `Window_Snapshot.ClientX`, `ClientY`, `ClientWidth`, `ClientHeight` in physical pixels.
- **DIP**: WPF device-independent pixel, equal to `physical_pixels / DpiScale`.
- **Overlay_State**: A value of the existing `WarframeRelicOverlay.OverlayApp.StateMachine.OverlayState` enum.

## Requirements

### Requirement 1: Overlay Window Visual Behaviour

**User Story:** As a Warframe player, I want the price overlay to float on top of the game without intercepting my mouse, so that I can see prices while continuing to interact with the game normally.

#### Acceptance Criteria

1. THE Overlay_Window SHALL be created with a fully transparent background.
2. THE Overlay_Window SHALL be configured as a topmost window so it renders above the Warframe client area.
3. THE Overlay_Window SHALL be configured as click-through such that all mouse input is passed to the window beneath it.
4. THE Overlay_Window SHALL be excluded from the Windows taskbar and Alt-Tab task switcher.
5. THE Overlay_Window SHALL render without a title bar, resize border, or minimize/maximize/close chrome.
6. THE Overlay_Window SHALL apply the opacity value from `AppSettings.OverlayOpacity` to its content layer.

### Requirement 2: Overlay Window Geometry Alignment

**User Story:** As a Warframe player, I want the overlay to cover exactly the same area as the game's client area, so that price cards line up with reward cards regardless of where I move or resize the game window.

#### Acceptance Criteria

1. WHEN the Overlay_Output receives a Pipeline_Result, THE Overlay_Window SHALL be positioned and sized so its bounds in DIPs equal `Window_Snapshot.LogicalX`, `LogicalY`, `LogicalWidth`, `LogicalHeight`.
2. WHEN the Overlay_Window is positioned, THE Overlay_Window SHALL place its top-left corner at the screen-space pixel coordinates `(Window_Snapshot.ClientX, Window_Snapshot.ClientY)`.
3. WHEN consecutive Pipeline_Results carry different `Window_Snapshot` values, THE Overlay_Window SHALL update its position and size to match the most recent Window_Snapshot.
4. IF the received Window_Snapshot has `IsValid` equal to `false`, THEN THE Overlay_Output SHALL not display any Price_Card and SHALL hide the Overlay_Window.

### Requirement 3: Price Card Positioning

**User Story:** As a Warframe player, I want each price to appear directly under (or above) its specific reward card, so that I can read each item's value without ambiguity about which card it refers to.

#### Acceptance Criteria

1. WHEN the Overlay_Output receives a Pipeline_Result with N cards, THE Overlay_Output SHALL render exactly N Price_Cards inside the Overlay_Window, one per `Card_Result`.
2. THE Overlay_Output SHALL position each Price_Card horizontally centered on its `Card_Result.BoundsInWindow` such that the Price_Card's horizontal center in DIPs equals `(BoundsInWindow.X + BoundsInWindow.Width / 2) / Window_Snapshot.DpiScaleX`.
3. THE Overlay_Output SHALL position each Price_Card vertically directly below its `Card_Result.BoundsInWindow` such that the Price_Card's top edge in DIPs equals `(BoundsInWindow.Y + BoundsInWindow.Height) / Window_Snapshot.DpiScaleY` plus a fixed vertical gap measured in DIPs.
4. WHERE the computed Price_Card bottom edge would exceed `Window_Snapshot.LogicalHeight`, THE Overlay_Output SHALL position the Price_Card directly above its `Card_Result.BoundsInWindow` instead, with its bottom edge in DIPs equal to `BoundsInWindow.Y / Window_Snapshot.DpiScaleY` minus the same fixed vertical gap.
5. THE Overlay_Output SHALL render each Price_Card using the `DisplayText` of its `Card_Result` verbatim.
6. THE Overlay_Output SHALL apply font sizing such that WHERE `AppSettings.PriceFontSizeOverride` is greater than zero, the Price_Card font size in DIPs equals that value, and WHERE `AppSettings.PriceFontSizeOverride` equals zero, the Price_Card font size is derived proportionally to `Window_Snapshot.LogicalHeight`.

### Requirement 4: DPI Invariance

**User Story:** As a Warframe player on a high-DPI monitor, I want price cards to align correctly regardless of my system scaling, so that the overlay works on both 100% and 150%/200% DPI setups.

#### Acceptance Criteria

1. THE Overlay_Output SHALL position the Overlay_Window and every Price_Card using DIPs derived from physical pixels via division by `Window_Snapshot.DpiScaleX` or `DpiScaleY`.
2. FOR ALL pairs of Pipeline_Results that differ only in the `Window_Snapshot.DpiScaleX` and `DpiScaleY` values, THE Overlay_Output SHALL produce Price_Cards whose screen-space pixel positions are equal within rounding tolerance of one physical pixel.
3. THE Overlay_Output SHALL NOT apply any DPI conversion to `Card_Result.BoundsInWindow` other than the division by `DpiScaleX` and `DpiScaleY` carried by the corresponding Window_Snapshot.

### Requirement 5: IOverlayOutput Lifecycle Methods

**User Story:** As the Overlay_Coordinator, I want the Overlay_Output to honour every method on `IOverlayOutput` predictably, so that state transitions translate into the correct visible UI state.

#### Acceptance Criteria

1. WHEN `IOverlayOutput.ShowPrices` is invoked, THE Overlay_Output SHALL replace any previously displayed Price_Cards with the cards derived from the supplied Pipeline_Result.
2. WHEN `IOverlayOutput.ClearPrices` is invoked, THE Overlay_Output SHALL remove every Price_Card from the Overlay_Window.
3. WHEN `IOverlayOutput.ClearPrices` is invoked while no Price_Cards are currently displayed, THE Overlay_Output SHALL leave the Overlay_Window contents unchanged and SHALL NOT throw.
4. WHEN `IOverlayOutput.ShowLoading` is invoked, THE Overlay_Output SHALL display the Loading_Indicator inside the Overlay_Window.
5. WHEN `IOverlayOutput.HideLoading` is invoked, THE Overlay_Output SHALL remove the Loading_Indicator from the Overlay_Window.
6. WHEN `IOverlayOutput.HideLoading` is invoked while the Loading_Indicator is not displayed, THE Overlay_Output SHALL leave the Overlay_Window contents unchanged and SHALL NOT throw.
7. THE Overlay_Output SHALL position the Loading_Indicator at the horizontal and vertical center of the Overlay_Window in DIPs.

### Requirement 6: Overlay Visibility By State

**User Story:** As a Warframe player, I want the overlay to only appear when there is something to show, so that an empty transparent window does not interfere with the game when I am not looking at a reward screen.

#### Acceptance Criteria

1. WHILE the Overlay_State is `Idle` or `Tracking`, THE Overlay_Window SHALL be hidden from the screen.
2. WHILE the Overlay_State is `Pricing` or `Displaying`, THE Overlay_Window SHALL be visible on the screen.
3. WHEN the Overlay_State transitions from `Displaying` to `Tracking`, THE Overlay_Output SHALL clear all Price_Cards before the Overlay_Window is hidden.
4. WHEN the Overlay_State transitions from `Idle` to any other state, THE Overlay_Output SHALL ensure the Overlay_Window is registered and ready to be shown.

### Requirement 7: Thread Marshalling

**User Story:** As the Overlay_Coordinator, I want to call `IOverlayOutput` methods from any thread without crashing the UI, so that I do not need to manage WPF dispatcher concerns in the coordinator.

#### Acceptance Criteria

1. WHEN any `IOverlayOutput` method is invoked from a thread other than the UI thread, THE Overlay_Output SHALL marshal the work onto the UI_Dispatcher before touching any WPF visual element.
2. WHEN any `IOverlayOutput` method is invoked from the UI thread, THE Overlay_Output SHALL execute the work without re-marshalling.
3. IF the UI_Dispatcher has been shut down at the time an `IOverlayOutput` method is invoked, THEN THE Overlay_Output SHALL discard the call and SHALL NOT throw.
4. THE Overlay_Output SHALL preserve the relative ordering of `IOverlayOutput` invocations made from a single thread when dispatching them to the UI_Dispatcher.

### Requirement 8: Composition Root and Application Startup

**User Story:** As a developer running the application, I want a single entry point that wires up every component and starts the coordinator, so that launching the executable produces a working overlay end-to-end.

#### Acceptance Criteria

1. WHEN the application starts, THE Composition_Root SHALL load `AppSettings` from the `data/settings.json` file using `AppSettings.Load`.
2. WHEN the application starts, THE Composition_Root SHALL construct the Overlay_Window, the Overlay_Output, the `OverlayStateMachine`, the `IProcessTracker`, the `IWindowTracker`, the `IRewardDetector`, the `IRewardPipeline`, and the Overlay_Coordinator.
3. WHEN the application starts, THE Composition_Root SHALL pass the Overlay_Output to the Overlay_Coordinator constructor as the `IOverlayOutput` argument.
4. WHEN the application starts, THE Composition_Root SHALL invoke `IProcessTracker.Start` and `OverlayCoordinator.Start` exactly once.
5. WHEN the application is shutting down, THE Composition_Root SHALL invoke `OverlayCoordinator.Dispose` exactly once.
6. IF `AppSettings.Load` reports validation warnings, THEN THE Composition_Root SHALL log each warning via the application logger and SHALL continue startup using the clamped values.
7. THE Composition_Root SHALL register the `IRewardDetector` implementation selected by `AppSettings.DetectionMode`, using `LogFileDetector` when the value is `EELog` and `OcrFallbackDetector` when the value is `OCR`.

### Requirement 9: Visual Style of Price Cards and Loading Indicator

**User Story:** As a Warframe player, I want price cards to be readable against any in-game background, so that I can read prices on bright loot screens and dark void backgrounds alike.

#### Acceptance Criteria

1. THE Overlay_Output SHALL render each Price_Card with a dark background and light foreground text, with a contrast ratio of at least 4.5 to 1 between the foreground and background colors.
2. THE Overlay_Output SHALL render each Price_Card with internal padding of at least 4 DIPs on every side around the text.
3. THE Overlay_Output SHALL size each Price_Card to fit its `DisplayText` content without text truncation at the configured font size.
4. THE Overlay_Output SHALL render the Loading_Indicator as a single visual element no larger than 64 DIPs by 64 DIPs.

### Requirement 10: Robustness Against Empty and Degenerate Pipeline Results

**User Story:** As a Warframe player, I want the overlay to behave sensibly when detection produces nothing useful, so that it does not flash garbage cards or freeze when the pipeline returns no data.

#### Acceptance Criteria

1. WHEN `IOverlayOutput.ShowPrices` is invoked with a Pipeline_Result whose `Cards` list is empty, THE Overlay_Output SHALL clear any previously displayed Price_Cards and SHALL display zero Price_Cards.
2. IF a `Card_Result.BoundsInWindow` has a width or height of zero or negative pixels, THEN THE Overlay_Output SHALL skip rendering the Price_Card for that Card_Result.
3. WHEN multiple `IOverlayOutput.ShowPrices` invocations arrive in rapid succession, THE Overlay_Output SHALL render only the Price_Cards corresponding to the most recently received Pipeline_Result once dispatcher work has drained.
4. THE Overlay_Output SHALL NOT throw when the supplied Pipeline_Result references a `Window_Snapshot` whose `DpiScaleX` or `DpiScaleY` is zero, and SHALL instead skip rendering for that invocation.
