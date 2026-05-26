namespace WarframeRelicOverlay.OverlayApp.Pipeline;

using System.Drawing;
using WarframeRelicOverlay.Domain.Models;

/// <summary>
/// The pipeline's output for a single reward card.  Carries everything
/// the overlay needs: the matched item (if any), its price, the raw OCR
/// text for diagnostics, and the card's bounding rectangle in physical
/// pixels so the UI layer can position price labels directly above each
/// detected card without any hardcoded offset math.
/// </summary>
public sealed record CardResult
{
    /// <summary>
    /// Zero-based index of this card within the detected set (left to right).
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// The card's bounding rectangle in physical-pixel coordinates
    /// relative to the window screenshot.  The presentation layer
    /// translates this to screen space by adding the window's
    /// <c>ClientX</c> / <c>ClientY</c> offset.
    /// </summary>
    public required Rectangle BoundsInWindow { get; init; }

    /// <summary>
    /// The reward item matched from the OCR text, or <c>null</c> if
    /// the fuzzy matcher found nothing above threshold.
    /// </summary>
    public RewardItem? MatchedItem { get; init; }

    /// <summary>
    /// Lowest sell price in platinum, or <c>null</c> if the item is
    /// untradeable, unmatched, or the API call failed.
    /// </summary>
    public int? PricePlatinum { get; init; }

    /// <summary>
    /// Raw text returned by the OCR engine for this card.
    /// Useful for the debug log tab and for diagnosing match failures.
    /// </summary>
    public string RawOcrText { get; init; } = string.Empty;

    /// <summary>
    /// True when the pipeline successfully matched an item and either
    /// fetched a price or confirmed the item is untradeable.
    /// </summary>
    public bool IsSuccessful => MatchedItem is not null;

    /// <summary>
    /// Display string for the overlay.  Returns the price in platinum,
    /// "Untradeable" for forma-style items, or "?" if matching failed.
    /// </summary>
    public string DisplayText => MatchedItem switch
    {
        null => "?",
        { IsUntradeable: true } => "Untradeable",
        _ when PricePlatinum.HasValue => $"{PricePlatinum.Value}p",
        _ => "N/A",
    };
}