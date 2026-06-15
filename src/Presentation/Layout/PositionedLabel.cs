namespace WarframeRelicOverlay.Presentation.Layout;

/// <summary>
/// Pre-computed placement for a single price card, in WPF logical
/// units (DIPs) relative to the overlay window's top-left corner.
/// </summary>
/// <param name="LeftDip">Left coordinate in DIPs.</param>
/// <param name="TopDip">Top coordinate in DIPs.</param>
/// <param name="MaxWidthDip">
/// Maximum width hint for the card; derived from the corresponding
/// reward card width so the label cannot grow wider than its anchor.
/// </param>
/// <param name="Text">Primary text (price / "N/A" / "?").</param>
/// <param name="ItemName">
/// The matched reward item name (e.g. "Nagantaka Prime Receiver"),
/// or <c>null</c> when no match was found.
/// </param>
/// <param name="BuyPrice">
/// Highest buy offer in platinum, or <c>null</c> if unavailable.
/// Displayed as "Buy: Xp" on the detail row.
/// </param>
/// <param name="SellerCount">
/// Number of in-game sellers.  Displayed as "X sellers" on the
/// detail row.  Zero means no data.
/// </param>
/// <param name="IsHighlighted">
/// <c>true</c> when this card represents the most valuable reward in
/// the current pipeline result.  Renders with a brighter gold border.
/// </param>
public readonly record struct PositionedLabel(
    double LeftDip,
    double TopDip,
    double MaxWidthDip,
    string Text,
    string? ItemName = null,
    int? BuyPrice = null,
    int SellerCount = 0,
    bool IsHighlighted = false);
