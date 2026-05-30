namespace WarframeRelicOverlay.Domain.Pricing;

/// <summary>
/// Provides platinum prices for tradeable items.
/// The pipeline depends on this interface — not the market API directly.
/// Implementations may cache, batch, or stub prices as needed.
/// </summary>
public interface IPriceProvider
{
    /// <summary>
    /// Returns the current lowest sell price (in platinum) for the given item slug,
    /// or null if the item is untradeable, not found, or pricing is unavailable.
    /// </summary>

    Task<int?> GetPriceAsync(string itemName, CancellationToken cancellationToken = default);
}
