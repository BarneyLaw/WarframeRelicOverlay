namespace Infrastructure.Tests;
 
using System.Net;
using System.Text.Json;
using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.Market;
using Xunit;
 
public class WarframeMarketClientTests
{
    // ── Test helpers ─────────────────────────────────────────────────
 
    /// <summary>
    /// A fake HttpMessageHandler that returns a fixed status code and body.
    /// Each test constructs its own, so responses are explicit — no shared state.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;
 
        public FakeHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }
 
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                return Task.FromCanceled<HttpResponseMessage>(ct);

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
 
    /// <summary>
    /// A handler that always throws — used for timeout and network error tests.
    /// </summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
 
        public ThrowingHandler(Exception exception) => _exception = exception;
 
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            throw _exception;
        }
    }
 
    /// <summary>
    /// Builds a JSON string matching the v2 /orders/item/{slug}/top response shape.
    /// </summary>
    private static string BuildResponseJson(List<MarketOrder>? sellOrders = null)
    {
        var response = new TopOrdersResponse
        {
            Data = new TopOrdersData { Sell = sellOrders }
        };
        return JsonSerializer.Serialize(response);
    }
 
    private static MarketOrder MakeOrder(
        int platinum,
        string userStatus = "ingame",
        string? platform = "pc")
    {
        return new MarketOrder
        {
            Platinum = platinum,
            OrderType = "sell",
            Quantity = 1,
            User = new MarketUser { Status = userStatus, Platform = platform }
        };
    }
 
    private static WarframeMarketClient CreateClient(HttpStatusCode status, string body)
    {
        var handler = new FakeHandler(status, body);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.warframe.market/v2/")
        };
        return new WarframeMarketClient(http);
    }
 
    private static WarframeMarketClient CreateClientThatThrows(Exception ex)
    {
        var handler = new ThrowingHandler(ex);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.warframe.market/v2/")
        };
        return new WarframeMarketClient(http);
    }
 
    // ── Happy path ──────────────────────────────────────────────────
 
    [Fact]
    public async Task ValidSlug_ReturnsLowestInGameSellPrice()
    {
        var json = BuildResponseJson(new List<MarketOrder>
        {
            MakeOrder(platinum: 45),
            MakeOrder(platinum: 30),
            MakeOrder(platinum: 60),
        });
 
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync("ash_prime_chassis");
 
        price.Should().Be(30);
    }
 
    [Fact]
    public async Task SingleSeller_ReturnsThatPrice()
    {
        var json = BuildResponseJson(new List<MarketOrder>
        {
            MakeOrder(platinum: 15),
        });
 
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync("braton_prime_barrel");
 
        price.Should().Be(15);
    }
 
    // ── Status filtering ────────────────────────────────────────────
 
    [Fact]
    public async Task IgnoresOfflineSellers_ReturnsLowestInGameOnly()
    {
        var json = BuildResponseJson(new List<MarketOrder>
        {
            MakeOrder(platinum: 10, userStatus: "offline"),
            MakeOrder(platinum: 50, userStatus: "ingame"),
            MakeOrder(platinum: 20, userStatus: "online"),  // online but not in-game
        });
 
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync("nikana_prime_blade");
 
        price.Should().Be(50, because: "only the in-game seller at 50p should count");
    }
 
    [Fact]
    public async Task NoInGameSellers_ReturnsNull()
    {
        var json = BuildResponseJson(new List<MarketOrder>
        {
            MakeOrder(platinum: 10, userStatus: "offline"),
            MakeOrder(platinum: 20, userStatus: "online"),
        });
 
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync("valkyr_prime_systems");
 
        price.Should().BeNull();
    }
 
    // ── Platform filtering ──────────────────────────────────────────
 
    [Fact]
    public async Task IgnoresNonPcPlatform()
    {
        var json = BuildResponseJson(new List<MarketOrder>
        {
            MakeOrder(platinum: 5, platform: "xbox"),
            MakeOrder(platinum: 80, platform: "pc"),
        });
 
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync("mesa_prime_chassis");
 
        price.Should().Be(80);
    }
 
    [Fact]
    public async Task NullPlatform_TreatedAsPc()
    {
        // The /top endpoint sometimes omits platform — treat null as valid.
        var order = MakeOrder(platinum: 25, platform: null);

        var json = BuildResponseJson(new List<MarketOrder> { order });
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync("rhino_prime_blueprint");
 
        price.Should().Be(25);
    }
 
    // ── Empty / missing data ────────────────────────────────────────
 
    [Fact]
    public async Task EmptySellList_ReturnsNull()
    {
        var json = BuildResponseJson(new List<MarketOrder>());
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync("loki_prime_blueprint");
 
        price.Should().BeNull();
    }
 
    [Fact]
    public async Task NullSellList_ReturnsNull()
    {
        var json = BuildResponseJson(sellOrders: null);
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync("saryn_prime_chassis");
 
        price.Should().BeNull();
    }
 
    [Fact]
    public async Task NullDataField_ReturnsNull()
    {
        // Response with data: null
        var json = """{"data": null}""";
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync("volt_prime_neuroptics");
 
        price.Should().BeNull();
    }
 
    // ── Input validation ────────────────────────────────────────────
 
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrWhitespaceSlug_ReturnsNull(string? slug)
    {
        // Should short-circuit without even making an HTTP call.
        var json = BuildResponseJson(new List<MarketOrder>
        {
            MakeOrder(platinum: 99),
        });
 
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var price = await client.GetLowestSellPriceAsync(slug!);
 
        price.Should().BeNull();
    }
 
    // ── HTTP error handling ─────────────────────────────────────────
 
    [Fact]
    public async Task Http404_ReturnsNull()
    {
        var client = CreateClient(HttpStatusCode.NotFound, "");
 
        var price = await client.GetLowestSellPriceAsync("nonexistent_prime_set");
 
        price.Should().BeNull();
    }
 
    [Fact]
    public async Task Http429_RateLimited_ReturnsNull()
    {
        var client = CreateClient(HttpStatusCode.TooManyRequests, "");
 
        var price = await client.GetLowestSellPriceAsync("ash_prime_chassis");
 
        price.Should().BeNull();
    }
 
    [Fact]
    public async Task Http500_ReturnsNull()
    {
        var client = CreateClient(HttpStatusCode.InternalServerError, "");
 
        var price = await client.GetLowestSellPriceAsync("ash_prime_chassis");
 
        price.Should().BeNull();
    }
 
    // ── Network / transport errors ──────────────────────────────────
 
    [Fact]
    public async Task Timeout_ReturnsNull()
    {
        // TaskCanceledException without a cancelled token = HTTP timeout.
        var client = CreateClientThatThrows(new TaskCanceledException());
 
        var price = await client.GetLowestSellPriceAsync("tiberon_prime_stock");
 
        price.Should().BeNull();
    }
 
    [Fact]
    public async Task NetworkError_ReturnsNull()
    {
        var client = CreateClientThatThrows(new HttpRequestException("connection refused"));
 
        var price = await client.GetLowestSellPriceAsync("tiberon_prime_stock");
 
        price.Should().BeNull();
    }
 
    [Fact]
    public async Task CallerCancellation_Throws()
    {
        // A cancelled CancellationToken should propagate, not be swallowed.
        var cts = new CancellationTokenSource();
        cts.Cancel();
 
        var json = BuildResponseJson(new List<MarketOrder> { MakeOrder(10) });
        var client = CreateClient(HttpStatusCode.OK, json);
 
        var act = () => client.GetLowestSellPriceAsync("any_slug", cts.Token);
 
        await act.Should().ThrowAsync<TaskCanceledException>();
    }
 
    // ── Malformed response ──────────────────────────────────────────
 
    [Fact]
    public async Task MalformedJson_ReturnsNull()
    {
        var client = CreateClient(HttpStatusCode.OK, "{{not valid json!!!");
 
        var price = await client.GetLowestSellPriceAsync("ash_prime_chassis");
 
        price.Should().BeNull();
    }
 
    [Fact]
    public async Task UnexpectedJsonShape_ReturnsNull()
    {
        // Valid JSON but not the expected structure.
        var client = CreateClient(HttpStatusCode.OK, """{"something": "else"}""");
 
        var price = await client.GetLowestSellPriceAsync("ash_prime_chassis");
 
        price.Should().BeNull();
    }
}
 
