namespace WarframeRelicOverlay.Presentation.Views;

using System;
using System.Windows;
using System.Windows.Interop;
using WarframeRelicOverlay.Infrastructure.Platform;

/// <summary>
/// Small always-on-top badge that reports what the overlay is currently
/// doing ("Detecting rewards...", "Fetching prices...") and carries a
/// shutdown button next to that text.
///
/// <para>
/// It exists as its own top-level window rather than as a panel inside
/// <see cref="OverlayWindow"/> because the overlay window sets
/// WS_EX_TRANSPARENT so mouse input falls straight through to Warframe.
/// That flag is all-or-nothing per window, so a button hosted there
/// could never be clicked.  This window keeps hit-testing enabled over
/// its own few dozen pixels and adds WS_EX_NOACTIVATE so clicking it
/// never pulls focus away from the game.
/// </para>
///
/// <para>
/// Positioning and visibility are driven by <see cref="OverlayWindow"/>,
/// which keeps the badge centred on the top edge of Warframe's client
/// area as the game window moves.
/// </para>
/// </summary>
public partial class StatusBadgeWindow : Window
{
    /// <summary>Constructs the badge without showing it.</summary>
    public StatusBadgeWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    // ── Win32 hit-testing ───────────────────────────────────────────

    private const int WM_NCHITTEST = 0x0084;
    private const nint HTTRANSPARENT = -1;
    private const nint HTCLIENT = 1;

    /// <summary>
    /// Marks the window as a non-activating tool window as soon as the
    /// HWND exists, and installs the hit-test hook.  Mouse clicks are
    /// still delivered — only the focus change is suppressed — so the
    /// shutdown button keeps working while Warframe stays the
    /// foreground window.
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        nint hwnd = helper.Handle;
        if (hwnd == nint.Zero) return;

        nint exStyle = Win32Interop.GetWindowLongPtr(
            hwnd, Win32Interop.GWL_EXSTYLE);

        nint newStyle = exStyle
            | Win32Interop.WS_EX_TOOLWINDOW
            | Win32Interop.WS_EX_NOACTIVATE;

        Win32Interop.SetWindowLongPtr(
            hwnd, Win32Interop.GWL_EXSTYLE, newStyle);

        HwndSource.FromHwnd(hwnd)?.AddHook(OnWindowMessage);
    }

    /// <summary>
    /// Answers WM_NCHITTEST with HTTRANSPARENT everywhere except over
    /// the shutdown button.
    ///
    /// <para>
    /// The badge sits over Warframe for the whole session, so making the
    /// entire window solid would permanently steal clicks from the
    /// game's top-centre HUD.  Reporting the status text as transparent
    /// lets Windows keep walking down the z-order and deliver those
    /// clicks to Warframe, while the button itself still receives hover
    /// and click messages normally.
    /// </para>
    /// </summary>
    private nint OnWindowMessage(
        nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST) return nint.Zero;

        // lParam packs the cursor's screen position as two signed
        // 16-bit values (x in the low word, y in the high word).
        long packed = lParam.ToInt64();
        int screenX = unchecked((short)(packed & 0xFFFF));
        int screenY = unchecked((short)((packed >> 16) & 0xFFFF));

        handled = true;
        return IsOverShutdownButton(screenX, screenY)
            ? HTCLIENT
            : HTTRANSPARENT;
    }

    /// <summary>
    /// Returns whether the given screen-pixel position falls inside the
    /// shutdown button.
    /// </summary>
    private bool IsOverShutdownButton(int screenX, int screenY)
    {
        if (ShutdownButton.ActualWidth <= 0 || ShutdownButton.ActualHeight <= 0)
            return false;

        try
        {
            Point local = ShutdownButton.PointFromScreen(
                new Point(screenX, screenY));

            return local.X >= 0 && local.X < ShutdownButton.ActualWidth
                && local.Y >= 0 && local.Y < ShutdownButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            // The visual isn't connected to a presentation source yet.
            return false;
        }
    }

    /// <summary>Shuts the whole application down.</summary>
    private void OnShutdownClick(object sender, RoutedEventArgs e)
    {
        Application.Current?.Shutdown();
    }
}
