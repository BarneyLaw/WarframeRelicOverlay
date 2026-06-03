namespace WarframeRelicOverlay.Infrastructure.History;

/// <summary>
/// One recorded reward-selection run, serialized as a single line in the
/// reward-history file. New fields can be added here over time without
/// breaking existing records — readers ignore unknown properties and the
/// append-only JSONL format never rewrites earlier lines.
/// </summary>
public sealed record RewardRunRecord
{
    /// <summary>When the run was priced (local time with UTC offset).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The rewards shown on the selection screen, left to right.</summary>
    public required IReadOnlyList<RewardRunItem> Items { get; init; }
}

/// <summary>
/// A single reward within a <see cref="RewardRunRecord"/>. Extend with
/// further metadata (relic tier, untradeable flag, raw OCR, …) as needed.
/// </summary>
public sealed record RewardRunItem
{
    /// <summary>
    /// Matched canonical item name, or <c>null</c> when the card could not
    /// be matched to a known reward.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Lowest sell price in platinum, or <c>null</c> when untradeable,
    /// unmatched, or the market lookup failed.
    /// </summary>
    public int? Price { get; init; }
}
