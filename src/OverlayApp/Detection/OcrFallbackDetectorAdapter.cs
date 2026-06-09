namespace WarframeRelicOverlay.OverlayApp.Detection;

using System;

/// <summary>
/// Adapts an <see cref="OcrFallbackDetector"/> (which implements
/// <see cref="IRewardScreenDetector"/>) to the broader
/// <see cref="IRewardDetector"/> contract that
/// <see cref="Core.OverlayCoordinator"/> consumes.
///
/// <para>
/// Event mapping:
/// <list type="bullet">
///   <item>
///     <see cref="OcrFallbackDetector.RewardScreenDetected"/> is
///     re-raised as <see cref="IRewardDetector.RewardDetected"/>.
///   </item>
///   <item>
///     <see cref="OcrFallbackDetector.RewardScreenExited"/> is
///     re-raised as <see cref="IRewardDetector.RewardScreenExited"/>.
///   </item>
///   <item>
///     <see cref="IRewardDetector.RewardLost"/> is left without a
///     backing event — <see cref="OcrFallbackDetector"/> does not
///     expose a separate "lost without exit" signal.  The existing
///     coordinator OCR-streak logic already uses
///     <see cref="IRewardDetector.RewardDetected"/> cumulatively to
///     manage streak resets.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// Mirrors the <c>LogDetectorAdapter</c> pattern used in
/// <c>tests/Integration.Tests/EndToEndPipelineTests.cs</c>: the
/// adapter takes ownership of the inner detector and forwards
/// <see cref="Start"/>, <see cref="Stop"/>, and <see cref="Dispose"/>
/// directly.
/// </para>
/// </summary>
public sealed class OcrFallbackDetectorAdapter : IRewardDetector
{
    // ── Construction ──────────────────────────────────────────────

    private readonly OcrFallbackDetector _inner;
    private bool _disposed;

    /// <summary>
    /// Wraps the supplied <see cref="OcrFallbackDetector"/>.  The
    /// adapter takes ownership of the inner detector and disposes
    /// it when the adapter itself is disposed.
    /// </summary>
    /// <param name="inner">The OCR fallback detector to adapt.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="inner"/> is <c>null</c>.
    /// </exception>
    public OcrFallbackDetectorAdapter(OcrFallbackDetector inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

        _inner.RewardScreenDetected += OnInnerDetected;
        _inner.RewardScreenExited += OnInnerExited;
    }

    // ── Events ────────────────────────────────────────────────────

    /// <inheritdoc />
    public event Action? RewardDetected;

    /// <inheritdoc />
    /// <remarks>
    /// Never fires for the OCR fallback adapter — the underlying
    /// <see cref="OcrFallbackDetector"/> does not surface a separate
    /// "lost without exit" signal.  The coordinator's OCR-streak
    /// logic relies on cumulative <see cref="RewardDetected"/>
    /// events instead.
    /// </remarks>
#pragma warning disable CS0067 // Event is never used — intentional, see remarks.
    public event Action? RewardLost;
#pragma warning restore CS0067

    /// <inheritdoc />
    public event Action? RewardScreenExited;

    // ── Lifecycle ─────────────────────────────────────────────────

    /// <inheritdoc />
    public void Start() => _inner.Start();

    /// <inheritdoc />
    public void Stop() => _inner.Stop();

    // ── Dispose ───────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inner.RewardScreenDetected -= OnInnerDetected;
        _inner.RewardScreenExited -= OnInnerExited;
        _inner.Dispose();
    }

    // ── Event routing ─────────────────────────────────────────────

    /// <summary>
    /// Forwards the inner detector's positive event to subscribers
    /// as a generic <see cref="RewardDetected"/> notification.
    /// </summary>
    private void OnInnerDetected()
    {
        RewardDetected?.Invoke();
    }

    /// <summary>
    /// Forwards the inner detector's screen-exit event to subscribers
    /// as <see cref="RewardScreenExited"/>.  <see cref="RewardLost"/>
    /// is intentionally not raised — see remarks on that event.
    /// </summary>
    private void OnInnerExited()
    {
        RewardScreenExited?.Invoke();
    }
}
