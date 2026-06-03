namespace WarframeRelicOverlay.Tests.Integration.Layout;

using System.Drawing;
using FluentAssertions;
using WarframeRelicOverlay.Domain.Matching;
using WarframeRelicOverlay.Infrastructure.OCR;
using WarframeRelicOverlay.Infrastructure.RewardData;
using WarframeRelicOverlay.OverlayApp.Layout;
using Xunit;

/// <summary>
/// Regression test for the "4-reward gibberish" bug: when four cards are
/// shown, each card is narrow enough that long item names wrap to two
/// lines (e.g. "Nidus Prime Neuroptics&#10;Blueprint").  The layout
/// detector crops the full two-line name, so the OCR engine must read a
/// multi-line block — <see cref="Tesseract.PageSegMode.SingleLine"/>
/// garbles stacked lines, whereas <c>SingleBlock</c> reads them correctly.
///
/// <para>The fixture <c>reward_4cards_wrapped.png</c> is a real four-card
/// capture whose first two cards are wrapped two-line names.  The test
/// runs the production path — detector → OCR → fuzzy matcher — and asserts
/// every card resolves to a reward, which only happens when the wrapped
/// names are read as a multi-line block.</para>
/// </summary>
public sealed class WrappedNameOcrTests
{
    private static readonly string TestImagesDir =
        Path.Combine(AppContext.BaseDirectory, "test-images");

    private static readonly string TessDataDir =
        Path.Combine(AppContext.BaseDirectory, "tessdata");

    private static readonly string ItemsJsonPath =
        Path.Combine(AppContext.BaseDirectory, "data", "items.json");

    private readonly WarmTextRowDetector _detector = new();

    [Fact]
    public void DetectorOcrAndMatcher_ResolveAllFourCards_IncludingWrappedNames()
    {
        string imagePath = Path.Combine(TestImagesDir, "reward_4cards_wrapped.png");
        File.Exists(imagePath).Should().BeTrue($"fixture must be present at {imagePath}");
        File.Exists(ItemsJsonPath).Should().BeTrue($"items.json must be present at {ItemsJsonPath}");

        using var screenshot = new Bitmap(imagePath);
        var cards = _detector.DetectCardBoundaries(screenshot, screenshot.Width, screenshot.Height);
        cards.Should().HaveCount(4, "the fixture is a four-reward screen");

        using var ocr = new TesseractOcrEngine(TessDataDir, poolSize: 2);
        var matcher = new FuzzyRewardMatcher(new JsonRewardRepository(ItemsJsonPath));

        var matched = new List<string?>();
        foreach (Rectangle rect in cards)
        {
            Rectangle safe = Rectangle.Intersect(rect, new Rectangle(0, 0, screenshot.Width, screenshot.Height));
            using var crop = screenshot.Clone(safe, screenshot.PixelFormat);
            using var prepared = ImagePreprocessor.Prepare(crop);
            string text = ocr.Recognize(prepared);
            matched.Add(matcher.MatchSingle(text)?.CanonicalName);
        }

        // Every card must resolve, and the two wrapped two-line names in
        // particular — they are the cases SingleLine OCR turned to gibberish.
        matched.Should().NotContainNulls("every visible card must match a reward");
        matched.Should().Contain(n => n!.Contains("Neuroptics", StringComparison.OrdinalIgnoreCase),
            "the wrapped 'Nidus Prime Neuroptics Blueprint' must resolve");
        matched.Should().Contain(n => n!.Contains("Nyx Prime Systems", StringComparison.OrdinalIgnoreCase),
            "the wrapped 'Nyx Prime Systems Blueprint' must resolve");
    }
}
