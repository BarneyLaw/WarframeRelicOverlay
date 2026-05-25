using WarframeRelicOverlay.Domain.Models;

namespace WarframeRelicOverlay.Domain.Matching;

/// <summary>
/// Matches OCR text to known relic reward items.
/// </summary>
public interface IRewardMatcher
{
    /// <summary>
    /// Returns every reward item detected in the supplied OCR text.
    /// </summary>
    IEnumerable<RewardItem> Match(string ocrText);

    /// <summary>
    /// Returns the best matching reward item, or null if nothing meets the threshold.
    /// </summary>
    RewardItem? MatchSingle(string ocrText);
}
