namespace WarframeRelicOverlay.Infrastructure.Market;

using System.Net;
using System.Net.Http;
using System.Text.Json;

public sealed class WarframeMarketClient : IWarframeMarketAPI
{
    private readonly HttpClient _Http;

/// <summary>
/// Fetches item prices from the Warframe Market v2 API.
///
/// Expects an HttpClient injected via constructor so that:
///   - BaseAddress, Timeout, and default headers can be configured at the DI level
///   - Tests can supply a mock HttpMessageHandler
///
/// Required HttpClient setup in the composition root:
/// <code>
///   var http = new HttpClient { BaseAddress = new Uri("https://api.warframe.market/v2/") };
///   http.Timeout = TimeSpan.FromSeconds(5);
///   http.DefaultRequestHeaders.Add("User-Agent", "WarframeRelicOverlay/1.0");
///   http.DefaultRequestHeaders.Add("Accept", "application/json");
///   http.DefaultRequestHeaders.Add("Platform", "pc");
///   http.DefaultRequestHeaders.Add("Language", "en");
/// </code>
/// </summary>

/// </summary>

    public WarframeMarketClient(HttpClient http)
    {   
        // HttpClient is intended to be shared and reused, so we inject it via constructor
        // Cannot be null since we need it to make API calls, but we can allow it to be configured by the caller (e.g. for testing with a mock)
        _Http = http ?? throw new ArgumentNullException(nameof(http));

    }

    public async Task<int?> GetLowestSellPriceAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        HttpResponseMessage response;

        try
        {
            response = await _Http.GetAsync($"orders/item/{slug}/top", ct);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A 404 likely means the item slug is invalid or the item has no orders, which is not exceptional but just means we have no price data.
                return null;
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // A 429 means we are being rate limited. This is important to log, but for this method we will just return null and let the caller decide if they want to retry after some delay.
                return null;
                // NOTE: We could implement an exponential backoff retry here, but that might be better handled at a higher level (e.g. a caching layer) rather than directly in the API client.
            }

            // In any other non-successful codes, we will also return null but it is worth logging these cases as they might indicate issues with the API or our requests.
            return null;
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<TopOrdersResponse>(json);
 
            if (result?.Data?.Sell is not { Count: > 0 } sellOrders)
                return null;
 
            var lowestInGame = sellOrders
                .Where(o => o.User?.Status == "ingame")
                .Where(o => o.User?.Platform is null or "pc")
                .OrderBy(o => o.Platinum)
                .FirstOrDefault();

            return lowestInGame?.Platinum;

        }
        catch (JsonException)
        {
            // Log as needed, but for this method we will just return null on deserialization failure since it likely means the API response format has changed or there is some unexpected data.
            return null;
        }
        
    }


}

