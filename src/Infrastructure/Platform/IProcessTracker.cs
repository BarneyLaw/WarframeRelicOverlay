namespace WarframeRelicOverlay.Infrastructure.Platform;

/// <summary>
/// Monitors whether the target game process is running and exposes
/// start/stop events so the overlay state machine can react.
/// </summary>
public interface IProcessTracker : IDisposable
{
    /// <summary>Whether the tracked process is currently running.</summary>
    bool IsRunning { get; }
 
    /// <summary>
    /// The OS process ID, or <c>null</c> if not running.
    /// </summary>
    int? ProcessId { get; }
 
    /// <summary>
    /// The main window handle, or <see cref="nint.Zero"/> if the
    /// process has no visible window yet or is not running.
    /// </summary>
    nint MainWindowHandle { get; }
 
    /// <summary>Raised when the game process starts. Arg = PID.</summary>
    event Action<int>? Started;
 
    /// <summary>Raised when the game process exits. Arg = PID.</summary>
    event Action<int>? Stopped;
 
    /// <summary>
    /// Begins monitoring. Checks for an already-running instance,
    /// then watches for future start/stop events.
    /// </summary>
    void Start();
}
