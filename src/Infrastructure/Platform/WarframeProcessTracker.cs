namespace WarframeRelicOverlay.Infrastructure.Platform;

using System.Diagnostics;
using System.IO;

/// <summary>
/// Orchestrates Warframe process lifecycle detection by combining two
/// complementary strategies:
///
///   <b>Primary — EE.log file watcher</b>
///   (<see cref="FileTriggerWatcher"/>).  Warframe truncates and
///   rewrites <c>%LOCALAPPDATA%\Warframe\EE.log</c> on every launch,
///   and writes <c>"Entering main loop"</c> / <c>"Exiting main loop"</c>
///   at the relevant moments.  A <see cref="FileSystemWatcher"/> gives
///   us near-instant, zero-CPU-cost start/stop signals.
///
///   <b>Safety net — slow polling</b>
///   (<see cref="ProcessPolling"/>).  A timer calls
///   <see cref="Process.GetProcessesByName"/> every few seconds to
///   catch cases the watcher misses (permissions, non-standard install,
///   watcher crash).  Also provides the initial "already running" check.
///
/// The two channels converge here: whichever fires first wins, and
/// the other is suppressed to avoid duplicate events.
/// </summary>
public class WarframeProcessTracker : IProcessTracker
{

    private const string LogFileName = "EE.log";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
 
    // ── Components ──────────────────────────────────────────────────
 
    private readonly ProcessPolling _polling = new ProcessPolling("Warframe.x64");
    private FileTriggerWatcher? _logWatcher;
    private Timer? _pollTimer;


    // STATE TRACKING
    private readonly object _lock = new();
    private Process? _trackedProcess;
    private bool _disposed;

    // IProcessTracker implementation

    /// <inheritdoc/>
     public bool IsRunning
    {
        get { lock (_lock) return _trackedProcess is not null && !HasExitedSafe(_trackedProcess); }
    }

    /// <inheritdoc />
    public int? ProcessId
    {
        get
        {
            lock (_lock)
            {
                if (_trackedProcess is null) return null;
                try { return _trackedProcess.HasExited ? null : _trackedProcess.Id; }
                catch { return null; }
            }
        }
    }

    /// <inheritdoc />
    public nint MainWindowHandle
    {
        get
        {
            lock (_lock)
            {
                if (_trackedProcess is null) return nint.Zero;
                try
                {
                    _trackedProcess.Refresh();
                    return _trackedProcess.HasExited
                        ? nint.Zero
                        : _trackedProcess.MainWindowHandle;
                }
                catch { return nint.Zero; }
            }
        }
    }

    /// <inheritdoc />
    public event Action<int>? Started;
 
    /// <inheritdoc />
    public event Action<int>? Stopped;

        // ── Lifecycle ───────────────────────────────────────────────────
 
    /// <inheritdoc />
    public void Start()
    {
        // 1. Wire up the polling component's events (used when the
        //    poller is the one that discovers a start/stop).
        _polling.ProcessStarted += OnPollingDetectedStart;
        _polling.ProcessStopped += OnPollingDetectedStop;
 
        // 2. Try to create the EE.log watcher (primary channel).
        TryStartLogWatcher();
 
        // 3. Immediate check: is Warframe already running?
        //    The poller handles this — if it finds a process it will
        //    raise ProcessStarted, which flows through our handler.
        _polling.Poll();
 
        // 4. Start the safety-net timer.
        _pollTimer = new Timer(OnPollTick, null, PollInterval, PollInterval);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
 
        _pollTimer?.Dispose();
        _logWatcher?.Dispose();
        _polling.ProcessStarted -= OnPollingDetectedStart;
        _polling.ProcessStopped -= OnPollingDetectedStop;
 
        lock (_lock) DetachProcess();
    }


    // ── EE.log watcher setup ────────────────────────────────────────
 
    private void TryStartLogWatcher()
    {
        string logPath = GetDefaultLogPath();
 
        try
        {
            _logWatcher = new FileTriggerWatcher(logPath);
            _logWatcher.OnTriggered += OnLogTrigger;
            Debug.WriteLine($"[ProcessTracker] EE.log watcher active at: {logPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[ProcessTracker] Could not watch EE.log ({ex.Message}). " +
                "Relying on polling only.");
            _logWatcher = null;
        }
    }
 
    // ── Log watcher callback ────────────────────────────────────────
 
    private void OnLogTrigger(string eventName)
    {
        switch (eventName)
        {
            case "GameStarted":
                OnLogDetectedStart();
                break;
 
            case "GameStopped":
                OnLogDetectedStop();
                break;
        }
    }
 
    private void OnLogDetectedStart()
    {
        // The log says the game entered its main loop.  Grab the actual
        // Process object so we have a PID and window handle.
        // Small delay: the process may not be fully visible to the OS
        // the instant the log line is written.
        Thread.Sleep(300);
 
        lock (_lock)
        {
            // Already tracking? Skip.
            if (_trackedProcess is not null && !HasExitedSafe(_trackedProcess))
                return;
        }
 
        if (TryAttachToProcess(out int pid))
        {
            _polling.AttachSilently(pid); // Keep poller in sync.
            Started?.Invoke(pid);
        }
    }
 
    private void OnLogDetectedStop()
    {
        int pid;
        lock (_lock)
        {
            if (_trackedProcess is null) return;
            pid = SafeGetId(_trackedProcess);
            DetachProcess();
        }
 
        _polling.DetachSilently();
        Debug.WriteLine($"[ProcessTracker] EE.log reported game exit (PID {pid}).");
        Stopped?.Invoke(pid);
    }
 
    // ── Polling callbacks ───────────────────────────────────────────
 
    private void OnPollTick(object? state)
    {
        if (_disposed) return;
 
        // If we already have a live process, the poller just confirms
        // it's still alive (and will fire ProcessStopped if it died
        // without the log watcher noticing).
        lock (_lock)
        {
            if (_trackedProcess is not null && !HasExitedSafe(_trackedProcess))
            {
                // Still alive — nothing to do.  The poller doesn't need
                // to scan because Process.Exited or the log watcher
                // will tell us when it stops.
                return;
            }
        }
 
        // Not tracking anything — let the poller scan.
        _polling.Poll();
    }
 
    private void OnPollingDetectedStart(int pid)
    {
        lock (_lock)
        {
            // Already tracking via the log watcher?
            if (_trackedProcess is not null && !HasExitedSafe(_trackedProcess))
                return;
        }
 
        if (TryAttachToProcess(out int attachedPid))
        {
            Debug.WriteLine($"[ProcessTracker] Polling found Warframe (PID {attachedPid}).");
            Started?.Invoke(attachedPid);
        }
    }
 
    private void OnPollingDetectedStop(int pid)
    {
        lock (_lock)
        {
            // Already cleaned up by the log watcher?
            if (_trackedProcess is null) return;
 
            DetachProcess();
        }
 
        _polling.DetachSilently();
        Debug.WriteLine($"[ProcessTracker] Polling detected exit (PID {pid}).");
        Stopped?.Invoke(pid);
    }
 
    // ── Process attach / detach ─────────────────────────────────────
 
    /// <summary>
    /// Scans for a running Warframe.x64 process and, if found, stores
    /// the <see cref="Process"/> handle and subscribes to
    /// <see cref="Process.Exited"/> as a third stop-detection channel.
    /// </summary>
    /// <returns><c>true</c> if a process was found and attached.</returns>
    private bool TryAttachToProcess(out int pid)
    {
        pid = -1;
        Process? found = null;
 
        try
        {
            foreach (var proc in Process.GetProcessesByName("Warframe.x64"))
            {
                try
                {
                    if (!proc.HasExited && found is null)
                    {
                        found = proc;
                    }
                    else
                    {
                        proc.Dispose();
                    }
                }
                catch
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProcessTracker] Error scanning processes: {ex.Message}");
            return false;
        }
 
        if (found is null) return false;
 
        lock (_lock)
        {
            // Another thread may have beaten us.
            if (_trackedProcess is not null && !HasExitedSafe(_trackedProcess))
            {
                found.Dispose();
                pid = SafeGetId(_trackedProcess);
                return false;
            }
 
            DetachProcess();
            _trackedProcess = found;
 
            // Belt-and-suspenders: Process.Exited fires if neither
            // the log watcher nor the poller catches the exit.
            _trackedProcess.EnableRaisingEvents = true;
            _trackedProcess.Exited += OnProcessExited;
        }
 
        pid = found.Id;
        Debug.WriteLine($"[ProcessTracker] Attached to Warframe.x64 (PID {pid}).");
        return true;
    }
 
    /// <summary>
    /// Third stop channel: the OS tells us the process handle closed.
    /// </summary>
    private void OnProcessExited(object? sender, EventArgs e)
    {
        int pid;
        lock (_lock)
        {
            if (_trackedProcess is null) return;
            pid = SafeGetId(_trackedProcess);
            DetachProcess();
        }
 
        _polling.DetachSilently();
        Debug.WriteLine($"[ProcessTracker] Process.Exited fired (PID {pid}).");
        Stopped?.Invoke(pid);
    }
 
    /// <summary>
    /// Unhooks events and disposes the tracked <see cref="Process"/>.
    /// Must be called under <see cref="_lock"/>.
    /// </summary>
    private void DetachProcess()
    {
        if (_trackedProcess is null) return;
 
        try { _trackedProcess.Exited -= OnProcessExited; } catch { }
        try { _trackedProcess.Dispose(); } catch { }
        _trackedProcess = null;
    }
 
    // ── Helpers ─────────────────────────────────────────────────────
 
    private static string GetDefaultLogPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Warframe", LogFileName);
    }
 
    private static bool HasExitedSafe(Process p)
    {
        try { return p.HasExited; }
        catch { return true; }
    }
 
    private static int SafeGetId(Process p)
    {
        try { return p.Id; }
        catch { return -1; }
    }


    public void TrackProcess(string processName)
    {

    }
}