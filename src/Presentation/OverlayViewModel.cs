namespace WarframeRelicOverlay.Presentation;

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WarframeRelicOverlay.Core;
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
    private Timer? _positionTimer;
    private Window? _overlayWindow;

    // ── Bindable state ──────────────────────────────────────────────

    private string _statusText = "";
    private bool _isStatusVisible;
    private bool _isLoadingVisible;
    private bool _isOverlayVisible;
    private double _windowLeft;
    private double _windowTop;
    private double _windowWidth = 800;
    private double _windowHeight = 600;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    /// <summary>Price labels positioned over detected reward cards.</summary>
    public ObservableCollection<PriceLabel> PriceLabels { get; } = new();

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

    public double WindowLeft
    {
        get => _windowLeft;
        private set => SetField(ref _windowLeft, value);
    }

    public double WindowTop
    {
        get => _windowTop;
        private set => SetField(ref _windowTop, value);
    }

    public double WindowWidth
    {
        get => _windowWidth;
        private set => SetField(ref _windowWidth, value);
    }

    public double WindowHeight
    {
        get => _windowHeight;
        private set => SetField(ref _windowHeight, value);
    }

    // ── Construction ────────────────────────────────────────────────

    public OverlayViewModel(
        OverlayStateMachine stateMachine,
        IWindowTracker windowTracker,
        IProcessTracker processTracker)
    {
        _dispatcher = Application.Current.Dispatcher;
        _stateMachine = stateMachine;
        _windowTracker = windowTracker;
        _processTracker = processTracker;

        _stateMachine.StateChanged += OnStateChanged;
    }

    /// <summary>
    /// Start polling the Warframe window position so the overlay tracks it.
    /// The <paramref name="overlayWindow"/> is used to obtain WPF's own
    /// device-to-logical DPI transform, which avoids mismatches between
    /// Win32 <c>GetDpiForMonitor</c> and WPF's coordinate system.
    /// </summary>
    public void StartPositionTracking(Window overlayWindow)
    {
        _overlayWindow = overlayWindow;
        _positionTimer?.Dispose();
        _positionTimer = new Timer(UpdateWindowPosition, null,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
    }

    public void StopPositionTracking()
    {
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
            WindowLeft = left;
            WindowTop = top;
            WindowWidth = width;
            WindowHeight = height;
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
                // using the cached WPF DPI scale (from PresentationSource,
                // not from Win32 GetDpiForMonitor — they can differ).
                double scaleX = _dpiScaleX;
                double scaleY = _dpiScaleY;

                double logicalX = card.BoundsInWindow.X / scaleX;
                double logicalY = card.BoundsInWindow.Y / scaleY;
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
        if (handle == nint.Zero) return;

        // Get the physical pixel bounds from Win32 (thread-safe).
        var snapshot = _windowTracker.TryGetBounds(handle);
        if (snapshot is null || !snapshot.Value.IsValid) return;

        var w = snapshot.Value;

        // Convert physical pixels → WPF logical units on the UI
        // thread using WPF's own transform.  This avoids the mismatch
        // between GetDpiForMonitor and WPF's internal DPI handling
        // (which differs in Per-Monitor DPI Aware v2 mode and on
        // multi-monitor setups).
        RunOnUi(() =>
        {
            var source = _overlayWindow is not null
                ? PresentationSource.FromVisual(_overlayWindow)
                : null;

            if (source?.CompositionTarget is not null)
            {
                var transform = source.CompositionTarget.TransformFromDevice;

                // TransformFromDevice.M11 = 1/dpiScaleX, M22 = 1/dpiScaleY
                WindowLeft   = w.ClientX     * transform.M11;
                WindowTop    = w.ClientY     * transform.M22;
                WindowWidth  = w.ClientWidth * transform.M11;
                WindowHeight = w.ClientHeight * transform.M22;

                // Cache the WPF-correct scale factors for ShowPrices
                _dpiScaleX = 1.0 / transform.M11;
                _dpiScaleY = 1.0 / transform.M22;
            }
            else
            {
                // Fallback: use the Win32 DPI values (better than nothing)
                WindowLeft   = w.LogicalX;
                WindowTop    = w.LogicalY;
                WindowWidth  = w.LogicalWidth;
                WindowHeight = w.LogicalHeight;

                _dpiScaleX = w.DpiScaleX;
                _dpiScaleY = w.DpiScaleY;
            }
        });
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