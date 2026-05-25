namespace Domain.Tests;

using System.Collections.Concurrent;
using FluentAssertions;
using WarframeRelicOverlay.Domain.Pricing;
using Xunit;

public class CachedPriceProviderBehaviorTests
{
    // ── Test double ──────────────────────────────────────────────────────────

    private sealed class FakePriceProvider : IPriceProvider
    {
        public Dictionary<string, int?> Prices { get; } = new();
        public ConcurrentDictionary<string, int> CallCounts { get; } = new();

        public Task<int?> GetPriceAsync(string itemName)
        {
            CallCounts.AddOrUpdate(itemName, 1, (_, count) => count + 1);
            return Task.FromResult(Prices.TryGetValue(itemName, out var price) ? price : null);
        }

        public int CallCountFor(string itemName) =>
            CallCounts.TryGetValue(itemName, out var count) ? count : 0;
    }

    // ── Cache hit / miss ─────────────────────────────────────────────────────

    [Fact]
    public async Task FirstCall_HitsProvider_ReturnsPriceAndCachesIt()
    {
        var provider = new FakePriceProvider();
        provider.Prices["ash_prime_chassis"] = 30;

        var cache = new CachedPriceProvider(provider, TimeSpan.FromMinutes(5));

        var price = await cache.GetPriceAsync("ash_prime_chassis");

        price.Should().Be(30);
        provider.CallCountFor("ash_prime_chassis").Should().Be(1);
    }

    [Fact]
    public async Task SecondCallWithinTtl_ReturnsCachedPrice_DoesNotHitProviderAgain()
    {
        var provider = new FakePriceProvider();
        provider.Prices["nikana_prime_blade"] = 15;

        var cache = new CachedPriceProvider(provider, TimeSpan.FromMinutes(5));

        await cache.GetPriceAsync("nikana_prime_blade");
        var second = await cache.GetPriceAsync("nikana_prime_blade");

        second.Should().Be(15);
        provider.CallCountFor("nikana_prime_blade").Should().Be(1);
    }

    [Fact]
    public async Task SecondCallAfterExpiry_HitsProviderAgain()
    {
        var provider = new FakePriceProvider();
        provider.Prices["volt_prime_neuroptics"] = 10;

        var cache = new CachedPriceProvider(provider, TimeSpan.Zero);

        await cache.GetPriceAsync("volt_prime_neuroptics");
        provider.Prices["volt_prime_neuroptics"] = 25;

        var second = await cache.GetPriceAsync("volt_prime_neuroptics");

        second.Should().Be(25);
        provider.CallCountFor("volt_prime_neuroptics").Should().Be(2);
    }

    [Fact]
    public async Task NullResult_IsCached()
    {
        var provider = new FakePriceProvider();
        var cache = new CachedPriceProvider(provider, TimeSpan.FromMinutes(5));

        var first = await cache.GetPriceAsync("missing_item");
        var second = await cache.GetPriceAsync("missing_item");

        first.Should().BeNull();
        second.Should().BeNull();
        provider.CallCountFor("missing_item").Should().Be(1);
    }

    // ── Input validation ────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NullOrWhitespaceItemName_ReturnsNull_WithoutCallingProvider(string? itemName)
    {
        var provider = new FakePriceProvider();
        var cache = new CachedPriceProvider(provider, TimeSpan.FromMinutes(5));

        var price = await cache.GetPriceAsync(itemName!);

        price.Should().BeNull();
        provider.CallCounts.Should().BeEmpty();
    }

    // ── Clear cache ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ClearCache_ForcesNextCallToHitProvider()
    {
        var provider = new FakePriceProvider();
        provider.Prices["mesa_prime_chassis"] = 40;

        var cache = new CachedPriceProvider(provider, TimeSpan.FromMinutes(5));

        await cache.GetPriceAsync("mesa_prime_chassis");
        cache.ClearCache();
        await cache.GetPriceAsync("mesa_prime_chassis");

        provider.CallCountFor("mesa_prime_chassis").Should().Be(2);
    }

    // ── Constructor validation ──────────────────────────────────────────────

    [Fact]
    public void NullInnerProvider_Throws()
    {
        var act = () => new CachedPriceProvider(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
