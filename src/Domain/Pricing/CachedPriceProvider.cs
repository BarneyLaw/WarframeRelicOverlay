namespace WarframeRelicOverlay.Domain.Pricing;

/// <summary>
/// Caches price lookups to avoid repeatedly calling the inner provider.
/// </summary>
public class CachedPriceProvider : IPriceProvider
{
    private readonly IPriceProvider _innerProvider;
    private readonly Dictionary<string, (int? Price, DateTime FetchedAt)> _cache;
    private readonly TimeSpan _cacheDuration;

    public CachedPriceProvider(IPriceProvider innerProvider, TimeSpan? cacheDuration = null)
    {
        _innerProvider = innerProvider ?? throw new ArgumentNullException(nameof(innerProvider));
        _cacheDuration = cacheDuration ?? TimeSpan.FromMinutes(5);
        _cache = new Dictionary<string, (int?, DateTime)>();
    }

    /// <summary>
    /// Gets a price from cache when possible, otherwise fetches and stores it.
    /// </summary>
    public async Task<int?> GetPriceAsync(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return null;

        if (_cache.TryGetValue(itemName, out var cached))
        {
            if (DateTime.UtcNow - cached.FetchedAt < _cacheDuration)
            {
                return cached.Price;
            }
            else
            {
                _cache.Remove(itemName);
            }
        }

        var price = await _innerProvider.GetPriceAsync(itemName);
        _cache[itemName] = (price, DateTime.UtcNow);

        return price;
    }

    /// <summary>
    /// Clears all cached price entries.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }
}

