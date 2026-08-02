namespace WarframeRelicOverlay.Tests.OverlayApp.Pipeline;

using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using WarframeRelicOverlay.Core;
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
        public Queue<Bitmap?> BitmapsToReturn { get; } = new();
        public int CaptureCount { get; private set; }

        public Bitmap? CaptureWindow(WindowSnapshot window)
        {
            CaptureCount++;
            return BitmapsToReturn.Count > 0 ? BitmapsToReturn.Dequeue() : BitmapToReturn;
        }

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
    /// Returns a different layout per call so a test can script the sequence
    /// of frames the readiness gate sees. The last entry repeats once the
    /// script runs out.
    /// </summary>
    private sealed class ScriptedLayoutDetector : IRewardLayoutDetector
    {
        private readonly List<Rectangle>[] _layouts;
        private int _callIndex = -1;

        public ScriptedLayoutDetector(params List<Rectangle>[] layouts) =>
            _layouts = layouts;

        public int CallCount => _callIndex + 1;

        public List<Rectangle> DetectCardBoundaries(
            Bitmap windowScreenshot, int windowWidth, int windowHeight)
        {
            int idx = Interlocked.Increment(ref _callIndex);
            return _layouts[Math.Min(idx, _layouts.Length - 1)];
        }
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

        public Task<int?> GetPriceAsync(string itemName, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            int? price = Prices.TryGetValue(itemName, out var p) ? p : null;
            return Task.FromResult(price);
        }

        public Task<int?> GetPriceAsync(string itemName) => GetPriceAsync(itemName, CancellationToken.None);

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
        sorted[0].DisplayText.Should().Be("15◆");

        sorted[1].MatchedItem!.CanonicalName.Should().Be("Braton Prime Receiver");
        sorted[1].PricePlatinum.Should().Be(5);

        sorted[2].MatchedItem!.CanonicalName.Should().Be("Forma Blueprint");
        sorted[2].MatchedItem!.IsUntradeable.Should().BeTrue();
        sorted[2].PricePlatinum.Should().BeNull();
        sorted[2].DisplayText.Should().Be("N/A");

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
        result.Cards[0].DisplayText.Should().Be("N/A");
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

    // ── Visual readiness gate ─────────────────────────────────

    [Fact]
    public async Task VisualReadinessGate_RecapturesAfterRewardTextSettles()
    {
        var capturer = new FakeCapturer();
        for (int i = 0; i < 4; i++)
            capturer.BitmapsToReturn.Enqueue(MakeTestBitmap());

        var ocr = new MappedOcrEngine();
        ocr.Map(HeaderCropWidth, "VOID FISSURE/REWARDS");
        ocr.Map(200, "Ash Prime Chassis Blueprint");

        var matcher = new FakeMatcher();
        matcher.Matches["Ash Prime Chassis Blueprint"] =
            new RewardItem("Ash Prime Chassis Blueprint");

        var pricer = new FakePricer();
        pricer.Prices["ash_prime_chassis_blueprint"] = 15;

        var pipeline = new RewardPricingPipeline(
            capturer,
            new FakeLayoutDetector { CardsToReturn = [new(100, 400, 200, 60)] },
            ocr,
            matcher,
            pricer,
            settings: new AppSettings());

        var result = await pipeline.ExecuteAsync(TestWindow);

        capturer.CaptureCount.Should().Be(3,
            "one capture confirms the reward header, then the readiness capture " +
            "is discarded and replaced after the settle delay");
        result.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(450));
        result.Cards.Should().ContainSingle();
        result.Cards[0].DisplayText.Should().Be("15◆");
    }

    /// <summary>
    /// The reward row the detector reports once the transition finishes:
    /// equally wide, side-by-side cards. Taken from a real EE.log session.
    /// </summary>
    private static List<Rectangle> SettledRow() =>
    [
        new(600, 416, 228, 48),
        new(846, 416, 228, 48),
    ];

    /// <summary>
    /// Width of the header crop the readiness gate OCRs, derived from the
    /// same fractions the pipeline uses against a 1920px-wide capture.
    /// </summary>
    private const int HeaderCropWidth = 769;

    /// <summary>
    /// Drives the readiness gate with <paramref name="layouts"/> and returns
    /// the card widths the pipeline ended up cropping.
    /// </summary>
    private static async Task<List<int>> RunReadinessGateAsync(
        params List<Rectangle>[] layouts)
    {
        var capturer = new FakeCapturer();
        for (int i = 0; i < 16; i++)
            capturer.BitmapsToReturn.Enqueue(MakeTestBitmap());

        var ocr = new MappedOcrEngine();
        ocr.Map(HeaderCropWidth, "VOID FISSURE/REWARDS");
        ocr.Map(228, "Ash Prime Chassis Blueprint");

        var matcher = new FakeMatcher();
        matcher.Matches["Ash Prime Chassis Blueprint"] =
            new RewardItem("Ash Prime Chassis Blueprint");

        var pricer = new FakePricer();
        pricer.Prices["ash_prime_chassis_blueprint"] = 15;

        var pipeline = new RewardPricingPipeline(
            capturer,
            new ScriptedLayoutDetector(layouts),
            ocr,
            matcher,
            pricer,
            settings: new AppSettings());

        var result = await pipeline.ExecuteAsync(TestWindow);
        return result.Cards.Select(c => c.BoundsInWindow.Width).ToList();
    }

    [Fact]
    public async Task VisualReadinessGate_DoesNotRunLayoutDetectionUntilTheHeaderIsOnScreen()
    {
        // Regression: EE.log announces the reward screen several seconds
        // before it finishes presenting, so the pipeline was running against
        // live gameplay. The layout detector duly found "cards" in HUD text
        // (REACTANT COLLECTED / THREAT: MINIMAL / VOID CASCADE) and priced a
        // mid-mission frame. Nothing may reach the detector until the
        // top-left "VOID FISSURE/REWARDS" header is actually visible.
        var capturer = new FakeCapturer { BitmapToReturn = MakeTestBitmap() };

        // OCR returns gameplay HUD text for the header region — never a match.
        var ocr = new MappedOcrEngine();
        ocr.Map(HeaderCropWidth, "REACTANT COLLECTED 0/10");

        // A layout the pipeline would happily price if it ever got to run.
        var detector = new ScriptedLayoutDetector(SettledRow());

        var pipeline = new RewardPricingPipeline(
            capturer, detector, ocr, new FakeMatcher(), new FakePricer(),
            settings: new AppSettings());

        // The gate polls for the full readiness budget before giving up, so
        // cancel instead of waiting it out; the assertion is about what the
        // pipeline did *not* do in the meantime.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.ExecuteAsync(TestWindow, cts.Token));

        detector.CallCount.Should().Be(0,
            "layout detection must not run on frames that are not the reward screen");
        capturer.CaptureCount.Should().BeGreaterThan(1,
            "the gate should have been polling for the header the whole time");
    }

    [Fact]
    public async Task VisualReadinessGate_RejectsOverlappingCards_EvenWhenTheyReappearIdentically()
    {
        // Regression: a mid-transition frame produced two 570px-wide "cards"
        // overlapping by 174px. Card centres are the gate's settle signal, so
        // a transition frame that lingers reports the same bogus centres twice
        // and the gate declares it settled — cropping a blank screen.
        // Real reward cards never overlap, so this layout is rejected outright.
        List<Rectangle> Overlapping() =>
        [
            new(0, 308, 570, 48),
            new(396, 308, 570, 48),
        ];

        var widths = await RunReadinessGateAsync(
            Overlapping(), Overlapping(), Overlapping(), SettledRow());

        widths.Should().Equal([228, 228],
            "the overlapping transition frame must be skipped in favour of the " +
            "real reward row, however many times it repeats");
    }

    [Fact]
    public async Task VisualReadinessGate_RejectsUnequalWidths_EvenWhenTheyReappearIdentically()
    {
        // The detector sizes every card from one median centre-to-centre
        // pitch, so cards of different widths mean one was clipped by the
        // window edge — the real row is centred and never is.
        List<Rectangle> EdgeClipped() =>
        [
            new(845, 286, 554, 35),
            new(1434, 286, 486, 35),
        ];

        var widths = await RunReadinessGateAsync(
            EdgeClipped(), EdgeClipped(), EdgeClipped(), SettledRow());

        widths.Should().Equal([228, 228],
            "an edge-clipped layout must be skipped in favour of the real row");
    }

    [Fact]
    public async Task VisualReadinessGate_ToleranceDoesNotScaleWithTheWiderDetection()
    {
        // The settle tolerance is a fraction of card width, so scaling it by
        // the wider of the two detections buys slack proportional to the very
        // over-detection it is meant to catch: 400 * 0.25 = 100px of drift
        // allowed, and these two layouts sit 90px apart. Scaling by the
        // narrower card holds the window to 100 * 0.25 = 25px and rejects them.
        // Both layouts are plausible on their own (equal widths, no overlap),
        // so only the tolerance decides the outcome here.
        List<Rectangle> Wide() =>
        [
            new(0, 308, 400, 48),
            new(440, 308, 400, 48),
        ];
        List<Rectangle> NarrowAndShifted() =>
        [
            new(240, 308, 100, 48),
            new(680, 308, 100, 48),
        ];

        var widths = await RunReadinessGateAsync(
            Wide(), NarrowAndShifted(), NarrowAndShifted(), SettledRow());

        widths.Should().Equal([228, 228],
            "cards 90px out of position are not the same settled row");
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
