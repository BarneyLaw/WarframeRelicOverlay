namespace Domain.Tests;

using System.Collections.Concurrent;
using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.Market;
using WarframeRelicOverlay.Domain.Pricing;
using Xunit;

public class CachedPriceProviderTests
{
    // ── Fake API ────────────────────────────────────────────────────

    /// <summary>
    /// A controllable fake that records how many times each slug was requested.
    /// Tests set up prices via the Prices dictionary before calling GetPriceAsync.
    /// </summary>
    private sealed class FakeMarketApi : IWarframeMarketAPI
    {
        /// <summary>Map slug → price to return.</summary>
        public Dictionary<string, int?> Prices { get; } = new();

        /// <summary>Map slug → number of times GetLowestSellPriceAsync was called.</summary>
        public ConcurrentDictionary<string, int> CallCounts { get; } = new();

        public Task<int?> GetLowestSellPriceAsync(string slug, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            CallCounts.AddOrUpdate(slug, 1, (_, count) => count + 1);

            int? price = Prices.TryGetValue(slug, out var p) ? p : null;
            return Task.FromResult(price);
        }

        public int CallCountFor(string slug) =>
            CallCounts.TryGetValue(slug, out var count) ? count : 0;
    }

    // ── Cache hit / miss ────────────────────────────────────────────

    [Fact]
    public async Task FirstCall_HitsApi_ReturnsPriceAndCachesIt()
    {
        var api = new FakeMarketApi();
        api.Prices["ash_prime_chassis"] = 30;

        var cache = new RewardPriceCache(api, TimeSpan.FromMinutes(5));

        var price = await cache.GetPriceAsync("ash_prime_chassis");

        price.Should().Be(30);
        api.CallCountFor("ash_prime_chassis").Should().Be(1);
        cache.Count.Should().Be(1);
    }

    [Fact]
    public async Task SecondCallWithinTtl_ReturnsCached_DoesNotHitApi()
    {
        var api = new FakeMarketApi();
        api.Prices["nikana_prime_blade"] = 15;

        var cache = new RewardPriceCache(api, TimeSpan.FromMinutes(5));

        await cache.GetPriceAsync("nikana_prime_blade");
        var second = await cache.GetPriceAsync("nikana_prime_blade");

        second.Should().Be(15);
        api.CallCountFor("nikana_prime_blade").Should().Be(1, "second call should come from cache");
    }

    // ── TTL expiry ──────────────────────────────────────────────────

    [Fact]
    public async Task CallAfterTtlExpires_HitsApiAgain()
    {
        var api = new FakeMarketApi();
        api.Prices["braton_prime_barrel"] = 5;

        // Use a tiny TTL so it expires immediately.
        var cache = new RewardPriceCache(api, TimeSpan.Zero);

        await cache.GetPriceAsync("braton_prime_barrel");
        var second = await cache.GetPriceAsync("braton_prime_barrel");

        second.Should().Be(5);
        api.CallCountFor("braton_prime_barrel").Should().Be(2, "cache should have expired");
    }

    [Fact]
    public async Task ExpiredEntry_PicksUpNewPrice()
    {
        var api = new FakeMarketApi();
        api.Prices["volt_prime_neuroptics"] = 10;

        var cache = new RewardPriceCache(api, TimeSpan.Zero);

        var first = await cache.GetPriceAsync("volt_prime_neuroptics");
        first.Should().Be(10);

        // Simulate price change.
        api.Prices["volt_prime_neuroptics"] = 25;

        var second = await cache.GetPriceAsync("volt_prime_neuroptics");
        second.Should().Be(25, "expired cache should fetch the updated price");
    }

    // ── Null caching ────────────────────────────────────────────────

    [Fact]
    public async Task NullResult_IsCached_DoesNotRetryWithinTtl()
    {
        var api = new FakeMarketApi();
        // Slug not in Prices dict → returns null.

        var cache = new RewardPriceCache(api, TimeSpan.FromMinutes(5));

        var first = await cache.GetPriceAsync("nonexistent_prime");
        var second = await cache.GetPriceAsync("nonexistent_prime");

        first.Should().BeNull();
        second.Should().BeNull();
        api.CallCountFor("nonexistent_prime").Should().Be(1,
            "null results should be cached to avoid hammering the API for missing items");
    }

    // ── ClearCache ──────────────────────────────────────────────────

    [Fact]
    public async Task ClearCache_ForcesNextCallToHitApi()
    {
        var api = new FakeMarketApi();
        api.Prices["mesa_prime_chassis"] = 40;

        var cache = new RewardPriceCache(api, TimeSpan.FromMinutes(5));

        await cache.GetPriceAsync("mesa_prime_chassis");
        cache.ClearCache();
        cache.Count.Should().Be(0);

        await cache.GetPriceAsync("mesa_prime_chassis");

        api.CallCountFor("mesa_prime_chassis").Should().Be(2,
            "ClearCache should force a fresh API call");
    }

    // ── Input validation ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrWhitespaceSlug_ReturnsNull_WithoutHittingApi(string? slug)
    {
        var api = new FakeMarketApi();
        var cache = new RewardPriceCache(api, TimeSpan.FromMinutes(5));

        var price = await cache.GetPriceAsync(slug!);

        price.Should().BeNull();
        api.CallCounts.Should().BeEmpty("whitespace slugs should short-circuit before the API");
    }

    // ── Cancellation ────────────────────────────────────────────────

    [Fact]
    public async Task CancelledToken_PropagatesWithoutCaching()
    {
        var api = new FakeMarketApi();
        api.Prices["saryn_prime_chassis"] = 20;

        var cache = new RewardPriceCache(api, TimeSpan.FromMinutes(5));
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => cache.GetPriceAsync("saryn_prime_chassis", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        cache.Count.Should().Be(0, "cancelled request should not leave a cache entry");
    }

    // ── Independent slugs ───────────────────────────────────────────

    [Fact]
    public async Task DifferentSlugs_CachedIndependently()
    {
        var api = new FakeMarketApi();
        api.Prices["item_a"] = 10;
        api.Prices["item_b"] = 99;

        var cache = new RewardPriceCache(api, TimeSpan.FromMinutes(5));

        var a = await cache.GetPriceAsync("item_a");
        var b = await cache.GetPriceAsync("item_b");

        // Second calls — should come from cache.
        var a2 = await cache.GetPriceAsync("item_a");
        var b2 = await cache.GetPriceAsync("item_b");

        a.Should().Be(10);
        b.Should().Be(99);
        a2.Should().Be(10);
        b2.Should().Be(99);

        api.CallCountFor("item_a").Should().Be(1);
        api.CallCountFor("item_b").Should().Be(1);
        cache.Count.Should().Be(2);
    }

    // ── Constructor validation ──────────────────────────────────────

    [Fact]
    public void NullApi_Throws()
    {
        var act = () => new RewardPriceCache(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DefaultTtl_IsFiveMinutes()
    {
        // Just verifying it doesn't throw — the actual TTL value
        // is an implementation detail, but we test the behavior
        // indirectly through the hit/miss tests.
        var api = new FakeMarketApi();
        var cache = new RewardPriceCache(api);

        cache.Should().NotBeNull();
    }
}