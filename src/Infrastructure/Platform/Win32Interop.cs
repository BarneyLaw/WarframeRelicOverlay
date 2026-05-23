namespace WarframeRelicOverlay.Infrastructure.Platform;

using System.Runtime.InteropServices;

/// <summary>
/// Thin wrapper around the Win32 API surface needed by the overlay.
/// All P/Invoke declarations live here — no other class should import
/// user32.dll or shcore.dll directly.
/// </summary>
internal static partial class Win32Interop
{
    // ── Window style constants ──────────────────────────────────────

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_LAYERED     = 0x00080000;

    // ── Structs ─────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width  => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    // ── DPI awareness ───────────────────────────────────────────────

    internal enum PROCESS_DPI_AWARENESS
    {
        PROCESS_DPI_UNAWARE           = 0,
        PROCESS_SYSTEM_DPI_AWARE      = 1,
        PROCESS_PER_MONITOR_DPI_AWARE = 2,
    }

    internal enum MONITOR_DPI_TYPE
    {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI   = 1,
        MDT_RAW_DPI       = 2,
    }

    // ── P/Invoke: window geometry ───────────────────────────────────

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(nint hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    // ── P/Invoke: foreground / focus ────────────────────────────────

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    // ── P/Invoke: window style (click-through toggle) ───────────────

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    // ── P/Invoke: DPI ───────────────────────────────────────────────

    [LibraryImport("shcore.dll")]
    internal static partial int SetProcessDpiAwareness(PROCESS_DPI_AWARENESS value);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(
        nint hMonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hWnd, uint dwFlags);

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the effective DPI scale factors for the monitor that
    /// contains the given window. Falls back to (1.0, 1.0) on failure.
    /// </summary>
    internal static (double ScaleX, double ScaleY) GetDpiScale(nint hWnd)
    {
        nint monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == nint.Zero)
            return (1.0, 1.0);

        int hr = GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                                  out uint dpiX, out uint dpiY);
        if (hr != 0)
            return (1.0, 1.0);

        return (dpiX / 96.0, dpiY / 96.0);
    }
}