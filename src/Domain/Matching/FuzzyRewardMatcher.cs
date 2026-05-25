using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Domain.Normalization;
using WarframeRelicOverlay.Infrastructure.RewardData;
using FuzzySharp;
using System.Text.RegularExpressions;

namespace WarframeRelicOverlay.Domain.Matching
{
    /// <summary>
    /// Fuzzy matcher that normalizes OCR text and compares it against the known reward pool.
    /// </summary>
    public class FuzzyRewardMatcher : IRewardMatcher
    {
        /// <summary>
        /// Minimum score required for a reward to be considered a match.
        /// </summary>
        private const int MatchThreshold = 70;

        private readonly IReadOnlyList<RewardItem> _rewardPool;

        /// <summary>
        /// Creates a matcher using the provided reward repository.
        /// </summary>
        public FuzzyRewardMatcher(IRewardRepository rewardRepository)
        {
            _rewardPool = rewardRepository.GetAll();
        }

        /// <summary>
        /// Matches all reward items found in the OCR text.
        /// Returns at most one reward because the OCR output is expected to represent a single card reward.
        /// </summary>
        public IEnumerable<RewardItem> Match(string ocrText)
        {
            var match = MatchSingle(ocrText);
            if (match is not null)
            {
                yield return match;
            }
        }

        /// <summary>
        /// Matches the best single reward item from the OCR text.
        /// </summary>
        public RewardItem? MatchSingle(string ocrText)
        {
            string candidate = NormalizeForMatching(ocrText);
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            RewardItem? bestMatch = null;
            int bestScore = 0;

            foreach (var item in _rewardPool)
            {
                int score = ScoreCandidate(candidate, item);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = item;
                }
            }

            if (bestMatch is null || bestScore < MatchThreshold)
            {
                return null;
            }

            return TryExtractQuantityPrefix(ocrText, out string? prefix)
                ? new RewardItem($"{prefix}{bestMatch.CanonicalName}", bestMatch.IsUntradeable)
                : bestMatch;
        }

        private static string NormalizeForMatching(string ocrText)
        {
            string normalized = OcrTextNormalizer.Normalize(ocrText);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var tokens = normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => !IsQuantityNoise(token) && token.Length > 1)
                .ToArray();

            return string.Concat(tokens);
        }

        private static bool TryExtractQuantityPrefix(string ocrText, out string? prefix)
        {
            prefix = null;

            if (string.IsNullOrWhiteSpace(ocrText))
            {
                return false;
            }

            string normalized = Regex.Replace(ocrText.Trim(), @"\s+", " ");
            var match = Regex.Match(normalized, @"^(?<qty>\d+)\s*(?:x|X)\s*", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            prefix = $"{match.Groups["qty"].Value} X ";
            return true;
        }

        private static bool IsQuantityNoise(string token)
        {
            if (token == "x")
            {
                return true;
            }

            return Regex.IsMatch(token, @"^\d+$")
                || Regex.IsMatch(token, @"^\d+x$")
                || Regex.IsMatch(token, @"^x\d+$");
        }

        private static int ScoreCandidate(string candidate, RewardItem reward)
        {
            string rewardText = reward.MatchPattern.Replace(" ", string.Empty);

            int ratio = Fuzz.Ratio(candidate, rewardText);
            int partialRatio = Fuzz.PartialRatio(candidate, rewardText);

            return Math.Max(ratio, partialRatio);
        }
    }
}

