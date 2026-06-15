namespace WarframeRelicOverlay.Presentation.Output;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Infrastructure.Logging;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.OverlayApp.Pipeline;
using WarframeRelicOverlay.OverlayApp.StateMachine;
using WarframeRelicOverlay.Presentation.Layout;
using WarframeRelicOverlay.Presentation.Views;

/// <summary>
/// Implementation of <see cref="IOverlayOutput"/> that drives the
/// <see cref="OverlayWindow"/>.
///
/// <para>
/// Combines four inputs to compute effective overlay visibility:
/// <list type="bullet">
///   <item>The current <see cref="OverlayState"/> reported by the state machine.</item>
///   <item>Whether Warframe is the foreground window (<see cref="IWindowTracker.IsForeground"/>).</item>
///   <item>The user-driven manual-show flag toggled by the global hotkey.</item>
///   <item>Whether the most recent <see cref="WindowSnapshot"/> is valid.</item>
/// </list>
/// The window is shown if and only if all four conditions allow it:
/// the snapshot is valid, Warframe has focus, and either the state is
/// <c>Pricing</c>/<c>Displaying</c> or the user pressed the hotkey.
/// </para>
///
/// <para>
/// All public methods are safe to call from any thread; calls are
/// marshalled to the WPF UI dispatcher when needed.  A 250ms polling
/// timer keeps the overlay aligned with Warframe when the player moves
/// or resizes the window between pipeline cycles.
/// </para>
/// </summary>
public sealed class WpfOverlayOutput : IOverlayOutput, IDisposable
{
    // ── Tunables ────────────────────────────────────────────────────

    /// <summary>
    /// How often the foreground / window-bounds tracker fires while the
    /// state machine is anywhere other than <see cref="OverlayState.Idle"/>.
    /// </summary>
    private static readonly TimeSpan TrackerInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Default vertical gap (DIPs) between the bottom of a reward card
    /// (the gold border line detected by the layout detector) and the
    /// top of the price card rendered by the overlay.  At 1080p the
    /// in-game reward names sit at ~32% of window height and the
    /// player-name labels sit at ~38%; leaving a 60 DIP gap puts the
    /// price card below the player names and above the timer bar,
    /// matching the position of common Warframe price-check overlays.
    /// </summary>
    private const double LabelGapDip = 60.0;

    /// <summary>
    /// Auto-sized font baseline.  At 1080 DIPs of window height the
    /// price text renders at 18 DIPs; the size scales linearly with
    /// window height.  Override via <see cref="AppSettings.PriceFontSizeOverride"/>.
    /// </summary>
    private const double AutoFontReferenceHeightDip = 1080.0;
    private const double AutoFontReferenceSizeDip = 18.0;

    // ── Dependencies ────────────────────────────────────────────────

    private readonly OverlayWindow _window;
    private readonly IWindowTracker _windowTracker;
    private readonly IProcessTracker _processTracker;
    private readonly OverlayStateMachine _stateMachine;
    private readonly AppSettings _settings;
    private readonly ILogger _logger;
    private readonly Dispatcher _dispatcher;

    // ── State ───────────────────────────────────────────────────────

    private readonly object _lock = new();
    private PipelineResult? _lastResult;
    private WindowSnapshot? _lastSnapshot;
    private bool _manualShown;
    private bool _isVisible;
    private bool _spinnerVisible;
    private DispatcherTimer? _trackerTimer;
    private bool _disposed;

    // ── Construction ────────────────────────────────────────────────

    /// <summary>
    /// Creates the output and subscribes to state-machine transitions
    /// so spinner/label visibility tracks the pipeline phase even when
    /// the coordinator does not invoke an explicit method (for example
    /// when transitioning <c>Pricing</c> to <c>Tracking</c> after a
    /// pipeline failure).
    /// </summary>
    public WpfOverlayOutput(
        OverlayWindow window,
        IWindowTracker windowTracker,
        IProcessTracker processTracker,
        OverlayStateMachine stateMachine,
        AppSettings settings,
        ILogger logger)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _windowTracker = windowTracker ?? throw new ArgumentNullException(nameof(windowTracker));
        _processTracker = processTracker ?? throw new ArgumentNullException(nameof(processTracker));
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _dispatcher = _window.Dispatcher
            ?? throw new InvalidOperationException("Overlay window has no dispatcher.");

        _stateMachine.StateChanged += OnStateChanged;

        // Polling timer that follows window moves/resizes and toggles
        // visibility when Warframe gains or loses focus.
        InvokeOnUi(StartTrackerTimer);
    }

    // ── IOverlayOutput ──────────────────────────────────────────────

    /// <summary>
    /// Receives a fresh <see cref="PipelineResult"/> from the
    /// coordinator and renders one price card per detected reward card.
    /// May be called from a background thread.
    /// </summary>
    public void ShowPrices(PipelineResult result)
    {
        if (result is null) return;

        lock (_lock)
        {
            _lastResult = result;
            _lastSnapshot = result.Window;
        }

        InvokeOnUi(() => ApplyVisibility());
    }

    /// <summary>
    /// Removes every visible price card.  Called by the coordinator
    /// when the displayed result becomes stale.
    /// </summary>
    public void ClearPrices()
    {
        lock (_lock)
        {
            _lastResult = null;
        }

        InvokeOnUi(() =>
        {
            _window.ClearLabels();
            ApplyVisibility();
        });
    }

    /// <summary>
    /// Shows the loading spinner anchored over the reward area.
    /// </summary>
    public void ShowLoading()
    {
        lock (_lock) { _spinnerVisible = true; }
        InvokeOnUi(() => ApplyVisibility());
    }

    /// <summary>
    /// Hides the loading spinner.
    /// </summary>
    public void HideLoading()
    {
        lock (_lock) { _spinnerVisible = false; }
        InvokeOnUi(() => ApplyVisibility());
    }

    // ── Manual hotkey toggle ────────────────────────────────────────

    /// <summary>
    /// Flips the manual-shown flag.  When set to <c>true</c> the
    /// overlay window appears regardless of the pipeline state, as
    /// long as Warframe is the foreground window and a valid
    /// <see cref="WindowSnapshot"/> is available.
    /// </summary>
    public void ToggleManualShown()
    {
        lock (_lock) { _manualShown = !_manualShown; }
        _logger.LogInfo($"Manual overlay shown = {_manualShown}");
        InvokeOnUi(() => ApplyVisibility());
    }

    // ── Disposal ────────────────────────────────────────────────────

    /// <summary>
    /// Stops the tracker timer and unsubscribes from state events.
    /// The window itself is owned by the DI container and disposed
    /// when the application shuts down.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stateMachine.StateChanged -= OnStateChanged;

        InvokeOnUi(() =>
        {
            _trackerTimer?.Stop();
            _trackerTimer = null;
        });
    }

    // ── State machine reaction ──────────────────────────────────────

    /// <summary>
    /// Re-evaluates visibility every time the state machine moves so
    /// that the spinner and labels disappear immediately on transitions
    /// like <c>Pricing</c> to <c>Tracking</c> when the pipeline fails.
    /// </summary>
    private void OnStateChanged(
        OverlayState previous, OverlayState current, OverlayTrigger trigger)
    {
        InvokeOnUi(() => ApplyVisibility());
    }

    // ── Tracker timer ───────────────────────────────────────────────

    /// <summary>
    /// Starts the dispatcher timer that re-checks foreground state and
    /// window bounds while the state machine is active.  Runs on the
    /// UI thread; cheap enough that we always poll, only short-circuit
    /// is the Idle-state guard inside <see cref="OnTrackerTick"/>.
    /// </summary>
    private void StartTrackerTimer()
    {
        _trackerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TrackerInterval,
        };
        _trackerTimer.Tick += (_, _) => OnTrackerTick();
        _trackerTimer.Start();
    }

    /// <summary>
    /// One tick of the tracker.  Refreshes the cached window snapshot
    /// from the live process and then re-applies visibility.  Skips
    /// when the state machine is Idle and the user has not forced
    /// the overlay on.
    /// </summary>
    private void OnTrackerTick()
    {
        if (_disposed) return;

        bool manualShown;
        OverlayState state = _stateMachine.Current;
        lock (_lock) { manualShown = _manualShown; }

        if (state == OverlayState.Idle && !manualShown) return;

        var handle = _processTracker.MainWindowHandle;
        WindowSnapshot? snapshot = handle == nint.Zero
            ? null
            : _windowTracker.TryGetBounds(handle);

        lock (_lock) { _lastSnapshot = snapshot; }

        ApplyVisibility();
    }

    // ── Core visibility logic ───────────────────────────────────────

    /// <summary>
    /// Computes whether the overlay window should currently be visible
    /// based on the four inputs (state, foreground, manual-shown, and
    /// snapshot validity) using <see cref="VisibilityDecision"/>, and
    /// pushes the result to the window.  Always called on the UI thread.
    /// </summary>
    private void ApplyVisibility()
    {
        if (_disposed) return;

        OverlayState state = _stateMachine.Current;

        bool manualShown;
        PipelineResult? result;
        WindowSnapshot? snapshot;
        bool spinnerVisible;
        lock (_lock)
        {
            manualShown = _manualShown;
            result = _lastResult;
            snapshot = _lastSnapshot;
            spinnerVisible = _spinnerVisible;
        }

        bool snapshotValid = snapshot is { IsValid: true };
        bool foreground = snapshotValid
            && _windowTracker.IsForeground(_processTracker.MainWindowHandle);

        bool shouldShow = VisibilityDecision.IsOverlayVisible(
            state, foreground, manualShown, snapshotValid);

        if (!shouldShow)
        {
            HideWindow();
            return;
        }

        // From here on snapshot is non-null and valid.
        var window = snapshot!.Value;

        ShowWindow();
        _window.ApplyWindowGeometry(window);

        // Render the spinner only while the pipeline is still running.
        if (spinnerVisible && state == OverlayState.Pricing)
            _window.ShowSpinner();
        else
            _window.HideSpinner();

        // Render labels when we have a result and we are either in the
        // Displaying state or the user has forced the overlay on.
        if (result is not null && (state == OverlayState.Displaying || manualShown))
            RenderResult(result, window);
        else
            _window.ClearLabels();
    }

    /// <summary>
    /// Pushes the cached pipeline result through the layout helpers to
    /// build a <see cref="PositionedLabel"/> per card and asks the
    /// window to render them.
    ///
    /// <para>
    /// Each label is centered horizontally on its detected card and
    /// positioned <see cref="LabelGapDip"/> DIPs below the card's
    /// bottom edge.  When the label would extend past the bottom of
    /// the overlay window, it is flipped to sit above the card with
    /// the same gap so it always remains on screen.
    /// </para>
    /// </summary>
    private void RenderResult(PipelineResult result, WindowSnapshot window)
    {
        if (window.DpiScaleX <= 0 || window.DpiScaleY <= 0
            || double.IsNaN(window.DpiScaleX) || double.IsNaN(window.DpiScaleY)
            || double.IsInfinity(window.DpiScaleX) || double.IsInfinity(window.DpiScaleY))
        {
            _window.ClearLabels();
            return;
        }

        double opacity = Math.Clamp(_settings.OverlayOpacity, 0.5, 1.0);
        double fontSize = ResolveFontSize(window);

        // Find the most valuable card so its border can be highlighted.
        // Untradeables and unmatched cards never win; only positive
        // platinum prices are considered.
        int bestIndex = FindBestPricedCardIndex(result);

        // Estimated label height (DIPs).  Three-line card (item name +
        // price + detail row) with the padding configured in
        // OverlayWindow.BuildPriceCard.
        double estimatedLabelHeightDip = fontSize * 2.8 + 20;

        var positioned = new List<PositionedLabel>(result.Cards.Count);

        for (int i = 0; i < result.Cards.Count; i++)
        {
            var card = result.Cards[i];
            var rect = card.BoundsInWindow;
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            // Convert card bounds (physical pixels) into DIPs.
            double cardLeftDip = rect.X / window.DpiScaleX;
            double cardTopDip = rect.Y / window.DpiScaleY;
            double cardWidthDip = rect.Width / window.DpiScaleX;
            double cardHeightDip = rect.Height / window.DpiScaleY;
            double cardCenterXDip = cardLeftDip + cardWidthDip / 2.0;
            double cardBottomDip = cardTopDip + cardHeightDip;

            // Place the label below the card; flip above if it would
            // overflow the window bottom.
            double topDip = cardBottomDip + LabelGapDip;
            if (topDip + estimatedLabelHeightDip > window.LogicalHeight)
                topDip = cardTopDip - estimatedLabelHeightDip - LabelGapDip;
            if (topDip < 0) topDip = 0;

            // Card width hint — the visible price card is sized to
            // content, but we cap MaxWidth to the reward card width
            // so a runaway value never spills into a neighbour.
            double maxWidthDip = Math.Max(cardWidthDip, 80);

            // Anchor at the card's horizontal center; the WPF panel
            // centers itself within MaxWidthDip so the visible price
            // ends up exactly under the reward.
            double leftDip = cardCenterXDip - maxWidthDip / 2.0;

            // Clamp horizontally to the window so labels never escape
            // the overlay surface even when a card sits flush against
            // an edge.
            if (leftDip < 0) leftDip = 0;
            if (leftDip + maxWidthDip > window.LogicalWidth)
                leftDip = Math.Max(0, window.LogicalWidth - maxWidthDip);

            positioned.Add(new PositionedLabel(
                LeftDip: leftDip,
                TopDip: topDip,
                MaxWidthDip: maxWidthDip,
                Text: card.DisplayText,
                ItemName: card.MatchedItem?.CanonicalName,
                BuyPrice: card.HighestBuyPrice,
                SellerCount: card.SellerCount,
                IsHighlighted: i == bestIndex));
        }

        _window.RenderLabels(positioned, opacity, fontSize);
    }

    /// <summary>
    /// Returns the index of the card with the strictly-highest
    /// platinum price in the result, or <c>-1</c> when no card has a
    /// usable price (untradeables and unmatched cards are skipped).
    /// </summary>
    private static int FindBestPricedCardIndex(PipelineResult result)
    {
        int bestIndex = -1;
        int bestPrice = int.MinValue;

        for (int i = 0; i < result.Cards.Count; i++)
        {
            var card = result.Cards[i];
            if (card.MatchedItem is null || card.MatchedItem.IsUntradeable) continue;
            if (!card.PricePlatinum.HasValue) continue;

            int p = card.PricePlatinum.Value;
            if (p > bestPrice)
            {
                bestPrice = p;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    /// <summary>
    /// Returns the platinum font size in DIPs, honouring an explicit
    /// override from settings or scaling linearly with window height.
    /// </summary>
    private double ResolveFontSize(WindowSnapshot window)
    {
        if (_settings.PriceFontSizeOverride > 0)
            return _settings.PriceFontSizeOverride;

        double scale = window.LogicalHeight / AutoFontReferenceHeightDip;
        return Math.Clamp(AutoFontReferenceSizeDip * scale, 12.0, 32.0);
    }

    // ── Window show / hide ──────────────────────────────────────────

    /// <summary>
    /// Makes the overlay window visible if it is not already.
    /// </summary>
    private void ShowWindow()
    {
        if (_isVisible) return;
        _window.Show();
        _isVisible = true;
    }

    /// <summary>
    /// Hides the overlay window and clears its content so we never
    /// leak labels from a previous reward screen onto the next one.
    /// </summary>
    private void HideWindow()
    {
        _window.HideSpinner();
        _window.ClearLabels();

        if (!_isVisible) return;
        _window.Hide();
        _isVisible = false;
    }

    // ── Dispatcher helper ───────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="action"/> on the WPF UI thread.  Synchronous
    /// when invoked from the UI thread already; queued via
    /// <see cref="Dispatcher.BeginInvoke(System.Delegate, object[])"/>
    /// otherwise.  Discards the call without throwing if the dispatcher
    /// has already shut down.
    /// </summary>
    private void InvokeOnUi(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            return;

        if (_dispatcher.CheckAccess())
        {
            try { action(); }
            catch (Exception ex) { Debug.WriteLine($"[WpfOverlayOutput] UI action failed: {ex.Message}"); }
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Render);
        }
        catch (TaskCanceledException) { /* dispatcher is shutting down */ }
        catch (OperationCanceledException) { /* dispatcher is shutting down */ }
    }
}
