namespace WarframeRelicOverlay.Infrastructure.Market;

/// <summary>
/// Interface for the Warframe Market API client. Abstracts away the implementation details of how we fetch prices from the API, 
/// allowing for easier testing and separation of concerns.
/// </summary>
public interface IWarframeMarketAPI
{
    /// <summary>
    /// Returns the lowest sell price for the given item slug, or null
    /// if unavailable.  Legacy method — prefer <see cref="GetMarketDataAsync"/>
    /// for richer results.
    /// </summary>
    Task<int?> GetLowestSellPriceAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns rich market data for the given item slug from the
    /// <c>/v2/orders/item/{slug}/top</c> endpoint: lowest sell price,
    /// highest buy price, and the number of in-game sellers.
    /// Returns <c>null</c> when no data is available.
    /// </summary>
    Task<MarketItemData?> GetMarketDataAsync(string slug, CancellationToken cancellationToken = default);
}

/// <summary>
/// Rich market summary for a single tradeable item, extracted from
/// the <c>/top</c> orders endpoint.
/// </summary>
/// <param name="LowestSellPrice">
/// Lowest platinum price among in-game PC sellers, or null if none.
/// </param>
/// <param name="HighestBuyPrice">
/// Highest platinum price among in-game PC buyers, or null if none.
/// </param>
/// <param name="SellerCount">
/// Number of distinct in-game PC sell orders returned by the endpoint.
/// </param>
public readonly record struct MarketItemData(
    int? LowestSellPrice,
    int? HighestBuyPrice,
    int SellerCount);