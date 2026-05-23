namespace WarframeRelicOverlay.Tests.OverlayApp.Layout;

using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using WarframeRelicOverlay.OverlayApp.Layout;
using Xunit;

/// <summary>
/// Unit tests for <see cref="IntensityProfileDetector"/> using fully synthetic
/// bitmaps — no game session or real screenshot required.
///
/// The helper <see cref="CreateBorderBitmap"/> produces an 800×450 black bitmap
/// with N equal-width bright rectangles painted at y=<see cref="BorderY"/>,
/// matching the structure the detector expects (a bright horizontal border line
/// segmented by card positions).
/// </summary>
public sealed class IntensityProfileDetectorTests
{
    // ── Synthetic-bitmap constants ──────────────────────────────────
    // 800×450 is large enough for the detector's fractions to produce
    // meaningful pixel counts while staying cheap to allocate.

    private const int W = 800;
    private const int H = 450;

    // Border painted at 40 % of height — well within [searchTop=30%, searchBot=55%].
    private const int BorderY = 180;

    // Gold-ish colour whose BT.601 luminance (≈ 201) is far above the
    // detector's 40-brightness rejection threshold.
    private static readonly Color BrightGold = Color.FromArgb(220, 200, 160);

    private readonly IntensityProfileDetector _detector = new();

    // ── Size guard ──────────────────────────────────────────────────

    [Theory]
    [InlineData(319, 239)]  // both below threshold
    [InlineData(320, 239)]  // height just below 240
    [InlineData(319, 240)]  // width just below 320
    [InlineData(1, 1)]
    public void DetectCardBoundaries_ReturnsEmpty_WhenDimensionsBelowMinimum(int w, int h)
    {
        // Bitmap must have at least 1×1 physical pixels; claimed dimensions
        // are what the detector checks before touching pixels.
        using var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format24bppRgb);
        _detector.DetectCardBoundaries(bmp, w, h).Should().BeEmpty();
    }

    // ── Featureless scenes ──────────────────────────────────────────

    [Fact]
    public void DetectCardBoundaries_ReturnsEmpty_WhenBitmapIsAllBlack()
    {
        // bestBrightness = 0, fails the > 40 guard → no border found.
        using var bmp = FilledBitmap(W, H, Color.Black);
        _detector.DetectCardBoundaries(bmp, W, H)
                 .Should().BeEmpty("a fully black frame has no bright border line");
    }

    [Fact]
    public void DetectCardBoundaries_ReturnsEmpty_WhenBitmapIsUniformGray()
    {
        // Uniform 128: Otsu threshold ≈ 128, profile[i] = 128, no column satisfies
        // profile[i] > threshold, so FindSegments returns nothing.
        using var bmp = FilledBitmap(W, H, Color.FromArgb(128, 128, 128));
        _detector.DetectCardBoundaries(bmp, W, H)
                 .Should().BeEmpty("a uniformly grey frame has no separable card segments");
    }

    // ── Card count detection ────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DetectCardBoundaries_ReturnsCorrectCount_ForNEqualCards(int numCards)
    {
        using var bmp = CreateBorderBitmap(numCards);
        _detector.DetectCardBoundaries(bmp, W, H)
                 .Should().HaveCount(numCards,
                     $"bitmap was painted with {numCards} equal-width card segments");
    }

    // ── Width-consistency rejection ─────────────────────────────────

    [Fact]
    public void DetectCardBoundaries_ReturnsEmpty_WhenCardWidthsAreHighlyInconsistent()
    {
        // Four segments with very different widths: 50, 130, 40, 130 px.
        // CV ≈ 0.49 >> MaxWidthCv(0.25) — should be rejected.
        var segments = new (int Start, int End)[]
        {
            (210, 260),
            (270, 400),
            (410, 450),
            (460, 590),
        };

        using var bmp = CreateBorderBitmapFromSegments(segments);
        _detector.DetectCardBoundaries(bmp, W, H)
                 .Should().BeEmpty("wildly unequal card widths exceed the CV threshold");
    }

    // ── Geometry of returned rectangles ────────────────────────────

    [Fact]
    public void DetectCardBoundaries_TextRects_LieAboveBorderLine()
    {
        using var bmp = CreateBorderBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().NotBeEmpty();
        foreach (var rect in result)
        {
            rect.Y.Should().BeGreaterThanOrEqualTo(0, "text top must be inside the bitmap");
            // textBottom = borderY; allow ±5 px for rounding at different band offsets.
            rect.Bottom.Should().BeLessThanOrEqualTo(BorderY + 5,
                "text row sits immediately above the detected border line");
        }
    }

    [Fact]
    public void DetectCardBoundaries_TextRects_HavePositiveDimensions()
    {
        using var bmp = CreateBorderBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().NotBeEmpty();
        foreach (var rect in result)
        {
            rect.Width.Should().BePositive();
            rect.Height.Should().BePositive();
        }
    }

    [Fact]
    public void DetectCardBoundaries_TextRects_AreOrderedLeftToRight()
    {
        using var bmp = CreateBorderBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().HaveCount(4);
        for (int i = 1; i < result.Count; i++)
            result[i].X.Should().BeGreaterThan(result[i - 1].X,
                $"rect[{i}] must start to the right of rect[{i - 1}]");
    }

    [Fact]
    public void DetectCardBoundaries_TextRects_ShareTheSameVerticalPosition()
    {
        using var bmp = CreateBorderBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().HaveCount(4);
        int expectedY      = result[0].Y;
        int expectedHeight = result[0].Height;

        foreach (var rect in result)
        {
            rect.Y.Should().Be(expectedY,     "all cards share the same text-row top");
            rect.Height.Should().Be(expectedHeight, "all cards share the same text-row height");
        }
    }

    [Fact]
    public void DetectCardBoundaries_TextRects_HaveMatchingWidths_ForEqualCards()
    {
        using var bmp = CreateBorderBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().HaveCount(4);
        int expectedWidth = result[0].Width;

        foreach (var rect in result)
            rect.Width.Should().Be(expectedWidth,
                "equal-width card segments produce equal-width text rectangles");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates N evenly-spaced equal-width bright segments spanning the
    /// horizontal centre of an 800×450 black bitmap, painted at
    /// <see cref="BorderY"/>.  The card area sits inside the detector's
    /// mid-X range [25 %, 75 %] = [200, 600] px.
    /// </summary>
    private static Bitmap CreateBorderBitmap(int numCards)
    {
        int areaStart = (int)(0.27 * W);  // 216 — inside mid-X range
        int areaEnd   = (int)(0.73 * W);  // 584
        int gapPx     = 20;
        int cardWidth  = (areaEnd - areaStart - gapPx * Math.Max(0, numCards - 1)) / numCards;

        var segments = Enumerable.Range(0, numCards)
            .Select(i =>
            {
                int x = areaStart + i * (cardWidth + gapPx);
                return (Start: x, End: x + cardWidth);
            })
            .ToArray();

        return CreateBorderBitmapFromSegments(segments);
    }

    /// <summary>
    /// Paints each segment as a bright horizontal rectangle at
    /// <see cref="BorderY"/> on an otherwise black 800×450 bitmap.
    /// Height 8 > BandHeight(4) ensures the entire averaging band
    /// falls within the painted region.
    /// </summary>
    private static Bitmap CreateBorderBitmapFromSegments((int Start, int End)[] segments)
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);

        using var brush = new SolidBrush(BrightGold);
        foreach (var (start, end) in segments)
            g.FillRectangle(brush, start, BorderY, end - start, 8);

        return bmp;
    }

    private static Bitmap FilledBitmap(int width, int height, Color color)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }
}
