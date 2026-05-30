namespace WarframeRelicOverlay.Presentation;

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Infrastructure.Logging;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.OverlayApp.Pipeline;
using WarframeRelicOverlay.OverlayApp.StateMachine;

/// <summary>
/// WPF ViewModel that implements <see cref="IOverlayOutput"/> so the
/// <see cref="OverlayCoordinator"/> can push state updates from
/// background threads.  All property changes are dispatched to the
/// UI thread automatically.
///
/// The view model exposes:
///   - A collection of <see cref="PriceLabel"/> items for positioning
///     price text over detected reward cards.
///   - Status text and visibility flags driven by the overlay state
///     machine.
///   - Window geometry (Left, Top, Width, Height) for positioning the
///     overlay over the Warframe client area.
/// </summary>
public sealed class OverlayViewModel : IOverlayOutput, INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private readonly IWindowTracker _windowTracker;
    private readonly IProcessTracker _processTracker;
    private readonly OverlayStateMachine _stateMachine;
    private readonly ILogger? _logger;
    private Timer? _positionTimer;

    // ── Bindable state ──────────────────────────────────────────────

    private string _statusText = "";
    private bool _isStatusVisible;
    private bool _isLoadingVisible;
    private bool _isOverlayVisible;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private double _gameOffsetX;
    private double _gameOffsetY;

    /// <summary>Price labels positioned over detected reward cards.</summary>
    public ObservableCollection<PriceLabel> PriceLabels { get; } = new();

    /// <summary>
    /// Raised (on the UI thread) with the overlay's target bounds in raw
    /// screen pixels (x, y, width, height). The view subscribes and calls
    /// <c>SetWindowPos</c>; pixels avoid WPF's unreliable per-monitor DIP
    /// conversion.
    /// </summary>
    public event Action<int, int, int, int>? PhysicalBoundsChanged;

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public bool IsStatusVisible
    {
        get => _isStatusVisible;
        private set => SetField(ref _isStatusVisible, value);
    }

    public bool IsLoadingVisible
    {
        get => _isLoadingVisible;
        private set => SetField(ref _isLoadingVisible, value);
    }

    public bool IsOverlayVisible
    {
        get => _isOverlayVisible;
        private set => SetField(ref _isOverlayVisible, value);
    }

    // ── Construction ────────────────────────────────────────────────

    public OverlayViewModel(
        OverlayStateMachine stateMachine,
        IWindowTracker windowTracker,
        IProcessTracker processTracker,
        ILogger? logger = null)
    {
        _dispatcher = Application.Current.Dispatcher;
        _stateMachine = stateMachine;
        _windowTracker = windowTracker;
        _processTracker = processTracker;
        _logger = logger;

        _stateMachine.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Start polling the Warframe window position so the overlay tracks it.
    /// </summary>
    public void StartPositionTracking()
    {
        _logger?.LogInfo("Position tracking poll started (100 ms interval).");
        _positionTimer?.Dispose();
        _positionTimer = new Timer(UpdateWindowPosition, null,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
    }

    public void StopPositionTracking()
    {
        _logger?.LogInfo("Position tracking poll stopped.");
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    /// <summary>
    /// Forces the overlay to a specific screen region (used by
    /// <see cref="DebugSimulator"/> when no Warframe window exists).
    /// </summary>
    public void ForceWindowGeometry(
        double left, double top, double width, double height)
    {
        RunOnUi(() =>
        {
            PhysicalBoundsChanged?.Invoke(
                (int)left, (int)top, (int)width, (int)height);
            IsOverlayVisible = true;
        });
    }

    // ── IOverlayOutput ──────────────────────────────────────────────

    public void ShowPrices(PipelineResult result)
    {
        RunOnUi(() =>
        {
            PriceLabels.Clear();

            foreach (var card in result.Cards)
            {
                // Convert physical-pixel card bounds to WPF logical units
                // using the DPI scale cached from Warframe's monitor.
                double scaleX = _dpiScaleX;
                double scaleY = _dpiScaleY;

                double logicalX = _gameOffsetX + card.BoundsInWindow.X / scaleX;
                double logicalY = _gameOffsetY + card.BoundsInWindow.Y / scaleY;
                double logicalW = card.BoundsInWindow.Width / scaleX;

                // Position the label centered horizontally over the card,
                // slightly above the card's top edge.  Estimate ~40 DIP
                // half-width for the label container; refined once the
                // full settings UI allows font size overrides.
                const double estimatedHalfWidth = 40;
                double labelLeft = logicalX + (logicalW / 2) - estimatedHalfWidth;
                double labelTop = logicalY - 35;

                PriceLabels.Add(new PriceLabel
                {
                    Text = card.DisplayText,
                    Left = Math.Max(0, labelLeft),
                    Top = Math.Max(0, labelTop),
                    IsUntradeable = card.MatchedItem?.IsUntradeable == true,
                    IsFailed = card.MatchedItem is null,
                });
            }

            Debug.WriteLine(
                $"[OverlayVM] Showing {PriceLabels.Count} price label(s).");
        });
    }

    public void ClearPrices()
    {
        RunOnUi(() =>
        {
            PriceLabels.Clear();
            Debug.WriteLine("[OverlayVM] Prices cleared.");
        });
    }

    public void ShowLoading()
    {
        RunOnUi(() => IsLoadingVisible = true);
    }

    public void HideLoading()
    {
        RunOnUi(() => IsLoadingVisible = false);
    }

    // ── State machine observation ───────────────────────────────────

    private void OnStateChanged(
        OverlayState previous, OverlayState current, OverlayTrigger trigger)
    {
        _logger?.LogInfo($"State: {previous} -> {current} (trigger: {trigger}).");
        RunOnUi(() =>
        {
            UpdateStatusForState(current);
            IsOverlayVisible = current != OverlayState.Idle;
        });
    }

    private void UpdateStatusForState(OverlayState state)
    {
        switch (state)
        {
            case OverlayState.Idle:
                IsStatusVisible = false;
                StatusText = "";
                break;

            case OverlayState.Tracking:
                IsStatusVisible = true;
                StatusText = "Detecting rewards...";
                break;

            case OverlayState.Detecting:
                IsStatusVisible = true;
                StatusText = "Confirming...";
                break;

            case OverlayState.Pricing:
                IsStatusVisible = true;
                StatusText = "Fetching prices...";
                break;

            case OverlayState.Displaying:
                IsStatusVisible = false;
                StatusText = "";
                break;
        }
    }

    // ── Window position tracking ────────────────────────────────────

    private void UpdateWindowPosition(object? _)
    {
        var handle = _processTracker.MainWindowHandle;
        if (handle == nint.Zero)
        {
            LogPositionFailure("MainWindowHandle is zero — process not attached or window not yet created");
            return;
        }

        // Track Warframe's render surface, not the full monitor. In
        // borderless/fullscreen these are usually the same, but in
        // windowed modes the client area is smaller and can move.
        var bounds = _windowTracker.TryGetBounds(handle);
        bool usingMonitorFallback = false;
        if (bounds is null || !bounds.Value.IsValid)
        {
            bounds = _windowTracker.TryGetMonitorBounds(handle);
            usingMonitorFallback = bounds is { } fallback && fallback.IsValid;
        }

        if (bounds is null || !bounds.Value.IsValid)
        {
            LogPositionFailure($"No valid Warframe bounds for handle 0x{handle:X}");
            return;
        }

        var target = bounds.Value;

        // Log the first successful resolution (and any recovery after a
        // failure), but not every 100 ms tick.
        if (!_positionLogged || _lastLoggedFailure is not null)
        {
            string msg =
                $"Position acquired: {(usingMonitorFallback ? "monitor fallback" : "client")} " +
                $"{target.LogicalWidth}x{target.LogicalHeight} " +
                $"@ ({target.LogicalX},{target.LogicalY}), handle 0x{handle:X}, " +
                $"DPI {target.DpiScaleX:0.##}x{target.DpiScaleY:0.##}.";
            _logger?.LogInfo(msg);
            Debug.WriteLine($"[OverlayVM] {msg}");
            _lastLoggedFailure = null;
            _positionLogged = true;
        }

        bool firstApply = !_geometryApplied;
        RunOnUi(() =>
        {
            try
            {
                // Card bounds are relative to the captured Warframe
                // window, and the overlay is anchored to that same window.
                _dpiScaleX   = target.DpiScaleX;
                _dpiScaleY   = target.DpiScaleY;
                _gameOffsetX = 0;
                _gameOffsetY = 0;

                // Drive the window in raw screen pixels (physical), not DIPs.
                PhysicalBoundsChanged?.Invoke(
                    target.ClientX, target.ClientY, target.ClientWidth, target.ClientHeight);

                if (firstApply)
                {
                    _geometryApplied = true;
                    _logger?.LogInfo(
                        $"Geometry applied: physical bounds {target.ClientWidth}x{target.ClientHeight} " +
                        $"@ ({target.ClientX},{target.ClientY}).");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Failed to apply overlay geometry on UI thread.", ex);
            }
        });
    }

    // Logs a position-tracking failure only when the reason changes, so
    // the 10 Hz timer doesn't flood the output with identical lines.
    private string? _lastLoggedFailure;
    private bool _positionLogged;
    private bool _geometryApplied;

    private void LogPositionFailure(string reason)
    {
        if (_lastLoggedFailure == reason) return;
        _lastLoggedFailure = reason;
        _positionLogged = false; // log the next success as a recovery
        string msg = $"[OverlayVM] Overlay not positioned: {reason}.";
        _logger?.LogWarning(msg);
        Debug.WriteLine(msg);
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }
}

/// <summary>
/// Data object for a single price label positioned over a reward card.
/// </summary>
public sealed class PriceLabel : INotifyPropertyChanged
{
    /// <summary>Display text (e.g. "45p", "Untradeable", "?").</summary>
    public required string Text { get; init; }

    /// <summary>Left edge of the label in logical (DIP) units.</summary>
    public required double Left { get; init; }

    /// <summary>Top edge of the label in logical (DIP) units.</summary>
    public required double Top { get; init; }

    /// <summary>True if the item is untradeable (e.g. Forma).</summary>
    public bool IsUntradeable { get; init; }

    /// <summary>True if the fuzzy matcher failed to identify the item.</summary>
    public bool IsFailed { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
