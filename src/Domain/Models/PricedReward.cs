namespace WarframeRelicOverlay.Domain.Models;

/// <summary>
/// A relic reward with its current market price and timestamp.
/// </summary>
public sealed record PricedReward(
    RewardItem Item,
    int? Price,
    DateTime PriceFetchedAt)
{
    /// <summary>
    /// Indicates whether a price was successfully fetched.
    /// </summary>
    public bool HasPrice => Price.HasValue;
}

