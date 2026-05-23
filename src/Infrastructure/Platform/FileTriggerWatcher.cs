namespace WarframeRelicOverlay.Infrastructure.Platform;

using System;
using System.IO;
using System.Threading;

/// <summary>
/// Watches a file for appended content and fires an event whenever a
/// known trigger phrase appears in the newly-written bytes.
///
/// Designed for Warframe's EE.log, which is truncated on each game
/// launch and then continuously appended while the game runs.
///
/// If the <see cref="FileSystemWatcher"/> cannot be created (permissions,
/// network path, etc.) the constructor throws — the caller should catch
/// and fall back to polling.
/// </summary>

public class FileTriggerWatcher : IDisposable
{
    private readonly FileSystemWatcher _fileWatcher;
    private readonly string _filePath;
    private long _lastPosition;
    private bool _disposed;

    /// <summary>
    /// Raised when a trigger phrase is detected in newly-appended content.
    /// The string argument is the trigger phrase that matched.
    /// </summary>
    public event Action<string>? OnTriggered;

    /// Known trigger phrases to scan for.  Each entry is a substring
    /// match against raw log lines.
    /// </summary>
    private static readonly (string Phrase, string EventName)[] Triggers =
    [
        ("===[ Entering main loop ]", "GameStarted"),
        ("===[ Exiting main loop ]",  "GameStopped"),
    ];

    /// <param name="filePath">Absolute path to the log file to watch.</param>
    /// <exception cref="ArgumentException">Path is null/empty or the directory does not exist.</exception>
    /// <exception cref="IOException">FileSystemWatcher could not be created.</exception>
    public FileTriggerWatcher(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must not be null or empty.", nameof(filePath));
 
        string? directory = Path.GetDirectoryName(filePath);
        if (directory is null || !Directory.Exists(directory))
            throw new ArgumentException($"Directory does not exist: {directory}", nameof(filePath));
 
        _filePath = filePath;
 
        // Seek to the end of whatever is already in the file so we
        // don't re-fire triggers from a previous session.  If the
        // file doesn't exist yet, we'll start at 0 and catch the
        // first write when Warframe launches.
        _lastPosition = File.Exists(filePath)
            ? new FileInfo(filePath).Length
            : 0;

        // If an exception is thrown here, we switch to a polling mechanism in the caller.
        _fileWatcher = new FileSystemWatcher
        {
            Path = directory,
            Filter = Path.GetFileName(filePath),
            NotifyFilter = NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.CreationTime,
        };
 
        _fileWatcher.Changed += OnFileChanged;
        _fileWatcher.Created += OnFileCreated;
        _fileWatcher.Error   += OnWatcherError;
        _fileWatcher.EnableRaisingEvents = true;

    }

    // ── Event handlers ──────────────────────────────────────────────
 
    /// <summary>
    /// File was created — Warframe truncates EE.log on launch, which
    /// some file systems report as a create rather than a change.
    /// Reset position to 0 so we read the full new content.
    /// </summary>
    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _lastPosition = 0;
        ScanNewContent();
    }
 
    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        ScanNewContent();
    }
 
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[FileTriggerWatcher] Watcher error: {e.GetException().Message}");
 
        // Attempt to restart the watcher.
        try
        {
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            // If restart fails, the caller's poll timer is the safety net.
        }
    }
 
    // ── Core logic ──────────────────────────────────────────────────
 
    private void ScanNewContent()
    {
        try
        {
            using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
 
            // If the file shrank (game restart truncated it), reset.
            if (stream.Length < _lastPosition)
                _lastPosition = 0;
 
            if (stream.Length <= _lastPosition)
                return;
 
            stream.Seek(_lastPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string newContent = reader.ReadToEnd();
            _lastPosition = stream.Position;
 
            foreach (var (phrase, eventName) in Triggers)
            {
                if (newContent.Contains(phrase, StringComparison.Ordinal))
                {
                    OnTriggered?.Invoke(eventName);
                }
            }
        }
        catch (IOException)
        {
            // File locked by the game engine — we'll catch it on the
            // next Changed event, which will fire shortly.
        }
    }
 
    // ── Cleanup ─────────────────────────────────────────────────────
 
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
 
        _fileWatcher.EnableRaisingEvents = false;
        _fileWatcher.Changed -= OnFileChanged;
        _fileWatcher.Created -= OnFileCreated;
        _fileWatcher.Error   -= OnWatcherError;
        _fileWatcher.Dispose();
    }
}

