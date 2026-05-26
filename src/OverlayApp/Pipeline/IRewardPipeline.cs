namespace WarframeRelicOverlay.OverlayApp.Pipeline;

using WarframeRelicOverlay.Infrastructure.Platform;

/// <summary>
/// Orchestrates one full reward-pricing cycle: capture the window,
/// detect card boundaries, OCR each card, match to known items, and
/// fetch prices.  The state machine calls this when entering the
/// <see cref="StateMachine.OverlayState.Pricing"/> state.
///
/// Implementations must be safe to call from a background thread —
/// nothing here touches the UI thread.
/// </summary>
public interface IRewardPipeline
{
    /// <summary>
    /// Executes the full capture-through-pricing pipeline against the
    /// Warframe window described by <paramref name="window"/>.
    ///
    /// The caller is responsible for:
    ///   - ensuring Warframe is running and focused,
    ///   - waiting any stabilization delay before calling,
    ///   - obtaining the <see cref="WindowSnapshot"/> from the window tracker.
    ///
    /// Returns a <see cref="PipelineResult"/> with per-card results and
    /// timing metadata.  Returns an empty result (no cards) if the
    /// window capture fails or no reward cards are detected.
    ///
    /// Does not throw on transient failures (capture fail, OCR garbage,
    /// network timeout) — those produce null/empty fields in the result.
    /// Only throws on cancellation.
    /// </summary>
    /// <param name="window">
    /// Snapshot of the Warframe window's client area at the moment
    /// the caller decided to trigger the pipeline.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token observed during OCR and network calls.
    /// </param>
    Task<PipelineResult> ExecuteAsync(
        WindowSnapshot window,
        CancellationToken cancellationToken = default);
}