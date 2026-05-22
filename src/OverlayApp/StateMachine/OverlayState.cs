namespace WarframeRelicOverlay.OverlayApp.StateMachine;

/// <summary>
/// The five states of the overlay lifecycle. Each state has a single
/// clear responsibility and a bounded set of legal outgoing transitions.
///
///   Idle ──▶ Tracking ──▶ Detecting ──▶ Pricing ──▶ Displaying
///     ▲         ▲    ◀────┘         ◀────┘    ▲    ◀────┘
///     └─────────┴────────────────────────────────────────┘
///                   (WarframeStopped from any state)
/// </summary>
public enum OverlayState
{
    /// <summary>
    /// Warframe is not running(or not yet detected), or focus is lost.
    /// </summary>
    Idle,
    /// <summary>
    /// Warframe is running and focused, but no relic detection is currently active.
    /// This is the state where the overlay waits for the right conditions to start detecting relics.
    /// </summary>       
    Tracking,

    /// <summary>
    /// The overlay get a hint that a relic reward screen is active.
    /// The streak counter begins
    /// OCR to be used as fallback if EELog detection fails   
    /// </summary>
    Detecting,

    /// <summary>
    /// A relic reward has been detected, and the overlay is now fetching prices for the rewards.
    /// </summary>
    Pricing,

    /// <summary>
    /// Prices have been fetched and the overlay is now rendering them on screen.
    /// The overlay will remain in this state until the reward screen is closed, at which point it will transition back to tracking.
    /// </summary>
    Displaying,
}

    /// <summary>
    /// Named triggers that drive state transitions. Each trigger corresponds
    /// to a discrete event in the system — never a timer tick.
    /// </summary>
    public enum OverlayTrigger
    {
        /// <summary>Warframe.x64 process was found / started.</summary>
        WarframeStarted,
 
        /// <summary>Warframe.x64 process exited.</summary>
        WarframeStopped,
 
        /// <summary>
        /// A single positive reward-screen detection occurred (one OCR hit
        /// or one EE.log line). In EE.log mode this fires once and the
        /// machine should treat it as an immediate confirmation.
        /// </summary>
        RewardHintDetected,
 
        /// <summary>
        /// The detection streak was broken — an OCR poll returned negative
        /// while in the Detecting state.
        /// </summary>
        DetectionStreakBroken,
 
        /// <summary>
        /// The required number of consecutive detections was reached (OCR mode),
        /// or the EE.log detector fired (which counts as instant confirmation).
        /// </summary>
        RewardConfirmed,
 
        /// <summary>
        /// The pricing pipeline finished successfully and prices are ready
        /// for display.
        /// </summary>
        PricingCompleted,
 
        /// <summary>
        /// The pricing pipeline failed (no cards detected, OCR garbage, network
        /// error). Fall back to Tracking rather than showing bad data.
        /// </summary>
        PricingFailed,
 
        /// <summary>
        /// The reward screen is no longer visible (player made a selection or
        /// the timer expired). Prices should be cleared.
        /// </summary>
        RewardScreenExited,
    }


