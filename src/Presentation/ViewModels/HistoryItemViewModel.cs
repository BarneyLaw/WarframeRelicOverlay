namespace WarframeRelicOverlay.Presentation.ViewModels;

/// <summary>
/// View model for a single reward item within a history run, rendered as a chip.
/// </summary>
public sealed class HistoryItemViewModel
{
    /// <summary>Matched item name (or "?" if unmatched).</summary>
    public required string Name { get; init; }

    /// <summary>Platinum price, or null if untradeable/unmatched/failed.</summary>
    public int? Price { get; init; }

    /// <summary>Formatted display text for the chip (e.g. "Nagantaka Prime Receiver · 45◆").</summary>
    public string DisplayText => Price.HasValue
        ? $"{Name} · {Price.Value}◆"
        : $"{Name} · N/A";
}
