namespace WarframeRelicOverlay.Infrastructure.RewardData;

using WarframeRelicOverlay.Domain.Models;

/// <summary>
/// Provides the full list of known relic reward items.
///
/// Implementations may load from a local JSON file (<see cref="JsonRewardRepository"/>)
/// or fetch from the Warframe Market API (<c>ApiRewardRepository</c>, future).
/// The matcher and pipeline depend on this interface, not on a specific data source.
/// </summary>
public interface IRewardRepository
{
    /// <summary>
    /// Returns all known reward items. Called once at startup to populate the matcher's pool.
    /// Implementations should cache the result internally so repeated calls are cheap.
    /// </summary>
    IReadOnlyList<RewardItem> GetAll();

    /// <summary>
    /// Returns the data version string (e.g. "2025-05-19") if the source provides one,
    /// or null if versioning is not supported.
    /// Useful for diagnostics and for checking whether the pool needs updating.
    /// </summary>
    string? Version { get; }

    // Open to future methods if the JSON format evolves, e.g. GetByName(string name) or GetById(int id).
}