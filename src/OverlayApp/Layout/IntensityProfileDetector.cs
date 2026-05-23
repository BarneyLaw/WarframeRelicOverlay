namespace WarframeRelicOverlay.OverlayApp.Layout;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

/// <summary>
/// Detects reward card boundaries by locating the bright horizontal
/// border line that sits directly below the reward-name text row,
/// then segmenting it to find individual card extents.
///
/// <para><b>How it works:</b> Warframe's reward cards are bordered by
/// a prominent gold/bright horizontal line that spans the full width of
/// all cards.  Within this line, each card occupies a bright segment and
/// the narrow gaps between cards (~8-10 px at 1080p) are dark.  Applying
/// Otsu thresholding to the 1-D column-averaged intensity of this line
/// cleanly separates the card segments:</para>
///
/// <code>
///   Border line profile:
///   ___/‾‾‾‾‾‾‾‾\_/‾‾‾‾‾‾‾‾\_/‾‾‾‾‾‾‾‾\_/‾‾‾‾‾‾‾‾\___
///       card 0      card 1      card 2      card 3
/// </code>
///
/// <para>This approach is resolution-independent, aspect-ratio-independent,
/// UI-scale-independent, and theme-independent (all Warframe themes use a
/// bright border line, regardless of colour).  Total cost: &lt; 1 ms.</para>
///
/// <para>The text-name row sits immediately <b>above</b> the border line.
/// Card segment x-positions give the horizontal bounds; the text crop
/// height is a stable fraction (~5.5 %) of window height.</para>
/// </summary>
public sealed class IntensityProfileDetector : IRewardLayoutDetector
{
    // ── Tunables ──────────────────────────────────────────────────

    /// <summary>
    /// Vertical search range for the border line, expressed as fractions
    /// of window height.  The border sits in the lower-center area of
    /// the reward screen; this range covers UI scale values from ~50 %
    /// to ~150 % at common resolutions.
    /// </summary>
    private const double SearchTopFraction = 0.30;
    private const double SearchBotFraction = 0.55;

    /// <summary>
    /// Horizontal middle section used for row-brightness scoring.
    /// The card area is always centered horizontally, so the outer
    /// 25 % on each side can be ignored to avoid false positives
    /// from HUD elements at the screen edges.
    /// </summary>
    private const double MidXStartFraction = 0.25;
    private const double MidXEndFraction = 0.75;

    /// <summary>
    /// Number of rows averaged together when searching for the border
    /// line and when building the segment profile.  Averaging a thin
    /// band smooths out single-pixel noise.
    /// </summary>
    private const int BandHeight = 4;

    /// <summary>
    /// Minimum width (as a fraction of window width) for a segment
    /// to be considered a real card.  At 1920 px wide, 4 % = 77 px —
    /// the narrowest a card can realistically be.
    /// </summary>
    private const double MinCardWidthFraction = 0.04;

    /// <summary>
    /// Maximum allowed coefficient of variation (std-dev / mean) for
    /// card widths.  Real reward cards are identically sized.
    /// </summary>
    private const double MaxWidthCv = 0.25;

    /// <summary>
    /// How tall the OCR crop rectangle should be, expressed as a
    /// fraction of window height.  Covers the reward item name text
    /// plus some vertical padding.
    /// </summary>
    private const double TextHeightFraction = 0.055;

    /// <summary>
    /// Maximum number of reward cards Warframe can display.
    /// </summary>
    private const int MaxCards = 4;

    // ── Public API ───────────────────────────────────────────────

    /// <inheritdoc />
    public List<Rectangle> DetectCardBoundaries(
        Bitmap windowScreenshot,
        int windowWidth,
        int windowHeight)
    {
        if (windowWidth < 320 || windowHeight < 240)
            return [];

        // ── Step 1: Find the border line ─────────────────────────
        // Scan rows in the search range and find the band of BandHeight
        // consecutive rows with the highest average brightness in the
        // horizontal mid-section.  This is the gold border line.

        int searchTop = (int)(SearchTopFraction * windowHeight);
        int searchBot = (int)(SearchBotFraction * windowHeight);
        int midXStart = (int)(MidXStartFraction * windowWidth);
        int midXEnd = (int)(MidXEndFraction * windowWidth);

        int borderY = FindBorderLine(
            windowScreenshot, windowWidth, windowHeight,
            searchTop, searchBot, midXStart, midXEnd);

        if (borderY < 0)
        {
            Debug.WriteLine("[IntensityProfileDetector] No border line found.");
            return [];
        }

        Debug.WriteLine(
            $"[IntensityProfileDetector] Border line at y={borderY} " +
            $"({(double)borderY / windowHeight:P1} of {windowHeight}px)");

        // ── Step 2: Build 1-D intensity profile of the border line ──

        double[] profile = BuildColumnIntensityProfile(
            windowScreenshot, borderY, BandHeight, windowWidth);

        // ── Step 3: Otsu threshold → find bright segments ────────

        byte threshold = OtsuThreshold(profile);
        var segments = FindSegments(profile, threshold, windowWidth);

        if (segments.Count < 1 || segments.Count > MaxCards * 2)
        {
            Debug.WriteLine(
                $"[IntensityProfileDetector] Rejected: {segments.Count} segments " +
                $"(expected 1-{MaxCards}).");
            return [];
        }

        // ── Step 4: Merge if needed ─────────────────────────────
        // If more than MaxCards segments, some cards have internal
        // decorative gaps that split them.  Iteratively merge the
        // closest pair until we have ≤ MaxCards.

        if (segments.Count > MaxCards)
        {
            segments = MergeClosestSegments(segments, MaxCards);
            Debug.WriteLine(
                $"[IntensityProfileDetector] After merging: {segments.Count} segments");
        }

        // ── Step 5: Validate width consistency ───────────────────

        if (!AreWidthsConsistent(segments))
        {
            Debug.WriteLine("[IntensityProfileDetector] Rejected: inconsistent card widths.");
            return [];
        }

        // ── Step 6: Build text crop rectangles ───────────────────
        // The text row sits immediately above the border line.

        int textHeight = Math.Max(1, (int)(TextHeightFraction * windowHeight));
        int textBottom = borderY;
        int textTop = Math.Max(0, textBottom - textHeight);

        if (textTop + textHeight > windowHeight)
            textHeight = windowHeight - textTop;

        var rects = new List<Rectangle>(segments.Count);
        foreach (var (start, end) in segments)
        {
            rects.Add(new Rectangle(start, textTop, end - start, textHeight));
        }

        Debug.WriteLine(
            $"[IntensityProfileDetector] Detected {rects.Count} card(s), " +
            $"text region y={textTop}-{textBottom}");

        return rects;
    }

    // ── Border line detection ────────────────────────────────────

    /// <summary>
    /// Scans rows in [<paramref name="searchTop"/>, <paramref name="searchBot"/>)
    /// and returns the Y-coordinate of the row band with the highest
    /// average brightness in the horizontal mid-section.
    /// Returns -1 if no sufficiently bright band is found.
    /// </summary>
    private static int FindBorderLine(
        Bitmap bitmap, int width, int height,
        int searchTop, int searchBot, int midXStart, int midXEnd)
    {
        int bestY = -1;
        double bestBrightness = 0;

        // Clamp search bounds to image.
        searchTop = Math.Max(0, searchTop);
        searchBot = Math.Min(height - BandHeight, searchBot);

        var rect = new Rectangle(0, searchTop, width, searchBot - searchTop + BandHeight);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;

                for (int y = searchTop; y < searchBot; y++)
                {
                    double bandSum = 0;
                    int pixelCount = 0;

                    for (int dy = 0; dy < BandHeight; dy++)
                    {
                        int rowInRect = (y - searchTop) + dy;
                        byte* rowPtr = basePtr + rowInRect * data.Stride;

                        for (int x = midXStart; x < midXEnd; x++)
                        {
                            byte* pixel = rowPtr + x * 3;
                            bandSum += 0.114 * pixel[0]
                                     + 0.587 * pixel[1]
                                     + 0.299 * pixel[2];
                            pixelCount++;
                        }
                    }

                    double avg = bandSum / pixelCount;
                    if (avg > bestBrightness)
                    {
                        bestBrightness = avg;
                        bestY = y;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // Reject if the "brightest band" isn't meaningfully bright.
        // A threshold of 40 rejects near-black game scenes where
        // there's no reward screen at all.
        return bestBrightness > 40 ? bestY : -1;
    }

    // ── Column intensity projection ──────────────────────────────

    /// <summary>
    /// Builds a 1-D array where each element is the average luminance
    /// of a vertical column within the specified horizontal band.
    /// </summary>
    private static double[] BuildColumnIntensityProfile(
        Bitmap bitmap, int stripY, int stripH, int width)
    {
        var profile = new double[width];
        int height = Math.Min(stripH, bitmap.Height - stripY);
        if (height <= 0)
            return profile;

        var rect = new Rectangle(0, stripY, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int x = 0; x < width; x++)
                {
                    double columnSum = 0;
                    for (int y = 0; y < height; y++)
                    {
                        byte* pixel = basePtr + y * data.Stride + x * 3;
                        columnSum += 0.114 * pixel[0]
                                   + 0.587 * pixel[1]
                                   + 0.299 * pixel[2];
                    }
                    profile[x] = columnSum / height;
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return profile;
    }

    // ── Segment detection ────────────────────────────────────────

    /// <summary>
    /// Finds contiguous runs of above-threshold columns and filters
    /// them by minimum width.
    /// </summary>
    private static List<(int Start, int End)> FindSegments(
        double[] profile, byte threshold, int windowWidth)
    {
        int minWidth = (int)(MinCardWidthFraction * windowWidth);
        var segments = new List<(int Start, int End)>();
        int i = 0;

        while (i < profile.Length)
        {
            if (profile[i] <= threshold)
            {
                i++;
                continue;
            }

            int start = i;
            while (i < profile.Length && profile[i] > threshold)
                i++;
            int end = i;

            if (end - start >= minWidth)
                segments.Add((start, end));
        }

        return segments;
    }

    // ── Segment merging ──────────────────────────────────────────

    /// <summary>
    /// Iteratively merges the two closest neighbouring segments until
    /// the count reaches <paramref name="maxCount"/>.  This handles
    /// cards whose border line has internal decorative gaps that split
    /// a single card into multiple sub-segments.
    /// </summary>
    private static List<(int Start, int End)> MergeClosestSegments(
        List<(int Start, int End)> segments, int maxCount)
    {
        var list = new List<(int Start, int End)>(segments);

        while (list.Count > maxCount)
        {
            // Find the pair of consecutive segments with the smallest gap.
            int bestIdx = 0;
            int bestGap = int.MaxValue;

            for (int i = 0; i < list.Count - 1; i++)
            {
                int gap = list[i + 1].Start - list[i].End;
                if (gap < bestGap)
                {
                    bestGap = gap;
                    bestIdx = i;
                }
            }

            // Merge.
            var merged = (list[bestIdx].Start, list[bestIdx + 1].End);
            list.RemoveAt(bestIdx + 1);
            list[bestIdx] = merged;
        }

        return list;
    }

    // ── Validation ───────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if all segments are roughly the same width.
    /// </summary>
    private static bool AreWidthsConsistent(List<(int Start, int End)> segments)
    {
        if (segments.Count <= 1)
            return true;

        double[] widths = segments
            .Select(s => (double)(s.End - s.Start))
            .ToArray();

        double mean = widths.Average();
        if (mean < 1.0)
            return false;

        double variance = widths.Sum(w => (w - mean) * (w - mean)) / widths.Length;
        double cv = Math.Sqrt(variance) / mean;

        if (cv > MaxWidthCv)
        {
            Debug.WriteLine(
                $"[IntensityProfileDetector] Width CV = {cv:F3} " +
                $"(max {MaxWidthCv:F3}), widths = " +
                $"[{string.Join(", ", widths.Select(w => w.ToString("F0")))}]");
            return false;
        }

        return true;
    }

    // ── Otsu threshold ───────────────────────────────────────────

    /// <summary>
    /// Otsu's method over a <c>double[]</c> intensity profile.
    /// Values are in [0, 255] range (luminance averages), quantized
    /// to integer histogram buckets.
    /// </summary>
    private static byte OtsuThreshold(double[] values)
    {
        int[] histogram = new int[256];
        foreach (double v in values)
        {
            int bucket = Math.Clamp((int)v, 0, 255);
            histogram[bucket]++;
        }

        int total = values.Length;
        double sum = 0;
        for (int i = 0; i < 256; i++)
            sum += i * histogram[i];

        double sumB = 0;
        int wB = 0;
        double maxVariance = 0;
        byte threshold = 128;

        for (int t = 0; t < 256; t++)
        {
            wB += histogram[t];
            if (wB == 0) continue;

            int wF = total - wB;
            if (wF == 0) break;

            sumB += t * histogram[t];
            double mB = sumB / wB;
            double mF = (sum - sumB) / wF;

            double variance = (double)wB * wF * (mB - mF) * (mB - mF);
            if (variance > maxVariance)
            {
                maxVariance = variance;
                threshold = (byte)t;
            }
        }

        return threshold;
    }
}