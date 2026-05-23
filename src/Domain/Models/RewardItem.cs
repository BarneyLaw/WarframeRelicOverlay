namespace WarframeRelicOverlay.Domain.Models;

/// <summary>
/// Immutable representation of a single relic reward (e.g. "Ash Prime Chassis Blueprint").
/// Loaded once at startup from the reward pool and shared across matching and pricing.
/// </summary>
/// <param name="CanonicalName">
/// The full display name exactly as it appears on the Warframe reward screen,
/// e.g. "Ash Prime Chassis Blueprint" or "Forma Blueprint".
/// </param>
/// <param name="IsUntradeable">
/// True for items that cannot be traded on Warframe Market (e.g. Forma Blueprint).
/// The pipeline skips the price lookup for these and displays "Untradeable" instead.
/// </param>
public sealed record RewardItem(string CanonicalName, bool IsUntradeable = false)
{
    /// <summary>
    /// Lowercased canonical name, computed once for use by the fuzzy matcher.
    /// Avoids repeated <c>ToLowerInvariant()</c> calls during matching hot paths.
    /// </summary>
    public string MatchPattern { get; } = CanonicalName.ToLowerInvariant();
}