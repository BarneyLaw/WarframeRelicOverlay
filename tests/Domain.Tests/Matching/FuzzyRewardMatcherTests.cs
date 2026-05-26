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
            new RewardItem("Ash Prime Neuroptics Blueprint"),
            new RewardItem("Forma Blueprint", IsUntradeable: true),
            new RewardItem("Nikana Prime Blade"),
        });

    private static FuzzyRewardMatcher CreateMatcher() => new(CreateRepository());

    // ── IRewardMatcher contract ──────────────────────────────────────────────

    [Fact]
    public void MatchSingle_WhenCalledThroughInterface_ReturnsBestReward()
    {
        IRewardMatcher matcher = CreateMatcher();

        var result = matcher.MatchSingle("Ash Prime Chassis Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ash Prime Chassis Blueprint");
    }

    // ── Single-item OCR expectation ─────────────────────────────────────────

    [Fact]
    public void MatchSingle_ReturnsOnlyOneReward_ForTypicalRewardText()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Ash Prime Neuroptics Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ash Prime Neuroptics Blueprint");
    }

    [Fact]
    public void MatchSingle_HandlesNoisyOCRText_WithInsertedCharactersAndSpacing()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("2 s w a prime neuroptics blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ash Prime Neuroptics Blueprint");
    }

    [Fact]
    public void MatchSingle_HandlesQuantityPrefixNoise_Like_2xFormaBlueprint()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("2 X Forma Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("2 X Forma Blueprint");
        result.IsUntradeable.Should().BeTrue();
    }

    // ── Exact / normalized matching ─────────────────────────────────────────

    [Fact]
    public void MatchSingle_ExactText_ReturnsMatchingReward()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Forma Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Forma Blueprint");
        result.IsUntradeable.Should().BeTrue();
    }

    [Fact]
    public void MatchSingle_NormalizesBluePrintAndWhitespace()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("  Ash Prime   Chassis  Blue Print  ");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ash Prime Chassis Blueprint");
    }

    // ── No match ────────────────────────────────────────────────────────────

    [Fact]
    public void MatchSingle_UnrelatedText_ReturnsNull()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("xylophone zzzq");

        result.Should().BeNull();
    }

    [Fact]
    public void Match_UnrelatedText_ReturnsEmptySequence()
    {
        var matcher = CreateMatcher();

        var results = matcher.Match("xylophone zzzq");

        results.Should().BeEmpty();
    }
}
