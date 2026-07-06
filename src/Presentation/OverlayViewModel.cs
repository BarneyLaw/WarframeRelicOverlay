namespace WarframeRelicOverlay.Presentation;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
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
    private string _priceDisplay;
    private double _overlayOpacity;
    private int _priceFontSize;
    private bool _isAutoFontSize;
    private bool _isHotkeyListening;
    private int _selectedSwatchIndex;
    private int _showTopPrices;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private double _gameOffsetX;
    private double _gameOffsetY;
    private bool _overlayStateActive;
    private bool _geometryApplied;
    private bool _positionLogged;
    private string? _lastLoggedFailure;

    // ── Colour swatch presets ───────────────────────────────────────

    /// <summary>Preset card background colour swatches.</summary>
    public static readonly string[] ColourSwatches =
    [
        "#EE181410",  // Dark Charcoal (default)
        "#80181410",  // Transparent
        "#EE2A2015",  // Warm Brown
        "#EE101828",  // Deep Blue
    ];

    // ── Collections ─────────────────────────────────────────────────

    /// <summary>Price labels positioned over detected reward cards.</summary>
    public ObservableCollection<PriceLabel> PriceLabels { get; } = new();

    /// <summary>History runs displayed in the history tab.</summary>
    public ObservableCollection<HistoryRunViewModel> HistoryRuns { get; } = new();

    /// <summary>
    /// The Warframe main window handle, used to return foreground
    /// focus after closing the history/settings panel.
    /// </summary>
    public nint WarframeWindowHandle => _processTracker.MainWindowHandle;

    // ── Events ──────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the hotkey string changes and the caller must
    /// re-register the Win32 global hotkey with the new combo.
    /// </summary>
    public event Action<string>? HistoryHotkeyChanged;

    /// <summary>
    /// Raised when the history/settings panel visibility changes.
    /// The bool indicates whether the panel is now visible.
    /// Used by the window to toggle click-through (interactive mode).
    /// </summary>
    public event Action<bool>? PanelVisibilityChanged;

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
        private set
        {
            if (!SetField(ref _isHistoryPanelVisible, value)) return;
            PanelVisibilityChanged?.Invoke(value);
        }
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
            // Update swatch selection to reflect new colour.
            SelectedSwatchIndex = Array.IndexOf(ColourSwatches, value);
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
            OnPropertyChanged(nameof(HotkeyDisplayText));
            SaveSettingsAsync();
            HistoryHotkeyChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Which price to display: "Sell", "Buy", or "Both".
    /// </summary>
    public string PriceDisplay
    {
        get => _priceDisplay;
        set
        {
            if (!SetField(ref _priceDisplay, value)) return;
            _settings.PriceDisplay = value;
            SaveSettingsAsync();
        }
    }

    // ── Opacity slider ──────────────────────────────────────────────

    /// <summary>
    /// Overlay opacity value (range 0.5–1.0). Bound to the opacity slider.
    /// </summary>
    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set
        {
            double clamped = Math.Clamp(value, 0.5, 1.0);
            if (!SetField(ref _overlayOpacity, clamped)) return;
            _settings.OverlayOpacity = clamped;
            OnPropertyChanged(nameof(OpacityPercentLabel));
            SaveSettingsAsync();
        }
    }

    /// <summary>Formatted opacity percentage for display (e.g. "85%").</summary>
    public string OpacityPercentLabel => $"{(int)(_overlayOpacity * 100)}%";

    // ── Font size slider ────────────────────────────────────────────

    /// <summary>
    /// Price font size override (12–32). When 0, auto-scaling is used.
    /// </summary>
    public int PriceFontSize
    {
        get => _priceFontSize;
        set
        {
            int clamped = _isAutoFontSize ? 0 : Math.Clamp(value, 12, 32);
            if (!SetField(ref _priceFontSize, clamped)) return;
            _settings.PriceFontSizeOverride = clamped;
            OnPropertyChanged(nameof(FontSizeLabel));
            OnPropertyChanged(nameof(PreviewFontSize));
            SaveSettingsAsync();
        }
    }

    /// <summary>
    /// When true, font size is determined automatically (PriceFontSizeOverride = 0).
    /// </summary>
    public bool IsAutoFontSize
    {
        get => _isAutoFontSize;
        set
        {
            if (!SetField(ref _isAutoFontSize, value)) return;
            if (value)
            {
                _priceFontSize = 0;
                _settings.PriceFontSizeOverride = 0;
                OnPropertyChanged(nameof(PriceFontSize));
                OnPropertyChanged(nameof(FontSizeLabel));
                OnPropertyChanged(nameof(PreviewFontSize));
                SaveSettingsAsync();
            }
            else if (_priceFontSize == 0)
            {
                // Restore to a sensible default when unchecking Auto.
                PriceFontSize = 18;
            }
        }
    }

    /// <summary>Formatted font size label (e.g. "18px" or "Auto").</summary>
    public string FontSizeLabel => _isAutoFontSize ? "Auto" : $"{_priceFontSize}px";

    /// <summary>Font size for the preview sample (returns 18 when Auto is active).</summary>
    public int PreviewFontSize => _isAutoFontSize ? 18 : Math.Max(12, _priceFontSize);

    // ── Hotkey recorder ─────────────────────────────────────────────

    /// <summary>
    /// Whether the hotkey recorder is in listening mode.
    /// </summary>
    public bool IsHotkeyListening
    {
        get => _isHotkeyListening;
        set => SetField(ref _isHotkeyListening, value);
    }

    /// <summary>
    /// Formatted display of the current hotkey (e.g. "Ctrl + Tab").
    /// </summary>
    public string HotkeyDisplayText =>
        _historyHotkey.Replace("+", " + ");

    /// <summary>Command to start hotkey listening mode.</summary>
    public ICommand StartHotkeyListeningCommand { get; private set; } = null!;

    // ── Colour swatch selection ─────────────────────────────────────

    /// <summary>
    /// Index of the currently selected swatch (-1 if custom hex is active).
    /// </summary>
    public int SelectedSwatchIndex
    {
        get => _selectedSwatchIndex;
        set => SetField(ref _selectedSwatchIndex, value);
    }

    /// <summary>Command to select a colour swatch by index.</summary>
    public ICommand SelectSwatchCommand { get; private set; } = null!;

    /// <summary>Command to set the price display mode.</summary>
    public ICommand SetPriceDisplayCommand { get; private set; } = null!;

    /// <summary>
    /// How many seller prices to show on the reward screen overlay (1 or 5).
    /// </summary>
    public int ShowTopPrices
    {
        get => _showTopPrices;
        set
        {
            int val = value is 1 or 5 ? value : 1;
            if (!SetField(ref _showTopPrices, val)) return;
            _settings.ShowTopPrices = val;
            SaveSettingsAsync();
        }
    }

    /// <summary>Command to set the ShowTopPrices value.</summary>
    public ICommand SetShowTopPricesCommand { get; private set; } = null!;

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
        _priceDisplay = _settings.PriceDisplay;
        _overlayOpacity = _settings.OverlayOpacity;
        _priceFontSize = _settings.PriceFontSizeOverride;
        _isAutoFontSize = _settings.PriceFontSizeOverride == 0;
        _showTopPrices = _settings.ShowTopPrices is 1 or 5 ? _settings.ShowTopPrices : 1;

        // Compute initial swatch selection.
        _selectedSwatchIndex = Array.IndexOf(ColourSwatches, _cardBackgroundColor);

        // Initialise commands.
        SetPriceDisplayCommand = new RelayCommand(param =>
        {
            if (param is string mode && mode is "Sell" or "Buy" or "Both")
                PriceDisplay = mode;
        });

        SelectSwatchCommand = new RelayCommand(param =>
        {
            if (param is string indexStr && int.TryParse(indexStr, out int idx)
                && idx >= 0 && idx < ColourSwatches.Length)
            {
                SelectedSwatchIndex = idx;
                CardBackgroundColor = ColourSwatches[idx];
            }
        });

        StartHotkeyListeningCommand = new RelayCommand(_ =>
        {
            IsHotkeyListening = true;
        });

        SetShowTopPricesCommand = new RelayCommand(param =>
        {
            if (param is string val && int.TryParse(val, out int count))
                ShowTopPrices = count;
        });

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

            // Skip runs where every item is unmatched ("?") or has no price ("N/A").
            bool hasAnyUsefulItem = false;
            foreach (var item in run.Items)
            {
                if (item.Price.HasValue || (item.Name is not null && item.Name != "?"))
                {
                    hasAnyUsefulItem = true;
                    break;
                }
            }
            if (!hasAnyUsefulItem) continue;

            var items = new ObservableCollection<HistoryItemViewModel>();

            foreach (var item in run.Items)
            {
                items.Add(new HistoryItemViewModel
                {
                    Name = item.Name ?? "?",
                    Price = item.Price,
                });
            }

            HistoryRuns.Add(new HistoryRunViewModel
            {
                Timestamp = run.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                Items = items,
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

            string mode = _priceDisplay;

            // Determine the best-priced card index for highlighting.
            int bestIndex = -1;
            int bestPrice = -1;
            for (int i = 0; i < result.Cards.Count; i++)
            {
                var c = result.Cards[i];
                if (c.MatchedItem is null || c.MatchedItem.IsUntradeable) continue;
                int p = c.PricePlatinum ?? 0;
                if (p > bestPrice) { bestPrice = p; bestIndex = i; }
            }

            for (int i = 0; i < result.Cards.Count; i++)
            {
                var card = result.Cards[i];
                double scaleX = _dpiScaleX;
                double scaleY = _dpiScaleY;

                double logicalX = _gameOffsetX + card.BoundsInWindow.X / scaleX;
                double logicalY = _gameOffsetY + card.BoundsInWindow.Y / scaleY;
                double logicalW = card.BoundsInWindow.Width / scaleX;
                double logicalH = card.BoundsInWindow.Height / scaleY;

                // Center the price label horizontally under the game card.
                // Set Left to the card's left edge; MaxWidth to the card's width.
                // The XAML template uses HorizontalAlignment="Center" inside
                // a container of MaxWidth, so the label content is centered.
                double labelLeft = logicalX;

                // Position the overlay card well below the game's reward UI.
                // The detected card bounds cover only the item-name text row
                // (~30-35% of window height). Below that sits the player-name
                // row (~38-42%) and the selection timer. We push the overlay
                // card to ~60% of the window height from the top to ensure it
                // sits clearly below all in-game elements, in the dark area
                // between the reward cards and the bottom HUD.
                double windowLogicalHeight = result.Window.LogicalHeight;
                double safeOffsetBelowCard = Math.Max(logicalH * 1.8, windowLogicalHeight * 0.12);
                double labelTop = logicalY + logicalH + safeOffsetBelowCard;

                // Clamp so the card doesn't go off-screen at the bottom.
                // Estimated card height ~100 DIPs.
                double estimatedCardHeight = 100;
                if (labelTop + estimatedCardHeight > windowLogicalHeight)
                    labelTop = windowLogicalHeight - estimatedCardHeight - 4;

                // Build the primary display text based on price mode.
                string displayText = BuildDisplayText(card, mode);

                PriceLabels.Add(new PriceLabel
                {
                    Text = displayText,
                    ItemName = card.MatchedItem?.CanonicalName,
                    Left = Math.Max(0, labelLeft),
                    Top = Math.Max(0, labelTop),
                    MaxWidth = Math.Max(80, logicalW),
                    IsUntradeable = card.MatchedItem?.IsUntradeable == true,
                    IsFailed = card.MatchedItem is null,
                    IsHighlighted = i == bestIndex && bestPrice > 0,
                    BackgroundColor = _cardBackgroundColor,
                    TopSellPrices = _showTopPrices == 5 ? card.TopSellPrices : [],
                    TopBuyPrices = _showTopPrices == 5 ? card.TopBuyPrices : [],
                    BuyPrice = card.HighestBuyPrice,
                    SellerCount = card.SellerCount,
                    PriceDisplayMode = mode,
                });
            }

            Debug.WriteLine($"[OverlayVM] Showing {PriceLabels.Count} price label(s).");
        });
    }

    /// <summary>
    /// Builds the primary display text for a card based on the price display mode.
    /// Uses clear labels so users know exactly what price they're looking at.
    /// In "Both" mode the sell price is primary and the buy price appears in the detail row.
    /// </summary>
    private static string BuildDisplayText(CardResult card, string mode)
    {
        if (card.MatchedItem is null) return "?";
        if (card.MatchedItem.IsUntradeable) return "N/A";

        return mode switch
        {
            "Buy" => card.HighestBuyPrice.HasValue
                ? $"Buyers pay: {card.HighestBuyPrice.Value}◆"
                : "No buyers",
            "Both" => card.PricePlatinum.HasValue
                ? $"Sells for: {card.PricePlatinum.Value}◆"
                : "No sellers",
            // "Sell" (default)
            _ => card.PricePlatinum.HasValue
                ? $"Sells for: {card.PricePlatinum.Value}◆"
                : "No sellers",
        };
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
            // Auto-close the history/settings panel when a reward is
            // confirmed so the GDI screen capture sees the game, not
            // our overlay panel.
            if ((current == OverlayState.Pricing || current == OverlayState.Detecting)
                && IsHistoryPanelVisible)
            {
                IsHistoryPanelVisible = false;
                HistoryRuns.Clear();
                _logger?.LogInfo("Panel auto-closed for reward detection/pricing.");
            }

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

    // ── Hotkey capture ─────────────────────────────────────────────

    /// <summary>
    /// Called from the code-behind when a key chord is pressed while
    /// <see cref="IsHotkeyListening"/> is true. Formats the chord and
    /// applies it as the new hotkey.
    /// </summary>
    public void CaptureHotkey(ModifierKeys modifiers, Key key)
    {
        if (!IsHotkeyListening) return;

        // Escape cancels.
        if (key == Key.Escape)
        {
            IsHotkeyListening = false;
            return;
        }

        // Ignore lone modifier keys.
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin
            or Key.System)
            return;

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());

        HistoryHotkey = string.Join("+", parts);
        IsHotkeyListening = false;
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
    /// <summary>Display text (e.g. "Sell: 45◆", "Buy: 38◆", "N/A", "?").</summary>
    public required string Text { get; init; }

    /// <summary>Matched canonical item name, or null if unmatched.</summary>
    public string? ItemName { get; init; }

    /// <summary>Left edge of the label in logical (DIP) units.</summary>
    public required double Left { get; init; }

    /// <summary>Top edge of the label in logical (DIP) units.</summary>
    public required double Top { get; init; }

    /// <summary>
    /// Width of the game's reward card in DIPs. The price label is
    /// constrained to this width and centered within it so it aligns
    /// with the in-game card above.
    /// </summary>
    public double MaxWidth { get; init; }

    /// <summary>True if the item is untradeable (e.g. Forma).</summary>
    public bool IsUntradeable { get; init; }

    /// <summary>True if the fuzzy matcher failed to identify the item.</summary>
    public bool IsFailed { get; init; }

    /// <summary>
    /// True when this card is the most valuable in the current result set.
    /// Renders with a brighter gold border and a "★ Best Pick" badge.
    /// </summary>
    public bool IsHighlighted { get; init; }

    /// <summary>
    /// ARGB hex colour for the card background, sourced from
    /// <see cref="AppSettings.CardBackgroundColor"/>.
    /// </summary>
    public string BackgroundColor { get; init; } = "#EE181410";

    /// <summary>
    /// Up to 5 lowest sell prices for display in Top 5 mode.
    /// Empty in Top 1 mode.
    /// </summary>
    public IReadOnlyList<int> TopSellPrices { get; init; } = [];

    /// <summary>
    /// Up to 5 highest buy prices for display in Top 5 mode.
    /// Empty in Top 1 mode.
    /// </summary>
    public IReadOnlyList<int> TopBuyPrices { get; init; } = [];

    /// <summary>
    /// Highest buy offer in platinum, or null if unavailable.
    /// Shown when price display mode is "Both".
    /// </summary>
    public int? BuyPrice { get; init; }

    /// <summary>
    /// Number of in-game sellers at or near the lowest sell price.
    /// </summary>
    public int SellerCount { get; init; }

    /// <summary>Whether there are sellers to display.</summary>
    public bool HasSellers => SellerCount > 0 && !IsUntradeable && !IsFailed;

    /// <summary>Formatted seller count text (e.g. "3 sellers online").</summary>
    public string SellerCountText => SellerCount == 1
        ? "1 seller online"
        : $"{SellerCount} sellers online";

    /// <summary>
    /// The active price display mode ("Sell", "Buy", or "Both").
    /// Used to determine what labels and detail rows to render.
    /// </summary>
    public string PriceDisplayMode { get; init; } = "Sell";

    /// <summary>Whether there are additional prices to show below the primary.</summary>
    public bool HasTopPrices =>
        (PriceDisplayMode is "Sell" or "Both" && TopSellPrices.Count > 1)
        || (PriceDisplayMode is "Buy" or "Both" && TopBuyPrices.Count > 1);

    /// <summary>
    /// Formatted string showing the additional sell/buy prices.
    /// Sell prices sorted ascending (cheapest first), buy prices sorted descending (highest first).
    /// </summary>
    public string TopPricesText
    {
        get
        {
            var lines = new List<string>();

            if ((PriceDisplayMode is "Sell" or "Both") && TopSellPrices.Count > 1)
            {
                var others = TopSellPrices.Skip(1).OrderBy(p => p).Select(p => $"{p}◆");
                lines.Add($"Next lowest sellers: {string.Join(", ", others)}");
            }

            if ((PriceDisplayMode is "Buy" or "Both") && TopBuyPrices.Count > 1)
            {
                var others = TopBuyPrices.Skip(1).OrderByDescending(p => p).Select(p => $"{p}◆");
                lines.Add($"Next highest buyers: {string.Join(", ", others)}");
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Detail text shown below the primary price.
    /// In "Both" mode shows the buy price.
    /// </summary>
    public string DetailText
    {
        get
        {
            if (PriceDisplayMode == "Both" && BuyPrice.HasValue)
                return $"Buyers pay: {BuyPrice.Value}◆";

            return string.Empty;
        }
    }

    /// <summary>Whether the detail row has content to display.</summary>
    public bool HasDetail => !string.IsNullOrEmpty(DetailText);

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

    /// <summary>Individual items in this run, each with name, price, and highlight state.</summary>
    public required ObservableCollection<HistoryItemViewModel> Items { get; init; }
}

/// <summary>
/// View model for a single reward item within a history run, rendered as a chip.
/// </summary>
public sealed class HistoryItemViewModel
{
    /// <summary>Matched item name (or "?" if unmatched).</summary>
    public required string Name { get; init; }

    /// <summary>Platinum price, or null if untradeable/unmatched/failed.</summary>
    public int? Price { get; init; }

    /// <summary>Formatted display text for the chip (e.g. "Nagantaka Prime Receiver · 45◆").</summary>
    public string DisplayText => Price.HasValue
        ? $"{Name} · {Price.Value}◆"
        : $"{Name} · N/A";
}
