namespace WarframeRelicOverlay.Tests.OverlayApp.Pipeline;

using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using WarframeRelicOverlay.Domain.Matching;
using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Domain.Pricing;
using WarframeRelicOverlay.Infrastructure.OCR;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.Infrastructure.ScreenCapture;
using WarframeRelicOverlay.OverlayApp.Layout;
using WarframeRelicOverlay.OverlayApp.Pipeline;
using Xunit;

public class RewardPricingPipelineTests
{
    // ── Fakes ───────────────────────────────────────────────────

    /// <summary>
    /// Returns a pre-configured bitmap on <see cref="CaptureWindow"/>.
    /// The bitmap is a plain white image large enough for the pipeline
    /// to crop sub-regions without hitting out-of-bounds.
    /// </summary>
    private sealed class FakeCapturer : IScreenCapturer
    {
        public Bitmap? BitmapToReturn { get; set; }

        public Bitmap? CaptureWindow(WindowSnapshot window) => BitmapToReturn;
        public Bitmap? CaptureRegion(Rectangle physicalRegion) => null;
    }

    /// <summary>
    /// Returns a pre-configured list of card rectangles.
    /// </summary>
    private sealed class FakeLayoutDetector : IRewardLayoutDetector
    {
        public List<Rectangle> CardsToReturn { get; set; } = [];

        public List<Rectangle> DetectCardBoundaries(
            Bitmap windowScreenshot, int windowWidth, int windowHeight) =>
            CardsToReturn;
    }

    /// <summary>
    /// Returns canned OCR text per call index. Thread-safe via
    /// <see cref="Interlocked.Increment"/>.
    /// </summary>
    private sealed class FakeOcrEngine : IOcrEngine
    {
        private readonly string[] _responses;
        private int _callIndex = -1;

        public FakeOcrEngine(params string[] responses) => _responses = responses;

        /// <summary>
        /// Maps bitmap identity to response. Since the pipeline calls
        /// Recognize from parallel tasks and ordering is non-deterministic,
        /// we return based on call order — tests that care about specific
        /// card-to-text mappings should use <see cref="MappedOcrEngine"/>.
        /// </summary>
        public string Recognize(Bitmap image)
        {
            int idx = Interlocked.Increment(ref _callIndex);
            return idx < _responses.Length ? _responses[idx] : string.Empty;
        }
    }

    /// <summary>
    /// Maps card widths to OCR responses so tests can control exactly
    /// which rectangle produces which text, regardless of parallel
    /// execution order.  Uses the card rectangle's Width as the key
    /// since each fake card has a unique width in the test setup.
    /// </summary>
    private sealed class MappedOcrEngine : IOcrEngine
    {
        private readonly Dictionary<int, string> _widthToText = new();

        public void Map(int bitmapWidth, string ocrText) =>
            _widthToText[bitmapWidth] = ocrText;

        public string Recognize(Bitmap image) =>
            _widthToText.TryGetValue(image.Width, out var text) ? text : string.Empty;
    }

    /// <summary>
    /// Returns a pre-configured match for a given OCR text.
    /// </summary>
    private sealed class FakeMatcher : IRewardMatcher
    {
        public Dictionary<string, RewardItem> Matches { get; } = new();

        public RewardItem? MatchSingle(string ocrText) =>
            Matches.TryGetValue(ocrText.Trim(), out var item) ? item : null;

        public IEnumerable<RewardItem> Match(string ocrText)
        {
            var m = MatchSingle(ocrText);
            if (m is not null) yield return m;
        }
    }

    /// <summary>
    /// Returns a pre-configured price for a given slug.
    /// Records call counts for verification.
    /// </summary>
    private sealed class FakePricer : IPriceProvider
    {
        public Dictionary<string, int?> Prices { get; } = new();
        private int _calls;

        public Task<int?> GetPriceAsync(string itemName)
        {
            Interlocked.Increment(ref _calls);
            int? price = Prices.TryGetValue(itemName, out var p) ? p : null;
            return Task.FromResult(price);
        }

        public int CallCount => _calls;
    }

    // ── Shared helpers ──────────────────────────────────────────

    private static readonly WindowSnapshot TestWindow = new(
        ClientX: 0,
        ClientY: 0,
        ClientWidth: 1920,
        ClientHeight: 1080,
        DpiScaleX: 1.0,
        DpiScaleY: 1.0);

    /// <summary>
    /// Creates a plain white bitmap suitable for cropping and preprocessing.
    /// The pipeline disposes the screenshot internally, so the test does
    /// not need to track it.
    /// </summary>
    private static Bitmap MakeTestBitmap(int width = 1920, int height = 1080)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        return bmp;
    }

    // ── Happy path ──────────────────────────────────────────────

    [Fact]
    public async Task HappyPath_FourCards_AllMatchedAndPriced()
    {
        // Arrange: 4 cards, each with distinct widths for MappedOcrEngine
        var rects = new List<Rectangle>
        {
            new(100, 400, 200, 60),   // width 200
            new(310, 400, 201, 60),   // width 201
            new(520, 400, 202, 60),   // width 202
            new(730, 400, 203, 60),   // width 203
        };

        var ocr = new MappedOcrEngine();
        ocr.Map(200, "Ash Prime Chassis Blueprint");
        ocr.Map(201, "Braton Prime Receiver");
        ocr.Map(202, "Forma Blueprint");
        ocr.Map(203, "Orthos Prime Blade");

        var matcher = new FakeMatcher();
        matcher.Matches["Ash Prime Chassis Blueprint"] =
            new RewardItem("Ash Prime Chassis Blueprint");
        matcher.Matches["Braton Prime Receiver"] =
            new RewardItem("Braton Prime Receiver");
        matcher.Matches["Forma Blueprint"] =
            new RewardItem("Forma Blueprint", IsUntradeable: true);
        matcher.Matches["Orthos Prime Blade"] =
            new RewardItem("Orthos Prime Blade");

        var pricer = new FakePricer();
        pricer.Prices["ash_prime_chassis_blueprint"] = 15;
        pricer.Prices["braton_prime_receiver"] = 5;
        pricer.Prices["orthos_prime_blade"] = 8;

        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = rects },
            ocr,
            matcher,
            pricer);

        // Act
        var result = await pipeline.ExecuteAsync(TestWindow);

        // Assert
        result.HasCards.Should().BeTrue();
        result.Cards.Should().HaveCount(4);

        // Sort by index since parallel execution order is non-deterministic
        var sorted = result.Cards.OrderBy(c => c.Index).ToList();

        sorted[0].MatchedItem!.CanonicalName.Should().Be("Ash Prime Chassis Blueprint");
        sorted[0].PricePlatinum.Should().Be(15);
        sorted[0].DisplayText.Should().Be("15p");

        sorted[1].MatchedItem!.CanonicalName.Should().Be("Braton Prime Receiver");
        sorted[1].PricePlatinum.Should().Be(5);

        sorted[2].MatchedItem!.CanonicalName.Should().Be("Forma Blueprint");
        sorted[2].MatchedItem!.IsUntradeable.Should().BeTrue();
        sorted[2].PricePlatinum.Should().BeNull();
        sorted[2].DisplayText.Should().Be("Untradeable");

        sorted[3].MatchedItem!.CanonicalName.Should().Be("Orthos Prime Blade");
        sorted[3].PricePlatinum.Should().Be(8);

        result.Window.Should().Be(TestWindow);
        result.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);
    }

    // ── Empty / null cases ──────────────────────────────────────

    [Fact]
    public async Task CaptureReturnsNull_ReturnsEmptyResult()
    {
        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = null },
            new FakeLayoutDetector(),
            new FakeOcrEngine(),
            new FakeMatcher(),
            new FakePricer());

        var result = await pipeline.ExecuteAsync(TestWindow);

        result.HasCards.Should().BeFalse();
        result.Cards.Should().BeEmpty();
        result.Window.Should().Be(TestWindow);
    }

    [Fact]
    public async Task NoCardsDetected_ReturnsEmptyResult()
    {
        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = [] },
            new FakeOcrEngine(),
            new FakeMatcher(),
            new FakePricer());

        var result = await pipeline.ExecuteAsync(TestWindow);

        result.HasCards.Should().BeFalse();
        result.Cards.Should().BeEmpty();
    }

    // ── Partial failures ────────────────────────────────────────

    [Fact]
    public async Task OcrReturnsEmpty_CardHasNullMatch()
    {
        var rects = new List<Rectangle> { new(100, 400, 200, 60) };

        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = rects },
            new FakeOcrEngine(""),  // empty OCR
            new FakeMatcher(),
            new FakePricer());

        var result = await pipeline.ExecuteAsync(TestWindow);

        result.Cards.Should().HaveCount(1);
        result.Cards[0].MatchedItem.Should().BeNull();
        result.Cards[0].DisplayText.Should().Be("?");
    }

    [Fact]
    public async Task MatcherReturnsNull_CardShowsQuestionMark()
    {
        var rects = new List<Rectangle> { new(100, 400, 200, 60) };

        var ocr = new MappedOcrEngine();
        ocr.Map(200, "garbled nonsense text");

        // Matcher has no matches configured → returns null
        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = rects },
            ocr,
            new FakeMatcher(),
            new FakePricer());

        var result = await pipeline.ExecuteAsync(TestWindow);

        result.Cards.Should().HaveCount(1);
        result.Cards[0].MatchedItem.Should().BeNull();
        result.Cards[0].RawOcrText.Should().Be("garbled nonsense text");
        result.Cards[0].DisplayText.Should().Be("?");
        result.AllMatched.Should().BeFalse();
    }

    [Fact]
    public async Task PricerReturnsNull_CardShowsNA()
    {
        var rects = new List<Rectangle> { new(100, 400, 200, 60) };

        var ocr = new MappedOcrEngine();
        ocr.Map(200, "Ash Prime Chassis Blueprint");

        var matcher = new FakeMatcher();
        matcher.Matches["Ash Prime Chassis Blueprint"] =
            new RewardItem("Ash Prime Chassis Blueprint");

        // Pricer has no prices → returns null
        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = rects },
            ocr,
            matcher,
            new FakePricer());

        var result = await pipeline.ExecuteAsync(TestWindow);

        result.Cards.Should().HaveCount(1);
        result.Cards[0].MatchedItem.Should().NotBeNull();
        result.Cards[0].PricePlatinum.Should().BeNull();
        result.Cards[0].DisplayText.Should().Be("N/A");
    }

    // ── Untradeable items skip pricing ──────────────────────────

    [Fact]
    public async Task UntradeableItem_SkipsPriceLookup()
    {
        var rects = new List<Rectangle> { new(100, 400, 200, 60) };

        var ocr = new MappedOcrEngine();
        ocr.Map(200, "Forma Blueprint");

        var matcher = new FakeMatcher();
        matcher.Matches["Forma Blueprint"] =
            new RewardItem("Forma Blueprint", IsUntradeable: true);

        var pricer = new FakePricer();

        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = rects },
            ocr,
            matcher,
            pricer);

        var result = await pipeline.ExecuteAsync(TestWindow);

        result.Cards.Should().HaveCount(1);
        result.Cards[0].DisplayText.Should().Be("Untradeable");
        pricer.CallCount.Should().Be(0, "untradeable items should not call the pricer");
    }

    // ── Cancellation ────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_ThrowsOperationCanceled()
    {
        var rects = new List<Rectangle> { new(100, 400, 200, 60) };

        var cts = new CancellationTokenSource();
        cts.Cancel();  // pre-cancelled

        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = rects },
            new FakeOcrEngine("Ash Prime Chassis Blueprint"),
            new FakeMatcher(),
            new FakePricer());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(TestWindow, cts.Token));
    }

    // ── Card bounds are preserved ───────────────────────────────

    [Fact]
    public async Task CardBounds_MatchDetectorOutput()
    {
        var expectedRect = new Rectangle(150, 410, 220, 55);
        var rects = new List<Rectangle> { expectedRect };

        var ocr = new MappedOcrEngine();
        ocr.Map(220, "Braton Prime Receiver");

        var matcher = new FakeMatcher();
        matcher.Matches["Braton Prime Receiver"] =
            new RewardItem("Braton Prime Receiver");

        var pricer = new FakePricer();
        pricer.Prices["braton_prime_receiver"] = 5;

        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = rects },
            ocr,
            matcher,
            pricer);

        var result = await pipeline.ExecuteAsync(TestWindow);

        result.Cards[0].BoundsInWindow.Should().Be(expectedRect);
        result.Cards[0].Index.Should().Be(0);
    }

    // ── Mixed results (some match, some don't) ──────────────────

    [Fact]
    public async Task MixedResults_PartialMatchesReportedCorrectly()
    {
        var rects = new List<Rectangle>
        {
            new(100, 400, 200, 60),
            new(310, 400, 201, 60),
        };

        var ocr = new MappedOcrEngine();
        ocr.Map(200, "Ash Prime Chassis Blueprint");
        ocr.Map(201, "xyzzy garbage text");

        var matcher = new FakeMatcher();
        matcher.Matches["Ash Prime Chassis Blueprint"] =
            new RewardItem("Ash Prime Chassis Blueprint");
        // No match for "xyzzy garbage text"

        var pricer = new FakePricer();
        pricer.Prices["ash_prime_chassis_blueprint"] = 25;

        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = rects },
            ocr,
            matcher,
            pricer);

        var result = await pipeline.ExecuteAsync(TestWindow);

        result.Cards.Should().HaveCount(2);
        result.AllMatched.Should().BeFalse();

        var sorted = result.Cards.OrderBy(c => c.Index).ToList();
        sorted[0].IsSuccessful.Should().BeTrue();
        sorted[0].PricePlatinum.Should().Be(25);
        sorted[1].IsSuccessful.Should().BeFalse();
        sorted[1].RawOcrText.Should().Be("xyzzy garbage text");
    }

    // ── Timing metadata ─────────────────────────────────────────

    [Fact]
    public async Task Elapsed_IsPopulated()
    {
        var pipeline = new RewardPricingPipeline(
            new FakeCapturer { BitmapToReturn = MakeTestBitmap() },
            new FakeLayoutDetector { CardsToReturn = [new(100, 400, 200, 60)] },
            new FakeOcrEngine("Ash Prime Chassis Blueprint"),
            new FakeMatcher(),
            new FakePricer());

        var result = await pipeline.ExecuteAsync(TestWindow);

        result.Elapsed.Should().BeGreaterThan(TimeSpan.Zero);
    }

    // ── Constructor null guards ─────────────────────────────────

    [Fact]
    public void NullCapturer_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new RewardPricingPipeline(null!, new FakeLayoutDetector(),
                new FakeOcrEngine(), new FakeMatcher(), new FakePricer()));

    [Fact]
    public void NullLayoutDetector_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new RewardPricingPipeline(new FakeCapturer(), null!,
                new FakeOcrEngine(), new FakeMatcher(), new FakePricer()));

    [Fact]
    public void NullOcr_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new RewardPricingPipeline(new FakeCapturer(), new FakeLayoutDetector(),
                null!, new FakeMatcher(), new FakePricer()));

    [Fact]
    public void NullMatcher_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new RewardPricingPipeline(new FakeCapturer(), new FakeLayoutDetector(),
                new FakeOcrEngine(), null!, new FakePricer()));

    [Fact]
    public void NullPricer_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new RewardPricingPipeline(new FakeCapturer(), new FakeLayoutDetector(),
                new FakeOcrEngine(), new FakeMatcher(), null!));
}