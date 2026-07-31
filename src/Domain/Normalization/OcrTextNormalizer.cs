using System.Text.RegularExpressions;

namespace WarframeRelicOverlay.Domain.Normalization;

/// <summary>
/// Normalizes OCR output so it can be matched more reliably.
/// Applies a series of corrections for common Tesseract misreads on
/// Warframe's UI font, then normalizes to lowercase alphanumeric.
/// </summary>
public static class OcrTextNormalizer
{
    /// <summary>
    /// Lowercases, applies OCR-specific corrections, removes punctuation,
    /// and collapses whitespace.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = text
            .ToLowerInvariant()
            .Replace("\r\n", " ")
            .Replace("\n", " ")
            .Replace("\r", " ")
            .Trim();

        // ── Common OCR word-level corrections ──────────────────────
        // Tesseract frequently splits or garbles these Warframe-specific terms.
        normalized = normalized
            .Replace("blue print", "blueprint")
            .Replace("biue print", "blueprint")
            .Replace("biueprint", "blueprint")
            .Replace("bIueprint", "blueprint")
            .Replace("bluepnnt", "blueprint")
            .Replace("bluep rint", "blueprint")
            .Replace("b lueprint", "blueprint")
            .Replace("blu eprint", "blueprint")
            .Replace("bluepr int", "blueprint")
            .Replace("neuro ptics", "neuroptics")
            .Replace("neurop tics", "neuroptics")
            .Replace("neur optics", "neuroptics")
            .Replace("neuroptic s", "neuroptics")
            .Replace("neuraptics", "neuroptics")
            .Replace("neuroplics", "neuroptics")
            .Replace("sys tems", "systems")
            .Replace("syst ems", "systems")
            .Replace("syste ms", "systems")
            .Replace("chas sis", "chassis")
            .Replace("chass is", "chassis")
            .Replace("cha ssis", "chassis")
            .Replace("chassi s", "chassis");

        // ── Character-level OCR corrections ────────────────────────
        // Common single-character substitutions Tesseract makes on
        // Warframe's Corpus-style font:
        // - '0' (zero) ↔ 'o' in word context
        // - '1' (one) ↔ 'l' or 'i'
        // - '5' ↔ 's' at word boundaries
        // - '|' ↔ 'l'
        // We only apply these when surrounded by letters (not in quantity prefixes).
        normalized = Regex.Replace(normalized, @"(?<=[a-z])0(?=[a-z])", "o");
        normalized = Regex.Replace(normalized, @"(?<=[a-z])1(?=[a-z])", "l");
        normalized = Regex.Replace(normalized, @"(?<=[a-z])\|(?=[a-z])", "l");

        // Keep letters, numbers, and spaces; remove everything else.
        normalized = Regex.Replace(normalized, "[^a-z0-9 ]", "");

        // Collapse multiple spaces into one.
        normalized = Regex.Replace(normalized, @"\s+", " ");

        return normalized.Trim();
    }
}

