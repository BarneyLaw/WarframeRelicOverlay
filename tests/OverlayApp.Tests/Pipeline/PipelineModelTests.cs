namespace WarframeRelicOverlay.Tests.OverlayApp.Pipeline;

using System.Drawing;
using FluentAssertions;
using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.OverlayApp.Pipeline;
using Xunit;

public class PipelineModelTests
{
    private static readonly Rectangle TestRect = new(100, 400, 200, 60);

    private static readonly WindowSnapshot TestWindow = new(
        ClientX: 0, ClientY: 0,
        ClientWidth: 1920, ClientHeight: 1080,
        DpiScaleX: 1.0, DpiScaleY: 1.0);

    // ── CardResult.DisplayText ──────────────────────────────────

    [Fact]
    public void DisplayText_NoMatch_ReturnsQuestionMark()
    {
        var card = new CardResult
        {
            Index = 0,
            BoundsInWindow = TestRect,
            MatchedItem = null,
            PricePlatinum = null,
        };

        card.DisplayText.Should().Be("?");
    }

    [Fact]
    public void DisplayText_Untradeable_ReturnsUntradeable()
    {
        var card = new CardResult
        {
            Index = 0,
            BoundsInWindow = TestRect,
            MatchedItem = new RewardItem("Forma Blueprint", IsUntradeable: true),
            PricePlatinum = null,
        };

        card.DisplayText.Should().Be("Untradeable");
    }

    [Fact]
    public void DisplayText_WithPrice_ReturnsPriceWithSuffix()
    {
        var card = new CardResult
        {
            Index = 0,
            BoundsInWindow = TestRect,
            MatchedItem = new RewardItem("Ash Prime Chassis Blueprint"),
            PricePlatinum = 42,
        };

        card.DisplayText.Should().Be("42p");
    }

    [Fact]
    public void DisplayText_MatchedButNoPrice_ReturnsNA()
    {
        var card = new CardResult
        {
            Index = 0,
            BoundsInWindow = TestRect,
            MatchedItem = new RewardItem("Ash Prime Chassis Blueprint"),
            PricePlatinum = null,
        };

        card.DisplayText.Should().Be("N/A");
    }

    [Fact]
    public void IsSuccessful_TrueWhenMatched()
    {
        var card = new CardResult
        {
            Index = 0,
            BoundsInWindow = TestRect,
            MatchedItem = new RewardItem("Ash Prime Chassis Blueprint"),
        };

        card.IsSuccessful.Should().BeTrue();
    }

    [Fact]
    public void IsSuccessful_FalseWhenNotMatched()
    {
        var card = new CardResult
        {
            Index = 0,
            BoundsInWindow = TestRect,
            MatchedItem = null,
        };

        card.IsSuccessful.Should().BeFalse();
    }

    // ── PipelineResult properties ───────────────────────────────

    [Fact]
    public void HasCards_TrueWhenCardsExist()
    {
        var result = new PipelineResult
        {
            Cards = [new CardResult { Index = 0, BoundsInWindow = TestRect, MatchedItem = null }],
            Window = TestWindow,
            Elapsed = TimeSpan.FromMilliseconds(100),
        };

        result.HasCards.Should().BeTrue();
    }

    [Fact]
    public void HasCards_FalseWhenEmpty()
    {
        var result = PipelineResult.Empty(TestWindow, TimeSpan.FromMilliseconds(5));
        result.HasCards.Should().BeFalse();
    }

    [Fact]
    public void AllMatched_TrueWhenEveryCardSuccessful()
    {
        var result = new PipelineResult
        {
            Cards =
            [
                new CardResult
                {
                    Index = 0, BoundsInWindow = TestRect,
                    MatchedItem = new RewardItem("Ash Prime Chassis Blueprint"),
                    PricePlatinum = 10,
                },
                new CardResult
                {
                    Index = 1, BoundsInWindow = TestRect,
                    MatchedItem = new RewardItem("Forma Blueprint", IsUntradeable: true),
                },
            ],
            Window = TestWindow,
            Elapsed = TimeSpan.FromMilliseconds(200),
        };

        result.AllMatched.Should().BeTrue();
    }

    [Fact]
    public void AllMatched_FalseWhenAnyCardUnmatched()
    {
        var result = new PipelineResult
        {
            Cards =
            [
                new CardResult
                {
                    Index = 0, BoundsInWindow = TestRect,
                    MatchedItem = new RewardItem("Ash Prime Chassis Blueprint"),
                },
                new CardResult
                {
                    Index = 1, BoundsInWindow = TestRect,
                    MatchedItem = null,
                },
            ],
            Window = TestWindow,
            Elapsed = TimeSpan.FromMilliseconds(200),
        };

        result.AllMatched.Should().BeFalse();
    }

    [Fact]
    public void AllMatched_FalseWhenNoCards()
    {
        var result = PipelineResult.Empty(TestWindow, TimeSpan.Zero);
        result.AllMatched.Should().BeFalse();
    }

    [Fact]
    public void Empty_CreatesCorrectResult()
    {
        var elapsed = TimeSpan.FromMilliseconds(42);
        var result = PipelineResult.Empty(TestWindow, elapsed);

        result.Cards.Should().BeEmpty();
        result.Window.Should().Be(TestWindow);
        result.Elapsed.Should().Be(elapsed);
        result.HasCards.Should().BeFalse();
    }
}