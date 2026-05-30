namespace WarframeRelicOverlay.Infrastructure.Platform;
 
/// <summary>
/// Queries the Warframe window's client-area bounds and DPI using
/// Win32 APIs.  Stateless — each call returns a fresh snapshot.
///
/// Uses <see cref="Win32Interop.GetClientRect"/> + 
/// <see cref="Win32Interop.ClientToScreen"/> rather than
/// <see cref="Win32Interop.GetWindowRect"/> so the result excludes
/// the title bar and borders (which don't exist in fullscreen, but
/// do exist in windowed mode).  This gives us the exact region that
/// Warframe renders into, which is what screen capture and overlay
/// positioning need.
/// </summary>
public sealed class WarframeWindowTracker : IWindowTracker
{
    /// <inheritdoc />
    public WindowSnapshot? TryGetBounds(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
            return null;
 
        // Get the client area size (width × height, relative to the window).
        if (!Win32Interop.GetClientRect(windowHandle, out var clientRect))
            return null;
 
        int clientWidth  = clientRect.Width;
        int clientHeight = clientRect.Height;
 
        // Minimized or otherwise invalid.
        if (clientWidth <= 0 || clientHeight <= 0)
            return null;
 
        // Translate the client area's top-left corner (0,0) to screen
        // coordinates so we know where it sits on the desktop.
        var topLeft = new Win32Interop.POINT { X = 0, Y = 0 };
        if (!Win32Interop.ClientToScreen(windowHandle, ref topLeft))
            return null;
 
        // DPI for the monitor this window lives on.
        var (scaleX, scaleY) = Win32Interop.GetDpiScale(windowHandle);
 
        var snapshot = new WindowSnapshot(
            ClientX:      topLeft.X,
            ClientY:      topLeft.Y,
            ClientWidth:  clientWidth,
            ClientHeight: clientHeight,
            DpiScaleX:    scaleX,
            DpiScaleY:    scaleY);
 
        return snapshot.IsValid ? snapshot : null;
    }
 
    /// <inheritdoc />
    public WindowSnapshot? TryGetMonitorBounds(nint windowHandle)
    {
        if (windowHandle == nint.Zero) return null;

        var (rect, scaleX, scaleY) = Win32Interop.GetMonitorBounds(windowHandle);
        if (rect.Width <= 0 || rect.Height <= 0) return null;

        var snapshot = new WindowSnapshot(
            ClientX:      rect.Left,
            ClientY:      rect.Top,
            ClientWidth:  rect.Width,
            ClientHeight: rect.Height,
            DpiScaleX:    scaleX,
            DpiScaleY:    scaleY);

        return snapshot.IsValid ? snapshot : null;
    }

    /// <inheritdoc />
    public bool IsForeground(nint windowHandle)
    {
        if (windowHandle == nint.Zero)
            return false;
 
        return Win32Interop.GetForegroundWindow() == windowHandle;
    }
}
