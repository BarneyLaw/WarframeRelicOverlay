namespace WarframeRelicOverlay.Tests.Infrastructure.Platform;

using System.Collections.Concurrent;
using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.Platform;
using Xunit;

public sealed class FileTriggerWatcherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logPath;

    /// <summary>
    /// The same process-lifecycle triggers that
    /// <see cref="WarframeProcessTracker"/> uses.
    /// </summary>
    private static readonly (string Phrase, string EventName)[] ProcessTriggers =
    [
        ("===[ Entering main loop ]", "GameStarted"),
        ("===[ Exiting main loop ]",  "GameStopped"),
    ];

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
        Action act = () => _ = new FileTriggerWatcher(null!, ProcessTriggers);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_ForWhitespacePath()
    {
        Action act = () => _ = new FileTriggerWatcher("   ", ProcessTriggers);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenDirectoryDoesNotExist()
    {
        string missingDir = Path.Combine(_tempDir, "NoSuchSubDir", "EE.log");
        Action act = () => _ = new FileTriggerWatcher(missingDir, ProcessTriggers);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenTriggersNull()
    {
        File.WriteAllText(_logPath, string.Empty);
        Action act = () => _ = new FileTriggerWatcher(_logPath, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ThrowsArgumentException_WhenTriggersEmpty()
    {
        File.WriteAllText(_logPath, string.Empty);
        Action act = () => _ = new FileTriggerWatcher(
            _logPath, Array.Empty<(string, string)>());
        act.Should().Throw<ArgumentException>();
    }

    // ── Trigger detection ───────────────────────────────────────────

    [Fact]
    public void OnTriggered_FiresGameStarted_WhenEnteringMainLoopLineAppended()
    {
        File.WriteAllText(_logPath, string.Empty);
        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();

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
        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();

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
        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();

        var events = new ConcurrentBag<string>();
        using var countdown = new CountdownEvent(2);
        watcher.OnTriggered += name =>
        {
            events.Add(name);
            if (countdown.CurrentCount > 0)
                countdown.Signal();
        };

        // Both triggers in one write so they land in the same scan pass.
        Append(
            "0.000 Script [Info]: ===[ Entering main loop ]===\n" +
            "1.000 Script [Info]: ===[ Exiting main loop ]===");

        countdown.Wait(TimeSpan.FromSeconds(3))
                 .Should().BeTrue("both events should fire within 3 s");
        events.Should().Contain("GameStarted");
        events.Should().Contain("GameStopped");
    }

    [Fact]
    public void OnTriggered_FiresCustomTrigger()
    {
        File.WriteAllText(_logPath, string.Empty);

        var customTriggers = new (string, string)[]
        {
            ("GotRewards", "RewardDetected"),
        };

        using var watcher = new FileTriggerWatcher(_logPath, customTriggers);
        watcher.Start();

        using var fired = new ManualResetEventSlim(false);
        string? receivedEvent = null;
        watcher.OnTriggered += name => { receivedEvent = name; fired.Set(); };

        Append("12345.678 Sys [Info]: GotRewards");

        fired.Wait(TimeSpan.FromSeconds(3))
             .Should().BeTrue("custom trigger should fire");
        receivedEvent.Should().Be("RewardDetected");
    }

    // ── Position tracking ───────────────────────────────────────────

    [Fact]
    public void OnTriggered_DoesNotRefireOldContent_WhenFileExistedAtStart()
    {
        // Write trigger content BEFORE calling Start().
        File.WriteAllText(_logPath, "0.000 Script [Info]: ===[ Entering main loop ]===\n");

        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();

        bool anyFired = false;
        watcher.OnTriggered += _ => anyFired = true;

        Thread.Sleep(600);
        anyFired.Should().BeFalse(
            "content written before Start() must not produce events");
    }

    [Fact]
    public void OnTriggered_PicksUpContent_WhenFileCreatedAfterStart()
    {
        // File does not exist yet — position will be 0.
        Assert.False(File.Exists(_logPath));

        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();

        using var fired = new ManualResetEventSlim(false);
        string? receivedEvent = null;
        watcher.OnTriggered += name => { receivedEvent = name; fired.Set(); };

        File.WriteAllText(_logPath, "0.000 Script [Info]: ===[ Entering main loop ]===\n");

        fired.Wait(TimeSpan.FromSeconds(3))
             .Should().BeTrue("watcher should detect file creation and read trigger");
        receivedEvent.Should().Be("GameStarted");
    }

    [Fact]
    public void OnTriggered_ResetsPosition_WhenFileShrinksBetweenScans()
    {
        File.WriteAllText(_logPath, new string('x', 500) + "\n");
        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();

        using var fired = new ManualResetEventSlim(false);
        string? receivedEvent = null;
        watcher.OnTriggered += name => { receivedEvent = name; fired.Set(); };

        // Simulate game restart: truncate and rewrite.
        File.WriteAllText(_logPath, "0.000 Script [Info]: ===[ Entering main loop ]===\n");

        fired.Wait(TimeSpan.FromSeconds(3))
             .Should().BeTrue("watcher must reset position when file shrinks");
        receivedEvent.Should().Be("GameStarted");
    }

    // ── Poll timer ──────────────────────────────────────────────────

    [Fact]
    public void PollTimer_DetectsTrigger_WhenFileSystemWatcherMightMiss()
    {
        File.WriteAllText(_logPath, string.Empty);

        // Use a poll interval of 100 ms for fast test feedback.
        using var watcher = new FileTriggerWatcher(
            _logPath, ProcessTriggers, TimeSpan.FromMilliseconds(100));
        watcher.Start();

        using var fired = new ManualResetEventSlim(false);
        watcher.OnTriggered += _ => fired.Set();

        Append("0.000 Script [Info]: ===[ Entering main loop ]===");

        fired.Wait(TimeSpan.FromSeconds(2))
             .Should().BeTrue("poll timer should catch the trigger");
    }

    // ── ScanNow ─────────────────────────────────────────────────────

    [Fact]
    public void ScanNow_ForcesImmediateScan()
    {
        File.WriteAllText(_logPath, string.Empty);
        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();

        using var fired = new ManualResetEventSlim(false);
        watcher.OnTriggered += _ => fired.Set();

        Append("0.000 Script [Info]: ===[ Entering main loop ]===");

        // Don't wait for the FSW — force a scan.
        watcher.ScanNow();

        fired.IsSet.Should().BeTrue("ScanNow should process pending content immediately");
    }

    // ── Lifecycle ───────────────────────────────────────────────────

    [Fact]
    public void Start_IsIdempotent()
    {
        File.WriteAllText(_logPath, string.Empty);
        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();
        Action second = () => watcher.Start();
        second.Should().NotThrow();
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        File.WriteAllText(_logPath, string.Empty);
        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Stop();
        Action second = () => watcher.Stop();
        second.Should().NotThrow();
    }

    [Fact]
    public void DoesNotFire_AfterStop()
    {
        File.WriteAllText(_logPath, string.Empty);
        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();
        watcher.Stop();

        bool fired = false;
        watcher.OnTriggered += _ => fired = true;

        Append("0.000 Script [Info]: ===[ Entering main loop ]===");
        Thread.Sleep(500);

        fired.Should().BeFalse("events should not fire after Stop()");
    }

    [Fact]
    public void StopThenRestart_StillDetects()
    {
        File.WriteAllText(_logPath, string.Empty);
        using var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();
        watcher.Stop();

        using var fired = new ManualResetEventSlim(false);
        watcher.OnTriggered += _ => fired.Set();

        watcher.Start();
        Append("0.000 Script [Info]: ===[ Entering main loop ]===");

        fired.Wait(TimeSpan.FromSeconds(3))
             .Should().BeTrue("watcher should detect after stop + restart");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        File.WriteAllText(_logPath, string.Empty);
        var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Start();
        watcher.Dispose();
        Action second = () => watcher.Dispose();
        second.Should().NotThrow();
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        File.WriteAllText(_logPath, string.Empty);
        var watcher = new FileTriggerWatcher(_logPath, ProcessTriggers);
        watcher.Dispose();

        Action act = () => watcher.Start();
        act.Should().Throw<ObjectDisposedException>();
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private void Append(string text)
    {
        using var fs = new FileStream(
            _logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        using var sw = new StreamWriter(fs);
        sw.WriteLine(text);
    }
}