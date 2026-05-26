namespace WarframeRelicOverlay.Infrastructure.Market;

public sealed class MarketSlugConverter
{
    /// <summary>
    /// Converts a market item name to a slug used in the Warframe Market API.
    /// E.g. "cobra & crane prime hilt" -> "cobra_and_crane_prime_hilt"
    /// Handles special cases like "Prime Access" and "Bundle".
    /// </summary>
    public static string ToSlug(string itemName)
    {
        // Handle special cases first
        if (itemName.Contains("&", StringComparison.OrdinalIgnoreCase))
            return itemName.Replace("&", "and").Replace(" ", "_").ToLower().Trim();

        // 2x forma will be hardcoded since it does not follow any patterns
        // and it is not tradeable
        if (itemName.Equals("2x Forma Blueprint", StringComparison.OrdinalIgnoreCase))
            return "forma_blueprint";    

        // General case: replace spaces with underscores and lowercase
        return itemName.Replace(" ", "_").ToLower().Trim();
    }
}