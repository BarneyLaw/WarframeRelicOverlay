namespace WarframeRelicOverlay.Tests.OverlayApp.Layout;

using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using WarframeRelicOverlay.OverlayApp.Layout;
using Xunit;

/// <summary>
/// Unit tests for <see cref="WarmTextRowDetector"/> using fully synthetic
/// bitmaps — no game session or real screenshot required.
///
/// The helper <see cref="CreateNameRowBitmap"/> produces an 800×450 black
/// bitmap with N equal-width, evenly-spaced warm rectangles painted at
/// y=<see cref="NameRowY"/>, matching the structure the detector expects
/// (a row of warm item-name text segments).
/// </summary>
public sealed class WarmTextRowDetectorTests
{
    private const int W = 800;
    private const int H = 450;

    // Name row painted at 44 % of height — inside [searchTop=25%, searchBot=58%].
    private const int NameRowY = 200;

    // Warm amber matching the detector's mask: R≥G>B, B<110, R−B>50.
    private static readonly Color WarmAmber = Color.FromArgb(220, 170, 60);

    private readonly WarmTextRowDetector _detector = new();

    // ── Size guard ──────────────────────────────────────────────────

    [Theory]
    [InlineData(319, 239)]
    [InlineData(320, 239)]
    [InlineData(319, 240)]
    [InlineData(1, 1)]
    public void DetectCardBoundaries_ReturnsEmpty_WhenDimensionsBelowMinimum(int w, int h)
    {
        using var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format24bppRgb);
        _detector.DetectCardBoundaries(bmp, w, h).Should().BeEmpty();
    }

    // ── Featureless scenes ──────────────────────────────────────────

    [Fact]
    public void DetectCardBoundaries_ReturnsEmpty_WhenBitmapIsAllBlack()
    {
        using var bmp = FilledBitmap(W, H, Color.Black);
        _detector.DetectCardBoundaries(bmp, W, H)
                 .Should().BeEmpty("a fully black frame has no warm text");
    }

    [Fact]
    public void DetectCardBoundaries_ReturnsEmpty_WhenBitmapIsUniformGray()
    {
        // Grey (128,128,128) is not warm (B not < 110, R not > B), so no mask.
        using var bmp = FilledBitmap(W, H, Color.FromArgb(128, 128, 128));
        _detector.DetectCardBoundaries(bmp, W, H)
                 .Should().BeEmpty("a uniformly grey frame has no warm text");
    }

    // ── Card count detection ────────────────────────────────────────

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void DetectCardBoundaries_ReturnsCorrectCount_ForNEqualCards(int numCards)
    {
        using var bmp = CreateNameRowBitmap(numCards);
        _detector.DetectCardBoundaries(bmp, W, H)
                 .Should().HaveCount(numCards,
                     $"bitmap was painted with {numCards} evenly-spaced name segments");
    }

    [Fact]
    public void DetectCardBoundaries_ReturnsEmpty_ForASingleCard()
    {
        // A single segment is below the 2-card minimum for a reward screen.
        using var bmp = CreateNameRowBitmap(1);
        _detector.DetectCardBoundaries(bmp, W, H)
                 .Should().BeEmpty("reward screens always show at least two cards");
    }

    // ── Spacing-consistency rejection ───────────────────────────────

    [Fact]
    public void DetectCardBoundaries_ReturnsEmpty_WhenCardSpacingIsUneven()
    {
        // Four equal-width segments with unequal centre spacing.
        var segments = new (int Start, int End)[]
        {
            (216, 256),
            (300, 340),
            (470, 510),
            (560, 600),
        };

        using var bmp = CreateNameRowBitmapFromSegments(segments);
        _detector.DetectCardBoundaries(bmp, W, H)
                 .Should().BeEmpty("unevenly-spaced segments are not reward cards");
    }

    // ── Geometry of returned rectangles ─────────────────────────────

    [Fact]
    public void DetectCardBoundaries_TextRects_HavePositiveDimensions()
    {
        using var bmp = CreateNameRowBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().NotBeEmpty();
        foreach (var rect in result)
        {
            rect.Width.Should().BePositive();
            rect.Height.Should().BePositive();
        }
    }

    [Fact]
    public void DetectCardBoundaries_TextRects_CoverTheNameRow()
    {
        using var bmp = CreateNameRowBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().NotBeEmpty();
        foreach (var rect in result)
        {
            rect.Y.Should().BeLessThanOrEqualTo(NameRowY, "crop starts above the name row");
            rect.Bottom.Should().BeGreaterThanOrEqualTo(NameRowY, "crop reaches the name row");
        }
    }

    [Fact]
    public void DetectCardBoundaries_TextRects_AreOrderedLeftToRight()
    {
        using var bmp = CreateNameRowBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().HaveCount(4);
        for (int i = 1; i < result.Count; i++)
            result[i].X.Should().BeGreaterThan(result[i - 1].X,
                $"rect[{i}] must start to the right of rect[{i - 1}]");
    }

    [Fact]
    public void DetectCardBoundaries_TextRects_ShareTheSameVerticalPosition()
    {
        using var bmp = CreateNameRowBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().HaveCount(4);
        int expectedY = result[0].Y;
        int expectedHeight = result[0].Height;

        foreach (var rect in result)
        {
            rect.Y.Should().Be(expectedY, "all cards share the same crop top");
            rect.Height.Should().Be(expectedHeight, "all cards share the same crop height");
        }
    }

    [Fact]
    public void DetectCardBoundaries_TextRects_HaveMatchingWidths_ForEqualCards()
    {
        using var bmp = CreateNameRowBitmap(4);
        var result = _detector.DetectCardBoundaries(bmp, W, H);

        result.Should().HaveCount(4);
        int expectedWidth = result[0].Width;

        foreach (var rect in result)
            rect.Width.Should().Be(expectedWidth,
                "uniform spacing yields uniform crop widths");
    }

    // ── Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates N evenly-spaced equal-width warm segments spanning the
    /// horizontal centre of an 800×450 black bitmap, painted at
    /// <see cref="NameRowY"/>.
    /// </summary>
    private static Bitmap CreateNameRowBitmap(int numCards)
    {
        int areaStart = (int)(0.27 * W);  // 216
        int areaEnd = (int)(0.73 * W);    // 584
        int gapPx = 30;
        int cardWidth = (areaEnd - areaStart - gapPx * Math.Max(0, numCards - 1))
                        / Math.Max(1, numCards);

        var segments = Enumerable.Range(0, numCards)
            .Select(i =>
            {
                int x = areaStart + i * (cardWidth + gapPx);
                return (Start: x, End: x + cardWidth);
            })
            .ToArray();

        return CreateNameRowBitmapFromSegments(segments);
    }

    /// <summary>
    /// Paints each segment as a warm horizontal rectangle at
    /// <see cref="NameRowY"/> on an otherwise black 800×450 bitmap.
    /// </summary>
    private static Bitmap CreateNameRowBitmapFromSegments((int Start, int End)[] segments)
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);

        using var brush = new SolidBrush(WarmAmber);
        foreach (var (start, end) in segments)
            g.FillRectangle(brush, start, NameRowY, end - start, 14);

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
