namespace WarframeRelicOverlay.OverlayApp.Detection;

using System;
using System.Diagnostics;

/// <summary>
/// Bridges the <see cref="IRewardScreenDetector"/> implementations
/// (LogFileDetector, OcrFallbackDetector) to the
/// <see cref="IRewardDetector"/> contract expected by the
/// <see cref="Core.OverlayCoordinator"/>.
///
/// The two interfaces differ in event naming and semantics:
///
///   <see cref="IRewardScreenDetector.RewardScreenDetected"/>
///     → <see cref="IRewardDetector.RewardDetected"/>
///
///   <see cref="IRewardScreenDetector.RewardScreenExited"/>
///     → <see cref="IRewardDetector.RewardScreenExited"/>
///
/// The <see cref="IRewardDetector.RewardLost"/> event is synthesised
/// from the OCR fallback detector's exit event when the detector is
/// non-definitive (i.e. streak-based): a negative poll means the
/// streak is broken, so RewardLost fires instead of RewardScreenExited
/// while the coordinator is still in the Detecting state.
///
/// For definitive detectors (EE.log), RewardLost is never fired — the
/// exit event always maps to RewardScreenExited.
/// </summary>
public sealed class RewardDetectorAdapter : IRewardDetector
{
    private readonly IRewardScreenDetector _inner;
    private bool _disposed;

    /// <summary>Raised when a reward screen has been detected.</summary>
    public event Action? RewardDetected;

    /// <summary>Raised when a detection streak is broken (non-definitive detectors only).</summary>
    public event Action? RewardLost;

    /// <summary>Raised when the reward screen has been exited.</summary>
    public event Action? RewardScreenExited;

    /// <summary>
    /// Initialises the adapter and subscribes to the inner detector's events.
    /// </summary>
    public RewardDetectorAdapter(IRewardScreenDetector inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        _inner.RewardScreenDetected += OnInnerDetected;
        _inner.RewardScreenExited += OnInnerExited;
    }

    /// <summary>Starts the inner detector.</summary>
    public void Start() => _inner.Start();

    /// <summary>Stops the inner detector.</summary>
    public void Stop() => _inner.Stop();

    /// <summary>
    /// Forwards the inner detector's screen-detected event as
    /// <see cref="RewardDetected"/>.
    /// </summary>
    private void OnInnerDetected()
    {
        Debug.WriteLine("[DetectorAdapter] RewardScreenDetected → RewardDetected");
        RewardDetected?.Invoke();
    }

    /// <summary>
    /// Forwards the inner detector's screen-exited event.  For definitive
    /// detectors maps directly to <see cref="RewardScreenExited"/>; for
    /// non-definitive detectors fires both <see cref="RewardLost"/> and
    /// <see cref="RewardScreenExited"/>.
    /// </summary>
    private void OnInnerExited()
    {
        if (_inner.IsDefinitive)
        {
            // EE.log: a real screen exit.
            Debug.WriteLine("[DetectorAdapter] RewardScreenExited → RewardScreenExited (definitive)");
            RewardScreenExited?.Invoke();
        }
        else
        {
            // OCR fallback: a negative poll.  We fire both events and
            // let the coordinator's state determine which one matters:
            //
            //   In Detecting: RewardLost resets the streak and fires
            //   DetectionStreakBroken -> Tracking.  The subsequent
            //   RewardScreenExited is ignored (no valid transition
            //   from Tracking).
            //
            //   In Displaying: RewardLost is a no-op (streak is 0).
            //   RewardScreenExited fires the valid transition back
            //   to Tracking, clearing prices.
            Debug.WriteLine("[DetectorAdapter] RewardScreenExited → RewardLost + RewardScreenExited (non-definitive)");
            RewardLost?.Invoke();
            RewardScreenExited?.Invoke();
        }
    }

    /// <summary>Unsubscribes from the inner detector and disposes it.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inner.RewardScreenDetected -= OnInnerDetected;
        _inner.RewardScreenExited -= OnInnerExited;
        _inner.Dispose();
    }
}
