namespace WarframeRelicOverlay.Core;

using System.Diagnostics;
using WarframeRelicOverlay.Infrastructure.Logging;
using WarframeRelicOverlay.OverlayApp.Detection;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.OverlayApp.Pipeline;
using WarframeRelicOverlay.OverlayApp.StateMachine;

/// <summary>
/// Top-level runtime coordinator that subscribes to detection and process
/// events, drives the state machine, invokes the reward-pricing pipeline,
/// and pushes results to the overlay UI.
///
/// The coordinator is entirely event-driven — it has no polling loop of
/// its own.  Events arrive from:
///
///   <see cref="IProcessTracker"/>    — Warframe started / stopped
///   <see cref="IRewardDetector"/>    — reward screen detected / lost / exited
///   <see cref="OverlayStateMachine"/> — state transitions (self-subscribes)
///
/// All OCR, network, and pipeline work runs on the thread pool.  The
/// coordinator itself is lightweight; its event handlers either fire a
/// state machine trigger (instant) or kick off an async task.
///
/// Lifetime: create once at startup, call <see cref="Start"/>, then
/// <see cref="Dispose"/> on shutdown.
/// </summary>

public sealed class OverlayCoordinator : IDisposable
{
    private readonly OverlayStateMachine _stateMachine;
    private readonly IProcessTracker _processTracker;
    private readonly IWindowTracker _windowTracker;
    private readonly IRewardDetector _detector;
    private readonly IRewardPipeline _pipeline;
    private readonly IOverlayOutput _output;
    private readonly AppSettings _settings;
    private readonly ILogger? _logger;

    // Mutable states, guarded by a lock
    private readonly object _lock = new();
    private int _streakCount;
    private CancellationTokenSource? _pipelineCts;
    private Timer? _displayTimeoutTimer;
    private Timer? _heartbeatTimer;
    private DateTime _lastEventUtc = DateTime.UtcNow;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// How long prices remain visible when the detector cannot report
    /// screen exit (Manual mode, or detector implementation limitation).
    /// After this timeout the coordinator fires
    /// <see cref="OverlayTrigger.RewardScreenExited"/> automatically.
    /// </summary>
    private static readonly TimeSpan DisplayTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often the coordinator writes a heartbeat line to the log
    /// summarising its current state and the time since the last event.
    /// Lets a user verify the overlay is alive without checking the
    /// process list.
    /// </summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    public OverlayCoordinator(
        OverlayStateMachine stateMachine,
        IProcessTracker processTracker,
        IWindowTracker windowTracker,
        IRewardDetector detector,
        IRewardPipeline pipeline,
        IOverlayOutput output,
        AppSettings settings,
        ILogger? logger = null)
    {
        _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        _processTracker = processTracker ?? throw new ArgumentNullException(nameof(processTracker));
        _windowTracker = windowTracker ?? throw new ArgumentNullException(nameof(windowTracker));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger;
    }

    /// <summary>
    /// Expose the state machine for the presentation layer to observe
    /// state transitions (e.g. showing/hiding the overlay window).
    /// </summary>
    public OverlayStateMachine StateMachine => _stateMachine;

    // Lifecycle: start tracking and subscribing to events.  Idempotent.
    public void Start()
    {
        if (_started) return;
        _started = true;

        _logger?.LogInfo(
            $"OverlayCoordinator starting: detectionMode={_settings.DetectionMode}, " +
            $"detectionStreak={_settings.DetectionStreak}, " +
            $"stabilizationDelayMs={_settings.StabilizationDelayMs}, " +
            $"displayTimeoutSec={DisplayTimeout.TotalSeconds:F0}, " +
            $"heartbeatSec={HeartbeatInterval.TotalSeconds:F0}");

        // Subscribe to state transitions — this is how the coordinator
        // reacts to state changes caused by its own trigger calls.
        _stateMachine.StateChanged += OnStateChanged;

        // Process lifecycle of the game
        _processTracker.Started += OnWarframeStarted;
        _processTracker.Stopped += OnWarframeStopped;

        // Reward detection
        _detector.RewardDetected += OnRewardDetected;
        _detector.RewardLost += OnRewardLost;
        _detector.RewardScreenExited += OnRewardScreenExited;

        // Heartbeat — periodic "I'm still alive" log so the user can
        // verify the coordinator is running without external tools.
        _heartbeatTimer = new Timer(
            OnHeartbeat, null, HeartbeatInterval, HeartbeatInterval);

        // Start the detector to begin monitoring immediately
        _detector.Start();

        _logger?.LogInfo("OverlayCoordinator started.");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unsubscribe everything to prevent callbacks on a disposed object.
        _stateMachine.StateChanged -= OnStateChanged;
        _processTracker.Started -= OnWarframeStarted;
        _processTracker.Stopped -= OnWarframeStopped;
        _detector.RewardDetected -= OnRewardDetected;
        _detector.RewardLost -= OnRewardLost;
        _detector.RewardScreenExited -= OnRewardScreenExited;

        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        CancelPipeline();
        StopDisplayTimeout();
        _detector.Stop();
        _processTracker.Dispose();

        _logger?.LogInfo("OverlayCoordinator disposed.");
    }

        private void OnWarframeStarted(int pid)
    {
        Debug.WriteLine($"[Coordinator] Warframe started (PID {pid}).");
        _logger?.LogInfo($"Warframe process started (PID {pid}).");
        MarkEvent();
        _stateMachine.Fire(OverlayTrigger.WarframeStarted);
    }

    private void OnWarframeStopped(int pid)
    {
        Debug.WriteLine($"[Coordinator] Warframe stopped (PID {pid}).");
        _logger?.LogInfo($"Warframe process stopped (PID {pid}).");
        MarkEvent();
        CancelPipeline();
        _detector.Stop();
        _stateMachine.Fire(OverlayTrigger.WarframeStopped);
    }

        private void OnRewardDetected()
    {
        if (_disposed) return;

        MarkEvent();

        bool isInstantConfirm =
            _settings.DetectionMode is "EELog" or "Manual";

        if (isInstantConfirm)
        {
            Debug.WriteLine("[Coordinator] Reward confirmed (instant).");
            _logger?.LogInfo(
                $"OverlayCoordinator: RewardDetected received (mode={_settings.DetectionMode}) " +
                "— confirming immediately.");
            _stateMachine.Fire(OverlayTrigger.RewardConfirmed);
            return;
        }

        // OCR mode — manage streak.
        lock (_lock)
        {
            _streakCount++;
            Debug.WriteLine(
                $"[Coordinator] OCR streak {_streakCount}/{_settings.DetectionStreak}.");
            _logger?.LogInfo(
                $"OverlayCoordinator: RewardDetected received (mode=OCR) — " +
                $"streak {_streakCount}/{_settings.DetectionStreak}.");

            if (_streakCount >= _settings.DetectionStreak)
            {
                _streakCount = 0;
                _stateMachine.Fire(OverlayTrigger.RewardConfirmed);
            }
            else
            {
                _stateMachine.Fire(OverlayTrigger.RewardHintDetected);
            }
        }
    }

    private void OnRewardLost()
    {
        if (_disposed) return;

        MarkEvent();

        lock (_lock)
        {
            if (_streakCount > 0)
            {
                Debug.WriteLine("[Coordinator] OCR streak broken.");
                _logger?.LogInfo(
                    $"OverlayCoordinator: RewardLost — streak broken at {_streakCount}.");
                _streakCount = 0;
                _stateMachine.Fire(OverlayTrigger.DetectionStreakBroken);
            }
        }
    }

    private void OnRewardScreenExited()
    {
        if (_disposed) return;

        MarkEvent();

        Debug.WriteLine("[Coordinator] Reward screen exited (detector).");
        _logger?.LogInfo("OverlayCoordinator: RewardScreenExited received from detector.");
        // This should remove the prices display on our UI.
        _stateMachine.Fire(OverlayTrigger.RewardScreenExited);
    }

    // State machine reactions
    /// <summary>
    /// Called on every state transition.  The handler must be fast and
    /// non-blocking because it executes inside the state machine's lock.
    /// Async work is kicked off via <see cref="Task.Run"/>.
    /// </summary>
    private void OnStateChanged(
        OverlayState previous, OverlayState current, OverlayTrigger trigger)
    {
        Debug.WriteLine(
            $"[Coordinator] {previous} → {current} (trigger: {trigger})");
        _logger?.LogInfo(
            $"State transition: {previous} -> {current} (trigger: {trigger})");
        MarkEvent();
 
        switch (current)
        {
            case OverlayState.Tracking:
                HandleEnterTracking(previous);
                break;
 
            case OverlayState.Pricing:
                HandleEnterPricing();
                break;
 
            case OverlayState.Displaying:
                HandleEnterDisplaying();
                break;
 
            case OverlayState.Idle:
                HandleEnterIdle();
                break;
 
            // Detecting — nothing to do; the detector keeps firing
            // events and OnRewardDetected manages the streak.
        }
    }

        private void HandleEnterTracking(OverlayState previous)
    {
        // Coming from Displaying or Detecting — clean up.
        if (previous == OverlayState.Displaying)
        {
            _output.ClearPrices();
            StopDisplayTimeout();
        }
 
        _output.HideLoading();
 
        // Make sure detection is running.
        _detector.Start();
    }
 
    private void HandleEnterPricing()
    {
        // Stop detection while the pipeline runs — we don't need more
        // detection events and the OCR engine pool is about to be busy.
        _detector.Stop();
        _output.ShowLoading();
 
        // Kick off the pipeline on the thread pool.
        var cts = new CancellationTokenSource();
        lock (_lock) { _pipelineCts = cts; }
 
        _ = Task.Run(() => RunPipelineAsync(cts.Token));
    }
 
    private void HandleEnterDisplaying()
    {
        _output.HideLoading();
 
        // Start a safety-net timeout.  If the detector supports exit
        // detection it will fire RewardScreenExited before this, and
        // we'll cancel the timer.  Otherwise this ensures we always
        // return to Tracking.
        StartDisplayTimeout();
 
        // Restart the detector so it can fire RewardScreenExited
        // (or RewardLost in OCR mode, which the coordinator can
        // interpret as screen exit while in Displaying state).
        _detector.Start();
    }
 
    private void HandleEnterIdle()
    {
        CancelPipeline();
        StopDisplayTimeout();
        _output.ClearPrices();
        _output.HideLoading();
    }

    // Pipeline execution

    /// <summary>
    /// Runs on the thread pool.  Gets window bounds, waits the
    /// stabilization delay, executes the pipeline, and fires the
    /// appropriate state machine trigger based on the result.
    /// </summary>

    private async Task RunPipelineAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Get current window bounds.
            var windowHandle = _processTracker.MainWindowHandle;
            var window = _windowTracker.TryGetBounds(windowHandle);
            if (window is null || !window.Value.IsValid)
            {
                Debug.WriteLine("[Coordinator] Window bounds unavailable — pricing failed.");
                _stateMachine.Fire(OverlayTrigger.PricingFailed);
                return;
            }
 
            // Stabilization delay — wait for the reward screen animation.
            if (_settings.StabilizationDelayMs > 0)
            {
                await Task.Delay(_settings.StabilizationDelayMs, cancellationToken);
            }
 
            // Run the pipeline.
            var result = await _pipeline.ExecuteAsync(window.Value, cancellationToken);

            // Re-check cancellation after the pipeline returns.
            // CancelPipeline() may have fired while the pipeline was
            // finishing — without this guard we'd push prices to the
            // overlay after WarframeStopped already moved us to Idle.
            cancellationToken.ThrowIfCancellationRequested();

            if (result.HasCards)
            {
                Debug.WriteLine(
                    $"[Coordinator] Pipeline produced {result.Cards.Count} card(s) " +
                    $"in {result.Elapsed.TotalMilliseconds:F0}ms.");
                _logger?.LogInfo(
                    $"Pipeline produced {result.Cards.Count} card(s) in " +
                    $"{result.Elapsed.TotalMilliseconds:F0}ms (allMatched={result.AllMatched}).");

                _output.ShowPrices(result);
                _stateMachine.Fire(OverlayTrigger.PricingCompleted);
            }
            else
            {
                Debug.WriteLine("[Coordinator] Pipeline detected no cards — pricing failed.");
                _logger?.LogWarning(
                    $"Pipeline detected no cards (elapsed " +
                    $"{result.Elapsed.TotalMilliseconds:F0}ms) — pricing failed.");
                _stateMachine.Fire(OverlayTrigger.PricingFailed);
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("[Coordinator] Pipeline cancelled.");
            _logger?.LogInfo("Pipeline cancelled.");
            // No trigger needed — the cancellation was caused by
            // WarframeStopped or Dispose, which handle their own
            // state transitions.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Coordinator] Pipeline error: {ex.Message}");
            _logger?.LogError("Pipeline error.", ex);
            _stateMachine.Fire(OverlayTrigger.PricingFailed);
        }

    }

    // Display timeout

    private void StartDisplayTimeout()
    {
        StopDisplayTimeout();
        _displayTimeoutTimer = new Timer(
            OnDisplayTimeout, null, DisplayTimeout, Timeout.InfiniteTimeSpan);
    }
 
    private void StopDisplayTimeout()
    {
        _displayTimeoutTimer?.Dispose();
        _displayTimeoutTimer = null;
    }
 
    private void OnDisplayTimeout(object? state)
    {
        if (_disposed) return;

        Debug.WriteLine("[Coordinator] Display timeout — auto-clearing prices.");
        _logger?.LogInfo(
            $"Display timeout ({DisplayTimeout.TotalSeconds:F0}s) elapsed — " +
            "auto-firing RewardScreenExited.");
        _stateMachine.Fire(OverlayTrigger.RewardScreenExited);
    }

    // ── Heartbeat ──────────────────────────────────────────────────

    /// <summary>
    /// Periodic "still alive" log line so the user can verify the
    /// coordinator is running and inspect how long it has been since
    /// the last meaningful event.  Useful for diagnosing the case
    /// where the overlay is running but no detection ever fires.
    /// </summary>
    private void OnHeartbeat(object? state)
    {
        if (_disposed) return;

        TimeSpan sinceLastEvent;
        lock (_lock)
        {
            sinceLastEvent = DateTime.UtcNow - _lastEventUtc;
        }

        _logger?.LogInfo(
            $"Heartbeat: state={_stateMachine.Current}, " +
            $"warframeRunning={_processTracker.IsRunning}, " +
            $"sinceLastEvent={sinceLastEvent.TotalSeconds:F0}s");
    }

    /// <summary>
    /// Records the current UTC timestamp as the moment of the most
    /// recent meaningful event.  Used by the heartbeat to report how
    /// long the coordinator has been idle.
    /// </summary>
    private void MarkEvent()
    {
        lock (_lock) { _lastEventUtc = DateTime.UtcNow; }
    }

    // Pipeline cancellation
    private void CancelPipeline()
    {
        lock (_lock)
        {
            if (_pipelineCts is not null)
            {
                _pipelineCts.Cancel();
                _pipelineCts.Dispose();
                _pipelineCts = null;
            }
        }
    }


}
