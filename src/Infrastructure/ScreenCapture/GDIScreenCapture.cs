namespace WarframeRelicOverlay.Infrastructure.ScreenCapture;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using WarframeRelicOverlay.Infrastructure.Platform;

/// <summary>
/// Captures screen regions via GDI <see cref="Graphics.CopyFromScreen"/>.
///
/// All coordinates are physical pixels — no DPI scaling is applied here.
/// The caller (typically <see cref="WindowSnapshot"/>) already provides
/// physical-pixel values from <c>GetClientRect</c> + <c>ClientToScreen</c>,
/// which is exactly what GDI expects.
///
/// Thread safety: each call creates and disposes its own <see cref="Graphics"/>
/// and <see cref="Bitmap"/>, so concurrent calls from the Tesseract pool
/// are safe.
/// </summary>
public sealed class GdiScreenCapturer : IScreenCapturer
{
    /// <inheritdoc />
    public Bitmap? CaptureWindow(WindowSnapshot window)
    {
        if (window.ClientWidth <= 0 || window.ClientHeight <= 0)
            return null;

        var region = new Rectangle(
            window.ClientX,
            window.ClientY,
            window.ClientWidth,
            window.ClientHeight);

        return CaptureRegion(region);
    }

    /// <inheritdoc />
    public Bitmap? CaptureRegion(Rectangle physicalRegion)
    {
        if (physicalRegion.Width <= 0 || physicalRegion.Height <= 0)
            return null;

        try
        {
            var bitmap = new Bitmap(
                physicalRegion.Width,
                physicalRegion.Height,
                PixelFormat.Format24bppRgb);

            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(
                physicalRegion.X,
                physicalRegion.Y,
                0,
                0,
                new Size(physicalRegion.Width, physicalRegion.Height),
                CopyPixelOperation.SourceCopy);

            return bitmap;
        }
        catch (Exception ex)
        {
            // CopyFromScreen can throw if the window is on a disconnected
            // monitor, the desktop is locked, or an RDP session resizes.
            Debug.WriteLine($"[GdiScreenCapturer] Capture failed: {ex.Message}");
            return null;
        }
    }
}