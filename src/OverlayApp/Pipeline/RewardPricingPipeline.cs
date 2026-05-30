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
    private readonly ILogger? _logger;
    private readonly bool _enableVisualReadinessGate;
    private readonly bool _saveDebugImages;
    private static readonly TimeSpan VisualReadinessTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan VisualReadinessPollInterval = TimeSpan.FromMilliseconds(250);

    public RewardPricingPipeline(
        IScreenCapturer capturer,
        IRewardLayoutDetector layoutDetector,
        IOcrEngine ocr,
        IRewardMatcher matcher,
        IPriceProvider priceProvider,
        ILogger? logger = null,
        AppSettings? settings = null)
    {
        _capturer = capturer ?? throw new ArgumentNullException(nameof(capturer));
        _layoutDetector = layoutDetector ?? throw new ArgumentNullException(nameof(layoutDetector));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _priceProvider = priceProvider ?? throw new ArgumentNullException(nameof(priceProvider));
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
            screenshot = await CaptureWhenVisuallyReadyAsync(window, runId, stopwatch, cancellationToken);
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

            var cardRects = await DetectCardBoundariesWhenReadyAsync(
                window, runId, stopwatch, screenshot, cancellationToken);
            screenshot = cardRects.Screenshot;

            if (cardRects.Rectangles.Count == 0)
            {
                LogWarning(runId,
                    $"Layout detection found 0 card boundaries after readiness polling in bitmap {screenshot.Width}x{screenshot.Height}; " +
                    $"elapsed={stopwatch.ElapsedMilliseconds} ms. Pricing will fail.");
                return PipelineResult.Empty(window, stopwatch.Elapsed);
            }

            LogInfo(runId,
                $"Layout detection found {cardRects.Rectangles.Count} card(s): {DescribeRects(cardRects.Rectangles)}; " +
                $"elapsed={stopwatch.ElapsedMilliseconds} ms.");

            var crops = new Bitmap?[cardRects.Rectangles.Count];
            try
            {
                for (int i = 0; i < cardRects.Rectangles.Count; i++)
                {
                    LogInfo(runId, $"Cropping card {i}: {DescribeRect(cardRects.Rectangles[i])}.");
                    crops[i] = CropRegion(screenshot, cardRects.Rectangles[i]);
                }

                cancellationToken.ThrowIfCancellationRequested();

                var tasks = new Task<CardResult>[cardRects.Rectangles.Count];
                for (int i = 0; i < cardRects.Rectangles.Count; i++)
                {
                    int index = i;
                    var crop = crops[i]!;
                    var rect = cardRects.Rectangles[i];
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

    private async Task<(Bitmap Screenshot, List<Rectangle> Rectangles)> DetectCardBoundariesWhenReadyAsync(
        WindowSnapshot window,
        string runId,
        Stopwatch stopwatch,
        Bitmap initialScreenshot,
        CancellationToken cancellationToken)
    {
        Bitmap screenshot = initialScreenshot;
        int layoutAttempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            layoutAttempt++;

            LogInfo(runId, $"Layout detection attempt {layoutAttempt} starting.");
            var cardRects = _layoutDetector.DetectCardBoundaries(
                screenshot, screenshot.Width, screenshot.Height);

            if (cardRects.Count > 0)
            {
                LogInfo(runId,
                    $"Layout detection attempt {layoutAttempt} found {cardRects.Count} card(s).");
                return (screenshot, cardRects);
            }

            LogWarning(runId,
                $"Layout detection attempt {layoutAttempt} found 0 card boundaries; " +
                $"elapsed={stopwatch.ElapsedMilliseconds} ms.");

            if (!_enableVisualReadinessGate || stopwatch.Elapsed >= VisualReadinessTimeout)
                return (screenshot, cardRects);

            await Task.Delay(VisualReadinessPollInterval, cancellationToken);

            Bitmap? next = await CaptureWhenVisuallyReadyAsync(window, runId, stopwatch, cancellationToken);
            if (next is null)
                return (screenshot, cardRects);

            if (!ReferenceEquals(next, screenshot))
            {
                screenshot.Dispose();
                screenshot = next;
            }
        }
    }

    private async Task<Bitmap?> CaptureWhenVisuallyReadyAsync(
        WindowSnapshot window,
        string runId,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        Bitmap? latest = null;
        string? latestHeaderText = null;
        int attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            LogInfo(runId, $"CaptureWindow attempt {attempt} starting.");
            latest?.Dispose();
            latest = _capturer.CaptureWindow(window);

            if (latest is null)
            {
                LogWarning(runId,
                    $"CaptureWindow attempt {attempt} returned null after {stopwatch.ElapsedMilliseconds} ms.");
                return null;
            }

            LogInfo(runId,
                $"CaptureWindow attempt {attempt} succeeded: bitmap={latest.Width}x{latest.Height}; " +
                $"elapsed={stopwatch.ElapsedMilliseconds} ms.");
            SaveDebugImage(latest, runId, $"capture-{attempt:00}");

            if (!_enableVisualReadinessGate)
                return latest;

            bool isReady = TryDetectRewardHeader(latest, runId, attempt, out latestHeaderText);
            if (isReady)
            {
                LogInfo(runId,
                    $"Reward header confirmed on attempt {attempt}; elapsed={stopwatch.ElapsedMilliseconds} ms.");
                return latest;
            }

            if (stopwatch.Elapsed >= VisualReadinessTimeout)
            {
                LogWarning(runId,
                    $"Reward header was not confirmed within {VisualReadinessTimeout.TotalMilliseconds:F0} ms; " +
                    $"continuing with latest capture. Last header OCR=\"{NormalizeForLog(latestHeaderText ?? string.Empty)}\".");
                return latest;
            }

            LogInfo(runId,
                $"Reward header not ready on attempt {attempt}; waiting {VisualReadinessPollInterval.TotalMilliseconds:F0} ms.");
            await Task.Delay(VisualReadinessPollInterval, cancellationToken);
        }
    }

    private bool TryDetectRewardHeader(Bitmap screenshot, string runId, int attempt, out string headerText)
    {
        headerText = string.Empty;

        try
        {
            Rectangle headerRect = ComputeRewardHeaderRegion(screenshot);
            using var headerCrop = CropRegion(screenshot, headerRect);
            SaveDebugImage(headerCrop, runId, $"header-{attempt:00}");

            using var preprocessed = ImagePreprocessor.Prepare(headerCrop);
            headerText = _ocr.Recognize(preprocessed);
            string normalized = NormalizeHeaderText(headerText);

            bool ready =
                normalized.Contains("REWARD", StringComparison.Ordinal) ||
                (normalized.Contains("VOID", StringComparison.Ordinal) &&
                 normalized.Contains("FISSURE", StringComparison.Ordinal));

            LogInfo(runId,
                $"Header readiness OCR attempt {attempt}: ready={ready}, " +
                $"region={DescribeRect(headerRect)}, text=\"{NormalizeForLog(headerText)}\".");

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
        int x = (int)(screenshot.Width * 0.10);
        int y = 0;
        int width = (int)(screenshot.Width * 0.50);
        int height = Math.Max(80, (int)(screenshot.Height * 0.14));
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
