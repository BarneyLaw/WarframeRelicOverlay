namespace WarframeRelicOverlay.Presentation.ViewModels;

using System.Collections.Generic;
using System.Linq;
using WarframeRelicOverlay.Core;

/// <summary>
/// Data object for a single price label positioned over a reward card.
///
/// <para>
/// Immutable: every property is <c>init</c>-only and the derived
/// properties are pure functions of those.  The view model replaces the
/// whole <c>PriceLabels</c> collection on each update rather than
/// mutating individual labels, so this type deliberately does not
/// implement <see cref="System.ComponentModel.INotifyPropertyChanged"/>.
/// </para>
/// </summary>
public sealed class PriceLabel
{
    /// <summary>Display text (e.g. "Sell: 45◆", "Buy: 38◆", "N/A", "?").</summary>
    public required string Text { get; init; }

    /// <summary>Matched canonical item name, or null if unmatched.</summary>
    public string? ItemName { get; init; }

    /// <summary>Left edge of the label in logical (DIP) units.</summary>
    public required double Left { get; init; }

    /// <summary>Top edge of the label in logical (DIP) units.</summary>
    public required double Top { get; init; }

    /// <summary>
    /// Width of the game's reward card in DIPs. The price label is
    /// constrained to this width and centered within it so it aligns
    /// with the in-game card above.
    /// </summary>
    public double MaxWidth { get; init; }

    /// <summary>True if the item is untradeable (e.g. Forma).</summary>
    public bool IsUntradeable { get; init; }

    /// <summary>True if the fuzzy matcher failed to identify the item.</summary>
    public bool IsFailed { get; init; }

    /// <summary>
    /// ARGB hex colour for the card background, sourced from
    /// <see cref="AppSettings.CardBackgroundColor"/>.
    /// </summary>
    public string BackgroundColor { get; init; } = "#EE181410";

    /// <summary>
    /// Up to 5 lowest sell prices for display in Top 5 mode.
    /// Empty in Top 1 mode.
    /// </summary>
    public IReadOnlyList<int> TopSellPrices { get; init; } = [];

    /// <summary>
    /// Up to 5 highest buy prices for display in Top 5 mode.
    /// Empty in Top 1 mode.
    /// </summary>
    public IReadOnlyList<int> TopBuyPrices { get; init; } = [];

    /// <summary>
    /// Highest buy offer in platinum, or null if unavailable.
    /// Shown when price display mode is "Both".
    /// </summary>
    public int? BuyPrice { get; init; }

    /// <summary>
    /// Number of in-game sellers at or near the lowest sell price.
    /// </summary>
    public int SellerCount { get; init; }

    /// <summary>
    /// The active price display mode ("Sell", "Buy", or "Both").
    /// Used to determine what labels and detail rows to render.
    /// </summary>
    public string PriceDisplayMode { get; init; } = "Sell";

    /// <summary>Whether there are additional prices to show below the primary.</summary>
    public bool HasTopPrices =>
        (PriceDisplayMode is "Sell" or "Both" && TopSellPrices.Count > 1)
        || (PriceDisplayMode is "Buy" or "Both" && TopBuyPrices.Count > 1);

    /// <summary>
    /// Formatted string showing the additional sell/buy prices.
    /// Sell prices sorted ascending (cheapest first), buy prices sorted descending (highest first).
    /// </summary>
    public string TopPricesText
    {
        get
        {
            var lines = new List<string>();

            if ((PriceDisplayMode is "Sell" or "Both") && TopSellPrices.Count > 1)
            {
                var others = TopSellPrices.Skip(1).OrderBy(p => p).Select(p => $"{p}◆");
                lines.Add($"Next lowest sellers: {string.Join(", ", others)}");
            }

            if ((PriceDisplayMode is "Buy" or "Both") && TopBuyPrices.Count > 1)
            {
                var others = TopBuyPrices.Skip(1).OrderByDescending(p => p).Select(p => $"{p}◆");
                lines.Add($"Next highest buyers: {string.Join(", ", others)}");
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>
    /// Detail text shown below the primary price.
    /// In "Both" mode shows the buy price.
    /// </summary>
    public string DetailText
    {
        get
        {
            if (PriceDisplayMode == "Both" && BuyPrice.HasValue)
                return $"Buyers pay: {BuyPrice.Value}◆";

            return string.Empty;
        }
    }

    /// <summary>Whether the detail row has content to display.</summary>
    public bool HasDetail => !string.IsNullOrEmpty(DetailText);
}
