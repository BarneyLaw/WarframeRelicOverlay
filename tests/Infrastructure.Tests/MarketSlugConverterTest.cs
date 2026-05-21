namespace Infrastructure.Tests;

using FluentAssertions;
using Xunit;

public class MarketSlugConverterTest
{
    [Fact]
    public void Space_Replaced_With_Underscore()
    {
        string itemName1 = "Ash Prime Chassis Blueprint";
        string expected1 = "ash_prime_chassis_blueprint";

        string itemName2 = "Corinth Prime Blueprint";
        string expected2 = "corinth_prime_blueprint";

        string itemName3 = "Forma Blueprint";
        string expected3 = "forma_blueprint";

        string itemName4 = "2x Forma Blueprint";
        string expected4 = "forma_blueprint";

        string result1 = MarketSlugConverter.ToSlug(itemName1);
        string result2 = MarketSlugConverter.ToSlug(itemName2);
        string result3 = MarketSlugConverter.ToSlug(itemName3);

        result1.Should().Be(expected1);
        result2.Should().Be(expected2);
        result3.Should().Be(expected3);
    }

    [Fact]

    public void Ampersand_Replaced_With_And()
    {
        string itemName = "Cobra & Crane Prime Hilt";
        string expected = "cobra_and_crane_prime_hilt";

        string result = MarketSlugConverter.ToSlug(itemName);

        result.Should().Be(expected);
    }
}
