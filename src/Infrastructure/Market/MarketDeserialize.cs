namespace WarframeRelicOverlay.Infrastructure.Market;

using System.Text.Json.Serialization;
 
/// <summary>
/// Top-level response from GET /v2/orders/item/{slug}/top
/// </summary>
public sealed class TopOrdersResponse
{
    [JsonPropertyName("data")]
    public TopOrdersData? Data { get; set; }
}
 
/// <summary>
/// Contains pre-separated sell and buy order lists.
/// The /top endpoint returns only the best-priced orders per side.
/// </summary>
public sealed class TopOrdersData
{
    [JsonPropertyName("sell")]
    public List<MarketOrder>? Sell { get; set; }
 
    [JsonPropertyName("buy")]
    public List<MarketOrder>? Buy { get; set; }
}
 
/// <summary>
/// A single order from the Warframe Market API.
/// Only the fields needed for price lookup are mapped.
/// </summary>
public sealed class MarketOrder
{
    [JsonPropertyName("platinum")]
    public int Platinum { get; set; }

    [JsonPropertyName("type")]
    public string? OrderType { get; set; }

    [JsonPropertyName("user")]
    public MarketUser? User { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

/// <summary>
/// Minimal user info attached to an order.
/// Status values: "ingame", "online", "offline".
/// Platform is null when the API omits it — treat as "pc".
/// </summary>
public sealed class MarketUser
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }
}
