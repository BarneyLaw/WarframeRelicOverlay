namespace WarframeRelicOverlay.Presentation;

using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Infrastructure.History;
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
/// The view model also owns the settings tab and the history tab
/// of the in-game panel, exposed via
/// <see cref="IsHistoryPanelVisible"/>, <see cref="IsSettingsTabActive"/>,
/// <see cref="CardBackgroundColor"/> and <see cref="HistoryHotkey"/>.
/// </summary>
public sealed class OverlayViewModel : IOverlayOutput, INotifyPropertyChanged
{
    // ── Dependencies ────────────────────────────────────────────────

    private readonly Dispatcher _dispatcher;
    private readonly IWindowTracker _windowTracker;
    private readonly IProcessTracker _processTracker;
    private readonly OverlayStateMachine _stateMachine;
    private readonly IRewardHistoryRecorder? _historyRecorder;
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly ILogger? _logger;
    private Timer? _positionTimer;

    // ── Bindable state ──────────────────────────────────────────────

    private string _statusText = "";
    private bool _isStatusVisible;
    private bool _isLoadingVisible;
    private bool _isOverlayVisible;
    private bool _isHistoryPanelVisible;
    private bool _isSettingsTabActive;
    private string _cardBackgroundColor;
    private string _historyHotkey;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private double _gameOffsetX;
    private double _gameOffsetY;
    private bool _overlayStateActive;
    private bool _geometryApplied;
    private bool _positionLogged;
    private string? _lastLoggedFailure;

    // ── Collections ─────────────────────────────────────────────────

    /// <summary>Price labels positioned over detected reward cards.</summary>
    public ObservableCollection<PriceLabel> PriceLabels { get; } = new();

    /// <summary>History runs displayed in the history tab.</summary>
    public ObservableCollection<HistoryRunViewModel> HistoryRuns { get; } = new();

    // ── Events ──────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the hotkey string changes and the caller must
    /// re-register the Win32 global hotkey with the new combo.
    /// </summary>
    public event Action<string>? HistoryHotkeyChanged;

    /// <summary>
    /// Raised (on the UI thread) with the overlay's target bounds in raw
    /// screen pixels (x, y, width, height).
    /// </summary>
    public event Action<int, int, int, int>? PhysicalBoundsChanged;

    // ── Bindable properties ─────────────────────────────────────────

    /// <summary>Status message shown in the overlay (e.g. "Detecting rewards...").</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    /// <summary>Whether the status text label is currently visible.</summary>
    public bool IsStatusVisible
    {
        get => _isStatusVisible;
        private set => SetField(ref _isStatusVisible, value);
    }

    /// <summary>Whether the loading spinner is currently visible.</summary>
    public bool IsLoadingVisible
    {
        get => _isLoadingVisible;
        private set => SetField(ref _isLoadingVisible, value);
    }

    /// <summary>Whether the overlay window should be shown.</summary>
    public bool IsOverlayVisible
    {
        get => _isOverlayVisible;
        private set => SetField(ref _isOverlayVisible, value);
    }

    /// <summary>
    /// Whether the full-screen panel (history/settings) is currently
    /// visible.  When true, click-through is temporarily disabled.
    /// </summary>
    public bool IsHistoryPanelVisible
    {
        get => _isHistoryPanelVisible;
        private set => SetField(ref _isHistoryPanelVisible, value);
    }

    /// <summary>
    /// True when the Settings tab is active; false when History tab is active.
    /// </summary>
    public bool IsSettingsTabActive
    {
        get => _isSettingsTabActive;
        set => SetField(ref _isSettingsTabActive, value);
    }

    /// <summary>
    /// Background colour of the price card as an ARGB hex string.
    /// Bound bidirectionally to the settings TextBox.
    /// </summary>
    public string CardBackgroundColor
    {
        get => _cardBackgroundColor;
        set
        {
            if (!SetField(ref _cardBackgroundColor, value)) return;
            _settings.CardBackgroundColor = value;
            SaveSettingsAsync();
        }
    }

    /// <summary>
    /// Hotkey combo for toggling the history/settings panel (e.g. "Shift+Tab").
    /// Bound bidirectionally to the settings TextBox.
    /// </summary>
    public string HistoryHotkey
    {
        get => _historyHotkey;
        set
        {
            if (!SetField(ref _historyHotkey, value)) return;
            _settings.HistoryHotkey = value;
            SaveSettingsAsync();
            HistoryHotkeyChanged?.Invoke(value);
        }
    }

    // ── Construction ────────────────────────────────────────────────

    /// <summary>
    /// Creates the ViewModel.  <paramref name="settings"/> and
    /// <paramref name="settingsPath"/> are required to persist
    /// user changes from the Settings tab.
    /// </summary>
    public OverlayViewModel(
        OverlayStateMachine stateMachine,
        IWindowTracker windowTracker,
        IProcessTracker processTracker,
        ILogger? logger = null,
        IRewardHistoryRecorder? historyRecorder = null,
        AppSettings? settings = null,
        string? settingsPath = null)
    {
        _dispatcher = Application.Current.Dispatcher;
        _stateMachine = stateMachine;
        _windowTracker = windowTracker;
        _processTracker = processTracker;
        _logger = logger;
        _historyRecorder = historyRecorder;
        _settings = settings ?? new AppSettings();
        _settingsPath = settingsPath ?? Path.Combine(
            AppContext.BaseDirectory, "data", "settings.json");

        // Initialise mutable bindable fields from current settings.
        _cardBackgroundColor = _settings.CardBackgroundColor;
        _historyHotkey = _settings.HistoryHotkey;

        _stateMachine.StateChanged += OnStateChanged;
    }

    // ── Lifecycle ───────────────────────────────────────────────────

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

    /// <summary>Stops the position polling timer.</summary>
    public void StopPositionTracking()
    {
        _logger?.LogInfo("Position tracking poll stopped.");
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    // ── History / Settings panel ────────────────────────────────────

    /// <summary>
    /// Toggles the full-screen panel.  Opens on the History tab by default.
    /// If already open on History, switches to Settings tab.
    /// If already open on Settings, closes the panel.
    /// </summary>
    public void ToggleHistoryPanel()
    {
        RunOnUi(() =>
        {
            if (!IsHistoryPanelVisible)
            {
                // Open on History tab.
                LoadHistoryRuns();
                IsSettingsTabActive = false;
                IsHistoryPanelVisible = true;
                _logger?.LogInfo("Panel opened (History tab).");
            }
            else
            {
                // Close.
                IsHistoryPanelVisible = false;
                HistoryRuns.Clear();
                _logger?.LogInfo("Panel closed.");
            }
        });
    }

    /// <summary>
    /// Switches to the History tab if the panel is open, or opens the
    /// panel on the History tab.
    /// </summary>
    public void ShowHistoryTab()
    {
        RunOnUi(() =>
        {
            LoadHistoryRuns();
            IsSettingsTabActive = false;
            if (!IsHistoryPanelVisible) IsHistoryPanelVisible = true;
        });
    }

    /// <summary>
    /// Switches to the Settings tab if the panel is open, or opens the
    /// panel on the Settings tab.
    /// </summary>
    public void ShowSettingsTab()
    {
        RunOnUi(() =>
        {
            IsSettingsTabActive = true;
            if (!IsHistoryPanelVisible) IsHistoryPanelVisible = true;
        });
    }

    /// <summary>Loads reward-run records from disk into <see cref="HistoryRuns"/>.</summary>
    private void LoadHistoryRuns()
    {
        HistoryRuns.Clear();
        var records = _historyRecorder?.LoadAll() ?? [];
        for (int i = records.Count - 1; i >= 0; i--)
        {
            var run = records[i];
            var lines = new System.Text.StringBuilder();
            foreach (var item in run.Items)
            {
                string name = item.Name ?? "?";
                string price = item.Price.HasValue ? $"{item.Price.Value}p" : "N/A";
                lines.AppendLine($"{name} — {price}");
            }

            HistoryRuns.Add(new HistoryRunViewModel
            {
                Timestamp = run.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                Items = lines.ToString().TrimEnd(),
            });
        }

        _logger?.LogInfo($"History loaded: {HistoryRuns.Count} run(s).");
    }

    /// <summary>
    /// Saves the current settings to disk asynchronously.  Best-effort —
    /// failures are logged but never surface to the UI.
    /// </summary>
    private void SaveSettingsAsync()
    {
        var settings = _settings;
        var path = _settingsPath;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try { settings.Save(path); }
            catch (Exception ex)
            {
                _logger?.LogError("Failed to save settings.", ex);
            }
        });
    }

    // ── IOverlayOutput ──────────────────────────────────────────────

    /// <summary>
    /// Receives a fresh <see cref="PipelineResult"/> and renders one
    /// price card per detected reward card.
    /// </summary>
    public void ShowPrices(PipelineResult result)
    {
        RunOnUi(() =>
        {
            PriceLabels.Clear();

            foreach (var card in result.Cards)
            {
                double scaleX = _dpiScaleX;
                double scaleY = _dpiScaleY;

                double logicalX = _gameOffsetX + card.BoundsInWindow.X / scaleX;
                double logicalY = _gameOffsetY + card.BoundsInWindow.Y / scaleY;
                double logicalW = card.BoundsInWindow.Width / scaleX;

                const double estimatedHalfWidth = 60;
                double labelLeft = logicalX + (logicalW / 2) - estimatedHalfWidth;
                // Position card below the item name, matching the 60-DIP gap.
                double labelTop = logicalY + (card.BoundsInWindow.Height / scaleY) + 8;

                PriceLabels.Add(new PriceLabel
                {
                    Text = card.DisplayText,
                    ItemName = card.MatchedItem?.CanonicalName,
                    Left = Math.Max(0, labelLeft),
                    Top = Math.Max(0, labelTop),
                    IsUntradeable = card.MatchedItem?.IsUntradeable == true,
                    IsFailed = card.MatchedItem is null,
                    BackgroundColor = _cardBackgroundColor,
                });
            }

            Debug.WriteLine($"[OverlayVM] Showing {PriceLabels.Count} price label(s).");
        });
    }

    /// <inheritdoc />
    public void ClearPrices()
    {
        RunOnUi(() =>
        {
            PriceLabels.Clear();
            Debug.WriteLine("[OverlayVM] Prices cleared.");
        });
    }

    /// <inheritdoc />
    public void ShowLoading()
    {
        RunOnUi(() => IsLoadingVisible = true);
    }

    /// <inheritdoc />
    public void HideLoading()
    {
        RunOnUi(() => IsLoadingVisible = false);
    }

    // ── Forced geometry (debug simulator) ──────────────────────────

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

    // ── State machine observation ───────────────────────────────────

    /// <summary>Reacts to state machine transitions by updating status text and overlay visibility.</summary>
    private void OnStateChanged(
        OverlayState previous, OverlayState current, OverlayTrigger trigger)
    {
        _logger?.LogInfo($"State: {previous} -> {current} (trigger: {trigger}).");
        RunOnUi(() =>
        {
            UpdateStatusForState(current);
            _overlayStateActive = current != OverlayState.Idle;
            ApplyOverlayVisibility();
        });
    }

    /// <summary>Applies the combined overlay visibility based on state and focus.</summary>
    private void ApplyOverlayVisibility() =>
        IsOverlayVisible = _overlayStateActive;

    /// <summary>Updates the game-focus state and re-evaluates overlay visibility.</summary>
    private void SetGameFocus(bool focused)
    {
        if (_gameHasFocus == focused) return;
        _gameHasFocus = focused;
        _logger?.LogInfo(
            $"Warframe focus changed: focused={focused}.");
        RunOnUi(ApplyOverlayVisibility);
    }

    private bool _gameHasFocus = true;

    /// <summary>Updates the status text and visibility based on the current overlay state.</summary>
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

    /// <summary>
    /// Timer callback that polls the Warframe window position and pushes
    /// updated bounds to the overlay via <see cref="PhysicalBoundsChanged"/>.
    /// </summary>
    private void UpdateWindowPosition(object? _)
    {
        var handle = _processTracker.MainWindowHandle;
        if (handle == nint.Zero)
        {
            SetGameFocus(false);
            LogPositionFailure("MainWindowHandle is zero");
            return;
        }

        SetGameFocus(_windowTracker.IsForeground(handle));

        var bounds = _windowTracker.TryGetBounds(handle);
        bool usingMonitorFallback = false;
        if (bounds is null || !bounds.Value.IsValid)
        {
            bounds = _windowTracker.TryGetMonitorBounds(handle);
            usingMonitorFallback = bounds is { } fb && fb.IsValid;
        }

        if (bounds is null || !bounds.Value.IsValid)
        {
            LogPositionFailure($"No valid bounds for 0x{handle:X}");
            return;
        }

        var target = bounds.Value;

        if (!_positionLogged || _lastLoggedFailure is not null)
        {
            string msg =
                $"Position acquired ({(usingMonitorFallback ? "monitor" : "client")}): " +
                $"{target.LogicalWidth}x{target.LogicalHeight} " +
                $"@ ({target.LogicalX},{target.LogicalY}), " +
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
                _dpiScaleX = target.DpiScaleX;
                _dpiScaleY = target.DpiScaleY;
                _gameOffsetX = 0;
                _gameOffsetY = 0;

                PhysicalBoundsChanged?.Invoke(
                    target.ClientX, target.ClientY,
                    target.ClientWidth, target.ClientHeight);

                if (firstApply)
                {
                    _geometryApplied = true;
                    _logger?.LogInfo(
                        $"Geometry applied: {target.ClientWidth}x{target.ClientHeight} " +
                        $"@ ({target.ClientX},{target.ClientY}).");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError("Failed to apply overlay geometry.", ex);
            }
        });
    }

    /// <summary>Logs a position-failure reason once, suppressing duplicate messages.</summary>
    private void LogPositionFailure(string reason)
    {
        if (_lastLoggedFailure == reason) return;
        _lastLoggedFailure = reason;
        _positionLogged = false;
        string msg = $"[OverlayVM] Overlay not positioned: {reason}.";
        _logger?.LogWarning(msg);
        Debug.WriteLine(msg);
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/> for the given property name.</summary>
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Sets a backing field and raises <see cref="PropertyChanged"/> when the value changes.
    /// Returns <c>true</c> if the value was changed.
    /// </summary>
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>Dispatches an action to the UI thread, executing inline if already on it.</summary>
    private void RunOnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.BeginInvoke(action);
    }
}

// ── Presentation models ─────────────────────────────────────────────

/// <summary>
/// Data object for a single price label positioned over a reward card.
/// </summary>
public sealed class PriceLabel : INotifyPropertyChanged
{
    /// <summary>Display text (e.g. "45p", "N/A", "?").</summary>
    public required string Text { get; init; }

    /// <summary>Matched canonical item name, or null if unmatched.</summary>
    public string? ItemName { get; init; }

    /// <summary>Left edge of the label in logical (DIP) units.</summary>
    public required double Left { get; init; }

    /// <summary>Top edge of the label in logical (DIP) units.</summary>
    public required double Top { get; init; }

    /// <summary>True if the item is untradeable (e.g. Forma).</summary>
    public bool IsUntradeable { get; init; }

    /// <summary>True if the fuzzy matcher failed to identify the item.</summary>
    public bool IsFailed { get; init; }

    /// <summary>
    /// ARGB hex colour for the card background, sourced from
    /// <see cref="AppSettings.CardBackgroundColor"/>.
    /// </summary>
    public string BackgroundColor { get; init; } = "#EE181410";

    /// <summary>Not used; required by the <see cref="INotifyPropertyChanged"/> interface.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// View model for a single reward-run entry in the history tab.
/// </summary>
public sealed class HistoryRunViewModel
{
    /// <summary>Formatted timestamp (e.g. "2025-06-15 14:32").</summary>
    public required string Timestamp { get; init; }

    /// <summary>
    /// Multi-line string with one item per line:
    /// "Item Name — Xp" or "Item Name — N/A".
    /// </summary>
    public required string Items { get; init; }
}
