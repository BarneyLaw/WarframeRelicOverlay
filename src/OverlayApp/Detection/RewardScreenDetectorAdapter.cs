namespace WarframeRelicOverlay.OverlayApp.Detection;

using System;

/// <summary>
/// Adapts an <see cref="IRewardScreenDetector"/> (which only knows about
/// the reward selection screen) to the broader
/// <see cref="IRewardDetector"/> contract that
/// <see cref="Core.OverlayCoordinator"/> consumes.
///
/// <para>
/// <see cref="IRewardDetector"/> exposes three events
/// (<see cref="IRewardDetector.RewardDetected"/>,
/// <see cref="IRewardDetector.RewardLost"/>,
/// <see cref="IRewardDetector.RewardScreenExited"/>)
/// while the underlying detectors only raise two
/// (<see cref="IRewardScreenDetector.RewardScreenDetected"/>,
/// <see cref="IRewardScreenDetector.RewardScreenExited"/>).
/// The mapping is one-to-one for the events that exist; the OCR
/// fallback's negative-poll signal already arrives as
/// <see cref="IRewardScreenDetector.RewardScreenExited"/>, so we
/// surface that as both <see cref="IRewardDetector.RewardLost"/> (when
/// in <c>Detecting</c>) and <see cref="IRewardDetector.RewardScreenExited"/>
/// (when in <c>Displaying</c>); the coordinator will only react to the
/// trigger that is valid in its current state.
/// </para>
/// </summary>
public sealed class RewardScreenDetectorAdapter : IRewardDetector
{
    private readonly IRewardScreenDetector _inner;
    private bool _disposed;

    /// <inheritdoc />
    public event Action? RewardDetected;

    /// <inheritdoc />
    public event Action? RewardLost;

    /// <inheritdoc />
    public event Action? RewardScreenExited;

    /// <summary>
    /// Wraps the supplied screen detector.  The adapter takes
    /// ownership of the inner detector and disposes it when the
    /// adapter itself is disposed.
    /// </summary>
    public RewardScreenDetectorAdapter(IRewardScreenDetector inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        _inner.RewardScreenDetected += OnInnerDetected;
        _inner.RewardScreenExited += OnInnerExited;
    }

    /// <inheritdoc />
    public void Start() => _inner.Start();

    /// <inheritdoc />
    public void Stop() => _inner.Stop();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inner.RewardScreenDetected -= OnInnerDetected;
        _inner.RewardScreenExited -= OnInnerExited;
        _inner.Dispose();
    }

    /// <summary>
    /// Forwards the detector's positive event to subscribers as a
    /// generic <see cref="RewardDetected"/> notification.
    /// </summary>
    private void OnInnerDetected()
    {
        RewardDetected?.Invoke();
    }

    /// <summary>
    /// Forwards the detector's negative event to both
    /// <see cref="RewardLost"/> and <see cref="RewardScreenExited"/>.
    /// The coordinator routes them through the state machine, which
    /// only fires the trigger valid in the current state.
    /// </summary>
    private void OnInnerExited()
    {
        RewardLost?.Invoke();
        RewardScreenExited?.Invoke();
    }
}
