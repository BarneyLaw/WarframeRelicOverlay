namespace WarframeRelicOverlay.Tests.Integration.Layout;

using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using WarframeRelicOverlay.OverlayApp.Layout;
using Xunit;

/// <summary>
/// Integration tests for <see cref="WarmTextRowDetector"/> against a real
/// Warframe screenshot.
///
/// <b>What this test does:</b>
/// <list type="number">
///   <item>Loads <c>test-images/whole_screen.png</c> — a full client-area capture
///         of the Warframe window during reward selection.</item>
///   <item>Runs <see cref="WarmTextRowDetector.DetectCardBoundaries"/>.</item>
///   <item>Asserts that 2–4 reward cards were found.</item>
///   <item>Crops each detected text region and saves it to
///         <c>image_output/card_{i}.png</c> next to the test binary for visual
///         inspection.</item>
/// </list>
/// </summary>
public sealed class WarmTextRowDetectorIntegrationTests
{
    private static readonly string TestImagesDir =
        Path.Combine(AppContext.BaseDirectory, "test-images");

    private static readonly string ImageOutputDir =
        Path.Combine(AppContext.BaseDirectory, "image_output");

    private readonly WarmTextRowDetector _detector = new();

    [Fact]
    public void DetectCardBoundaries_OnWholeScreen_FindsRewardCardsAndSavesCrops()
    {
        string imagePath = Path.Combine(TestImagesDir, "whole_screen.png");
        File.Exists(imagePath).Should().BeTrue(
            $"test image must be present at {imagePath} — check the test-images/ directory");

        Directory.CreateDirectory(ImageOutputDir);

        using var screenshot = new Bitmap(imagePath);
        int w = screenshot.Width;
        int h = screenshot.Height;

        var cards = _detector.DetectCardBoundaries(screenshot, w, h);

        cards.Count.Should().BeInRange(2, 4,
            "whole_screen.png shows a Warframe reward-selection screen with 2–4 cards");

        for (int i = 0; i < cards.Count; i++)
        {
            Rectangle rect = cards[i];

            // Clamp to bitmap bounds to guard against ±1 rounding at edges.
            Rectangle safe = Rectangle.Intersect(rect, new Rectangle(0, 0, w, h));
            safe.Width.Should().BePositive($"card {i} crop must have positive width");
            safe.Height.Should().BePositive($"card {i} crop must have positive height");

            using var crop = screenshot.Clone(safe, screenshot.PixelFormat);
            string outputPath = Path.Combine(ImageOutputDir, $"card_{i}.png");
            crop.Save(outputPath, ImageFormat.Png);

            File.Exists(outputPath).Should().BeTrue(
                $"card_{i}.png should have been written to {ImageOutputDir}");
        }
    }

    /// <summary>
    /// Regression test for the dropped-card mismatch: <c>reward_4cards_silva_wrapped.png</c>
    /// is a real four-card capture whose leftmost reward, "Silva &amp; Aegis Prime
    /// Blade", wraps to two lines.  On the row shared by the three single-line
    /// names, that card contributes only its short second line ("Blade"), which
    /// is below the minimum segment width, so a row-only detector reported just
    /// three cards.  The detector must project the warm mask down the crop band
    /// to recover the wrapped card and return all four.
    /// </summary>
    [Fact]
    public void DetectCardBoundaries_CountsWrappedCard_WhenSecondLineIsNarrow()
    {
        string imagePath = Path.Combine(TestImagesDir, "reward_4cards_silva_wrapped.png");
        File.Exists(imagePath).Should().BeTrue($"fixture must be present at {imagePath}");

        using var screenshot = new Bitmap(imagePath);
        var cards = _detector.DetectCardBoundaries(screenshot, screenshot.Width, screenshot.Height);

        cards.Should().HaveCount(4,
            "the screen shows four cards even though the leftmost name wraps to two lines");
    }
}
