namespace WarframeRelicOverlay.Tests.Integration;

using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using Xunit;
using WarframeRelicOverlay.Infrastructure.OCR;

// ── What these tests prove ────────────────────────────────────────────────────
// 1. ImagePreprocessorTests  — pure logic, no Tesseract, no game needed.
//    We feed in synthetic bitmaps with known pixel values and assert the output.
//
// 2. TesseractOcrEngineTests — loads a real reward-box PNG captured from the game
//    (committed to test-images/) and asserts that the full preprocess → OCR
//    pipeline produces recognisable text.  Requires tessdata/eng.traineddata
//    which is copied to the output directory by Integration.Tests.csproj.
// ─────────────────────────────────────────────────────────────────────────────

public class ImagePreprocessorTests
{
    [Fact]
    public void Prepare_ReturnsBitmapWithSameDimensions()
    {
        // A 200×80 bitmap with a clear dark/light split so Otsu has something to work with.
        using var input = MakeSplitBitmap(200, 80);

        using var result = ImagePreprocessor.Prepare(input);

        result.Width.Should().Be(200);
        result.Height.Should().Be(80);
    }

    [Fact]
    public void Prepare_OutputIsBinaryImage()
    {
        // Every pixel in the output must be exactly black (0) or white (255).
        // No in-between greys — if there are any, thresholding is broken.
        using var input = MakeSplitBitmap(100, 60);

        using var result = ImagePreprocessor.Prepare(input);

        for (int x = 0; x < result.Width; x++)
        {
            for (int y = 0; y < result.Height; y++)
            {
                var px = result.GetPixel(x, y);
                bool isBinary = (px.R == 0 || px.R == 255) &&
                                (px.G == 0 || px.G == 255) &&
                                (px.B == 0 || px.B == 255);
                isBinary.Should().BeTrue(
                    $"pixel at ({x},{y}) has value ({px.R},{px.G},{px.B}) — not a clean binary result");
            }
        }
    }

    [Fact]
    public void Prepare_DarkAndLightRegionsEndUpWithDifferentValues()
    {
        // Left half black, right half white.
        // After thresholding + polarity correction the two halves must differ.
        using var input = MakeSplitBitmap(100, 40);

        using var result = ImagePreprocessor.Prepare(input);

        var leftPixel  = result.GetPixel(10, 20);   // inside the dark half
        var rightPixel = result.GetPixel(80, 20);   // inside the bright half

        leftPixel.R.Should().NotBe(rightPixel.R,
            "the dark and bright halves of the source should map to different binary values");
    }

    // ── helper ──────────────────────────────────────────────────────────────
    // Creates a bitmap whose left half is black and right half is white.
    // This gives Otsu a perfectly bimodal histogram to work with.
    private static Bitmap MakeSplitBitmap(int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.FillRectangle(Brushes.Black, 0,         0, width / 2, height);
        g.FillRectangle(Brushes.White, width / 2, 0, width / 2, height);
        return bmp;
    }
}

public class TesseractOcrEngineTests
{
    // AppContext.BaseDirectory is the test output directory at runtime — the same
    // folder where Integration.Tests.csproj copies tessdata\ and test-images\.
    private static string BaseDir => AppContext.BaseDirectory;

    [Fact]
    public void Recognize_AfterPreprocessing_ExtractsTextFromRealRewardBox()
    {
        // Arrange — paths resolved to output dir so they work in VS, Rider, and dotnet test
        string imagePath    = Path.Combine(BaseDir, "test-images", "reward_box_braton_stock.png");
        string tessDataPath = Path.Combine(BaseDir, "tessdata");

        // Confirm test assets are in place before blaming OCR
        File.Exists(imagePath).Should().BeTrue(
            $"test image not found at {imagePath} — check Integration.Tests.csproj Content items");
        Directory.Exists(tessDataPath).Should().BeTrue(
            $"tessdata not found at {tessDataPath} — check Integration.Tests.csproj Content items");

        // Act
        using var raw          = new Bitmap(imagePath);
        using var preprocessed = ImagePreprocessor.Prepare(raw);
        using var engine       = new TesseractOcrEngine(tessDataPath, poolSize: 1);
        string result          = engine.Recognize(preprocessed);

        // Assert — "Braton Prime Stock" is in the image; at minimum "prime" should survive OCR
        bool containsExpected = result.Contains("prime",  StringComparison.OrdinalIgnoreCase)
                             || result.Contains("braton", StringComparison.OrdinalIgnoreCase)
                             || result.Contains("stock",  StringComparison.OrdinalIgnoreCase);

        containsExpected.Should().BeTrue(
            $"expected the OCR result to contain 'prime', 'braton', or 'stock', " +
            $"but got: '{result}'");
    }

    [Fact]
    public void Recognize_PreprocessedImage_IsDisposedCleanly()
    {
        // Verifies that Dispose() releases all pooled engines without throwing.
        string tessDataPath = Path.Combine(BaseDir, "tessdata");
        Directory.Exists(tessDataPath).Should().BeTrue();

        using var imagePath = new Bitmap(50, 20, PixelFormat.Format24bppRgb);
        using var preprocessed = ImagePreprocessor.Prepare(imagePath);
        var engine = new TesseractOcrEngine(tessDataPath, poolSize: 2);

        // Force pool to create an engine by calling Recognize
        engine.Recognize(preprocessed);

        // Should not throw
        var dispose = () => engine.Dispose();
        dispose.Should().NotThrow();
    }
}
