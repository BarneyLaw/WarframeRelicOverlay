namespace WarframeRelicOverlay.Infrastructure.History;

/// <summary>
/// Persists a record of each reward-selection run to a data file so the
/// player can review what they received and at what price over time.
/// Implementations must never throw — recording is best-effort and must
/// not disrupt the pricing pipeline.
/// </summary>
public interface IRewardHistoryRecorder
{
    /// <summary>Appends a single run to the history file.</summary>
    void Record(RewardRunRecord record);
}
