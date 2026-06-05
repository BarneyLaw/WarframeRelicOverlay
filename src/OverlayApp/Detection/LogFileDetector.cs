namespace WarframeRelicOverlay.OverlayApp.Detection;

using System;
using System.Diagnostics;
using System.IO;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Infrastructure.Logging;
using WarframeRelicOverlay.Infrastructure.Platform;

/// <summary>
/// Primary reward-screen detector.  Tails Warframe's debug log file
/// (<c>%LOCALAPPDATA%\Warframe\EE.log</c>) and fires
/// <see cref="RewardScreenDetected"/> the instant the game writes
/// its reward trigger line.
///
/// <b>Why this works:</b> Warframe logs <c>"Got rewards"</c> at the
/// exact moment the reward selection UI is created internally —
/// before the animation even starts playing.  Tailing the log gives
/// us a zero-latency, zero-CPU trigger with no OCR cost.
///
/// Implementation: composes with <see cref="FileTriggerWatcher"/>
/// (which handles <see cref="FileSystemWatcher"/> + optional poll
/// timer + position tracking + file truncation).  This class adds
/// the reward-specific trigger phrase and maps the generic
/// <see cref="FileTriggerWatcher.OnTriggered"/> event to the
/// <see cref="IRewardScreenDetector"/> contract.
/// </summary>
public sealed class LogFileDetector : IRewardScreenDetector
{
    // ── Trigger configuration ─────────────────────────────────────

    private const string RewardDetectedEvent = "RewardDetected";

    /// <summary>
    /// Trigger phrases scanned in newly-appended EE.log content.
    /// Screen-open phrases intentionally fire before reward cards are
    /// guaranteed visible; the pricing pipeline gates on card layout.
    /// </summary>
    private static readonly (string Phrase, string EventName)[] _rewardTriggers =
    [
        ("VoidProjections: OpenVoidProjectionRewardScreen", RewardDetectedEvent),
        ("Created /Lotus/Interface/ProjectionRewardChoice.swf", RewardDetectedEvent),
        ("ProjectionRewardChoice.lua: Relic rewards initialized", RewardDetectedEvent),
        ("ProjectionRewardChoice.lua: Got rewards", RewardDetectedEvent),
        ("Got rewards", RewardDetectedEvent),
        ("GotRewards", RewardDetectedEvent),
    ];

    /// <summary>
    /// Safety-net poll interval for the inner
    /// <see cref="FileTriggerWatcher"/>.  Kept short because each
    /// poll only reads the delta — typically a few hundred bytes.
    /// </summary>
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(200);

    // ── State ─────────────────────────────────────────────────────

    private readonly string _logPath;
    private readonly ILogger? _logger;
    private FileTriggerWatcher? _watcher;
    private bool _disposed;

    // ── IRewardScreenDetector ─────────────────────────────────────

    /// <inheritdoc />
    public event Action? RewardScreenDetected;

    /// <inheritdoc />
    public event Action? RewardScreenExited;

    /// <inheritdoc />
    /// <remarks>
    /// <c>true</c> — a single EE.log trigger is definitive.  The
    /// overlay state machine should fire
    /// <see cref="StateMachine.OverlayTrigger.RewardConfirmed"/>
    /// immediately, skipping the streak-accumulation phase.
    /// </remarks>
    public bool IsDefinitive => true;

    /// <summary>
    /// Resolved absolute path of the EE.log file being watched.
    /// Useful for startup self-checks.
    /// </summary>
    public string LogPath => _logPath;

    // ── Construction ──────────────────────────────────────────────

    /// <summary>
    /// Creates a detector using the log path from
    /// <see cref="AppSettings"/>.  If
    /// <see cref="AppSettings.EeLogPathOverride"/> is set, that
    /// path is used; otherwise the default
    /// <c>%LOCALAPPDATA%\Warframe\EE.log</c>.
    /// </summary>
    public LogFileDetector(AppSettings settings, ILogger? logger = null)
    {
        _logPath = !string.IsNullOrWhiteSpace(settings?.EeLogPathOverride)
            ? settings!.EeLogPathOverride!
            : GetDefaultLogPath();
        _logger = logger;
    }

    /// <summary>
    /// Creates a detector for an explicit log file path.
    /// Useful for testing or when the caller already knows the path.
    /// </summary>
    public LogFileDetector(string logPath, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            throw new ArgumentException(
                "Log path must not be null or empty.", nameof(logPath));

        _logPath = logPath;
        _logger = logger;
    }

    // ── Lifecycle ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_watcher != null)
            return; // Already running — no-op

        // Self-check before constructing the watcher: log the
        // resolved path, whether it exists, and its current size.
        // This is the single most useful diagnostic for "the overlay
        // is running but never sees a reward screen".
        bool fileExists = File.Exists(_logPath);
        long fileSize = fileExists ? new FileInfo(_logPath).Length : 0;
        _logger?.LogInfo(
            $"LogFileDetector starting: path='{_logPath}', " +
            $"exists={fileExists}, sizeBytes={fileSize}, " +
            $"trigger='GotRewards'");

        try
        {
            _watcher = new FileTriggerWatcher(
                _logPath, _rewardTriggers, _pollInterval, _logger);
            _watcher.OnTriggered += OnWatcherTriggered;
            _watcher.Start();
            _logger?.LogInfo("LogFileDetector started successfully.");
        }
        catch (ArgumentException ex)
        {
            // The Warframe local-app-data directory does not exist yet —
            // the game has never been launched on this machine, or the
            // user pointed us at a bogus EeLogPathOverride.  Don't take
            // the whole app down for it; log and remain inert.  When
            // Warframe runs for the first time the launcher creates the
            // directory and the next IRewardDetector.Start (driven by
            // the state machine) will succeed.
            Trace.TraceWarning(
                $"{nameof(LogFileDetector)} disabled: {ex.Message}. " +
                "EE.log path will be retried on the next state-machine " +
                "transition.");
            _logger?.LogWarning(
                $"LogFileDetector disabled — EE.log directory missing. " +
                $"Path was '{_logPath}'. Will retry on next state transition.");
            _watcher = null;
        }
        catch (Exception ex)
        {
            // Log and rethrow — the caller needs to know if we fail to start
            Trace.TraceError(
                $"Failed to start {nameof(LogFileDetector)}: {ex}");
            _logger?.LogError("LogFileDetector failed to start.", ex);
            throw;
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        if (_watcher is null) return;

        _watcher.OnTriggered -= OnWatcherTriggered;
        _watcher.Dispose();
        _watcher = null;

        Debug.WriteLine("[LogFileDetector] Stopped.");
        _logger?.LogInfo("LogFileDetector stopped.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }

    // ── Event routing ─────────────────────────────────────────────

    /// <summary>
    /// Forwards a matched trigger phrase to subscribers as a
    /// <see cref="RewardScreenDetected"/> event.
    /// </summary>
    private void OnWatcherTriggered(string eventName)
    {
        if (eventName == RewardDetectedEvent)
        {
            Debug.WriteLine(
                "[LogFileDetector] Reward trigger detected.");
            _logger?.LogInfo(
                "LogFileDetector: 'GotRewards' phrase observed in EE.log " +
                "— firing RewardScreenDetected.");
            RewardScreenDetected?.Invoke();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Returns the default EE.log path:
    /// <c>%LOCALAPPDATA%\Warframe\EE.log</c>.
    /// </summary>
    private static string GetDefaultLogPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Warframe", "EE.log");
    }
}

