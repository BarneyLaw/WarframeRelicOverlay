namespace WarframeRelicOverlay.OverlayApp.Pipeline;

using WarframeRelicOverlay.Infrastructure.Platform;

/// <summary>
/// Aggregate result of a single pipeline execution cycle.  Contains
/// per-card results, the window snapshot used for capture (so the
/// overlay can translate bitmap-relative rectangles to screen space),
/// and timing metadata for the debug log.
/// </summary>
public sealed record PipelineResult
{
    /// <summary>
    /// Per-card results, ordered left to right as they appear on screen.
    /// Empty if no reward cards were detected.
    /// </summary>
    public required IReadOnlyList<CardResult> Cards { get; init; }

    /// <summary>
    /// The window snapshot at the time of capture.  The presentation
    /// layer uses <c>ClientX</c> / <c>ClientY</c> to translate each
    /// card's <see cref="CardResult.BoundsInWindow"/> into screen
    /// coordinates for overlay positioning.
    /// </summary>
    public required WindowSnapshot Window { get; init; }

    /// <summary>
    /// Wall-clock time the pipeline took from capture through pricing.
    /// Displayed in the debug log tab.
    /// </summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// True if at least one card was detected, regardless of whether
    /// matching and pricing succeeded.
    /// </summary>
    public bool HasCards => Cards.Count > 0;

    /// <summary>
    /// True if every detected card was successfully matched to a
    /// reward item (some may still lack a price if the API failed).
    /// </summary>
    public bool AllMatched => Cards.Count > 0 && Cards.All(c => c.IsSuccessful);

    /// <summary>
    /// Returns an empty result indicating no reward screen was detected.
    /// </summary>
    public static PipelineResult Empty(WindowSnapshot window, TimeSpan elapsed) =>
        new()
        {
            Cards = [],
            Window = window,
            Elapsed = elapsed,
        };
}