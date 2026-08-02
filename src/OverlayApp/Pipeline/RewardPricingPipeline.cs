namespace WarframeRelicOverlay.OverlayApp.Pipeline;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Domain.Matching;
using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Domain.Pricing;
using WarframeRelicOverlay.Infrastructure.Logging;
using WarframeRelicOverlay.Infrastructure.Market;
using WarframeRelicOverlay.Infrastructure.OCR;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.Infrastructure.ScreenCapture;
using WarframeRelicOverlay.OverlayApp.Layout;

/// <summary>
/// Executes the full reward-pricing pipeline: capture the Warframe
/// window, detect card boundaries via intensity profiling, OCR each
/// card in parallel using pooled Tesseract engines, fuzzy-match to
/// known reward items, and fetch prices from Warframe Market.
/// </summary>
public sealed class RewardPricingPipeline : IRewardPipeline
{
    private readonly IScreenCapturer _capturer;
    private readonly IRewardLayoutDetector _layoutDetector;
    private readonly IOcrEngine _ocr;
    private readonly IRewardMatcher _matcher;
    private readonly IPriceProvider _priceProvider;
    private readonly IWarframeMarketAPI? _marketApi;
    private readonly ILogger? _logger;
    private readonly bool _enableVisualReadinessGate;
    private readonly bool _saveDebugImages;
    private static readonly TimeSpan RewardHeaderPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RewardCardReadinessTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RewardCardPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RewardTextSettleDelay = TimeSpan.FromMilliseconds(500);
    private const double RewardHeaderX = 0.0729;
    private const double RewardHeaderY = 0.0324;
    private const double RewardHeaderWidth = 0.401;
    private const double RewardHeaderHeight = 0.0602;

    public RewardPricingPipeline(
        IScreenCapturer capturer,
        IRewardLayoutDetector layoutDetector,
        IOcrEngine ocr,
        IRewardMatcher matcher,
        IPriceProvider priceProvider,
        ILogger? logger = null,
        AppSettings? settings = null,
        IWarframeMarketAPI? marketApi = null)
    {
        _capturer = capturer ?? throw new ArgumentNullException(nameof(capturer));
        _layoutDetector = layoutDetector ?? throw new ArgumentNullException(nameof(layoutDetector));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _priceProvider = priceProvider ?? throw new ArgumentNullException(nameof(priceProvider));
        _marketApi = marketApi;
        _logger = logger;
        _enableVisualReadinessGate = settings is not null;
        _saveDebugImages = settings is not null;
    }

    /// <inheritdoc cref="IRewardPipeline.ExecuteAsync"/>
    public async Task<PipelineResult> ExecuteAsync(
        WindowSnapshot window,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string runId = Guid.NewGuid().ToString("N")[..8];

        LogInfo(runId,
            $"Starting pipeline for window physical={window.ClientWidth}x{window.ClientHeight} " +
            $"@ ({window.ClientX},{window.ClientY}), logical={window.LogicalWidth:0.##}x{window.LogicalHeight:0.##}, " +
            $"dpi={window.DpiScaleX:0.##}x{window.DpiScaleY:0.##}.");

        Bitmap? screenshot = null;

        try
        {
            screenshot = await CaptureWhenRewardHeaderReadyAsync(window, runId, stopwatch, cancellationToken);
            if (screenshot is null)
            {
                LogWarning(runId,
                    $"CaptureWindow returned null after {stopwatch.ElapsedMilliseconds} ms. " +
                    "Pricing will fail because there is no screenshot to inspect.");
                return PipelineResult.Empty(window, stopwatch.Elapsed);
            }

            LogInfo(runId,
                $"Using screenshot for layout: bitmap={screenshot.Width}x{screenshot.Height}; " +
                $"elapsed={stopwatch.ElapsedMilliseconds} ms.");

            cancellationToken.ThrowIfCancellationRequested();

            LogInfo(runId, "Layout detection starting.");
            var cardRects = _layoutDetector.DetectCardBoundaries(
                screenshot, screenshot.Width, screenshot.Height);
            if (cardRects.Count == 0)
            {
                LogWarning(runId,
                    $"Layout detection found 0 card boundaries in bitmap {screenshot.Width}x{screenshot.Height}; " +
                    $"elapsed={stopwatch.ElapsedMilliseconds} ms. Pricing will fail.");
                return PipelineResult.Empty(window, stopwatch.Elapsed);
            }

            LogInfo(runId,
                $"Layout detection found {cardRects.Count} card(s): {DescribeRects(cardRects)}; " +
                $"elapsed={stopwatch.ElapsedMilliseconds} ms.");

            var crops = new Bitmap?[cardRects.Count];
            try
            {
                for (int i = 0; i < cardRects.Count; i++)
                {
                    LogInfo(runId, $"Cropping card {i}: {DescribeRect(cardRects[i])}.");
                    crops[i] = CropRegion(screenshot, cardRects[i]);
                    SaveDebugImage(crops[i]!, runId, $"card{i}-crop");
                }

                cancellationToken.ThrowIfCancellationRequested();

                var tasks = new Task<CardResult>[cardRects.Count];
                for (int i = 0; i < cardRects.Count; i++)
                {
                    int index = i;
                    var crop = crops[i]!;
                    var rect = cardRects[i];
                    tasks[i] = Task.Run(
                        () => ProcessSingleCard(crop, rect, index, runId, cancellationToken),
                        cancellationToken);
                }

                var cards = await Task.WhenAll(tasks);

                stopwatch.Stop();

                LogInfo(runId,
                    $"Pipeline completed: cards={cards.Length}, matched={cards.Count(c => c.MatchedItem is not null)}, " +
                    $"priced={cards.Count(c => c.PricePlatinum.HasValue)}, elapsed={stopwatch.ElapsedMilliseconds} ms.");

                return new PipelineResult
                {
                    Cards = cards,
                    Window = window,
                    Elapsed = stopwatch.Elapsed,
                };
            }
            finally
            {
                foreach (var crop in crops) crop?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            LogWarning(runId, $"Pipeline cancelled after {stopwatch.ElapsedMilliseconds} ms.");
            throw;
        }
        catch (Exception ex)
        {
            LogError(runId, $"Pipeline failed after {stopwatch.ElapsedMilliseconds} ms.", ex);
            throw;
        }
        finally
        {
            screenshot?.Dispose();
        }
    }

    private async Task<CardResult> ProcessSingleCard(
        Bitmap crop,
        Rectangle cardRect,
        int index,
        string runId,
        CancellationToken cancellationToken)
    {
        string rawOcrText = string.Empty;
        RewardItem? matchedItem = null;
        int? price = null;

        try
        {
            LogInfo(runId,
                $"Card {index}: preprocessing starting; crop={crop.Width}x{crop.Height}; bounds={DescribeRect(cardRect)}.");
            using var preprocessed = ImagePreprocessor.Prepare(crop);
            LogInfo(runId, $"Card {index}: preprocessing complete; bitmap={preprocessed.Width}x{preprocessed.Height}.");
            SaveDebugImage(preprocessed, runId, $"card{index}-preprocessed");

            LogInfo(runId, $"Card {index}: OCR starting.");
            rawOcrText = _ocr.Recognize(preprocessed);
            LogInfo(runId, $"Card {index}: OCR text=\"{NormalizeForLog(rawOcrText)}\".");

            cancellationToken.ThrowIfCancellationRequested();

            LogInfo(runId, $"Card {index}: matching starting.");
            matchedItem = _matcher.MatchSingle(rawOcrText);

            if (matchedItem is null)
            {
                LogWarning(runId,
                    $"Card {index}: no reward match for OCR text=\"{NormalizeForLog(rawOcrText)}\".");
                return BuildResult(index, cardRect, null, null, rawOcrText);
            }

            LogInfo(runId,
                $"Card {index}: matched item=\"{matchedItem.CanonicalName}\", " +
                $"untradeable={matchedItem.IsUntradeable}.");

            if (matchedItem.IsUntradeable)
            {
                LogInfo(runId, $"Card {index}: skipping market lookup because item is untradeable.");
                return BuildResult(index, cardRect, matchedItem, null, rawOcrText);
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Prefer the slug precomputed when the reward pool was generated
            // (in-game name joined against the live market catalog). Fall back
            // to deriving one from the name only for older pools that predate
            // slug enrichment.
            string slug = !string.IsNullOrWhiteSpace(matchedItem.MarketSlug)
                ? matchedItem.MarketSlug!
                : MarketSlugConverter.ToSlug(matchedItem.CanonicalName);
            LogInfo(runId, $"Card {index}: price lookup starting; slug=\"{slug}\".");
            price = await _priceProvider.GetPriceAsync(slug, cancellationToken);

            // Fetch richer market data (buy price + seller count) from
            // the same /top endpoint without an extra HTTP call if the
            // market API reference is available.
            int? buyPrice = null;
            int sellerCount = 0;
            IReadOnlyList<int> topSellPrices = [];
            IReadOnlyList<int> topBuyPrices = [];
            if (_marketApi is not null)
            {
                var marketData = await _marketApi.GetMarketDataAsync(slug, cancellationToken);
                if (marketData.HasValue)
                {
                    buyPrice = marketData.Value.HighestBuyPrice;
                    sellerCount = marketData.Value.SellerCount;
                    topSellPrices = marketData.Value.TopSellPrices;
                    topBuyPrices = marketData.Value.TopBuyPrices;
                    // Use the richer sell price if we didn't get one from the cache
                    price ??= marketData.Value.LowestSellPrice;
                }
            }

            LogInfo(runId,
                $"Card {index}: price lookup complete; item=\"{matchedItem.CanonicalName}\", " +
                $"slug=\"{slug}\", sell={(price.HasValue ? $"{price.Value}p" : "no price")}, " +
                $"buy={(buyPrice.HasValue ? $"{buyPrice.Value}p" : "none")}, sellers={sellerCount}.");

            return BuildResult(index, cardRect, matchedItem, price, rawOcrText, buyPrice, sellerCount, topSellPrices, topBuyPrices);
        }
        catch (OperationCanceledException)
        {
            LogWarning(runId, $"Card {index}: processing cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            LogError(runId, $"Card {index}: processing failed.", ex);
        }

        return BuildResult(index, cardRect, matchedItem, price, rawOcrText);
    }

    private async Task<Bitmap?> CaptureWhenRewardHeaderReadyAsync(
        WindowSnapshot window,
        string runId,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (!_enableVisualReadinessGate)
        {
            LogInfo(runId, "Visual readiness gate disabled; capturing immediately.");
            return CaptureOnce(window, runId, stopwatch, "capture-ready");
        }

        // Both phases below share one budget so confirming the header cannot
        // push the total past what the coordinator's display timeout allows.
        var readinessStopwatch = Stopwatch.StartNew();

        // Phase 1 — prove we are actually on the reward screen. Without this
        // the layout detector happily finds "cards" in mid-mission HUD text
        // (REACTANT COLLECTED, THREAT: MINIMAL, ...) and the pipeline prices
        // a gameplay frame.
        if (!await WaitForRewardHeaderAsync(
                window, runId, stopwatch, readinessStopwatch, cancellationToken))
        {
            return null;
        }

        // Phase 2 — the header is up, but the cards may still be sliding in.
        LogInfo(runId,
            $"Polling for a settled reward card layout every " +
            $"{RewardCardPollInterval.TotalMilliseconds:F0} ms for the remainder of the " +
            $"{RewardCardReadinessTimeout.TotalMilliseconds:F0} ms readiness budget.");

        int attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            LogInfo(runId, $"Card readiness capture attempt {attempt} starting.");
            Bitmap? candidate = _capturer.CaptureWindow(window);

            if (candidate is null)
            {
                LogWarning(runId,
                    $"Card readiness capture attempt {attempt} returned null after " +
                    $"{stopwatch.ElapsedMilliseconds} ms.");
                return null;
            }

            LogInfo(runId,
                $"Card readiness capture attempt {attempt} succeeded: bitmap={candidate.Width}x{candidate.Height}; " +
                $"elapsed={stopwatch.ElapsedMilliseconds} ms.");

            var candidateRects = _layoutDetector.DetectCardBoundaries(candidate, candidate.Width, candidate.Height);
            candidate.Dispose();

            if (candidateRects.Count > 0 && !IsPlausibleCardLayout(candidateRects))
            {
                LogInfo(runId,
                    $"Discarding implausible layout on attempt {attempt}: {candidateRects.Count} card(s), " +
                    $"bounds={DescribeRects(candidateRects)}. {LayoutImplausibilityHint} " +
                    $"elapsed={stopwatch.ElapsedMilliseconds} ms.");
            }
            else if (candidateRects.Count > 0)
            {
                LogInfo(runId,
                    $"Reward cards visible on attempt {attempt}: {candidateRects.Count} card(s), " +
                    $"bounds={DescribeRects(candidateRects)}; elapsed={stopwatch.ElapsedMilliseconds} ms.");

                // The card slide/zoom-in animation can momentarily present
                // 2-4 evenly-spaced warm runs before the cards reach their
                // final positions, so a single positive frame is not proof the
                // layout is settled. Wait for the text to settle, recapture,
                // and only proceed once the settled frame's layout matches what
                // we just saw — otherwise the card count and positions used for
                // cropping would disagree with the frame they came from.
                Bitmap? settled = await CaptureSettledLayoutAsync(
                    window, candidateRects, runId, stopwatch, cancellationToken);
                if (settled is not null)
                    return settled;

                LogInfo(runId,
                    $"Reward layout had not settled on attempt {attempt}; continuing to poll.");
            }
            else
            {
                LogInfo(runId, $"Reward cards not visible on attempt {attempt}.");
            }

            if (readinessStopwatch.Elapsed >= RewardCardReadinessTimeout)
            {
                LogWarning(runId,
                    $"Reward layout did not settle within {RewardCardReadinessTimeout.TotalMilliseconds:F0} ms; " +
                    "returning a final capture so layout diagnostics can report the failure.");
                Bitmap? fallback = _capturer.CaptureWindow(window);
                if (fallback is not null)
                    SaveDebugImage(fallback, runId, "capture-no-cards");
                return fallback;
            }

            LogInfo(runId,
                $"Waiting {RewardCardPollInterval.TotalMilliseconds:F0} ms before the next readiness capture.");
            await Task.Delay(RewardCardPollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Polls the top-left header region until it OCRs as Warframe's
    /// "VOID FISSURE/REWARDS" title, proving the reward screen is actually on
    /// screen before anything is captured for pricing.
    ///
    /// <para>
    /// Returns <c>false</c> if the header never appears within the shared
    /// readiness budget, which abandons the trigger rather than pricing
    /// whatever happens to be on screen. EE.log announces the reward screen
    /// several seconds before it finishes presenting, so without this the
    /// pipeline runs against live gameplay frames.
    /// </para>
    /// </summary>
    private async Task<bool> WaitForRewardHeaderAsync(
        WindowSnapshot window,
        string runId,
        Stopwatch stopwatch,
        Stopwatch readinessStopwatch,
        CancellationToken cancellationToken)
    {
        LogInfo(runId,
            $"Waiting up to {RewardCardReadinessTimeout.TotalMilliseconds:F0} ms for the reward-screen " +
            $"header before any frame is used for pricing.");

        int attempt = 0;
        string lastHeaderText = string.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            Bitmap? frame = _capturer.CaptureWindow(window);
            if (frame is null)
            {
                LogWarning(runId,
                    $"Header capture attempt {attempt} returned null after " +
                    $"{stopwatch.ElapsedMilliseconds} ms.");
                return false;
            }

            bool ready;
            try
            {
                ready = TryDetectRewardHeader(frame, runId, attempt, out lastHeaderText);
            }
            finally
            {
                frame.Dispose();
            }

            if (ready)
            {
                LogInfo(runId,
                    $"Reward header confirmed on attempt {attempt}; " +
                    $"elapsed={stopwatch.ElapsedMilliseconds} ms.");
                return true;
            }

            if (readinessStopwatch.Elapsed >= RewardCardReadinessTimeout)
            {
                LogWarning(runId,
                    $"Reward-screen header never appeared within " +
                    $"{RewardCardReadinessTimeout.TotalMilliseconds:F0} ms; abandoning this trigger " +
                    $"rather than pricing whatever is on screen. " +
                    $"Last header OCR=\"{NormalizeForLog(lastHeaderText)}\".");
                return false;
            }

            await Task.Delay(RewardHeaderPollInterval, cancellationToken);
        }
    }

    /// <summary>
    /// Waits for the reward text to finish rendering, recaptures, and re-runs
    /// layout detection. Returns the settled capture only when its detected
    /// layout matches <paramref name="expectedRects"/> (same card count and
    /// aligned card centres); otherwise returns <c>null</c> so the caller keeps
    /// polling. This stops the pipeline from cropping a frame captured mid-
    /// animation, where the card count and positions differ from the final UI.
    /// </summary>
    private async Task<Bitmap?> CaptureSettledLayoutAsync(
        WindowSnapshot window,
        IReadOnlyList<Rectangle> expectedRects,
        string runId,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        LogInfo(runId,
            $"Waiting {RewardTextSettleDelay.TotalMilliseconds:F0} ms for reward text to settle before final capture.");
        await Task.Delay(RewardTextSettleDelay, cancellationToken);

        LogInfo(runId, "Final reward capture starting after settle delay.");
        Bitmap? settled = _capturer.CaptureWindow(window);

        if (settled is null)
        {
            LogWarning(runId,
                $"Final reward capture returned null after {stopwatch.ElapsedMilliseconds} ms.");
            return null;
        }

        LogInfo(runId,
            $"Final reward capture succeeded: bitmap={settled.Width}x{settled.Height}; " +
            $"elapsed={stopwatch.ElapsedMilliseconds} ms.");

        var settledRects = _layoutDetector.DetectCardBoundaries(settled, settled.Width, settled.Height);
        if (settledRects.Count > 0 && !IsPlausibleCardLayout(settledRects))
        {
            LogInfo(runId,
                $"Settled layout ({settledRects.Count} card(s): {DescribeRects(settledRects)}) is implausible. " +
                $"{LayoutImplausibilityHint} Discarding capture and re-polling.");
            settled.Dispose();
            return null;
        }

        if (!LayoutsAgree(expectedRects, settledRects))
        {
            LogInfo(runId,
                $"Settled layout ({settledRects.Count} card(s): {DescribeRects(settledRects)}) does not match the " +
                $"pre-settle layout ({expectedRects.Count} card(s)); discarding capture and re-polling.");
            settled.Dispose();
            return null;
        }

        LogInfo(runId,
            $"Settled layout confirmed: {settledRects.Count} card(s) match the pre-settle detection.");
        SaveDebugImage(settled, runId, "capture-ready");
        return settled;
    }

    /// <summary>
    /// Explanation appended to every implausible-layout log line, so the log
    /// says why a frame was thrown away rather than just that it was.
    /// </summary>
    private const string LayoutImplausibilityHint =
        "Reward cards never overlap and are all the same width, so this frame " +
        "was captured mid-transition and its card boundaries are not real.";

    /// <summary>
    /// Largest ratio between the widest and narrowest card in one row before
    /// the layout is rejected. Deliberately tight: the detector gives every
    /// card in a row the same width, so the only thing that makes them differ
    /// is a card being clipped by the window edge. The few percent of slack
    /// covers rounding rather than any real variation.
    /// </summary>
    private const double MaxCardWidthRatio = 1.05;

    /// <summary>
    /// True when a detected layout could actually be Warframe's reward row.
    ///
    /// <para>
    /// The detector sizes every card from a single median centre-to-centre
    /// pitch, so a genuine row always comes back as equally wide, side-by-side
    /// rectangles. Two things break that, and both mean the centres it locked
    /// onto were not one card apart:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>Overlapping cards — the pitch was measured too wide, so each
    /// rectangle spills into its neighbour.</item>
    /// <item>Unequal widths — a rectangle ran off the window edge and was
    /// clipped, which the real row (centred, with margins) never does.</item>
    /// </list>
    ///
    /// <para>
    /// Screening these out matters because <see cref="LayoutsAgree"/> scales
    /// its tolerance by card width: a bogus 570px-wide "card" buys a 142px
    /// tolerance, wide enough for two unrelated transition frames to look
    /// settled and send a blank capture down the pipeline.
    /// </para>
    /// </summary>
    private static bool IsPlausibleCardLayout(IReadOnlyList<Rectangle> rects)
    {
        if (rects.Count == 0) return false;

        int minWidth = int.MaxValue;
        int maxWidth = 0;
        foreach (var rect in rects)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return false;
            minWidth = Math.Min(minWidth, rect.Width);
            maxWidth = Math.Max(maxWidth, rect.Width);
        }

        if (maxWidth > minWidth * MaxCardWidthRatio) return false;

        // Sorted defensively: the detector emits left-to-right, but the
        // overlap test is only meaningful on ordered rectangles.
        var ordered = rects.OrderBy(r => r.X).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].X < ordered[i - 1].Right) return false;
        }

        return true;
    }

    /// <summary>
    /// True when two detected layouts describe the same settled set of cards:
    /// identical card count and each card's horizontal centre aligned within a
    /// fraction of the card width. Horizontal centres are the stable invariant
    /// once cards stop sliding; vertical position is ignored because a wrapped
    /// second line can shift a card's crop top without changing which card it is.
    ///
    /// <para>
    /// The tolerance is scaled by the <em>narrower</em> of the two cards so an
    /// over-wide detection cannot widen the very window that is supposed to
    /// catch it.
    /// </para>
    /// </summary>
    private static bool LayoutsAgree(
        IReadOnlyList<Rectangle> expected, IReadOnlyList<Rectangle> actual)
    {
        if (expected.Count == 0 || expected.Count != actual.Count)
            return false;

        for (int i = 0; i < expected.Count; i++)
        {
            double expectedCenter = expected[i].X + expected[i].Width / 2.0;
            double actualCenter = actual[i].X + actual[i].Width / 2.0;
            double width = Math.Min(expected[i].Width, actual[i].Width);
            double tolerance = Math.Max(8.0, width * 0.25);
            if (Math.Abs(expectedCenter - actualCenter) > tolerance)
                return false;
        }

        return true;
    }

    private Bitmap? CaptureOnce(WindowSnapshot window, string runId, Stopwatch stopwatch, string debugName)
    {
        LogInfo(runId, "CaptureWindow starting.");
        Bitmap? screenshot = _capturer.CaptureWindow(window);

        if (screenshot is null)
        {
            LogWarning(runId,
                $"CaptureWindow returned null after {stopwatch.ElapsedMilliseconds} ms.");
            return null;
        }

        LogInfo(runId,
            $"CaptureWindow succeeded: bitmap={screenshot.Width}x{screenshot.Height}; " +
            $"elapsed={stopwatch.ElapsedMilliseconds} ms.");

        SaveDebugImage(screenshot, runId, debugName);
        return screenshot;
    }

    private bool TryDetectRewardHeader(Bitmap screenshot, string runId, int attempt, out string headerText)
    {
        headerText = string.Empty;

        try
        {
            Rectangle headerRect = ComputeRewardHeaderRegion(screenshot);
            using var headerCrop = CropRegion(screenshot, headerRect);

            using var preprocessed = ImagePreprocessor.Prepare(headerCrop);
            headerText = _ocr.Recognize(preprocessed);
            string normalized = NormalizeHeaderText(headerText);
            bool ready = IsRewardHeaderMatch(normalized);

            LogInfo(runId,
                $"Header readiness OCR attempt {attempt}: ready={ready}, " +
                $"region={DescribeRect(headerRect)}, normalized=\"{normalized}\", " +
                $"text=\"{NormalizeForLog(headerText)}\".");

            return ready;
        }
        catch (Exception ex)
        {
            LogError(runId, $"Header readiness OCR attempt {attempt} failed.", ex);
            return false;
        }
    }

    private static CardResult BuildResult(
        int index,
        Rectangle cardRect,
        RewardItem? matched,
        int? price,
        string ocrText,
        int? buyPrice = null,
        int sellerCount = 0,
        IReadOnlyList<int>? topSellPrices = null,
        IReadOnlyList<int>? topBuyPrices = null) =>
        new()
        {
            Index = index,
            BoundsInWindow = cardRect,
            MatchedItem = matched,
            PricePlatinum = price,
            HighestBuyPrice = buyPrice,
            SellerCount = sellerCount,
            TopSellPrices = topSellPrices ?? [],
            TopBuyPrices = topBuyPrices ?? [],
            RawOcrText = ocrText,
        };

    private static Bitmap CropRegion(Bitmap source, Rectangle rect)
    {
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int right = Math.Min(source.Width, rect.X + rect.Width);
        int bottom = Math.Min(source.Height, rect.Y + rect.Height);
        int width = Math.Max(1, right - x);
        int height = Math.Max(1, bottom - y);

        var clamped = new Rectangle(x, y, width, height);
        var cropped = new Bitmap(width, height, source.PixelFormat);

        using var g = Graphics.FromImage(cropped);
        g.DrawImage(source, 0, 0, clamped, GraphicsUnit.Pixel);

        return cropped;
    }

    private void SaveDebugImage(Bitmap bitmap, string runId, string name)
    {
        if (!_saveDebugImages) return;

        try
        {
            string directory = ResolveDebugImageDirectory();
            Directory.CreateDirectory(directory);

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string path = Path.Combine(directory, $"{timestamp}-{runId}-{name}.png");
            bitmap.Save(path, ImageFormat.Png);
            LogInfo(runId, $"Saved debug image: {path}");
        }
        catch (Exception ex)
        {
            LogError(runId, "Failed to save debug image.", ex);
        }
    }

    private void LogInfo(string runId, string message)
    {
        string line = $"[Pipeline:{runId}] {message}";
        _logger?.LogInfo(line);
        Debug.WriteLine(line);
    }

    private void LogWarning(string runId, string message)
    {
        string line = $"[Pipeline:{runId}] {message}";
        _logger?.LogWarning(line);
        Debug.WriteLine(line);
    }

    private void LogError(string runId, string message, Exception exception)
    {
        string line = $"[Pipeline:{runId}] {message}";
        _logger?.LogError(line, exception);
        Debug.WriteLine($"{line} {exception}");
    }

    private static string DescribeRects(IReadOnlyList<Rectangle> rects) =>
        string.Join(", ", rects.Select((r, i) => $"{i}:{DescribeRect(r)}"));

    private static string DescribeRect(Rectangle rect) =>
        $"x={rect.X},y={rect.Y},w={rect.Width},h={rect.Height}";

    private static Rectangle ComputeRewardHeaderRegion(Bitmap screenshot)
    {
        int x = (int)(screenshot.Width * RewardHeaderX);
        int y = (int)(screenshot.Height * RewardHeaderY);
        int width = Math.Max(1, (int)(screenshot.Width * RewardHeaderWidth));
        int height = Math.Max(1, (int)(screenshot.Height * RewardHeaderHeight));
        return new Rectangle(x, y, width, height);
    }

    private static string NormalizeForLog(string text)
    {
        string normalized = text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Trim();

        return normalized.Length <= 300 ? normalized : normalized[..300] + "...";
    }

    private static string NormalizeHeaderText(string text)
    {
        return new string(
            text.ToUpperInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
    }

    private static bool IsRewardHeaderMatch(string normalizedText)
    {
        if (normalizedText.Length < 4)
            return false;

        if (normalizedText.Contains("REWARD", StringComparison.Ordinal) ||
            normalizedText.Contains("VOIDFISSURE", StringComparison.Ordinal) ||
            normalizedText.Contains("RELICFISSURE", StringComparison.Ordinal))
        {
            return true;
        }

        return BestSimilarity(normalizedText, "VOIDFISSUREREWARDS") >= 0.62 ||
               BestSimilarity(normalizedText, "VOIDFISSURE") >= 0.70 ||
               BestSimilarity(normalizedText, "REWARDS") >= 0.70 ||
               BestSimilarity(normalizedText, "RELICFISSUREREWARDS") >= 0.62;
    }

    private static double BestSimilarity(string candidate, string target)
    {
        if (candidate.Length == 0 || target.Length == 0)
            return 0;

        if (candidate.Length <= target.Length)
            return Similarity(candidate, target);

        double best = Similarity(candidate, target);
        for (int i = 0; i <= candidate.Length - target.Length; i++)
        {
            double score = Similarity(candidate.Substring(i, target.Length), target);
            if (score > best) best = score;
        }

        return best;
    }

    private static double Similarity(string left, string right)
    {
        int maxLength = Math.Max(left.Length, right.Length);
        if (maxLength == 0) return 1;
        return 1.0 - ((double)LevenshteinDistance(left, right) / maxLength);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var distances = new int[left.Length + 1, right.Length + 1];

        for (int i = 0; i <= left.Length; i++)
            distances[i, 0] = i;

        for (int j = 0; j <= right.Length; j++)
            distances[0, j] = j;

        for (int i = 1; i <= left.Length; i++)
        {
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                distances[i, j] = Math.Min(
                    Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
                    distances[i - 1, j - 1] + cost);
            }
        }

        return distances[left.Length, right.Length];
    }

    private static string ResolveDebugImageDirectory()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "WarframeRelicOverlay.sln")) ||
                    Directory.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    return Path.Combine(directory.FullName, "debug-images");
                }

                directory = directory.Parent;
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "debug-images");
    }
}
