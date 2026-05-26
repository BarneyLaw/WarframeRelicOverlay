namespace Domain.Tests;

using FluentAssertions;
using WarframeRelicOverlay.Domain.Matching;
using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Infrastructure.RewardData;
using Xunit;

public class FuzzyRewardMatcherTests
{
    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class FakeRewardRepository : IRewardRepository
    {
        public FakeRewardRepository(IEnumerable<RewardItem> items, string? version = "2025-05-19")
        {
            Items = items.ToList().AsReadOnly();
            _version = version;
        }

        public IReadOnlyList<RewardItem> Items { get; }
        private readonly string? _version;

        public IReadOnlyList<RewardItem> GetAll() => Items;
        public string? Version => _version;
    }

    private static IRewardRepository CreateRepository() => new FakeRewardRepository(
        new[]
        {
            new RewardItem("Ash Prime Chassis Blueprint"),
            new RewardItem("Wisp Prime Blueprint"),
            new RewardItem("Guandao Prime Handle"),
            new RewardItem("Baza Prime Stock"),
            new RewardItem("Aklex Prime Blueprint"),
            new RewardItem("Soma Prime Barrel"),
            new RewardItem("Forma Blueprint", IsUntradeable: true),
            new RewardItem("2 X Forma Blueprint", IsUntradeable: true),
            new RewardItem("Mesa Prime Blueprint"),
        });

    private static FuzzyRewardMatcher CreateMatcher() => new(CreateRepository());

    // ── Test Cases ──────────────────────────────────────────────────────────

    [Fact]
    public void Test1_AshPrimeChassisBlueprintShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Ash Prime Chassis Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ash Prime Chassis Blueprint");
    }

    [Fact]
    public void Test4_WispPrimeBlueprintShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Wisp Prime Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Wisp Prime Blueprint");
    }

    [Fact]
    public void Test5_GuandaoPrimeHandleShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Guandao Prime Handle");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Guandao Prime Handle");
    }

    [Fact]
    public void Test7_BazaPrimeStockShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Baza Prime Stock");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Baza Prime Stock");
    }

    [Fact]
    public void Test8_AklexPrimeBlueprintShouldMatchAklexPrimeBlueprint()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("aklex prime blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Aklex Prime Blueprint");
    }

    [Fact]
    public void Test9_SoaPrimeBarrelShouldMatchSomaPrimeBarrel()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("soa prime barrel");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Soma Prime Barrel");
    }

    [Fact]
    public void Test9_FormaBlueprintShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Forma Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Forma Blueprint");
        result.IsUntradeable.Should().BeTrue();
    }

    [Fact]
    public void Test10_2XFormaBlueprintShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("2 X Forma Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("2 X Forma Blueprint");
        result.IsUntradeable.Should().BeTrue();
    }

    [Fact]
    public void Test11_MesaPriMeblueprintShouldMatchMesaPrimeBlueprint()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("me sa pri meblueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Mesa Prime Blueprint");
    }

    [Fact]
    public void Test11_NoiseWithWispPrimeBlueprintShouldMatch()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle(" a sd w s d wisp prime blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Wisp Prime Blueprint");
    }

    [Fact]
    public void Test12_XylophoneShouldReturnError()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("xylophone");

        result.Should().BeNull();
    }
}
