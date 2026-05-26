using System.Text.RegularExpressions;

namespace WarframeRelicOverlay.Domain.Normalization;

/// <summary>
/// Normalizes OCR output so it can be matched more reliably.
/// </summary>
public static class OcrTextNormalizer
{
    /// <summary>
    /// Lowercases, removes punctuation, and collapses whitespace.
    /// </summary>
    public static string Normalize(string text)
    {
        string normalized = text
            .ToLowerInvariant()
            .Replace("blue print", "blueprint")
            .Replace("\n", " ")
            .Trim();

        // Keep letters, numbers, and spaces; remove everything else.
        normalized = Regex.Replace(normalized, "[^a-zA-Z0-9 ]", "");

        // Collapse multiple spaces into one.
        normalized = Regex.Replace(normalized, @"\s+", " ");

        return normalized;
    }
}

