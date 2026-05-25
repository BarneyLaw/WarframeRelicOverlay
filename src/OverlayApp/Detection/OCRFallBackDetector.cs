namespace WarframeRelicOverlay.OverlayApp.Detection;

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Infrastructure.OCR;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.Infrastructure.ScreenCapture;

/// <summary>
/// Fallback reward-screen detector for when Warframe's EE.log is
/// unavailable (non-standard install path, permissions issue, network
/// drive, etc.).
///
/// Captures a thin horizontal strip near the upper-centre of the
/// Warframe window and runs Tesseract looking for the word "REWARDS".
/// Each positive poll fires <see cref="RewardScreenDetected"/>; each
/// negative poll fires <see cref="RewardScreenExited"/>.  The caller
/// (state machine orchestrator) accumulates a configurable streak of
/// positives before treating the detection as confirmed.
///
/// <b>Cost:</b> one small-region screen capture (~5 ms) plus one
/// Tesseract call on a narrow strip (~30–80 ms) per poll.  At the
/// default 250 ms interval this is well within budget.
///
/// Thread safety: the poll timer fires on a thread-pool thread.  All
/// mutable state is accessed under <see cref="_scanLock"/>.
/// </summary>
public sealed class OcrFallbackDetector : IRewardScreenDetector
{
    // ── Constants ─────────────────────────────────────────────────

    /// <summary>
    /// The text we look for in the OCR output.  Warframe renders
    /// "VOID RELIC REWARDS" (or similar) as a header above the
    /// reward cards.  We match on "REWARD" to tolerate minor OCR
    /// artifacts ("REWARDS" → "REWARD5", "REWARDŞ", etc.).
    /// </summary>
    private const string HeaderKeyword = "REWARD";

    /// <summary>
    /// Vertical band to capture, expressed as fractions of window
    /// height.  The "REWARDS" header sits at roughly 28–34 % of the
    /// window height across different aspect ratios and UI scales.
    /// We capture a generous band and let OCR find it.
    /// </summary>
    private const double StripTopFraction    = 0.26;
    private const double StripBottomFraction = 0.36;

    /// <summary>
    /// Horizontal margins to exclude.  The header is centred, so we
    /// skip the outer 25 % on each side to reduce the OCR region
    /// and avoid stray text from HUD elements.
    /// </summary>
    private const double StripLeftFraction  = 0.25;
    private const double StripRightFraction = 0.75;

    // ── Dependencies ─────────────────────────────────────────────

    private readonly IScreenCapturer _capturer;
    private readonly IOcrEngine _ocr;
    private readonly IProcessTracker _processTracker;
    private readonly IWindowTracker _windowTracker;
    private readonly int _intervalMs;

    // ── State ─────────────────────────────────────────────────────

    private readonly object _scanLock = new();
    private Timer? _pollTimer;
    private bool _lastPollWasPositive;
    private bool _running;
    private bool _disposed;

    // IRewardScreenDetector

    /// <inheritdoc />
    public event Action? RewardScreenDetected;

    /// <inheritdoc />
    public event Action? RewardScreenExited;

    /// <inheritdoc />
    /// <remarks>
    /// <c>false</c> — each positive poll is a hint, not a
    /// confirmation.  The caller must accumulate a streak of
    /// <see cref="AppSettings.DetectionStreak"/> consecutive
    /// positives before firing
    /// <see cref="StateMachine.OverlayTrigger.RewardConfirmed"/>.
    /// </remarks>
    public bool IsDefinitive => false;

    // Construction

    /// <summary>
    /// Creates an OCR-based fallback detector.
    /// </summary>
    /// <param name="capturer">Screen capture implementation.</param>
    /// <param name="ocr">OCR engine (should be pooled for thread safety).</param>
    /// <param name="processTracker">
    /// Provides the Warframe window handle for capture targeting.
    /// </param>
    /// <param name="windowTracker">
    /// Provides DPI-aware window bounds for the capture region.
    /// </param>
    /// <param name="settings">
    /// Supplies <see cref="AppSettings.DetectionIntervalMs"/>.
    /// </param>
    public OcrFallbackDetector(
        IScreenCapturer capturer,
        IOcrEngine ocr,
        IProcessTracker processTracker,
        IWindowTracker windowTracker,
        AppSettings settings)
    {
        _capturer       = capturer ?? throw new ArgumentNullException(nameof(capturer));
        _ocr            = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _processTracker = processTracker ?? throw new ArgumentNullException(nameof(processTracker));
        _windowTracker  = windowTracker ?? throw new ArgumentNullException(nameof(windowTracker));
        _intervalMs     = settings?.DetectionIntervalMs ?? 250;
    }

    // Lifecycle

    /// <inheritdoc />
    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OcrFallbackDetector));

        if (_running) return;
        _running = true;
        _lastPollWasPositive = false;

        var interval = TimeSpan.FromMilliseconds(_intervalMs);
        _pollTimer = new Timer(OnPollTick, null, interval, interval);

        Debug.WriteLine(
            $"[OcrFallbackDetector] Polling every {_intervalMs} ms.");
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (!_running) return;
        _running = false;

        _pollTimer?.Dispose();
        _pollTimer = null;

        lock (_scanLock)
        {
            _lastPollWasPositive = false;
        }

        Debug.WriteLine("[OcrFallbackDetector] Stopped.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }

    // Poll logic

    private void OnPollTick(object? state)
    {
        if (!_running) return;

        bool isPositive = PollOnce();

        lock (_scanLock)
        {
            if (isPositive)
            {
                _lastPollWasPositive = true;
                RewardScreenDetected?.Invoke();
            }
            else if (_lastPollWasPositive)
            {
                // Transition from positive → negative: the reward
                // screen has gone away.
                _lastPollWasPositive = false;
                RewardScreenExited?.Invoke();
            }
        }
    }

    /// <summary>
    /// Captures the header region of the Warframe window and checks
    /// for the "REWARDS" text via OCR.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the header keyword was found in the OCR output.
    /// </returns>
    private bool PollOnce()
    {
        try
        {
            nint handle = _processTracker.MainWindowHandle;
            if (handle == nint.Zero) return false;

            WindowSnapshot? snapshot = _windowTracker.TryGetBounds(handle);
            if (snapshot is null || !snapshot.Value.IsValid) return false;

            var window = snapshot.Value;
            Rectangle strip = ComputeHeaderStrip(window);

            if (strip.Width <= 0 || strip.Height <= 0) return false;

            using Bitmap? capture = _capturer.CaptureRegion(strip);
            if (capture is null) return false;

            string text = _ocr.Recognize(capture);

            return text.Contains(HeaderKeyword, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[OcrFallbackDetector] Poll error: {ex.Message}");
            return false;
        }
    }

    // ── Geometry ──────────────────────────────────────────────────

    /// <summary>
    /// Computes the physical-pixel rectangle for the horizontal strip
    /// where the "REWARDS" header text should appear.
    /// </summary>
    private static Rectangle ComputeHeaderStrip(WindowSnapshot window)
    {
        int x      = window.ClientX + (int)(StripLeftFraction * window.ClientWidth);
        int y      = window.ClientY + (int)(StripTopFraction * window.ClientHeight);
        int width  = (int)((StripRightFraction - StripLeftFraction) * window.ClientWidth);
        int height = (int)((StripBottomFraction - StripTopFraction) * window.ClientHeight);

        return new Rectangle(x, y, width, height);
    }
}