namespace WarframeRelicOverlay.Presentation;

using WarframeRelicOverlay.OverlayApp.StateMachine;

/// <summary>
/// Pure decision helper that determines whether the overlay window should
/// currently be visible. The rule is intentionally expressed as a single
/// boolean expression so it can be unit/property tested in isolation
/// without spinning up WPF.
///
/// Truth table (R19.6):
///
///   snapshotValid | manualShown | state                | visible
///   ──────────────┼─────────────┼──────────────────────┼────────
///        false    |      *      |          *           |  false
///        true     |    true     |          *           |  true
///        true     |   false     | Pricing | Displaying |  true
///        true     |   false     | Idle | Tracking |    |  false
///                 |             | Detecting            |
///
/// In words: the overlay is visible only when the captured window snapshot
/// is valid AND either the user has manually toggled it on (hotkey) OR the
/// state machine is actively pricing or displaying results.
/// </summary>
public static class VisibilityDecision
{
    // ── Decision ────────────────────────────────────────────────────

    /// <summary>
    /// Compute whether the overlay window should be visible given the
    /// current overlay state, foreground status of the tracked process,
    /// the user's manual show/hide toggle, and whether the most recent
    /// window snapshot is valid.
    ///
    /// Pure function — no side effects, no dependencies on WPF or
    /// platform state. See <see cref="VisibilityDecision"/> for the full
    /// truth table (R19.6).
    /// </summary>
    /// <param name="state">Current overlay lifecycle state.</param>
    /// <param name="foreground">
    /// True when Warframe's main window is the foreground window (R8.1).
    /// </param>
    /// <param name="manualShown">
    /// True when the user has toggled the overlay on via the global
    /// hotkey (R9.2, R9.3).
    /// </param>
    /// <param name="snapshotValid">
    /// True when the most recent <c>WindowSnapshot</c> reports valid
    /// bounds and positive DPI scales (R2.4, R5.5, R17.4).
    /// </param>
    /// <returns>
    /// <c>true</c> if the overlay window should be shown, otherwise
    /// <c>false</c>.
    /// </returns>
    public static bool IsOverlayVisible(
        OverlayState state, bool foreground, bool manualShown, bool snapshotValid)
    {
        return snapshotValid
            && (manualShown || state is OverlayState.Pricing or OverlayState.Displaying);
    }
}
