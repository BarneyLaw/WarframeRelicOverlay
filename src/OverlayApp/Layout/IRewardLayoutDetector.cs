namespace WarframeRelicOverlay.OverlayApp.Layout;

using System.Drawing;

/// <summary>
/// Detects the positions and count of reward cards in a Warframe
/// window screenshot.  Implementations must be resolution-independent,
/// aspect-ratio-independent, and UI-scale-independent.
///
/// The returned rectangles are in **physical pixel coordinates relative
/// to the bitmap** (not screen coordinates).  The caller translates them
/// to screen space by adding <c>WindowSnapshot.ClientX/ClientY</c>
/// when positioning the overlay, or uses them directly for cropping
/// sub-regions from the same bitmap.
/// </summary>
public interface IRewardLayoutDetector
{
    /// <summary>
    /// Analyses <paramref name="windowScreenshot"/> and returns one
    /// <see cref="Rectangle"/> per detected reward card (typically 1–4).
    ///
    /// Each rectangle covers the text region of a single card — sized
    /// for OCR cropping, not the full card artwork.
    ///
    /// Returns an empty list if no reward screen is visible in the image
    /// (e.g. wrong game phase, animation still playing, or unrecognized
    /// layout).
    /// </summary>
    /// <param name="windowScreenshot">
    /// Full client-area capture of the Warframe window, in physical pixels.
    /// </param>
    /// <param name="windowWidth">
    /// Width of <paramref name="windowScreenshot"/> in pixels.
    /// Passed explicitly so implementations never touch the Bitmap's
    /// internal properties on a different thread.
    /// </param>
    /// <param name="windowHeight">
    /// Height of <paramref name="windowScreenshot"/> in pixels.
    /// </param>
    List<Rectangle> DetectCardBoundaries(
        Bitmap windowScreenshot,
        int windowWidth,
        int windowHeight);
}