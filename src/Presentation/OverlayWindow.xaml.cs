namespace WarframeRelicOverlay.Presentation;

using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
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
    private const int WM_NCHITTEST = 0x0084;
    private static readonly nint HTTRANSPARENT = -1;
    private static readonly nint HTCLIENT = 1;

    private readonly ILogger? _logger;

    // When true, the window is click-through everywhere except interactive
    // controls (the close button), decided per-pixel in WndProc. When false
    // (debug mode), the window captures all input so it can take keyboard focus.
    private bool _clickThrough = true;

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

        nint hwnd = new WindowInteropHelper(this).Handle;

        // Hook WM_NCHITTEST so we can make the window click-through per-pixel:
        // every point reports HTTRANSPARENT (falls through to Warframe) except
        // where an interactive control sits. This is what lets the close button
        // be clickable while the rest of the overlay never blocks the game.
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        _logger?.LogInfo($"OverlayWindow SourceInitialized: hwnd 0x{hwnd:X}, " +
                         $"Size {Width}x{Height}, Left/Top ({Left},{Top}).");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST && _clickThrough)
        {
            handled = true;
            return IsOverInteractiveControl(lParam) ? HTCLIENT : HTTRANSPARENT;
        }

        return nint.Zero;
    }

    /// <summary>
    /// True when the cursor (given in the WM_NCHITTEST lParam, in physical
    /// screen pixels) is over an interactive overlay control — currently just
    /// the close button. Used to decide whether a hit is click-through.
    /// </summary>
    private bool IsOverInteractiveControl(nint lParam)
    {
        long packed = lParam.ToInt64();
        int x = unchecked((short)(packed & 0xFFFF));
        int y = unchecked((short)((packed >> 16) & 0xFFFF));

        try
        {
            Point local = PointFromScreen(new Point(x, y));
            if (VisualTreeHelper.HitTest(this, local)?.VisualHit is DependencyObject hit)
            {
                for (DependencyObject? d = hit; d is not null; d = VisualTreeHelper.GetParent(d))
                {
                    if (ReferenceEquals(d, CloseButton))
                        return true;
                }
            }
        }
        catch
        {
            // PointFromScreen can throw while the HWND is tearing down —
            // treat as not-interactive so the click falls through.
        }

        return false;
    }

    private void OnCloseClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        _logger?.LogInfo("Close button pressed — shutting down the overlay.");
        Application.Current.Shutdown();
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
    /// Toggles click-through on the window. When enabled (the default), all
    /// mouse events pass through to the window below (Warframe) except over
    /// interactive controls, decided per-pixel in <see cref="WndProc"/>. When
    /// disabled, the window captures all input (used by the debug simulator so
    /// it can receive keyboard focus).
    /// </summary>
    public void SetClickThrough(bool enabled) => _clickThrough = enabled;
}