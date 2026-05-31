namespace WarframeRelicOverlay.Infrastructure.Platform;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

/// <summary>
/// Watches a file for appended content and fires an event whenever a
/// known trigger phrase appears in the newly-written bytes.
///
/// Designed for Warframe's EE.log, which is truncated on each game
/// launch and then continuously appended while the game runs.
///
/// Detection uses two complementary channels:
///
///   <b>Primary — <see cref="FileSystemWatcher"/></b>: near-instant
///   notification when the OS detects a write.
///
///   <b>Safety net — poll timer</b> (optional): catches events that
///   the <see cref="FileSystemWatcher"/> may coalesce or miss (buffer
///   overflow, network paths, permissions).  Disabled by default;
///   enable by passing a <paramref name="pollInterval"/> to
///   <see cref="FileTriggerWatcher(string, IReadOnlyList{ValueTuple{string, string}}, TimeSpan?)"/>.
///
/// Lifecycle: call <see cref="Start"/> to begin monitoring,
/// <see cref="Stop"/> to pause, <see cref="Dispose"/> to release.
/// The watcher does <b>not</b> auto-start on construction so the
/// caller controls exactly when monitoring begins.
///
/// Thread safety: <see cref="ScanNewContent"/> is serialised by
/// <see cref="_scanLock"/> so concurrent calls from the
/// <see cref="FileSystemWatcher"/> thread and the poll timer never
/// corrupt the position cursor.
/// </summary>
public sealed class FileTriggerWatcher : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────

    private readonly string _filePath;
    private readonly IReadOnlyList<(string Phrase, string EventName)> _triggers;
    private readonly TimeSpan? _pollInterval;

    // ── State ─────────────────────────────────────────────────────

    private readonly object _scanLock = new();
    private long _lastPosition;
    private bool _running;
    private bool _disposed;

    private FileSystemWatcher? _fileWatcher;
    private Timer? _pollTimer;

    // ── Events ────────────────────────────────────────────────────

    /// <summary>
    /// Raised when a trigger phrase is detected in newly-appended
    /// content.  The string argument is the <c>EventName</c> from
    /// the matching trigger entry.
    /// </summary>
    public event Action<string>? OnTriggered;

    // ── Construction ──────────────────────────────────────────────

    /// <summary>
    /// Creates a watcher for the given file and trigger phrases.
    /// Call <see cref="Start"/> to begin monitoring.
    /// </summary>
    /// <param name="filePath">
    /// Absolute path to the log file to watch.
    /// </param>
    /// <param name="triggers">
    /// Trigger phrases to scan for.  Each entry is a
    /// <c>(Phrase, EventName)</c> tuple: when <c>Phrase</c> is found
    /// (ordinal, case-insensitive) in newly-appended content,
    /// <see cref="OnTriggered"/> fires with <c>EventName</c>.
    /// </param>
    /// <param name="pollInterval">
    /// Optional safety-net polling interval.  When set, a
    /// <see cref="Timer"/> calls <see cref="ScanNewContent"/> at
    /// this rate in addition to <see cref="FileSystemWatcher"/>
    /// events.  Pass <c>null</c> (default) to rely solely on the
    /// <see cref="FileSystemWatcher"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="filePath"/> is null/empty or its directory
    /// does not exist.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="triggers"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="triggers"/> is empty.
    /// </exception>
    public FileTriggerWatcher(
        string filePath,
        IReadOnlyList<(string Phrase, string EventName)> triggers,
        TimeSpan? pollInterval = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException(
                "File path must not be null or empty.", nameof(filePath));

        if (triggers is null)
            throw new ArgumentNullException(nameof(triggers));

        if (triggers.Count == 0)
            throw new ArgumentException(
                "At least one trigger must be provided.", nameof(triggers));

        string? directory = Path.GetDirectoryName(filePath);
        if (directory is null || !Directory.Exists(directory))
            throw new ArgumentException(
                $"Directory does not exist: {directory}", nameof(filePath));

        _filePath     = filePath;
        _triggers     = triggers;
        _pollInterval = pollInterval;
    }

    // ── Lifecycle ─────────────────────────────────────────────────

    /// <summary>
    /// Begin monitoring the file.  Idempotent — calling
    /// <see cref="Start"/> on an already-running watcher is a no-op.
    ///
    /// <para>
    /// Seeks to the end of whatever is already in the file so
    /// pre-existing content is never re-fired.  If the file does
    /// not exist yet, the position starts at 0 and the first write
    /// (e.g. Warframe creating the log) will be picked up.
    /// </para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">
    /// The watcher has been disposed.
    /// </exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_running) return;
        _running = true;

        // Capture the current file length so we only read new bytes.
        _lastPosition = File.Exists(_filePath)
            ? new FileInfo(_filePath).Length
            : 0;

        TryStartFileWatcher();

        if (_pollInterval.HasValue)
        {
            _pollTimer = new Timer(
                OnPollTick, null, _pollInterval.Value, _pollInterval.Value);
        }

        Debug.WriteLine($"[FileTriggerWatcher] Started for: {_filePath}");
    }

    /// <summary>
    /// Stop monitoring.  The watcher can be restarted with
    /// <see cref="Start"/>.  Idempotent.
    /// </summary>
    public void Stop()
    {
        if (!_running) return;
        _running = false;

        _pollTimer?.Dispose();
        _pollTimer = null;

        DisposeFileWatcher();

        Debug.WriteLine("[FileTriggerWatcher] Stopped.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }

    // ── Public scan entry point ──────────────────────────────────

    /// <summary>
    /// Manually trigger a scan of newly-appended content.  Useful
    /// for callers that want to force an immediate check outside the
    /// normal <see cref="FileSystemWatcher"/> / poll-timer cadence.
    /// Thread-safe — can be called from any thread at any time.
    /// </summary>
    public void ScanNow() => ScanNewContent();

    // ── FileSystemWatcher setup ──────────────────────────────────

    private void TryStartFileWatcher()
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (directory is null || !Directory.Exists(directory))
        {
            Debug.WriteLine(
                $"[FileTriggerWatcher] Directory '{directory}' not found. " +
                "Relying on poll timer only.");
            return;
        }

        try
        {
            _fileWatcher = new FileSystemWatcher
            {
                Path = directory,
                Filter = Path.GetFileName(_filePath),
                NotifyFilter = NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
            };

            _fileWatcher.Changed += OnFileChanged;
            _fileWatcher.Created += OnFileCreated;
            _fileWatcher.Error   += OnWatcherError;
            _fileWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[FileTriggerWatcher] FileSystemWatcher failed " +
                $"({ex.Message}). Relying on poll timer only.");
            _fileWatcher = null;
        }
    }

    private void DisposeFileWatcher()
    {
        if (_fileWatcher is null) return;

        _fileWatcher.EnableRaisingEvents = false;
        _fileWatcher.Changed -= OnFileChanged;
        _fileWatcher.Created -= OnFileCreated;
        _fileWatcher.Error   -= OnWatcherError;
        _fileWatcher.Dispose();
        _fileWatcher = null;
    }

    // ── Event handlers ───────────────────────────────────────────

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        ScanNewContent();
    }

    /// <summary>
    /// Warframe truncates EE.log on launch, which some file systems
    /// report as a create rather than a change.  Reset position to 0
    /// so we read the full new content.
    /// </summary>
    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        lock (_scanLock)
        {
            _lastPosition = 0;
        }

        ScanNewContent();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Debug.WriteLine(
            $"[FileTriggerWatcher] Watcher error: " +
            $"{e.GetException().Message}");

        try
        {
            if (_fileWatcher is not null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.EnableRaisingEvents = true;
            }
        }
        catch
        {
            // Poll timer is the safety net.
        }
    }

    private void OnPollTick(object? state)
    {
        if (!_running) return;
        ScanNewContent();
    }

    // ── Core scan logic ──────────────────────────────────────────

    private void ScanNewContent()
    {
        lock (_scanLock)
        {
            try
            {
                if (!File.Exists(_filePath)) return;

                using var stream = new FileStream(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                // File shrank → game restarted and truncated the log.
                if (stream.Length < _lastPosition)
                    _lastPosition = 0;

                if (stream.Length <= _lastPosition)
                    return;

                stream.Seek(_lastPosition, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                string newContent = reader.ReadToEnd();
                _lastPosition = stream.Position;

                var triggeredEvents = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (phrase, eventName) in _triggers)
                {
                    if (newContent.Contains(phrase, StringComparison.OrdinalIgnoreCase) &&
                        triggeredEvents.Add(eventName))
                    {
                        OnTriggered?.Invoke(eventName);
                    }
                }
            }
            catch (IOException)
            {
                // File locked by the game engine — we'll catch it on
                // the next watcher event or poll tick.
            }
        }
    }
}
