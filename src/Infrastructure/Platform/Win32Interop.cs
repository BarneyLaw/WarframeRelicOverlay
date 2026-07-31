namespace WarframeRelicOverlay.Infrastructure.Platform;

using System;
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
    internal const int WS_EX_TOOLWINDOW  = 0x00000080;

    // ── Window messages ─────────────────────────────────────────────

    internal const int WM_HOTKEY = 0x0312;

    // ── Hotkey modifier flags ───────────────────────────────────────

    /// <summary>
    /// Modifier flag bitmap accepted by <see cref="RegisterHotKey"/>.
    /// Values match the Win32 <c>MOD_*</c> constants.
    /// </summary>
    [Flags]
    internal enum HotkeyModifiers : uint
    {
        None = 0x0000,
        Alt = 0x0001,
        Ctrl = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
    }

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

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
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

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

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

    // ── P/Invoke: foreground / focus ────────────────────────────────

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    // ── P/Invoke: global hotkeys ────────────────────────────────────

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(
        nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);

    // ── P/Invoke: window placement ──────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
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

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(nint value);

    internal static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(
        nint hMonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

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

        try { SetProcessDpiAwareness(PROCESS_DPI_AWARENESS.PROCESS_PER_MONITOR_DPI_AWARE); }
        catch { /* DPI awareness already set by manifest — fine */ }
    }

    /// <summary>
    /// Finds the most likely "main" top-level window owned by the given
    /// process id: the largest visible non-empty top-level window.
    /// </summary>
    internal static nint GetTopLevelWindowForProcess(int pid)
    {
        nint best = nint.Zero;
        long bestArea = 0;

        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out uint windowPid);
            if (windowPid != (uint)pid) return true;
            if (!IsWindowVisible(hWnd)) return true;
            if (!GetWindowRect(hWnd, out var rect)) return true;

            long area = (long)rect.Width * rect.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = hWnd;
            }
            return true;
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
