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

    public event Action? RewardDetected;
    public event Action? RewardLost;
    public event Action? RewardScreenExited;

    public RewardDetectorAdapter(IRewardScreenDetector inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        _inner.RewardScreenDetected += OnInnerDetected;
        _inner.RewardScreenExited += OnInnerExited;
    }

    public void Start() => _inner.Start();
    public void Stop() => _inner.Stop();

    private void OnInnerDetected()
    {
        Debug.WriteLine("[DetectorAdapter] RewardScreenDetected → RewardDetected");
        RewardDetected?.Invoke();
    }

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inner.RewardScreenDetected -= OnInnerDetected;
        _inner.RewardScreenExited -= OnInnerExited;
        _inner.Dispose();
    }
}