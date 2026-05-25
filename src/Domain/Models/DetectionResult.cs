namespace WarframeRelicOverlay.Domain.Models;

/// <summary>
/// The result of an OCR-based reward detection pass.
/// </summary>
public sealed record DetectionResult(
    List<RewardItem> DetectedRewards,
    string OcrText,
    DateTime DetectedAt)
{
    /// <summary>
    /// Indicates whether any rewards were detected.
    /// </summary>
    public bool HasRewards => DetectedRewards.Count > 0;
}

