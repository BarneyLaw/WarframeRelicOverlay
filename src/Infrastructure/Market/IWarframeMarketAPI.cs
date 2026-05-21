namespace WarframeRelicOverlay.Infrastructure.Market;

/// <summary>
/// Interface for the Warframe Market API client. Abstracts away the implementation details of how we fetch prices from the API, 
/// allowing for easier testing and separation of concerns.
/// </summary>
public interface IWarframeMarketAPI
{
    public Task<int?> GetLowestSellPriceAsync(string slug, CancellationToken cancellationToken = default);
}