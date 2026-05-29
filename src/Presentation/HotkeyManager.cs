namespace WarframeRelicOverlay.Presentation;

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WarframeRelicOverlay.Infrastructure.Logging;

/// <summary>
/// Registers a single global hotkey via the Win32
/// <c>RegisterHotKey</c> API and invokes a callback when it fires.
///
/// <para>
/// Hotkey strings follow a simple grammar: zero or more modifiers
/// from the set <c>{Ctrl, Shift, Alt, Win}</c> plus exactly one
/// non-modifier key, joined by <c>+</c> and parsed case-insensitively.
/// Examples: <c>Shift+F9</c>, <c>Ctrl+Alt+P</c>, <c>F8</c>.
/// </para>
///
/// <para>
/// The hotkey is bound to the overlay window's HWND.  Because the
/// overlay window is hidden by default we still need a real HWND, so
/// the manager waits for the window's source to be initialised
/// (<see cref="HwndSource.FromHwnd(System.IntPtr)"/>) before calling
/// <c>RegisterHotKey</c>.
/// </para>
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    // ── Win32 ───────────────────────────────────────────────────────

    private const int WM_HOTKEY = 0x0312;

    [Flags]
    private enum Modifiers : uint
    {
        None = 0x0000,
        Alt = 0x0001,
        Ctrl = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    private const int HotkeyId = 0xB337;

    // ── State ───────────────────────────────────────────────────────

    private readonly Window _window;
    private readonly string _configuredCombo;
    private readonly Action _onPressed;
    private readonly ILogger _logger;

    private HwndSource? _hwndSource;
    private bool _registered;
    private bool _disposed;

    // ── Construction ────────────────────────────────────────────────

    /// <summary>
    /// Creates a manager that will attempt to register
    /// <paramref name="combo"/> against the supplied window's HWND.
    /// Call <see cref="TryRegister"/> after the window has its source.
    /// </summary>
    public HotkeyManager(Window window, string? combo, Action onPressed, ILogger logger)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _onPressed = onPressed ?? throw new ArgumentNullException(nameof(onPressed));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuredCombo = string.IsNullOrWhiteSpace(combo) ? "Shift+F9" : combo!;
    }

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Parses the configured combo and registers it with Windows.
    /// Falls back to <c>Shift+F9</c> on parse failure.  Logs and
    /// continues if Windows refuses the registration (already owned
    /// by another process).
    /// </summary>
    public void TryRegister()
    {
        if (_registered || _disposed) return;

        if (!TryParseCombo(_configuredCombo, out Modifiers modifiers, out uint vk))
        {
            _logger.LogWarning(
                $"Could not parse ToggleHotkey '{_configuredCombo}'. Falling back to 'Shift+F9'.");

            if (!TryParseCombo("Shift+F9", out modifiers, out vk))
                return; // Genuinely impossible, but be defensive.
        }

        EnsureHwndSource();
        nint hwnd = _hwndSource?.Handle ?? nint.Zero;
        if (hwnd == nint.Zero)
        {
            _logger.LogWarning("Cannot register hotkey: overlay window has no HWND yet.");
            return;
        }

        if (!RegisterHotKey(hwnd, HotkeyId, (uint)modifiers, vk))
        {
            int err = Marshal.GetLastWin32Error();
            _logger.LogWarning(
                $"RegisterHotKey('{_configuredCombo}') failed (Win32 error {err}). " +
                "The hotkey will be unavailable for the rest of this session.");
            return;
        }

        _registered = true;
        _logger.LogInfo($"Hotkey '{_configuredCombo}' registered.");
    }

    /// <summary>
    /// Unregisters the hotkey if it was successfully registered.
    /// Idempotent.  Safe to call from any thread; marshals to the UI
    /// thread internally.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwndSource is null) return;

        try
        {
            _window.Dispatcher.Invoke(() =>
            {
                if (_registered && _hwndSource is not null)
                {
                    UnregisterHotKey(_hwndSource.Handle, HotkeyId);
                    _registered = false;
                }

                if (_hwndSource is not null)
                {
                    _hwndSource.RemoveHook(OnWindowProc);
                    _hwndSource = null;
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HotkeyManager] Dispose failed: {ex.Message}");
        }
    }

    // ── HWND wiring ─────────────────────────────────────────────────

    /// <summary>
    /// Resolves the underlying <see cref="HwndSource"/> for the overlay
    /// window, creating it on demand by forcing the window's source to
    /// initialise if it hasn't already.
    /// </summary>
    private void EnsureHwndSource()
    {
        if (_hwndSource is not null) return;

        var helper = new WindowInteropHelper(_window);
        nint hwnd = helper.EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(OnWindowProc);
    }

    /// <summary>
    /// Wndproc hook that catches WM_HOTKEY and fires the callback.
    /// </summary>
    private nint OnWindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            try { _onPressed(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HotkeyManager] Callback threw: {ex.Message}");
            }
            handled = true;
        }
        return nint.Zero;
    }

    // ── Combo parsing ───────────────────────────────────────────────

    /// <summary>
    /// Parses a combo string into Win32 modifier flags and a virtual
    /// key code.  Returns <c>false</c> for null/empty input or any
    /// invalid token.
    /// </summary>
    private static bool TryParseCombo(string? combo, out Modifiers modifiers, out uint vk)
    {
        modifiers = Modifiers.None;
        vk = 0;

        if (string.IsNullOrWhiteSpace(combo)) return false;

        string[] parts = combo.Split('+', StringSplitOptions.RemoveEmptyEntries
                                          | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        Key? mainKey = null;
        foreach (string part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= Modifiers.Ctrl;
                    break;
                case "shift":
                    modifiers |= Modifiers.Shift;
                    break;
                case "alt":
                    modifiers |= Modifiers.Alt;
                    break;
                case "win":
                case "windows":
                    modifiers |= Modifiers.Win;
                    break;
                default:
                    if (mainKey is not null)
                        return false; // More than one non-modifier key.

                    if (!Enum.TryParse<Key>(part, ignoreCase: true, out var parsed))
                        return false;

                    mainKey = parsed;
                    break;
            }
        }

        if (mainKey is null) return false;

        vk = (uint)KeyInterop.VirtualKeyFromKey(mainKey.Value);
        return vk != 0;
    }
}
