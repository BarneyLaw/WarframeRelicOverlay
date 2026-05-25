namespace WarframeRelicOverlay.OverlayApp.Detection;

/// <summary>
/// Detects when the Warframe reward selection screen appears or
/// disappears.  Implementations must be safe to call from any thread
/// and must raise events asynchronously (never from inside a lock or
/// on the UI thread).
///
/// Two implementations exist:
///
///   <see cref="LogFileDetector"/> — tails Warframe's EE.log for the
///   "Got rewards" line.  Zero-latency, 100 % reliable, no OCR cost.
///   This is the primary strategy.
///
///   <see cref="OcrFallbackDetector"/> — captures a thin strip of the
///   Warframe window and runs Tesseract looking for the word "REWARDS".
///   Used only when EE.log is unavailable (non-standard install path,
///   permissions issue, etc.).
///
/// The overlay state machine treats both identically: a single
/// <see cref="RewardScreenDetected"/> event maps to either
/// <see cref="StateMachine.OverlayTrigger.RewardConfirmed"/> (EE.log,
/// instant) or <see cref="StateMachine.OverlayTrigger.RewardHintDetected"/>
/// (OCR, needs streak confirmation).
/// </summary>
public interface IRewardScreenDetector : IDisposable
{
    /// <summary>
    /// Raised when the detector has evidence that a reward screen is
    /// currently visible.  For <see cref="LogFileDetector"/> this is a
    /// single definitive event.  For <see cref="OcrFallbackDetector"/>
    /// it fires on each positive poll (the caller manages streaks).
    /// </summary>
    event Action? RewardScreenDetected;

    /// <summary>
    /// Raised when the detector determines the reward screen is no
    /// longer visible.  Not every implementation can detect this —
    /// <see cref="LogFileDetector"/> does (via a subsequent log line),
    /// while <see cref="OcrFallbackDetector"/> reports a negative poll
    /// that the caller can interpret.
    /// </summary>
    event Action? RewardScreenExited;

    /// <summary>
    /// Whether this detector provides a definitive one-shot confirmation
    /// (true for EE.log) or requires the caller to accumulate a streak
    /// of positive polls before confirming (false for OCR fallback).
    /// </summary>
    bool IsDefinitive { get; }

    /// <summary>
    /// Begin monitoring.  Idempotent — calling <see cref="Start"/> on
    /// an already-running detector is a no-op.
    /// </summary>
    void Start();

    /// <summary>
    /// Stop monitoring and release resources.  The detector can be
    /// restarted with <see cref="Start"/>.
    /// </summary>
    void Stop();
}