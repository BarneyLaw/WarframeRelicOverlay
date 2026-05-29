namespace WarframeRelicOverlay.Presentation;

using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.OverlayApp.Pipeline;

/// <summary>
/// Pure layout helper that maps a <see cref="CardResult"/>'s bounding
/// rectangle (in physical pixels relative to the captured window
/// bitmap) onto the WPF <c>OverlayWindow</c> coordinate space (DIPs).
///
/// Every position is derived exclusively from
/// <see cref="CardResult.BoundsInWindow"/> and the
/// <see cref="WindowSnapshot"/> DPI / offset values — no screen-space
/// constants, resolution tables, or display-mode branches are ever
/// involved (R4.4, R4.5).
///
/// The helper is deliberately stateless and side-effect free so it
/// can be exercised by property tests without any WPF infrastructure.
/// </summary>
public static class OverlayLayout
{
    /// <summary>
    /// Compute the top-left position (in DIPs, relative to the
    /// OverlayWindow) at which a price label of the given size should
    /// be drawn for the supplied card.
    ///
    /// The label is placed directly below the card with a 4 DIP gap
    /// (R4.1, R4.2). If that placement would push the label past the
    /// bottom edge of the OverlayWindow, the label is flipped to sit
    /// directly above the card with the same 4 DIP gap (R4.3).
    ///
    /// The returned coordinates are NOT clamped to the window edges —
    /// that responsibility belongs to the caller (or, in practice,
    /// to WPF's normal off-canvas clipping behaviour).
    /// </summary>
    /// <param name="card">The detected reward card whose label is being placed.</param>
    /// <param name="window">The window snapshot that produced <paramref name="card"/>.</param>
    /// <param name="labelWidth">Measured label width in DIPs.</param>
    /// <param name="labelHeight">Measured label height in DIPs.</param>
    /// <param name="windowLogicalHeight">Overlay window height in DIPs (used for the flip check).</param>
    /// <returns>The label's top-left position in DIPs, relative to the OverlayWindow.</returns>
    public static (double Left, double Top) ComputeLabelPosition(
        CardResult card,
        WindowSnapshot window,
        double labelWidth,
        double labelHeight,
        double windowLogicalHeight)
    {
        // ── Below-card placement ──

        double centerX = (card.BoundsInWindow.X + card.BoundsInWindow.Width / 2.0) / window.DpiScaleX;
        double top = (card.BoundsInWindow.Y + card.BoundsInWindow.Height) / window.DpiScaleY + 4.0;
        double left = centerX - labelWidth / 2.0;

        // ── Above-card flip ──

        if (top + labelHeight > windowLogicalHeight)
        {
            top = card.BoundsInWindow.Y / window.DpiScaleY - 4.0 - labelHeight;
        }

        return (left, top);
    }
}
