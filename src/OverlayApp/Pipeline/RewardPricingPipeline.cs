namespace WarframeRelicOverlay.OverlayApp.Pipeline;

using System.Diagnostics;
using System.Drawing;
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
    private readonly ILogger? _logger;

    public RewardPricingPipeline(
        IScreenCapturer capturer,
        IRewardLayoutDetector layoutDetector,
        IOcrEngine ocr,
        IRewardMatcher matcher,
        IPriceProvider priceProvider,
        ILogger? logger = null)
    {
        _capturer = capturer ?? throw new ArgumentNullException(nameof(capturer));
        _layoutDetector = layoutDetector ?? throw new ArgumentNullException(nameof(layoutDetector));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _priceProvider = priceProvider ?? throw new ArgumentNullException(nameof(priceProvider));
        _logger = logger;
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
            LogInfo(runId, "CaptureWindow starting.");
            screenshot = _capturer.CaptureWindow(window);
            if (screenshot is null)
            {
                LogWarning(runId,
                    $"CaptureWindow returned null after {stopwatch.ElapsedMilliseconds} ms. " +
                    "Pricing will fail because there is no screenshot to inspect.");
                return PipelineResult.Empty(window, stopwatch.Elapsed);
            }

            LogInfo(runId,
                $"CaptureWindow succeeded: bitmap={screenshot.Width}x{screenshot.Height}; " +
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

            string slug = MarketSlugConverter.ToSlug(matchedItem.CanonicalName);
            LogInfo(runId, $"Card {index}: price lookup starting; slug=\"{slug}\".");
            price = await _priceProvider.GetPriceAsync(slug, cancellationToken);

            LogInfo(runId,
                $"Card {index}: price lookup complete; item=\"{matchedItem.CanonicalName}\", " +
                $"slug=\"{slug}\", result={(price.HasValue ? $"{price.Value}p" : "no price")}.");
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

    private static string NormalizeForLog(string text)
    {
        string normalized = text
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Trim();

        return normalized.Length <= 300 ? normalized : normalized[..300] + "...";
    }
}
