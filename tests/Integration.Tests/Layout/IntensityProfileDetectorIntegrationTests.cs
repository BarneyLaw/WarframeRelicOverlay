namespace WarframeRelicOverlay.Tests.Integration.Layout;

using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using WarframeRelicOverlay.OverlayApp.Layout;
using Xunit;

/// <summary>
/// Integration tests for <see cref="IntensityProfileDetector"/> against a real
/// Warframe screenshot.
///
/// <b>What this test does:</b>
/// <list type="number">
///   <item>Loads <c>test-images/whole_screen.png</c> — a full client-area capture
///         of the Warframe window during reward selection.</item>
///   <item>Runs <see cref="IntensityProfileDetector.DetectCardBoundaries"/>.</item>
///   <item>Asserts that 1–4 reward cards were found.</item>
///   <item>Crops each detected text region and saves it to
///         <c>image_output/card_{i}.png</c> next to the test binary for visual
///         inspection.</item>
/// </list>
/// </summary>
public sealed class IntensityProfileDetectorIntegrationTests
{
    private static readonly string TestImagesDir =
        Path.Combine(AppContext.BaseDirectory, "test-images");

    private static readonly string ImageOutputDir =
        Path.Combine(AppContext.BaseDirectory, "image_output");

    private readonly IntensityProfileDetector _detector = new();

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

        cards.Count.Should().BeInRange(1, 4,
            "whole_screen.png shows a Warframe reward-selection screen with 1–4 cards");

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
}
