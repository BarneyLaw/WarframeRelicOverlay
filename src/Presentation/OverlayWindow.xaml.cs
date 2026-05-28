namespace WarframeRelicOverlay.Presentation;

using System;
using System.Windows;
using System.Windows.Interop;
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
    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetClickThrough(true);
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