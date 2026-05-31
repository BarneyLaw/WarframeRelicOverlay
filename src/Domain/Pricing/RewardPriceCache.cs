namespace WarframeRelicOverlay.Domain.Pricing;

using System.Collections.Concurrent;
using WarframeRelicOverlay.Infrastructure.Logging;
using WarframeRelicOverlay.Infrastructure.Market;

/// <summary>
/// Caching decorator over <see cref="IWarframeMarketAPI"/>.
///
/// Stores price lookups (including null results) in memory with a configurable TTL.
/// Two concurrent cache misses for the same slug will both call the API —
/// this is acceptable since the second write simply overwrites with a slightly
/// newer price, and adding locking would complicate the code for no real benefit.
/// </summary>
public sealed class RewardPriceCache : IPriceProvider
{
    private readonly IWarframeMarketAPI _marketApi;
    private readonly TimeSpan _ttl;
    private readonly ILogger? _logger;
    // thread-safe dictionary to store cached prices along with their timestamps.
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(); 

    private record struct CacheEntry
    {
        public int? Price { get; init; }
        public DateTime Timestamp { get; init; }
    }

    /// <summary>
    /// Wraps the given market API with an in-memory cache.
    /// </summary>
    /// <param name="api">The underlying market API to call on cache miss.</param>
    /// <param name="ttl">
    /// How long a cached price is valid. Defaults to 5 minutes.
    /// Intended to be driven by AppSettings.PriceCacheTtlMinutes.
    /// </param>
    public RewardPriceCache(IWarframeMarketAPI api, TimeSpan? ttl = null, ILogger? logger = null)
    {
        _marketApi = api ?? throw new ArgumentNullException(nameof(api));
        _ttl = ttl ?? TimeSpan.FromMinutes(5);
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<int?> GetPriceAsync(string itemName)
    {
        return GetPriceAsync(itemName, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task<int?> GetPriceAsync(string itemName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            _logger?.LogWarning("[PriceCache] Skipping lookup: item slug was empty.");
            return null;
        }

        if (_cache.TryGetValue(itemName, out var entry) && DateTime.UtcNow - entry.Timestamp < _ttl)
        {
            _logger?.LogInfo(
                $"[PriceCache] Hit for '{itemName}': {(entry.Price.HasValue ? $"{entry.Price.Value}p" : "no price")}.");
            return entry.Price;
        }

        _logger?.LogInfo($"[PriceCache] Miss for '{itemName}'; querying market.");
        int? price = await _marketApi.GetLowestSellPriceAsync(itemName, cancellationToken);
        _cache[itemName] = new CacheEntry { Price = price, Timestamp = DateTime.UtcNow };
        _logger?.LogInfo(
            $"[PriceCache] Stored '{itemName}': {(price.HasValue ? $"{price.Value}p" : "no price")}.");
        return price;
    }

    /// <summary>
    /// Returns the number of entries currently in the cache. Useful for testing or monitoring cache size over time.
    /// </summary>
    public int Count => _cache.Count;

    /// <summary>
    /// Clears all entries from the cache. Useful for testing or if you want to force refresh all prices.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Removes the cached price for a specific item, if it exists. 
    /// Useful for testing or if you want to force refresh the price for a specific item on the next lookup.
    /// </summary>
    /// <param name="itemName"></param>
    public void Remove(string itemName)
    {
        _cache.TryRemove(itemName, out _);
    }
    
    /// <summary>
    /// Removes all expired entries from the cache. This can be called periodically (e.g. via a timer) 
    /// to prevent unbounded memory growth if many unique items are looked up over time.
    /// </summary>
    public void RemoveExpiredEntries()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var kvp in _cache)
        {
            if (now - kvp.Value.Timestamp >= _ttl)
            {
                _cache.TryRemove(kvp.Key, out _);
            }
        }
    }
}
