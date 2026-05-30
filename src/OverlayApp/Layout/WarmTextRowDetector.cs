namespace WarframeRelicOverlay.OverlayApp.Layout;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

/// <summary>
/// Detects reward card boundaries by locating the row of <b>item-name
/// text</b> shared by all cards, keyed on Warframe's warm/amber UI text
/// colour and on the rigidly uniform horizontal spacing of the cards.
///
/// <para><b>Why not a brightness/border approach?</b> The reward screen
/// sits in front of a live game scene whose bright effects bleed all the
/// way down the frame, so the brightest horizontal band is usually the
/// background — not a card border.  And item names wrap to one or two
/// lines, so card <i>width</i> is not a stable signal.  The two things
/// that <i>are</i> invariant are:</para>
///
/// <list type="number">
///   <item>The item-name text is a warm off-white/amber colour
///         (R ≥ G &gt; B, with a large R−B gap) that survives any
///         background because it is keyed on hue, not brightness.</item>
///   <item>The cards are evenly spaced and horizontally centred, so the
///         <b>centre-to-centre spacing</b> of the name segments has a
///         near-zero coefficient of variation even when their widths
///         differ wildly.</item>
/// </list>
///
/// <para><b>How it works:</b> build a warm-colour mask over the lower-
/// centre search band; for each row, segment the warm columns into runs
/// and keep rows that yield 2–4 evenly-spaced runs; cluster those rows
/// vertically and take the <i>uppermost</i> cluster that reaches the
/// maximum card count (item names sit above the player-name row, which
/// is also evenly spaced).  The card width is derived from the uniform
/// spacing, and the crop is extended upward by one line to capture a
/// possible wrapped second line.</para>
/// </summary>
public sealed class WarmTextRowDetector : IRewardLayoutDetector
{
    // ── Tunables (fractions of window dimensions) ────────────────────

    /// <summary>
    /// Vertical search range for the item-name row, as fractions of
    /// window height.  Covers the name band across common UI scales.
    /// </summary>
    private const double SearchTopFraction = 0.25;
    private const double SearchBotFraction = 0.58;

    /// <summary>
    /// Minimum width of a warm run for it to count as a card's name
    /// segment, as a fraction of window width.
    /// </summary>
    private const double MinSegmentWidthFraction = 0.035;

    /// <summary>
    /// Horizontal gap (fraction of width) bridged when joining warm runs
    /// into a single name segment.  Inter-word gaps inside one name are
    /// smaller than the dark gaps between cards.
    /// </summary>
    private const double GapCloseFraction = 0.011;

    /// <summary>
    /// Maximum coefficient of variation of card centre-to-centre spacing
    /// for a row to be accepted.  Real cards are evenly spaced; text that
    /// merely happens to be warm is not.
    /// </summary>
    private const double MaxSpacingCv = 0.06;

    /// <summary>
    /// Maximum vertical gap (fraction of height) between qualifying rows
    /// that still belong to the same text cluster.
    /// </summary>
    private const double RowClusterGapFraction = 0.010;

    /// <summary>
    /// Height of one line of name text, as a fraction of window height.
    /// Used to extend the crop upward for a wrapped second line.
    /// </summary>
    private const double NameLineHeightFraction = 0.020;

    /// <summary>
    /// Card crop width as a fraction of the centre-to-centre spacing.
    /// Slightly under 1 so adjacent crops never overlap.
    /// </summary>
    private const double CardWidthFactor = 0.94;

    /// <summary>Reward screens show between 2 and 4 cards.</summary>
    private const int MinCards = 2;
    private const int MaxCards = 4;

    // ── Public API ───────────────────────────────────────────────────

    /// <inheritdoc />
    public List<Rectangle> DetectCardBoundaries(
        Bitmap windowScreenshot,
        int windowWidth,
        int windowHeight)
    {
        if (windowWidth < 320 || windowHeight < 240)
            return [];

        int searchTop = (int)(SearchTopFraction * windowHeight);
        int searchBot = Math.Min(windowHeight, (int)(SearchBotFraction * windowHeight));
        int bandHeight = searchBot - searchTop;
        if (bandHeight < 3)
            return [];

        bool[,] warm = BuildWarmMask(windowScreenshot, windowWidth, searchTop, bandHeight);

        int minSegWidth = (int)(MinSegmentWidthFraction * windowWidth);
        int gapClose = Math.Max(8, (int)(GapCloseFraction * windowWidth));

        // ── Find rows of 2–4 evenly-spaced warm-text segments ────────
        var rows = new List<RowMatch>();
        for (int r = 0; r < bandHeight; r++)
        {
            var segments = SegmentRow(warm, r, bandHeight, windowWidth, gapClose, minSegWidth);
            if (segments.Count < MinCards || segments.Count > MaxCards)
                continue;

            double[] centers = segments.Select(s => (s.Start + s.End) / 2.0).ToArray();
            if (SpacingCv(centers) > MaxSpacingCv)
                continue;

            rows.Add(new RowMatch(searchTop + r, centers));
        }

        if (rows.Count == 0)
        {
            Debug.WriteLine("[WarmTextRowDetector] No evenly-spaced warm-text rows found.");
            return [];
        }

        // ── Cluster rows vertically; pick the uppermost cluster that
        //    reaches the maximum card count (names sit above players) ──
        int rowClusterGap = Math.Max(4, (int)(RowClusterGapFraction * windowHeight));
        var clusters = ClusterRows(rows, rowClusterGap);

        int maxCount = clusters.Max(c => c.Max(r => r.Centers.Length));
        var nameCluster = clusters.First(c => c.Max(r => r.Centers.Length) == maxCount);

        RowMatch rep = nameCluster
            .OrderByDescending(r => r.Centers.Length)
            .First();

        double[] cardCenters = rep.Centers;
        int n = cardCenters.Length;

        double pitch = MedianSpacing(cardCenters);
        int cardWidth = Math.Max(minSegWidth, (int)(pitch * CardWidthFactor));

        // ── Build the bottom-anchored crop rectangles ────────────────
        // The cluster's top row is the highest detected name line; extend
        // up one line for a possible wrapped line, down half a line for
        // descenders.
        int lineHeight = Math.Max(1, (int)(NameLineHeightFraction * windowHeight));
        int clusterTop = nameCluster.Min(r => r.Y);
        int clusterBot = nameCluster.Max(r => r.Y);

        int top = Math.Max(0, clusterTop - lineHeight - 4);
        int bottom = Math.Min(windowHeight, clusterBot + lineHeight / 2);
        int height = Math.Max(1, bottom - top);

        var rects = new List<Rectangle>(n);
        foreach (double cx in cardCenters)
        {
            int x = Math.Max(0, (int)(cx - cardWidth / 2.0));
            int width = Math.Min(cardWidth, windowWidth - x);
            rects.Add(new Rectangle(x, top, width, height));
        }

        Debug.WriteLine(
            $"[WarmTextRowDetector] Detected {n} card(s); name band y={top}-{bottom}, " +
            $"pitch={pitch:F0}, width={cardWidth}.");

        return rects;
    }

    // ── Warm-colour mask ─────────────────────────────────────────────

    /// <summary>
    /// Builds a boolean mask of warm/amber pixels (Warframe UI text) over
    /// the horizontal strip [<paramref name="top"/>,
    /// <paramref name="top"/> + <paramref name="height"/>).
    /// </summary>
    private static bool[,] BuildWarmMask(Bitmap bitmap, int width, int top, int height)
    {
        var mask = new bool[height, width];
        var rect = new Rectangle(0, top, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

        try
        {
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < height; y++)
                {
                    byte* rowPtr = basePtr + y * data.Stride;
                    for (int x = 0; x < width; x++)
                    {
                        byte* px = rowPtr + x * 3;
                        int b = px[0], g = px[1], r = px[2];
                        // Warm amber/off-white text: R dominant, B low,
                        // with a wide R−B separation.
                        mask[y, x] = r > 120 && g > 70 && b < 110
                                  && r >= g && g > b && (r - b) > 50;
                    }
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return mask;
    }

    // ── Per-row segmentation ─────────────────────────────────────────

    /// <summary>
    /// Segments one row into warm runs.  A column counts as "on" when any
    /// of the three rows centred on <paramref name="r"/> is warm (smooths
    /// single-row gaps in anti-aliased text).  Runs separated by no more
    /// than <paramref name="gapClose"/> px are merged; runs narrower than
    /// <paramref name="minWidth"/> px are discarded.
    /// </summary>
    private static List<Segment> SegmentRow(
        bool[,] warm, int r, int bandHeight, int width, int gapClose, int minWidth)
    {
        var segments = new List<Segment>();
        int runStart = -1;
        int lastOn = -1;

        int rUp = Math.Max(0, r - 1);
        int rDn = Math.Min(bandHeight - 1, r + 1);

        for (int x = 0; x < width; x++)
        {
            bool on = warm[r, x] || warm[rUp, x] || warm[rDn, x];
            if (on)
            {
                if (runStart < 0)
                    runStart = x;
                lastOn = x;
            }
            else if (runStart >= 0 && x - lastOn > gapClose)
            {
                AddSegment(segments, runStart, lastOn, minWidth);
                runStart = -1;
            }
        }

        if (runStart >= 0)
            AddSegment(segments, runStart, lastOn, minWidth);

        return segments;
    }

    private static void AddSegment(List<Segment> segments, int start, int end, int minWidth)
    {
        if (end - start >= minWidth)
            segments.Add(new Segment(start, end));
    }

    // ── Row clustering ───────────────────────────────────────────────

    /// <summary>
    /// Groups vertically-adjacent qualifying rows (gap ≤
    /// <paramref name="maxGap"/>) into clusters, preserving top-to-bottom
    /// order.  <paramref name="rows"/> is already in ascending Y order.
    /// </summary>
    private static List<List<RowMatch>> ClusterRows(List<RowMatch> rows, int maxGap)
    {
        var clusters = new List<List<RowMatch>> { new() { rows[0] } };
        for (int i = 1; i < rows.Count; i++)
        {
            var current = clusters[^1];
            if (rows[i].Y - current[^1].Y <= maxGap)
                current.Add(rows[i]);
            else
                clusters.Add(new List<RowMatch> { rows[i] });
        }

        return clusters;
    }

    // ── Spacing statistics ───────────────────────────────────────────

    /// <summary>
    /// Coefficient of variation (std-dev / mean) of the gaps between
    /// consecutive centres.  Returns 0 for a single gap (two cards).
    /// </summary>
    private static double SpacingCv(double[] centers)
    {
        if (centers.Length < 3)
            return 0;

        double[] gaps = new double[centers.Length - 1];
        for (int i = 0; i < gaps.Length; i++)
            gaps[i] = centers[i + 1] - centers[i];

        double mean = gaps.Average();
        if (mean <= 0)
            return double.MaxValue;

        double variance = gaps.Sum(x => (x - mean) * (x - mean)) / gaps.Length;
        return Math.Sqrt(variance) / mean;
    }

    /// <summary>Median centre-to-centre spacing.</summary>
    private static double MedianSpacing(double[] centers)
    {
        double[] gaps = new double[centers.Length - 1];
        for (int i = 0; i < gaps.Length; i++)
            gaps[i] = centers[i + 1] - centers[i];

        Array.Sort(gaps);
        int mid = gaps.Length / 2;
        return gaps.Length % 2 == 1
            ? gaps[mid]
            : (gaps[mid - 1] + gaps[mid]) / 2.0;
    }

    private readonly record struct Segment(int Start, int End);

    private readonly record struct RowMatch(int Y, double[] Centers);
}
