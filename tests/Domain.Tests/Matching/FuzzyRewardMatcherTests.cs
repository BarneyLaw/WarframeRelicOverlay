namespace Domain.Tests;

using FluentAssertions;
using WarframeRelicOverlay.Domain.Matching;
using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Infrastructure.RewardData;
using Xunit;

public class FuzzyRewardMatcherTests
{
    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class FakeRewardRepository : IRewardRepository
    {
        public FakeRewardRepository(IEnumerable<RewardItem> items, string? version = "2025-05-19")
        {
            Items = items.ToList().AsReadOnly();
            _version = version;
        }

        public IReadOnlyList<RewardItem> Items { get; }
        private readonly string? _version;

        public IReadOnlyList<RewardItem> GetAll() => Items;
        public string? Version => _version;
    }

    private static IRewardRepository CreateRepository() => new FakeRewardRepository(
        new[]
        {
            new RewardItem("Ash Prime Chassis Blueprint"),
            new RewardItem("Wisp Prime Blueprint"),
            new RewardItem("Guandao Prime Handle"),
            new RewardItem("Baza Prime Stock"),
            new RewardItem("Aklex Prime Blueprint"),
            new RewardItem("Soma Prime Barrel"),
            new RewardItem("Forma Blueprint", IsUntradeable: true),
            new RewardItem("2 X Forma Blueprint", IsUntradeable: true),
            new RewardItem("Mesa Prime Blueprint"),
            new RewardItem("Ash Prime Neuroptics Blueprint"),
            new RewardItem("Ash Prime Systems Blueprint"),
            new RewardItem("Nikana Prime Blade"),
            new RewardItem("Nikana Prime Blueprint"),
            new RewardItem("Braton Prime Barrel"),
            new RewardItem("Braton Prime Receiver"),
            new RewardItem("Braton Prime Stock"),
            new RewardItem("Ivara Prime Chassis Blueprint"),
            new RewardItem("Ivara Prime Neuroptics Blueprint"),
            new RewardItem("Ivara Prime Systems Blueprint"),
            new RewardItem("Saryn Prime Chassis Blueprint"),
            new RewardItem("Saryn Prime Systems Blueprint"),
            new RewardItem("Volt Prime Neuroptics Blueprint"),
            new RewardItem("Valkyr Prime Chassis Blueprint"),
            new RewardItem("Trinity Prime Systems Blueprint"),
            new RewardItem("Tiberon Prime Barrel"),
            new RewardItem("Tiberon Prime Receiver"),
            new RewardItem("Tiberon Prime Stock"),
            new RewardItem("Kavasa Prime Band"),
            new RewardItem("Kavasa Prime Buckle"),
            new RewardItem("Ayatan Cyan Star"),
        });

    private static FuzzyRewardMatcher CreateMatcher() => new(CreateRepository());

    // ══════════════════════════════════════════════════════════════════════════
    // Original tests
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test1_AshPrimeChassisBlueprintShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Ash Prime Chassis Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ash Prime Chassis Blueprint");
    }

    [Fact]
    public void Test4_WispPrimeBlueprintShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Wisp Prime Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Wisp Prime Blueprint");
    }

    [Fact]
    public void Test5_GuandaoPrimeHandleShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Guandao Prime Handle");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Guandao Prime Handle");
    }

    [Fact]
    public void Test7_BazaPrimeStockShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Baza Prime Stock");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Baza Prime Stock");
    }

    [Fact]
    public void Test8_AklexPrimeBlueprintShouldMatchAklexPrimeBlueprint()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("aklex prime blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Aklex Prime Blueprint");
    }

    [Fact]
    public void Test9_SoaPrimeBarrelShouldMatchSomaPrimeBarrel()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("soa prime barrel");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Soma Prime Barrel");
    }

    [Fact]
    public void Test9_FormaBlueprintShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("Forma Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Forma Blueprint");
        result.IsUntradeable.Should().BeTrue();
    }

    [Fact]
    public void Test10_2XFormaBlueprintShouldMatchTheSame()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("2 X Forma Blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("2 X Forma Blueprint");
        result.IsUntradeable.Should().BeTrue();
    }

    [Fact]
    public void Test11_MesaPriMeblueprintShouldMatchMesaPrimeBlueprint()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("me sa pri meblueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Mesa Prime Blueprint");
    }

    [Fact]
    public void Test11_NoiseWithWispPrimeBlueprintShouldMatch()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle(" a sd w s d wisp prime blueprint");

        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Wisp Prime Blueprint");
    }

    [Fact]
    public void Test12_XylophoneShouldReturnError()
    {
        var matcher = CreateMatcher();

        var result = matcher.MatchSingle("xylophone");

        result.Should().BeNull();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Tesseract OCR-realistic failure modes
    //
    // These simulate what Tesseract actually produces when reading
    // Warframe's reward screen through the ImagePreprocessor:
    //
    // - The font is a narrow sans-serif with tight kerning; Tesseract
    //   frequently merges adjacent characters or splits at the wrong
    //   boundary (e.g. "rn" → "m", "cl" → "d", "li" → "h").
    // - The Otsu binarization sometimes leaves haloing artifacts that
    //   inject stray characters at the start/end of the recognized line.
    // - Anti-aliased edges on a dark-to-gold gradient cause character
    //   substitutions like 'l' ↔ '1', 'O' ↔ '0', 'I' ↔ 'l'.
    // - The whitelist (A-Z a-z 0-9 space) prevents most symbols, but
    //   Tesseract still inserts spurious spaces or drops them entirely.
    // - At lower resolutions (720p), thin strokes disappear and letters
    //   like 'i' become 'l', 'r' becomes 'n', 't' becomes 'l'.
    // - PageSegMode.SingleBlock means multi-word names sometimes get a
    //   newline inserted between words when the crop is narrow.
    // ══════════════════════════════════════════════════════════════════════════

    // ── Character substitution (most common Tesseract error) ─────────────

    [Fact]
    public void OCR_CharSubstitution_rn_to_m_SarynPrime()
    {
        // Tesseract reads "rn" as "m" — very common with narrow fonts
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Sayn Prime Chassis Blueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Saryn Prime Chassis Blueprint");
    }

    [Fact]
    public void OCR_CharSubstitution_l_to_1_ValkyrPrime()
    {
        // 'l' misread as '1' inside a word
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Va1kyr Prime Chassis B1ueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Valkyr Prime Chassis Blueprint");
    }

    [Fact]
    public void OCR_CharSubstitution_O_to_0_SomaPrime()
    {
        // 'O'/'o' misread as '0' (zero)
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("S0ma Prime Barrel");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Soma Prime Barrel");
    }

    [Fact]
    public void OCR_CharSubstitution_i_to_l_TrinityPrime()
    {
        // 'i' misread as 'l' — common at lower resolutions
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Trlnlty Prime Systems Blueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Trinity Prime Systems Blueprint");
    }

    // ── Spurious spaces inserted (word boundary confusion) ──────────────

    [Fact]
    public void OCR_SpuriousSpaces_NikanaPrimeBlade()
    {
        // Tesseract inserts spaces in the middle of words
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Nik ana Pr ime Bla de");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Nikana Prime Blade");
    }

    [Fact]
    public void OCR_SpuriousSpaces_BratonPrimeReceiver()
    {
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Brat on Pr ime Rece iver");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Braton Prime Receiver");
    }

    // ── Dropped spaces (characters merged together) ─────────────────────

    [Fact]
    public void OCR_MergedWords_IvaraPrimeNeuroptics()
    {
        // All spaces dropped — Tesseract does this with tight kerning
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("IvaraPrimeNeuropticsBlueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ivara Prime Neuroptics Blueprint");
    }

    [Fact]
    public void OCR_PartialMerge_VoltPrimeNeuroptics()
    {
        // Some words merged, others not
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("VoltPrime Neuroptics Blueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Volt Prime Neuroptics Blueprint");
    }

    // ── Newline inserted (narrow crop, SingleBlock mode) ────────────────

    [Fact]
    public void OCR_Newline_GuandaoPrimeHandle()
    {
        // Tesseract wraps the text at an arbitrary point
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Guandao Prime\nHandle");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Guandao Prime Handle");
    }

    [Fact]
    public void OCR_Newline_AshPrimeNeuropticsBlueprint()
    {
        // Newline mid-word — happens when crop clips the text
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Ash Prime Neurop\ntics Blueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ash Prime Neuroptics Blueprint");
    }

    // ── Leading/trailing garbage from binarization artifacts ────────────

    [Fact]
    public void OCR_LeadingGarbage_TiberonPrimeBarrel()
    {
        // Haloing from Otsu threshold produces random chars at the start
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("ll  Tiberon Prime Barrel");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Tiberon Prime Barrel");
    }

    [Fact]
    public void OCR_TrailingGarbage_KavasaPrimeBand()
    {
        // Stray characters appended at the end
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Kavasa Prime Band  ii");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Kavasa Prime Band");
    }

    [Fact]
    public void OCR_LeadingAndTrailingGarbage_BazaPrimeStock()
    {
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("1l Baza Prime Stock l1");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Baza Prime Stock");
    }

    // ── Missing characters (thin strokes lost in binarization) ──────────

    [Fact]
    public void OCR_MissingChars_TiberonPrimeReceiver()
    {
        // Letters dropped — thin vertical strokes vanish at 720p
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Tibern Prime Receier");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Tiberon Prime Receiver");
    }

    [Fact]
    public void OCR_MissingChars_AyatanCyanStar()
    {
        // Short item name with a dropped character
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Ayatan Cyn Star");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ayatan Cyan Star");
    }

    // ── Combined errors (real-world worst case) ─────────────────────────

    [Fact]
    public void OCR_Combined_CharSubAndSpaces_NikanaPrimeBlueprint()
    {
        // Substitution + spurious space + missing char
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Nlkana Pr1me Blue prlnt");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Nikana Prime Blueprint");
    }

    [Fact]
    public void OCR_Combined_MergeAndSubstitution_SarynPrimeSystems()
    {
        // Merged words + character substitution
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("SarynPrirne SystemsBlueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Saryn Prime Systems Blueprint");
    }

    [Fact]
    public void OCR_Combined_GarbageAndNewline_FormaBlueprintUntradeable()
    {
        // Leading garbage + newline, should still detect Forma as untradeable
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("l Forma\nBlueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Forma Blueprint");
        result.IsUntradeable.Should().BeTrue();
    }

    // ── Disambiguation (similar items must not cross-match) ─────────────

    [Fact]
    public void OCR_Disambiguation_AshChassis_NotSystems()
    {
        // Slightly garbled "Chassis" must not match "Systems"
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Ash Prime Chassls Blueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ash Prime Chassis Blueprint");
    }

    [Fact]
    public void OCR_Disambiguation_BratonBarrel_NotStock()
    {
        // "Barrel" with a typo must not jump to "Stock"
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Braton Prime Barr el");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Braton Prime Barrel");
    }

    [Fact]
    public void OCR_Disambiguation_IvaraChassisNotNeuropticsNotSystems()
    {
        // Must distinguish between Chassis/Neuroptics/Systems
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("lvara Prime Chassis Blueprint");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Ivara Prime Chassis Blueprint");
    }

    // ── "blue print" split (known OCR artifact, handled by normalizer) ──

    [Fact]
    public void OCR_BluePrintSplit_WispPrime()
    {
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Wisp Prime Blue print");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Wisp Prime Blueprint");
    }

    [Fact]
    public void OCR_BluePrintSplit_WithCharSub_AklexPrime()
    {
        // "blue print" split combined with 'l' → '1'
        var matcher = CreateMatcher();
        var result = matcher.MatchSingle("Ak1ex Prime B1ue print");
        result.Should().NotBeNull();
        result!.CanonicalName.Should().Be("Aklex Prime Blueprint");
    }

    // ── Empty / whitespace-only input ───────────────────────────────────

    [Fact]
    public void OCR_EmptyString_ReturnsNull()
    {
        var matcher = CreateMatcher();
        matcher.MatchSingle("").Should().BeNull();
    }

    [Fact]
    public void OCR_WhitespaceOnly_ReturnsNull()
    {
        var matcher = CreateMatcher();
        matcher.MatchSingle("   \n  \t  ").Should().BeNull();
    }

    [Fact]
    public void OCR_SingleChar_ReturnsNull()
    {
        // All tokens are length 1 — should be filtered as noise
        var matcher = CreateMatcher();
        matcher.MatchSingle("a b c d e").Should().BeNull();
    }
}
