namespace WarframeRelicOverlay.Tests.Infrastructure.Platform;

using System.Collections.Concurrent;
using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.Platform;
using Xunit;

public sealed class FileTriggerWatcherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logPath;

    public FileTriggerWatcherTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "WFO_FileTriggerTests_" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "EE.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Constructor guards ──────────────────────────────────────────

    [Fact]
    public void Constructor_ThrowsArgumentException_ForNullPath()
    {
        Action act = () => _ = new FileTriggerWatcher(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_ForWhitespacePath()
    {
        Action act = () => _ = new FileTriggerWatcher("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenDirectoryDoesNotExist()
    {
        string missingDir = Path.Combine(_tempDir, "NoSuchSubDir", "EE.log");
        Action act = () => _ = new FileTriggerWatcher(missingDir);
        act.Should().Throw<ArgumentException>();
    }

    // ── Trigger detection ───────────────────────────────────────────

    [Fact]
    public void OnTriggered_FiresGameStarted_WhenEnteringMainLoopLineAppended()
    {
        // File exists and is empty — watcher starts at position 0 (end of empty file).
        File.WriteAllText(_logPath, string.Empty);
        using var watcher = new FileTriggerWatcher(_logPath);

        using var fired = new ManualResetEventSlim(false);
        string? receivedEvent = null;
        watcher.OnTriggered += name => { receivedEvent = name; fired.Set(); };

        Append("0.000 Script [Info]: ===[ Entering main loop ]===");

        fired.Wait(TimeSpan.FromSeconds(3))
             .Should().BeTrue("the watcher should detect the trigger within 3 s");
        receivedEvent.Should().Be("GameStarted");
    }

    [Fact]
    public void OnTriggered_FiresGameStopped_WhenExitingMainLoopLineAppended()
    {
        File.WriteAllText(_logPath, string.Empty);
        using var watcher = new FileTriggerWatcher(_logPath);

        using var fired = new ManualResetEventSlim(false);
        string? receivedEvent = null;
        watcher.OnTriggered += name => { receivedEvent = name; fired.Set(); };

        Append("0.000 Script [Info]: ===[ Exiting main loop ]===");

        fired.Wait(TimeSpan.FromSeconds(3)).Should().BeTrue();
        receivedEvent.Should().Be("GameStopped");
    }

    [Fact]
    public void OnTriggered_FiresBothEvents_WhenBothTriggersAppendedTogether()
    {
        File.WriteAllText(_logPath, string.Empty);
        using var watcher = new FileTriggerWatcher(_logPath);

        var events = new ConcurrentBag<string>();
        using var countdown = new CountdownEvent(2);
        watcher.OnTriggered += name =>
        {
            events.Add(name);
            if (countdown.CurrentCount > 0)
                countdown.Signal();
        };

        // Both triggers in one write so they land in the same ScanNewContent pass.
        Append(
            "0.000 Script [Info]: ===[ Entering main loop ]===\n" +
            "1.000 Script [Info]: ===[ Exiting main loop ]===");

        countdown.Wait(TimeSpan.FromSeconds(3))
                 .Should().BeTrue("both events should fire within 3 s");
        events.Should().Contain("GameStarted");
        events.Should().Contain("GameStopped");
    }

    // ── Position tracking ───────────────────────────────────────────

    [Fact]
    public void OnTriggered_DoesNotRefireOldContent_WhenFileExistedAtConstruction()
    {
        // Write trigger content BEFORE the watcher is created.
        // The watcher should start at the current end of the file, skipping it.
        File.WriteAllText(_logPath, "0.000 Script [Info]: ===[ Entering main loop ]===\n");

        using var watcher = new FileTriggerWatcher(_logPath);
        bool anyFired = false;
        watcher.OnTriggered += _ => anyFired = true;

        Thread.Sleep(600); // Give the watcher time to miss-fire if broken.
        anyFired.Should().BeFalse(
            "content written before watcher construction must not produce events");
    }

    [Fact]
    public void OnTriggered_PicksUpContent_WhenFileCreatedAfterWatcherStarts()
    {
        // File does not exist yet — _lastPosition will be 0.
        // Warframe creates EE.log fresh on every launch; we simulate that.
        Assert.False(File.Exists(_logPath));

        using var watcher = new FileTriggerWatcher(_logPath);

        using var fired = new ManualResetEventSlim(false);
        string? receivedEvent = null;
        watcher.OnTriggered += name => { receivedEvent = name; fired.Set(); };

        // Simulate Warframe creating the log file for the first time.
        File.WriteAllText(_logPath, "0.000 Script [Info]: ===[ Entering main loop ]===\n");

        fired.Wait(TimeSpan.FromSeconds(3))
             .Should().BeTrue("watcher should detect file creation and read trigger");
        receivedEvent.Should().Be("GameStarted");
    }

    [Fact]
    public void OnTriggered_ResetsPosition_WhenFileShrinksBetweenScans()
    {
        // Write non-trigger content so the watcher starts at a non-zero offset.
        File.WriteAllText(_logPath, new string('x', 500) + "\n");
        using var watcher = new FileTriggerWatcher(_logPath);

        using var fired = new ManualResetEventSlim(false);
        string? receivedEvent = null;
        watcher.OnTriggered += name => { receivedEvent = name; fired.Set(); };

        // Simulate the game restarting: truncate and rewrite the log.
        File.WriteAllText(_logPath, "0.000 Script [Info]: ===[ Entering main loop ]===\n");

        fired.Wait(TimeSpan.FromSeconds(3))
             .Should().BeTrue("watcher must reset _lastPosition when file shrinks");
        receivedEvent.Should().Be("GameStarted");
    }

    // ── Dispose ─────────────────────────────────────────────────────

    [Fact]
    public void Dispose_IsIdempotent()
    {
        File.WriteAllText(_logPath, string.Empty);
        var watcher = new FileTriggerWatcher(_logPath);
        watcher.Dispose();
        Action second = () => watcher.Dispose();
        second.Should().NotThrow();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void Append(string text)
    {
        using var fs = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var sw = new StreamWriter(fs);
        sw.WriteLine(text);
    }
}
