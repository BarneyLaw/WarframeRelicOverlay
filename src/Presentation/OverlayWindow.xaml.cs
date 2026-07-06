namespace WarframeRelicOverlay.Presentation;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WarframeRelicOverlay.Infrastructure.Logging;
using WarframeRelicOverlay.Infrastructure.Platform;

/// <summary>
/// Transparent, click-through, topmost WPF window that hosts the price
/// labels and the loading spinner.
///
/// <para>
/// The window itself never paints anything visible; it is just a
/// transparent surface positioned exactly over Warframe's client area
/// (using the <see cref="WindowSnapshot"/> the pipeline produced).
/// All visible content lives inside <see cref="LabelCanvas"/> and
/// <see cref="SpinnerHost"/>, which are positioned in DIPs relative to
/// the window's top-left corner.
/// </para>
///
/// <para>
/// Click-through is enabled by setting the WS_EX_TRANSPARENT extended
/// window style after the HWND has been created.  Without this, even
/// a fully transparent window will eat mouse input.
/// </para>
/// </summary>
public partial class OverlayWindow : Window
{
    // ── State ───────────────────────────────────────────────────────

    private readonly List<UIElement> _priceLabels = [];
    private readonly ILogger? _logger;
    private Storyboard? _spinnerStoryboard;

    /// <summary>
    /// Constructs the window without showing it.  The composition root
    /// resolves this exactly once and registers it with the DI
    /// container; visibility is controlled by
    /// <see cref="OverlayViewModel"/>.
    /// </summary>
    public OverlayWindow(ILogger? logger = null)
    {
        _logger = logger;
        InitializeComponent();

        // Closing the overlay window means the user wants to quit.
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    // ── Lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Applies the click-through extended style as soon as the HWND is
    /// available.  Doing this before <see cref="Window.Show"/> avoids
    /// a single frame in which the window can intercept input.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        nint hwnd = helper.Handle;
        if (hwnd == nint.Zero) return;

        nint exStyle = Win32Interop.GetWindowLongPtr(
            hwnd, Win32Interop.GWL_EXSTYLE);

        nint newStyle = exStyle
            | Win32Interop.WS_EX_LAYERED
            | Win32Interop.WS_EX_TRANSPARENT
            | Win32Interop.WS_EX_TOOLWINDOW;

        Win32Interop.SetWindowLongPtr(
            hwnd, Win32Interop.GWL_EXSTYLE, newStyle);
    }

    /// <summary>
    /// Sets up the spinner rotation animation once the visual tree has
    /// loaded.  The animation runs continuously; the spinner is shown
    /// or hidden by toggling <see cref="SpinnerHost"/> visibility.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var rotateAnimation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(1.5),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(rotateAnimation, SpinnerRotation);
        Storyboard.SetTargetProperty(
            rotateAnimation, new PropertyPath("Angle"));

        _spinnerStoryboard = new Storyboard();
        _spinnerStoryboard.Children.Add(rotateAnimation);
        _spinnerStoryboard.Begin();
    }

    /// <summary>
    /// Treats a window close as a request to exit the application.
    /// The composition root listens to <see cref="Application.Exit"/>
    /// and disposes everything it owns.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        Application.Current?.Shutdown();
    }

    /// <summary>
    /// Handles key presses when the window is interactive.
    /// - Escape closes the history panel.
    /// - When the hotkey recorder is listening, captures the key chord.
    /// </summary>
    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not OverlayViewModel vm) return;

        // Hotkey recorder capture takes priority.
        if (vm.IsHotkeyListening)
        {
            var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
            var modifiers = System.Windows.Input.Keyboard.Modifiers;
            vm.CaptureHotkey(modifiers, key);
            e.Handled = true;
            return;
        }

        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (vm.IsHistoryPanelVisible)
            {
                vm.ToggleHistoryPanel();
                SetInteractive(false);

                // Return focus to Warframe so the player doesn't have
                // to click the game window after closing the panel.
                nint wfHwnd = vm.WarframeWindowHandle;
                if (wfHwnd != nint.Zero)
                    Win32Interop.SetForegroundWindow(wfHwnd);

                e.Handled = true;
            }
        }
    }

    // ── Tab click handlers ───────────────────────────────────────────

    /// <summary>Switches the panel to the History tab.</summary>
    private void OnHistoryTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverlayViewModel vm)
            vm.ShowHistoryTab();
    }

    /// <summary>Switches the panel to the Settings tab.</summary>
    private void OnSettingsTabClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is OverlayViewModel vm)
            vm.ShowSettingsTab();
    }

    /// <summary>Shuts down the entire application when the X button is clicked.</summary>
    private void OnCloseButtonClick(object sender, RoutedEventArgs e)
    {
        Application.Current?.Shutdown();
    }

    // ── Click-through toggle ────────────────────────────────────────

    /// <summary>
    /// Temporarily makes the overlay window interactive (removes
    /// WS_EX_TRANSPARENT) when the history panel is open, so the user
    /// can scroll and click within it.  Restores click-through when
    /// the panel closes.
    /// </summary>
    public void SetInteractive(bool interactive)
    {
        var helper = new WindowInteropHelper(this);
        nint hwnd = helper.Handle;
        if (hwnd == nint.Zero) return;

        nint exStyle = Win32Interop.GetWindowLongPtr(
            hwnd, Win32Interop.GWL_EXSTYLE);

        if (interactive)
        {
            // Remove WS_EX_TRANSPARENT to allow mouse interaction.
            nint newStyle = exStyle & ~(nint)Win32Interop.WS_EX_TRANSPARENT;
            Win32Interop.SetWindowLongPtr(
                hwnd, Win32Interop.GWL_EXSTYLE, newStyle);
            IsHitTestVisible = true;
            Focusable = true;
            Activate();
            Focus();
        }
        else
        {
            // Restore click-through.
            nint newStyle = exStyle | Win32Interop.WS_EX_TRANSPARENT;
            Win32Interop.SetWindowLongPtr(
                hwnd, Win32Interop.GWL_EXSTYLE, newStyle);
            IsHitTestVisible = false;
            Focusable = false;
        }
    }

    // ── Public surface used by WpfOverlayOutput ─────────────────────

    /// <summary>
    /// Repositions and resizes the overlay window so its client area
    /// covers Warframe's client area exactly, in WPF logical units.
    /// </summary>
    public void ApplyWindowGeometry(WindowSnapshot window)
    {
        Left = window.LogicalX;
        Top = window.LogicalY;
        Width = window.LogicalWidth;
        Height = window.LogicalHeight;
    }

    /// <summary>
    /// Enables or disables click-through by updating the window's extended style.
    /// Used by the debug simulator to allow keyboard interaction while testing.
    /// </summary>
    public void SetClickThrough(bool enabled)
    {
        var helper = new WindowInteropHelper(this);
        nint hwnd = helper.Handle;
        if (hwnd == nint.Zero) return;

        nint exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE);

        if (enabled)
        {
            exStyle |= Win32Interop.WS_EX_TRANSPARENT;
            exStyle |= Win32Interop.WS_EX_LAYERED;
            exStyle |= Win32Interop.WS_EX_TOOLWINDOW;
        }
        else
        {
            exStyle &= ~Win32Interop.WS_EX_TRANSPARENT;
        }

        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, exStyle);
    }

    // ── Card construction ───────────────────────────────────────────

    /// <summary>
    /// Builds a single price card styled to match Warframe's HUD
    /// palette: a warm charcoal panel with a thin gold border, sitting
    /// quietly on top of the brown reward-screen backdrop.
    ///
    /// <para>
    /// The colours were picked to read cleanly on both the warm tan
    /// underlay and on the brighter highlight bands behind each
    /// reward card without competing visually with the in-game text.
    /// When the card represents the most valuable reward in the set,
    /// the border and price text both lift to a brighter gold so the
    /// player can pick it out at a glance.
    /// </para>
    /// </summary>
    private static UIElement BuildPriceCard(
        PositionedLabel label, double opacity, double fontSize)
    {
        // ── Colours ──────────────────────────────────────────────
        // Warm charcoal backdrop pulled from Warframe's HUD chrome,
        // with the gold accents lifted from the reward border line.
        Color cardFill = Color.FromArgb(0xEE, 0x18, 0x14, 0x10);
        Color borderNormal = Color.FromArgb(0xDD, 0xB0, 0x82, 0x33);
        Color borderHighlight = Color.FromRgb(0xFF, 0xD4, 0x6A);
        Color priceColor = label.IsHighlighted
            ? Color.FromRgb(0xFF, 0xE6, 0x9D)
            : Color.FromRgb(0xF1, 0xDC, 0xA6);
        Color suffixColor = Color.FromRgb(0xC0, 0xD8, 0xE8);

        var border = new Border
        {
            Background = new SolidColorBrush(cardFill),
            BorderBrush = new SolidColorBrush(
                label.IsHighlighted ? borderHighlight : borderNormal),
            BorderThickness = new Thickness(label.IsHighlighted ? 1.5 : 1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(14, 4, 14, 5),
            Opacity = Math.Clamp(opacity, 0.0, 1.0),
            SnapsToDevicePixels = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 7,
                ShadowDepth = 1,
                Opacity = 0.55,
            },
        };

        // Renders item name (if matched) as a title above the price,
        // then the platinum price below, then a detail row with buy
        // price and seller count.
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        if (!string.IsNullOrWhiteSpace(label.ItemName))
        {
            Color nameColor = Color.FromRgb(0xE8, 0xE8, 0xE8);
            stack.Children.Add(new TextBlock
            {
                Text = label.ItemName,
                FontSize = Math.Max(10, fontSize * 0.7),
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(nameColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = Math.Max(80, label.MaxWidthDip - 16),
                Margin = new Thickness(0, 0, 0, 3),
            });
        }

        // Splits "42p" into the number and a smaller "p" suffix so the
        // price reads at a glance.  Other DisplayText values
        // ("N/A", "?") render as a single block.
        stack.Children.Add(BuildPriceRow(
            label.Text,
            fontSize,
            priceColor,
            suffixColor));

        // Detail row: "Buy: Xp · Y sellers"
        string? detailText = BuildDetailText(label.BuyPrice, label.SellerCount);
        if (detailText is not null)
        {
            Color detailColor = Color.FromRgb(0xA8, 0xA8, 0xA8);
            stack.Children.Add(new TextBlock
            {
                Text = detailText,
                FontSize = Math.Max(9, fontSize * 0.55),
                Foreground = new SolidColorBrush(detailColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        border.Child = stack;

        return border;
    }

    /// <summary>
    /// Builds the detail text string showing buy price and seller count.
    /// Returns <c>null</c> when there's nothing to display.
    /// </summary>
    private static string? BuildDetailText(int? buyPrice, int sellerCount)
    {
        var parts = new List<string>(2);

        if (buyPrice.HasValue)
            parts.Add($"Buy: {buyPrice.Value}◆");

        if (sellerCount > 0)
            parts.Add($"{sellerCount} seller{(sellerCount == 1 ? "" : "s")}");

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    /// <summary>
    /// Builds the price row.  Splits a numeric platinum value (e.g.
    /// <c>"42p"</c>) into the number and the trailing <c>p</c> so the
    /// two halves can be styled independently.  Falls back to a single
    /// block for non-numeric values.
    /// </summary>
    private static UIElement BuildPriceRow(
        string displayText, double fontSize, Color priceColor, Color suffixColor)
    {
        if (TrySplitPlatinum(displayText, out string number, out string suffix))
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            row.Children.Add(new TextBlock
            {
                Text = number,
                FontSize = fontSize * 1.1,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(priceColor),
                VerticalAlignment = VerticalAlignment.Bottom,
            });

            row.Children.Add(new TextBlock
            {
                Text = suffix,
                FontSize = fontSize * 0.7,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(suffixColor),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(2, 0, 0, fontSize * 0.10),
            });

            return row;
        }

        return new TextBlock
        {
            Text = displayText,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(priceColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
    }

    /// <summary>
    /// Splits a string of the form <c>"&lt;digits&gt;◆"</c> into the
    /// numeric portion and the trailing <c>"◆"</c> suffix.  Returns
    /// <c>false</c> for any other input.
    /// </summary>
    private static bool TrySplitPlatinum(string text, out string number, out string suffix)
    {
        number = string.Empty;
        suffix = string.Empty;

        if (string.IsNullOrEmpty(text)) return false;
        if (text[^1] != '◆') return false;

        string head = text[..^1];
        if (head.Length == 0) return false;

        for (int i = 0; i < head.Length; i++)
        {
            if (!char.IsDigit(head[i])) return false;
        }

        number = head;
        suffix = "◆";
        return true;
    }

    /// <summary>
    /// Compatibility shim for the older WpfOverlayOutput path.
    /// The live overlay is now rendered through the bound view model,
    /// so this method intentionally does nothing.
    /// </summary>
    public void ClearLabels()
    {
    }

    /// <summary>
    /// Compatibility shim for the older WpfOverlayOutput path.
    /// The live overlay is now rendered through the bound view model,
    /// so this method intentionally does nothing.
    /// </summary>
    public void RenderLabels(IReadOnlyList<PositionedLabel> labels, double opacity, double fontSize)
    {
    }

    /// <summary>
    /// Compatibility shim for the older WpfOverlayOutput path.
    /// The live overlay is now rendered through the bound view model,
    /// so this method intentionally does nothing.
    /// </summary>
    public void ShowSpinner()
    {
    }

    /// <summary>
    /// Compatibility shim for the older WpfOverlayOutput path.
    /// The live overlay is now rendered through the bound view model,
    /// so this method intentionally does nothing.
    /// </summary>
    public void HideSpinner()
    {
    }

    /// <summary>
    /// Positions and sizes the overlay window using physical screen
    /// pixels.  Called by <see cref="OverlayViewModel"/> via the
    /// <c>PhysicalBoundsChanged</c> event.  Converts from physical
    /// pixels to WPF logical units (DIPs) using the window's current
    /// DPI transform so the overlay aligns precisely with Warframe's
    /// client area regardless of display scaling.
    /// </summary>
    /// <param name="x">Physical-pixel left edge of the Warframe client area.</param>
    /// <param name="y">Physical-pixel top edge of the Warframe client area.</param>
    /// <param name="width">Physical-pixel width of the Warframe client area.</param>
    /// <param name="height">Physical-pixel height of the Warframe client area.</param>
    public void SetPhysicalBounds(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;

        // Get the DPI scale for this window so we can convert physical → logical.
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        Left = x * dpiX;
        Top = y * dpiY;
        Width = width * dpiX;
        Height = height * dpiY;
    }
}

/// <summary>
/// Pre-computed placement for a single price card, in WPF logical
/// units (DIPs) relative to the overlay window's top-left corner.
/// </summary>
/// <param name="LeftDip">Left coordinate in DIPs.</param>
/// <param name="TopDip">Top coordinate in DIPs.</param>
/// <param name="MaxWidthDip">
/// Maximum width hint for the card; derived from the corresponding
/// reward card width so the label cannot grow wider than its anchor.
/// </param>
/// <param name="Text">Primary text (price / "N/A" / "?").</param>
/// <param name="ItemName">
/// The matched reward item name (e.g. "Nagantaka Prime Receiver"),
/// or <c>null</c> when no match was found.
/// </param>
/// <param name="BuyPrice">
/// Highest buy offer in platinum, or <c>null</c> if unavailable.
/// Displayed as "Buy: Xp" on the detail row.
/// </param>
/// <param name="SellerCount">
/// Number of in-game sellers.  Displayed as "X sellers" on the
/// detail row.  Zero means no data.
/// </param>
/// <param name="IsHighlighted">
/// <c>true</c> when this card represents the most valuable reward in
/// the current pipeline result.  Renders with a brighter gold border.
/// </param>
public readonly record struct PositionedLabel(
    double LeftDip,
    double TopDip,
    double MaxWidthDip,
    string Text,
    string? ItemName = null,
    int? BuyPrice = null,
    int SellerCount = 0,
    bool IsHighlighted = false);
