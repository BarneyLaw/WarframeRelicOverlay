using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Domain.Normalization;
using WarframeRelicOverlay.Infrastructure.RewardData;
using FuzzySharp;
using System.Text.RegularExpressions;

namespace WarframeRelicOverlay.Domain.Matching
{
    /// <summary>
    /// Fuzzy matcher that normalizes OCR text and compares it against the known reward pool.
    /// Uses a multi-strategy scoring approach combining token-based matching with full-string
    /// fuzzy comparison to achieve high accuracy even with noisy OCR output.
    /// </summary>
    public class FuzzyRewardMatcher : IRewardMatcher
    {
        /// <summary>
        /// Minimum combined score required for a reward to be considered a match.
        /// </summary>
        private const int MatchThreshold = 55;

        /// <summary>
        /// If the best match score exceeds this value, accept it unconditionally
        /// without checking the gap to the second-best match.
        /// </summary>
        private const int HighConfidenceThreshold = 80;

        /// <summary>
        /// Minimum gap between the best and second-best match scores for a
        /// low-confidence match (below <see cref="HighConfidenceThreshold"/>).
        /// Prevents false matches when multiple items score similarly.
        /// </summary>
        private const int MinScoreGap = 5;

        private readonly IReadOnlyList<RewardItem> _rewardPool;
        private readonly string[][] _rewardTokens;

        /// <summary>
        /// Creates a matcher using the provided reward repository.
        /// Pre-tokenizes the reward pool for fast token-based scoring.
        /// </summary>
        public FuzzyRewardMatcher(IRewardRepository rewardRepository)
        {
            _rewardPool = rewardRepository.GetAll();
            _rewardTokens = new string[_rewardPool.Count][];
            for (int i = 0; i < _rewardPool.Count; i++)
            {
                _rewardTokens[i] = _rewardPool[i].MatchPattern
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            }
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
        /// <para>
        /// Uses a multi-strategy approach:
        /// 1. Token-based scoring — matches individual words against reward tokens,
        ///    weighting distinctive tokens (names) higher than common ones (Blueprint, Prime).
        /// 2. Concatenated fuzzy scoring — joins all tokens and uses Levenshtein-based
        ///    ratios for whole-string similarity.
        /// 3. Token-set ratio — order-independent comparison using FuzzySharp's TokenSetRatio.
        /// The final score is the weighted max of all strategies.
        /// </para>
        /// <para>
        /// Forma blueprints (with or without a quantity prefix) are
        /// always returned with <see cref="RewardItem.IsUntradeable"/>
        /// set to <c>true</c> so the pipeline skips the
        /// Warframe Market lookup and the overlay shows "Untradeable".
        /// Forma is account-bound and never has a market price.
        /// </para>
        /// </summary>
        public RewardItem? MatchSingle(string ocrText)
        {
            string[] candidateTokens = TokenizeForMatching(ocrText);
            if (candidateTokens.Length == 0)
            {
                return null;
            }

            string candidateConcat = string.Concat(candidateTokens);
            string candidateSpaced = string.Join(" ", candidateTokens);

            if (string.IsNullOrWhiteSpace(candidateConcat))
            {
                return null;
            }

            RewardItem? bestMatch = null;
            int bestScore = 0;
            int secondBestScore = 0;

            for (int i = 0; i < _rewardPool.Count; i++)
            {
                int score = ScoreCandidate(candidateTokens, candidateConcat, candidateSpaced, i);

                if (score > bestScore)
                {
                    secondBestScore = bestScore;
                    bestScore = score;
                    bestMatch = _rewardPool[i];
                }
                else if (score > secondBestScore)
                {
                    secondBestScore = score;
                }
            }

            if (bestMatch is null || bestScore < MatchThreshold)
            {
                return null;
            }

            // For scores below high confidence, require a meaningful gap to
            // the second-best match to avoid picking the wrong similar item.
            if (bestScore < HighConfidenceThreshold && (bestScore - secondBestScore) < MinScoreGap)
            {
                return null;
            }

            // Forma blueprints are always untradeable regardless of the
            // pool flag — guard here so a stale items.json without the
            // flag still produces the correct UI.
            bool isUntradeable = bestMatch.IsUntradeable
                || IsFormaBlueprint(bestMatch.CanonicalName);

            return TryExtractQuantityPrefix(ocrText, out string? prefix)
                ? new RewardItem($"{prefix}{bestMatch.CanonicalName}", IsUntradeable: isUntradeable)
                : (isUntradeable && !bestMatch.IsUntradeable
                    ? new RewardItem(bestMatch.CanonicalName, IsUntradeable: true)
                    : bestMatch);
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="canonicalName"/>
        /// refers to a Forma blueprint reward (e.g. "Forma Blueprint",
        /// "2 X Forma Blueprint").  Match is case-insensitive and
        /// ignores any leading whitespace or quantity prefix.
        /// </summary>
        private static bool IsFormaBlueprint(string canonicalName)
        {
            if (string.IsNullOrWhiteSpace(canonicalName)) return false;

            // Strip any "<digits> X " prefix and normalise whitespace.
            string stripped = Regex.Replace(
                canonicalName.Trim(),
                @"^\d+\s*[xX]\s*",
                string.Empty);

            return string.Equals(
                stripped,
                "Forma Blueprint",
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes and tokenizes OCR text for matching. Applies OCR-specific
        /// corrections, removes quantity noise, and filters single-character tokens.
        /// </summary>
        private static string[] TokenizeForMatching(string ocrText)
        {
            string normalized = OcrTextNormalizer.Normalize(ocrText);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return [];
            }

            return normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => !IsQuantityNoise(token) && token.Length > 1)
                .ToArray();
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

        /// <summary>
        /// Multi-strategy scoring for a candidate against a reward item.
        /// Combines:
        /// 1. Token matching score (best token-to-token fuzzy match, weighted by distinctiveness)
        /// 2. Concatenated string ratio (FuzzySharp Ratio on spaceless strings)
        /// 3. Token-set ratio (order-independent, handles OCR word splits/merges)
        /// 4. Weighted ratio (FuzzySharp WeightedRatio for balanced comparison)
        /// </summary>
        private int ScoreCandidate(string[] candidateTokens, string candidateConcat, string candidateSpaced, int rewardIndex)
        {
            string[] rewardTokens = _rewardTokens[rewardIndex];
            string rewardConcat = _rewardPool[rewardIndex].MatchPattern.Replace(" ", string.Empty);
            string rewardSpaced = _rewardPool[rewardIndex].MatchPattern;

            // Strategy 1: Concatenated fuzzy ratio (original approach, still valuable)
            int concatRatio = Fuzz.Ratio(candidateConcat, rewardConcat);
            int concatPartial = Fuzz.PartialRatio(candidateConcat, rewardConcat);
            int concatScore = Math.Max(concatRatio, concatPartial);

            // Strategy 2: Token-set ratio — handles word boundary shifts from OCR
            // e.g., "AshPrime Chassis" vs "Ash Prime Chassis"
            int tokenSetScore = Fuzz.TokenSetRatio(candidateSpaced, rewardSpaced);

            // Strategy 3: Token-sort ratio — order-independent matching
            int tokenSortScore = Fuzz.TokenSortRatio(candidateSpaced, rewardSpaced);

            // Strategy 4: Token-by-token matching with distinctiveness weighting.
            // Distinctive tokens (the warframe/weapon name) contribute more than
            // common tokens like "prime", "blueprint", "systems", "chassis", "neuroptics".
            int tokenScore = ComputeTokenMatchScore(candidateTokens, rewardTokens);

            // Take the best of all strategies. Each strategy handles different OCR failure modes:
            // - concatScore: good when OCR merges/splits words but characters are mostly correct
            // - tokenSetScore: good when OCR gets words right but in wrong order or with extras
            // - tokenSortScore: good when OCR shuffles word order
            // - tokenScore: good when OCR garbles individual characters but preserves word boundaries
            int best = Math.Max(
                Math.Max(concatScore, tokenSetScore),
                Math.Max(tokenSortScore, tokenScore));

            return best;
        }

        /// <summary>
        /// Computes a token-level matching score. For each reward token, finds the best
        /// matching candidate token using character-level similarity. Weights distinctive
        /// tokens (names like "ash", "braton", "rubico") more heavily than ubiquitous
        /// tokens ("prime", "blueprint", "chassis", "systems", "neuroptics").
        /// </summary>
        private static int ComputeTokenMatchScore(string[] candidateTokens, string[] rewardTokens)
        {
            if (candidateTokens.Length == 0 || rewardTokens.Length == 0)
                return 0;

            double totalWeight = 0;
            double weightedScore = 0;

            foreach (string rewardToken in rewardTokens)
            {
                double weight = GetTokenWeight(rewardToken);
                int bestTokenScore = 0;

                foreach (string candidateToken in candidateTokens)
                {
                    int score = FuzzyTokenCompare(candidateToken, rewardToken);
                    if (score > bestTokenScore)
                        bestTokenScore = score;
                }

                weightedScore += bestTokenScore * weight;
                totalWeight += weight;
            }

            // Penalize when candidate has significantly more tokens than reward
            // (likely OCR noise), but don't penalize for fewer tokens (OCR might
            // have merged words).
            int extraTokens = Math.Max(0, candidateTokens.Length - rewardTokens.Length - 1);
            double noisePenalty = Math.Max(0.0, 1.0 - extraTokens * 0.05);

            return totalWeight > 0
                ? (int)(weightedScore / totalWeight * noisePenalty)
                : 0;
        }

        /// <summary>
        /// Returns the weight for a reward token based on its distinctiveness.
        /// Common tokens shared by many items get lower weight; distinctive
        /// name tokens get higher weight. This ensures "Ash" vs "Atlas" matters
        /// more than "Blueprint" vs "Blueprint" in the final score.
        /// </summary>
        private static double GetTokenWeight(string token)
        {
            // Common tokens that appear across many/all items — lowest weight.
            return token switch
            {
                "blueprint" => 0.4,
                "prime" => 0.3,
                "chassis" => 0.7,
                "neuroptics" => 0.7,
                "systems" => 0.7,
                "barrel" => 0.7,
                "stock" => 0.7,
                "receiver" => 0.7,
                "blade" => 0.7,
                "handle" => 0.7,
                "grip" => 0.7,
                "string" => 0.7,
                "link" => 0.7,
                "disc" => 0.7,
                "guard" => 0.7,
                "head" => 0.7,
                "hilt" => 0.7,
                "ornament" => 0.7,
                "pouch" => 0.7,
                "boot" => 0.7,
                "gauntlet" => 0.7,
                "upper" => 0.7,
                "lower" => 0.7,
                "limb" => 0.7,
                "stars" => 0.7,
                "band" => 0.7,
                "buckle" => 0.7,
                "chain" => 0.7,
                "carapace" => 0.7,
                "cerebrum" => 0.7,
                // Everything else is likely a distinctive name token — highest weight.
                _ => 1.0,
            };
        }

        /// <summary>
        /// Character-level fuzzy comparison between two tokens.
        /// Uses Levenshtein distance normalized to 0–100 scale, which handles
        /// common OCR errors (single char substitution/insertion/deletion) well
        /// for short strings where FuzzySharp's ratio might be too harsh.
        /// </summary>
        private static int FuzzyTokenCompare(string candidate, string reward)
        {
            if (candidate == reward)
                return 100;

            // For very short tokens (2-3 chars), an exact or near-exact match
            // should be required since a single edit is too large a fraction.
            int maxLen = Math.Max(candidate.Length, reward.Length);
            if (maxLen == 0) return 0;

            int distance = LevenshteinDistance(candidate, reward);
            int score = (int)(100.0 * (1.0 - (double)distance / maxLen));

            // Bonus for shared prefix (OCR rarely garbles the start of a word)
            int prefixLen = CommonPrefixLength(candidate, reward);
            if (prefixLen >= 3)
            {
                score = Math.Min(100, score + Math.Min(10, prefixLen * 2));
            }

            return Math.Max(0, score);
        }

        /// <summary>
        /// Length of the common prefix between two strings.
        /// </summary>
        private static int CommonPrefixLength(string a, string b)
        {
            int len = Math.Min(a.Length, b.Length);
            int i = 0;
            while (i < len && a[i] == b[i]) i++;
            return i;
        }

        /// <summary>
        /// Standard Levenshtein edit distance.
        /// </summary>
        private static int LevenshteinDistance(string source, string target)
        {
            if (source.Length == 0) return target.Length;
            if (target.Length == 0) return source.Length;

            // Use single-row optimization for memory efficiency.
            int[] prev = new int[target.Length + 1];
            int[] curr = new int[target.Length + 1];

            for (int j = 0; j <= target.Length; j++)
                prev[j] = j;

            for (int i = 1; i <= source.Length; i++)
            {
                curr[0] = i;
                for (int j = 1; j <= target.Length; j++)
                {
                    int cost = source[i - 1] == target[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                }
                (prev, curr) = (curr, prev);
            }

            return prev[target.Length];
        }
    }
}

