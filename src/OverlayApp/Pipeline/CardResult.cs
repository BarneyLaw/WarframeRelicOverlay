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
    /// Highest buy offer in platinum from an in-game PC buyer, or
    /// <c>null</c> if no buy orders exist.  Shown on the overlay card
    /// so the player knows the instant-sell value.
    /// </summary>
    public int? HighestBuyPrice { get; init; }

    /// <summary>
    /// Number of in-game PC sellers at or near the lowest sell price.
    /// Gives the player confidence that the displayed price is liquid.
    /// Zero when no data is available.
    /// </summary>
    public int SellerCount { get; init; }

    /// <summary>
    /// Up to 5 lowest sell prices from in-game PC sellers, ordered ascending.
    /// Used when the user toggles "Top 5" mode on the overlay card.
    /// Empty when no data is available.
    /// </summary>
    public IReadOnlyList<int> TopSellPrices { get; init; } = [];

    /// <summary>
    /// Up to 5 highest buy prices from in-game PC buyers, ordered descending.
    /// Used when the user toggles "Top 5" mode on the overlay card.
    /// Empty when no data is available.
    /// </summary>
    public IReadOnlyList<int> TopBuyPrices { get; init; } = [];

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
    /// "N/A" for untradeable items (e.g. Forma), or "?" if matching failed.
    /// </summary>
    public string DisplayText => MatchedItem switch
    {
        null => "?",
        { IsUntradeable: true } => "N/A",
        _ when PricePlatinum.HasValue => $"{PricePlatinum.Value}◆",
        _ => "N/A",
    };
}