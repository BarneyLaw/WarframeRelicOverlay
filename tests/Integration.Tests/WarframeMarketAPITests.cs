namespace Infrastructure.Tests;
 
using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.Market;
using Xunit;
 
/// <summary>
/// These tests hit the live Warframe Market API.
/// They verify that the endpoint, JSON shape, and filtering logic
/// work against real responses — not mocked ones.
///
/// Excluded from normal test runs via the "Integration" trait.
/// Run explicitly with:  dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class WarframeMarketClientIntegrationTests : IDisposable
{
    private readonly HttpClient _http;
    private readonly WarframeMarketClient _client;
 
    public WarframeMarketClientIntegrationTests()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.warframe.market/v2/"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "WarframeRelicOverlay/1.0-test");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.Add("Platform", "pc");
        _http.DefaultRequestHeaders.Add("Language", "en");
 
        _client = new WarframeMarketClient(_http);
    }
 
    [Fact]
    public async Task KnownItem_ReturnsAPrice()
    {
        // Ash Prime Chassis has been in the game since 2015 — it will always have sellers.
        var price = await _client.GetLowestSellPriceAsync("ash_prime_chassis_blueprint");
 
        price.Should().NotBeNull("a long-standing tradeable item should have at least one in-game seller");
        price.Should().BeInRange(1, 10000, "price should be a reasonable platinum value");
    }
 
    [Fact]
    public async Task NonexistentItem_ReturnsNull()
    {
        var price = await _client.GetLowestSellPriceAsync("this_item_does_not_exist_99999");
 
        price.Should().BeNull();
    }
 
    [Fact]
    public async Task Cancellation_StopsRequest()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
 
        var act = () => _client.GetLowestSellPriceAsync("ash_prime_chassis", cts.Token);
 
        await act.Should().ThrowAsync<TaskCanceledException>();
    }
 
    public void Dispose()
    {
        _http.Dispose();
    }
}
