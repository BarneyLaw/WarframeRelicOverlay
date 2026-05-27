namespace WarframeRelicOverlay.OverlayApp.Detection;

/// <summary>
/// Abstracts reward screen detection so the coordinator reacts to events
/// without knowing *how* detection works.  Each implementation covers one
/// detection strategy; the DI container selects the right one based on
/// <see cref="Core.AppSettings.DetectionMode"/>.
///
/// <list type="bullet">
///   <item><b>EE.log detector</b> — watches for <c>"Got rewards"</c> in
///   Warframe's debug log.  Fires <see cref="RewardDetected"/> once per
///   reward screen.  Never fires <see cref="RewardLost"/>.</item>
///
///   <item><b>OCR fallback detector</b> — polls at a configurable interval
///   and runs a lightweight OCR check for the word "REWARDS".  Fires
///   <see cref="RewardDetected"/> on each positive poll and
///   <see cref="RewardLost"/> on each negative poll so the coordinator
///   can manage the confirmation streak.</item>
///
///   <item><b>Manual detector</b> — fires <see cref="RewardDetected"/>
///   when the user presses a hotkey.  Never fires
///   <see cref="RewardLost"/>.</item>
/// </list>
///
/// Thread safety: events may fire on any thread (file-system callback,
/// timer thread, UI thread).  The coordinator handles marshalling.
/// </summary>
public interface IRewardDetector : IDisposable
{
    /// <summary>
    /// Fired when a positive reward-screen indication is received.
    ///
    /// For EE.log / Manual mode this means detection is confirmed in a
    /// single event.  For OCR mode this is one positive poll — the
    /// coordinator decides how many consecutive hits are required.
    /// </summary>
    event Action? RewardDetected;

    /// <summary>
    /// Fired when an OCR poll comes back negative while previously
    /// positive.  Only meaningful in OCR mode; EE.log and Manual
    /// detectors never raise this event.
    ///
    /// The coordinator uses this to reset the streak counter and fire
    /// <see cref="StateMachine.OverlayTrigger.DetectionStreakBroken"/>.
    /// </summary>
    event Action? RewardLost;

    /// <summary>
    /// Fired when the reward screen is no longer visible (the player
    /// selected a reward or the timer expired).  The coordinator uses
    /// this to transition from Displaying back to Tracking.
    ///
    /// Implementations that cannot detect screen exit (e.g. Manual mode)
    /// should never fire this; the coordinator will fall back to a
    /// configurable timeout.
    /// </summary>
    event Action? RewardScreenExited;

    /// <summary>
    /// Begin monitoring.  Must be idempotent — calling Start on an
    /// already-started detector is a no-op.
    /// </summary>
    void Start();

    /// <summary>
    /// Stop monitoring.  Must be idempotent.  After Stop returns, no
    /// further events will fire until the next <see cref="Start"/>.
    /// </summary>
    void Stop();
}