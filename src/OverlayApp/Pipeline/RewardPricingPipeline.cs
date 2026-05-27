namespace WarframeRelicOverlay.OverlayApp.Pipeline;

using System.Diagnostics;
using System.Drawing;
using WarframeRelicOverlay.Domain.Matching;
using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Domain.Pricing;
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
///
/// All work runs on the thread pool — nothing here touches the UI
/// thread.  The caller (state machine coordinator) is responsible for
/// the stabilization delay and for dispatching results back to the UI.
///
/// Thread safety: a single instance can be called concurrently (each
/// call creates its own bitmaps and tasks), but in practice the state
/// machine ensures only one pipeline execution runs at a time.
/// </summary>

public sealed class RewardPricingPipeline : IRewardPipeline
{
    private readonly IScreenCapturer _capturer;
    private readonly IRewardLayoutDetector _layoutDetector;
    private readonly IOcrEngine _ocr;
    private readonly IRewardMatcher _matcher;
    private readonly IPriceProvider _priceProvider;

    public RewardPricingPipeline(
        IScreenCapturer capturer,
        IRewardLayoutDetector layoutDetector,
        IOcrEngine ocr,
        IRewardMatcher matcher,
        IPriceProvider priceProvider)
    {
        _capturer = capturer ?? throw new ArgumentNullException(nameof(capturer));
        _layoutDetector = layoutDetector ?? throw new ArgumentNullException(nameof(layoutDetector));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _priceProvider = priceProvider ?? throw new ArgumentNullException(nameof(priceProvider));
    }

    /// <inheritdoc cref="IRewardPipeline.ExecuteAsync"/>
    public async Task<PipelineResult> ExecuteAsync(
        WindowSnapshot window,
        CancellationToken cancellationToken = default)
    {
        var stopWatch = Stopwatch.StartNew();

        // Capture the full window
        Bitmap? screenshot = null;

        try
        {
            screenshot = _capturer.CaptureWindow(window);
            if (screenshot is null)
            {
                Debug.WriteLine("[Pipeline] Window capture returned null bitmap");
                return PipelineResult.Empty(window, stopWatch.Elapsed);
            }

            Debug.WriteLine($"[Pipeline] Captured window: {screenshot.Width}x{screenshot.Height}" + 
                            $"in {stopWatch.ElapsedMilliseconds} ms.");

            // Detect card boundaries
            cancellationToken.ThrowIfCancellationRequested();

            var cardRects = _layoutDetector.DetectCardBoundaries(screenshot, screenshot.Width, screenshot.Height);
            if (cardRects.Count == 0)
            {
                Debug.WriteLine("[Pipeline] No card boundaries detected.");
                return PipelineResult.Empty(window, stopWatch.Elapsed);
            }

            Debug.WriteLine($"[Pipeline] Detected {cardRects.Count} card(s) in {stopWatch.ElapsedMilliseconds} ms.");

            // Crop all card regions sequentially before parallelising.
            // GDI+ Bitmap is not thread-safe: concurrent DrawImage calls on
            // the same source bitmap corrupt internal GDI+ state and throw
            // InvalidOperationException, which the catch block would silently
            // swallow — leaving cards with null MatchedItem.
            var crops = new Bitmap?[cardRects.Count];
            try
            {
                for (int i = 0; i < cardRects.Count; i++)
                    crops[i] = CropRegion(screenshot, cardRects[i]);

                // Process each card in parallel.
                // Each task receives its own pre-cropped bitmap so no
                // shared-bitmap access occurs after this point.
                // Pooled Tesseract engines (typically 4) ensure true
                // parallelism for the OCR step.
                cancellationToken.ThrowIfCancellationRequested();

                var tasks = new Task<CardResult>[cardRects.Count];
                for (int i = 0; i < cardRects.Count; i++)
                {
                    int index = i; // capture for closure
                    var crop = crops[i]!;
                    var rect = cardRects[i];
                    tasks[i] = Task.Run(
                        () => ProcessSingleCard(crop, rect, index, cancellationToken),
                        cancellationToken);
                }

                var cards = await Task.WhenAll(tasks);

                stopWatch.Stop();

                Debug.WriteLine($"[Pipeline] Completed processing {cards.Length} card(s) in {stopWatch.ElapsedMilliseconds} ms.");

                return new PipelineResult
                {
                    Cards = cards,
                    Window = window,
                    Elapsed = stopWatch.Elapsed,
                };
            }
            finally
            {
                foreach (var crop in crops) crop?.Dispose();
            }
        }
        finally
        {
            screenshot?.Dispose();
        }
    }

    // PER CARD PROCESSING

    /// <summary>
    /// Processes a single pre-cropped card bitmap: binarize, OCR,
    /// fuzzy-match, and price lookup.
    /// Never throws — returns a degraded <see cref="CardResult"/>
    /// on any failure so the pipeline always completes for all cards.
    /// The caller owns <paramref name="crop"/> and disposes it after all
    /// tasks complete.
    /// </summary>
    private async Task<CardResult> ProcessSingleCard(
        Bitmap crop,
        Rectangle cardRect,
        int index,
        CancellationToken cancellationToken)
    {
        string rawOcrText = string.Empty;
        RewardItem? matchedItem = null;
        int? price = null;

        try
        {
            // Pre-process to binary image
            using var preprocessed = ImagePreprocessor.Prepare(crop);

            // OCR into text
            rawOcrText = _ocr.Recognize(preprocessed);

            Debug.WriteLine($"[Pipeline] Card {index}: OCR text: \"{rawOcrText}\"");

            // Fuzzy match to known reward items
            cancellationToken.ThrowIfCancellationRequested();

            matchedItem = _matcher.MatchSingle(rawOcrText);

            if (matchedItem is null)
            {
                Debug.WriteLine($"[Pipeline] Card {index}: No match found for OCR text.");
                return BuildResult(index, cardRect, null, null, rawOcrText);
            }

            Debug.WriteLine($"[Pipeline] Card {index}: Matched item: {matchedItem.CanonicalName}");

            // Fetch price from API (null if untradeable or API failure)
            if (matchedItem.IsUntradeable)
                return BuildResult(index, cardRect, matchedItem, null, rawOcrText);
 
            cancellationToken.ThrowIfCancellationRequested();
 
            string slug = MarketSlugConverter.ToSlug(matchedItem.CanonicalName);
            price = await _priceProvider.GetPriceAsync(slug);
 
            Debug.WriteLine(
                $"[Pipeline] Card {index}: \"{matchedItem.CanonicalName}\" → " +
                $"slug \"{slug}\" → {(price.HasValue ? $"{price.Value}p" : "no price")}");
        } catch (OperationCanceledException)
        {
            // Cancellation is expected and should propagate up to stop the pipeline.
            throw;
        }
        catch (Exception ex)
        {
            // Any other failure is non-fatal for the card — log and return what we have.
            Debug.WriteLine($"[Pipeline] Card {index}: Processing failed: {ex}");
        }

        return BuildResult(index, cardRect, matchedItem, price, rawOcrText);
    }

    // HELPERS

    private static CardResult BuildResult(
    int index,
    Rectangle cardRect,
    RewardItem? matched,
    int? price,
    string ocrText) =>
    new()
    {
        Index = index,
        BoundsInWindow = cardRect,
        MatchedItem = matched,
        PricePlatinum = price,
        RawOcrText = ocrText,
    };

    /// <summary>
    /// Crops a sub-region from the screenshot.  The source bitmap is
    /// read concurrently by multiple card tasks, so we use
    /// <see cref="Graphics.DrawImage(Image, int, int, Rectangle, GraphicsUnit)"/>
    /// which only reads from the source and is safe for concurrent use
    /// when each caller draws into its own destination bitmap.
    /// </summary>
    private static Bitmap CropRegion(Bitmap source, Rectangle rect)
    {
        // Clamp to source bounds to avoid GDI+ OutOfMemory exceptions
        // on edge cases where the detector produces slightly out-of-bounds rects.
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

}
