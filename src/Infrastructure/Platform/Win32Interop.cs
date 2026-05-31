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

    // ── P/Invoke: top-level window enumeration ──────────────────────
    // Used to recover the game's render window when
    // Process.MainWindowHandle returns zero (it caches stale/empty
    // values and can report 0 for borderless game windows).

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    // DllImport: the LibraryImport source-generator doesn't marshal
    // managed delegate callbacks here as cleanly.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

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

    // ── P/Invoke: window placement ──────────────────────────────────
    // Used to size/position the overlay in raw screen pixels, bypassing
    // WPF's per-monitor logical-unit (DIP) handling which is unreliable
    // across monitors with differing DPI.

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint hWnd, nint hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    internal static readonly nint HWND_TOPMOST = -1;
    internal const uint SWP_NOACTIVATE = 0x0010;

    // ── P/Invoke: window style (click-through toggle) ───────────────

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    // ── P/Invoke: DPI ───────────────────────────────────────────────

    [LibraryImport("shcore.dll")]
    internal static partial int SetProcessDpiAwareness(PROCESS_DPI_AWARENESS value);

    // Modern (Win10 1703+) per-monitor-v2 awareness. Preferred over
    // SetProcessDpiAwareness. The manifest is the primary mechanism;
    // this is a defensive fallback for launch scenarios where the
    // manifest may not be honored. Returns false (no throw) if a more
    // specific awareness was already set — e.g. via the manifest.
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint value);
 
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    internal static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
 
    /// <summary>
    /// Best-effort attempt to make the process Per-Monitor-v2 DPI aware
    /// at runtime. The application manifest is the authoritative source;
    /// this only helps if the manifest was stripped. Safe to call once,
    /// before any HWND is created. Never throws.
    /// </summary>
    internal static void TryEnablePerMonitorV2()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                return;
        }
        catch { /* older OS — fall through */ }
 
        // Fallback for Win8.1 / very old Win10 builds.
        try { SetProcessDpiAwareness(PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE); }
        catch { /* DPI awareness already set by manifest — fine */ }
    }


    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(
        nint hMonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hWnd, uint dwFlags);

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    // ── P/Invoke: monitor geometry ──────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    // LibraryImport source-generator does not support ref on custom structs;
    // DllImport handles it correctly and is fine to mix in the same class.
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

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

    /// <summary>
    /// Finds the most likely "main" top-level window owned by the given
    /// process id: the largest visible non-empty top-level window. This
    /// is a robust replacement for <c>Process.MainWindowHandle</c>, which
    /// can return <c>0</c> for game windows or cache an empty value.
    /// Returns <see cref="nint.Zero"/> if no suitable window is found.
    /// </summary>
    internal static nint GetTopLevelWindowForProcess(int pid)
    {
        nint best = nint.Zero;
        long bestArea = 0;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != (uint)pid) return true;          // not ours
            if (!IsWindowVisible(hWnd)) return true;            // hidden
            if (!GetWindowRect(hWnd, out var rect)) return true;

            long area = (long)rect.Width * rect.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }
            return true; // keep enumerating
        }, nint.Zero);

        return best;
    }

    /// <summary>
    /// Returns the physical-pixel bounds of the monitor containing
    /// <paramref name="hWnd"/> and its effective DPI scale.
    /// Falls back to a zero rect / unit scale on failure.
    /// </summary>
    internal static (RECT Rect, double ScaleX, double ScaleY) GetMonitorBounds(nint hWnd)
    {
        nint monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == nint.Zero) return (default, 1.0, 1.0);

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return (default, 1.0, 1.0);

        int hr = GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                                  out uint dpiX, out uint dpiY);
        double scaleX = hr == 0 ? dpiX / 96.0 : 1.0;
        double scaleY = hr == 0 ? dpiY / 96.0 : 1.0;

        return (info.rcMonitor, scaleX, scaleY);
    }
}