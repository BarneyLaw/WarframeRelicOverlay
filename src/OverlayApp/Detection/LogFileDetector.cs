namespace WarframeRelicOverlay.OverlayApp.Detection;
 
using System;
using System.Diagnostics;
using System.IO;
using WarframeRelicOverlay.Core;
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
    // Trigger configuration
 
    private const string RewardDetectedEvent = "RewardDetected";
 
    /// <summary>
    /// Trigger phrases scanned in newly-appended EE.log content.
    /// </summary>
    private static readonly (string Phrase, string EventName)[] RewardTriggers =
    [
        ("Got rewards", RewardDetectedEvent),
        ("GotRewards", RewardDetectedEvent),
    ];
 
    /// <summary>
    /// Safety-net poll interval for the inner
    /// <see cref="FileTriggerWatcher"/>.  Kept short because each
    /// poll only reads the delta — typically a few hundred bytes.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
 
    // State
 
    private readonly string _logPath;
    private FileTriggerWatcher? _watcher;
    private bool _disposed;
 
    // IRewardScreenDetector
 
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
 
    // ── Construction ──────────────────────────────────────────────
 
    /// <summary>
    /// Creates a detector using the log path from
    /// <see cref="AppSettings"/>.  If
    /// <see cref="AppSettings.EeLogPathOverride"/> is set, that
    /// path is used; otherwise the default
    /// <c>%LOCALAPPDATA%\Warframe\EE.log</c>.
    /// </summary>
    public LogFileDetector(AppSettings settings)
    {
        _logPath = !string.IsNullOrWhiteSpace(settings?.EeLogPathOverride)
            ? settings!.EeLogPathOverride!
            : GetDefaultLogPath();
    }

    /// <summary>
    /// Creates a detector for an explicit log file path.
    /// Useful for testing or when the caller already knows the path.
    /// </summary>
    public LogFileDetector(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            throw new ArgumentException(
                "Log path must not be null or empty.", nameof(logPath));
 
        _logPath = logPath;
    }

    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_watcher != null)
            return; // Already running — no-op

        try
        {
            _watcher = new FileTriggerWatcher(
                _logPath, RewardTriggers, PollInterval);
            _watcher.OnTriggered += OnWatcherTriggered;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            // Log and rethrow — the caller needs to know if we fail to start
            Trace.TraceError(
                $"Failed to start {nameof(LogFileDetector)}: {ex}");
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
    }
 
    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
 
        Stop();
    }
 
    // Event routing
 
    private void OnWatcherTriggered(string eventName)
    {
        if (eventName == RewardDetectedEvent)
        {
            Debug.WriteLine(
                "[LogFileDetector] Reward trigger detected.");
            RewardScreenDetected?.Invoke();
        }
    }
 
    // Helpers
 
    private static string GetDefaultLogPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Warframe", "EE.log");
    }


}
