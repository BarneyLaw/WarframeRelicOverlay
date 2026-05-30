namespace WarframeRelicOverlay.Presentation;

using System;
using System.Windows;
using System.Windows.Interop;
using WarframeRelicOverlay.Infrastructure.Logging;
using WarframeRelicOverlay.Infrastructure.Platform;

/// <summary>
/// Transparent topmost overlay window that sits on top of Warframe.
/// Click-through by default (WS_EX_TRANSPARENT) so the player can
/// interact with the game normally while prices are displayed.
///
/// The window's position and size are driven by
/// <see cref="OverlayViewModel"/> which tracks the Warframe client
/// area via <see cref="IWindowTracker"/>.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly ILogger? _logger;

    public OverlayWindow() : this(null) { }

    public OverlayWindow(ILogger? logger)
    {
        _logger = logger;
        InitializeComponent();

        Loaded += (_, _) =>
            _logger?.LogInfo($"OverlayWindow Loaded: ActualSize {ActualWidth}x{ActualHeight}, " +
                             $"Left/Top ({Left},{Top}), Size {Width}x{Height}.");
        SizeChanged += (_, e) =>
            _logger?.LogInfo($"OverlayWindow SizeChanged -> {e.NewSize.Width}x{e.NewSize.Height}.");
        LocationChanged += (_, _) =>
            _logger?.LogInfo($"OverlayWindow LocationChanged -> ({Left},{Top}).");
    }

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetClickThrough(true);

        nint hwnd = new WindowInteropHelper(this).Handle;
        _logger?.LogInfo($"OverlayWindow SourceInitialized: hwnd 0x{hwnd:X}, " +
                         $"Size {Width}x{Height}, Left/Top ({Left},{Top}).");
    }

    /// <summary>
    /// Positions and sizes the overlay using raw screen pixels via
    /// <c>SetWindowPos</c>. This bypasses WPF's logical-unit (DIP)
    /// Width/Height handling, which does not reliably track a window
    /// across monitors of differing DPI. WPF re-renders its content to
    /// fill whatever physical size the HWND receives. Must be called on
    /// the UI thread (the window's owning thread).
    /// </summary>
    public void SetPhysicalBounds(int x, int y, int width, int height)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return; // HWND not created yet — retried next tick

        Win32Interop.SetWindowPos(
            hwnd, Win32Interop.HWND_TOPMOST,
            x, y, width, height,
            Win32Interop.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Toggles click-through on the window. When enabled, all mouse
    /// events pass through to the window below (Warframe). When
    /// disabled, the window captures mouse input (for the future
    /// settings menu).
    /// </summary>
    public void SetClickThrough(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        nint exStyle = Win32Interop.GetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE);

        if (enabled)
            exStyle |= Win32Interop.WS_EX_TRANSPARENT | Win32Interop.WS_EX_LAYERED;
        else
            exStyle &= ~(nint)Win32Interop.WS_EX_TRANSPARENT;

        Win32Interop.SetWindowLongPtr(hwnd, Win32Interop.GWL_EXSTYLE, exStyle);
    }
}