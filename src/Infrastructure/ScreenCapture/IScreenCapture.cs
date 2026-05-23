namespace WarframeRelicOverlay.Infrastructure.ScreenCapture;

using System.Drawing;
using WarframeRelicOverlay.Infrastructure.Platform;

/// <summary>
/// Abstracts screen-capture so callers never touch GDI directly and the
/// implementation can be swapped for a stub in tests.
/// </summary>
public interface IScreenCapturer
{
    /// <summary>
    /// Captures the entire client area of the window described by
    /// <paramref name="window"/> and returns the resulting bitmap.
    /// The bitmap dimensions match the physical pixel size of the
    /// client area (<see cref="WindowSnapshot.ClientWidth"/> ×
    /// <see cref="WindowSnapshot.ClientHeight"/>).
    /// Returns <c>null</c> if the capture fails (e.g. window minimized,
    /// access denied, or zero-size client area).
    /// </summary>
    Bitmap? CaptureWindow(WindowSnapshot window);

    /// <summary>
    /// Captures a sub-region of the screen defined in physical pixel
    /// coordinates.  Useful for grabbing a single reward card after
    /// the layout detector has identified its bounds.
    /// Returns <c>null</c> if the region is invalid or capture fails.
    /// </summary>
    Bitmap? CaptureRegion(Rectangle physicalRegion);
}