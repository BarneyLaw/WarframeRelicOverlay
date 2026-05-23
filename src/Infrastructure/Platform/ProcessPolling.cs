namespace WarframeRelicOverlay.Infrastructure.Platform;

using System.Diagnostics;

/// <summary>
/// Polling-based process tracker.  Scans the OS process list to find
/// a process by name, tracks its PID, and reports start/stop transitions.
///
/// Used as a safety-net fallback alongside <see cref="FileTriggerWatcher"/>,
/// or as the sole detection mechanism when the EE.log watcher cannot be
/// created.
/// </summary>
public sealed class ProcessPolling
{
    private readonly string _processName;
    private int? _trackedPid;

    /// <summary>Raised when the process is found for the first time (or restarted).</summary>
    public event Action<int>? ProcessStarted;

    /// <summary>Raised when a previously-tracked process is no longer running.</summary>
    public event Action<int>? ProcessStopped;

    public ProcessPolling(string processName = "Warframe.x64")
    {
        _processName = processName;
    }

    /// <summary>The PID being tracked, or <c>null</c> if no process is running.</summary>
    public int? TrackedPid => _trackedPid;

    /// <summary>
    /// Checks whether the currently-tracked PID is still alive.
    /// Returns <c>false</c> if no PID is tracked or the process exited.
    /// </summary>
    public bool IsProcessRunning()
    {
        if (!_trackedPid.HasValue)
            return false;

        try
        {
            return !Process.GetProcessById(_trackedPid.Value).HasExited;
        }
        catch (ArgumentException)
        {
            // PID no longer exists.
            return false;
        }
    }

    /// <summary>
    /// One poll cycle: checks the current tracked process and scans
    /// for a new one if needed.  Raises <see cref="ProcessStarted"/>
    /// or <see cref="ProcessStopped"/> on transitions.
    ///
    /// Call this on a timer from <see cref="WarframeProcessTracker"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a process is now being tracked, <c>false</c> otherwise.
    /// </returns>
    public bool Poll()
    {
        // Step 1: if we're tracking a PID, check it's still alive
        if (_trackedPid.HasValue)
        {
            if (IsProcessRunning())
                return true;

            // Process died between polls.
            int exitedPid = _trackedPid.Value;
            _trackedPid = null;
            ProcessStopped?.Invoke(exitedPid);
            return false;
        }

        // Step 2: no tracked PID — scan for a running instance
        Process? found = FindProcess();
        if (found is null)
            return false;

        _trackedPid = found.Id;
        found.Dispose();  // We only need the PID, not the handle.
        ProcessStarted?.Invoke(_trackedPid.Value);
        return true;
    }

    /// <summary>
    /// Attaches to a known PID without raising the Started event.
    /// Used when the log watcher detects a start and then the
    /// orchestrator confirms via a process scan.
    /// </summary>
    public void AttachSilently(int pid)
    {
        _trackedPid = pid;
    }

    /// <summary>
    /// Clears the tracked PID without raising the Stopped event.
    /// Used when the orchestrator handles the stop through another channel.
    /// </summary>
    public void DetachSilently()
    {
        _trackedPid = null;
    }

    // ── Internals ───────────────────────────────────────────────────

    private Process? FindProcess()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(_processName);
        }
        catch
        {
            return null;
        }

        Process? result = null;
        foreach (var proc in processes)
        {
            if (result is not null)
            {
                proc.Dispose();
                continue;
            }

            try
            {
                if (!proc.HasExited)
                    result = proc;
                else
                    proc.Dispose();
            }
            catch
            {
                proc.Dispose();
            }
        }

        return result;
    }
}