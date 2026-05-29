namespace WarframeRelicOverlay.Presentation;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private Storyboard? _spinnerStoryboard;

    /// <summary>
    /// Constructs the window without showing it.  The composition root
    /// resolves this exactly once and registers it with the DI
    /// container; visibility is controlled by
    /// <see cref="WpfOverlayOutput"/>.
    /// </summary>
    public OverlayWindow()
    {
        InitializeComponent();

        // Closing the overlay window means the user wants to quit.
        Closing += OnClosing;
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
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

    // ── Public surface used by WpfOverlayOutput ─────────────────────

    /// <summary>
    /// Repositions and resizes the overlay window so its client area
    /// covers Warframe's client area exactly, in WPF logical units.
    /// </summary>
    /// <param name="window">
    /// The most recent snapshot of Warframe's client-area geometry.
    /// </param>
    public void ApplyWindowGeometry(WindowSnapshot window)
    {
        Left = window.LogicalX;
        Top = window.LogicalY;
        Width = window.LogicalWidth;
        Height = window.LogicalHeight;

        LabelCanvas.Width = window.LogicalWidth;
        LabelCanvas.Height = window.LogicalHeight;
    }

    /// <summary>
    /// Removes every price label currently parented to
    /// <see cref="LabelCanvas"/>.  Safe to call when no labels exist.
    /// </summary>
    public void ClearLabels()
    {
        foreach (var label in _priceLabels)
            LabelCanvas.Children.Remove(label);
        _priceLabels.Clear();
    }

    /// <summary>
    /// Replaces the current set of price labels with one new label per
    /// supplied <see cref="PositionedLabel"/>.  Each label is a small
    /// dark card with a gold border and large platinum text, identical
    /// in style to common in-game price-check overlays.
    /// </summary>
    /// <param name="labels">
    /// The pre-positioned labels to render.  Position values are in
    /// DIPs relative to the canvas top-left.
    /// </param>
    /// <param name="opacity">
    /// Overall opacity to apply (0.5 - 1.0).  Comes from
    /// <see cref="Core.AppSettings.OverlayOpacity"/>.
    /// </param>
    /// <param name="fontSize">
    /// Font size in DIPs for the price text.
    /// </param>
    public void RenderLabels(IReadOnlyList<PositionedLabel> labels, double opacity, double fontSize)
    {
        ClearLabels();

        foreach (var positioned in labels)
        {
            UIElement element = BuildPriceCard(positioned, opacity, fontSize);

            Canvas.SetLeft(element, positioned.LeftDip);
            Canvas.SetTop(element, positioned.TopDip);

            LabelCanvas.Children.Add(element);
            _priceLabels.Add(element);
        }
    }

    /// <summary>
    /// Shows the loading spinner anchored over the reward area
    /// (vertically between 30% and 70% of the window height).
    /// </summary>
    public void ShowSpinner()
    {
        SpinnerHost.Margin = new Thickness(
            left: 0,
            top: ActualHeight * 0.30,
            right: 0,
            bottom: ActualHeight * 0.30);

        SpinnerHost.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Hides the loading spinner.  Idempotent.
    /// </summary>
    public void HideSpinner()
    {
        SpinnerHost.Visibility = Visibility.Collapsed;
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
        Color suffixColor = Color.FromRgb(0xC1, 0x9A, 0x4F);

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

        // Splits "42p" into the number and a smaller "p" suffix so the
        // price reads at a glance.  Other DisplayText values
        // ("Untradeable", "N/A", "?") render as a single block.
        border.Child = BuildPriceRow(
            label.Text,
            fontSize,
            priceColor,
            suffixColor);

        return border;
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
    /// Splits a string of the form <c>"&lt;digits&gt;p"</c> into the
    /// numeric portion and the trailing <c>"p"</c> suffix.  Returns
    /// <c>false</c> for any other input.
    /// </summary>
    private static bool TrySplitPlatinum(string text, out string number, out string suffix)
    {
        number = string.Empty;
        suffix = string.Empty;

        if (string.IsNullOrEmpty(text)) return false;
        if (text[^1] != 'p' && text[^1] != 'P') return false;

        string head = text[..^1];
        if (head.Length == 0) return false;

        for (int i = 0; i < head.Length; i++)
        {
            if (!char.IsDigit(head[i])) return false;
        }

        number = head;
        suffix = "p";
        return true;
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
/// <param name="Text">Primary text (price / "Untradeable" / "?").</param>
/// <param name="IsHighlighted">
/// <c>true</c> when this card represents the most valuable reward in
/// the current pipeline result.  Renders with a brighter gold border.
/// </param>
public readonly record struct PositionedLabel(
    double LeftDip,
    double TopDip,
    double MaxWidthDip,
    string Text,
    bool IsHighlighted = false);
