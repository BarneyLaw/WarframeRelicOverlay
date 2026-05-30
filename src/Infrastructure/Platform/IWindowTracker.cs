namespace WarframeRelicOverlay.Infrastructure.Platform;
 
/// <summary>
/// Queries the window geometry of the tracked game process.
/// Handles DPI scaling and the distinction between physical pixels
/// (for screen capture) and logical DIPs (for WPF overlay positioning).
/// </summary>
public interface IWindowTracker
{
    /// <summary>
    /// Snapshots the current window bounds.  Returns <c>null</c> if the
    /// handle is invalid, the process exited, or the window is minimized.
    /// </summary>
    WindowSnapshot? TryGetBounds(nint windowHandle);
 
    /// <summary>
    /// Returns a snapshot whose <c>ClientX/Y/Width/Height</c> describe
    /// the full physical bounds of the monitor containing
    /// <paramref name="windowHandle"/>. Used as a fallback when client
    /// bounds are temporarily unavailable.
    /// Returns <c>null</c> if the handle is invalid or the monitor
    /// cannot be determined.
    /// </summary>
    WindowSnapshot? TryGetMonitorBounds(nint windowHandle);

    /// <summary>
    /// Returns <c>true</c> if the given window is the current foreground
    /// (focused) window.
    /// </summary>
    bool IsForeground(nint windowHandle);
}
