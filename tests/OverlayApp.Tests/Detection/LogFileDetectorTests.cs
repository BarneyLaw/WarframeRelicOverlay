namespace WarframeRelicOverlay.Tests.OverlayApp.Detection;

using System;
using System.IO;
using System.Threading;
using WarframeRelicOverlay.OverlayApp.Detection;
using Xunit;

public class LogFileDetectorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logPath;

    public LogFileDetectorTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"LogDetectorTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _logPath = Path.Combine(_tempDir, "EE.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }

    // ────────────────────────────────────────────────────────────────
    //  Construction
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LogFileDetector(""));
    }

    [Fact]
    public void Constructor_WhitespacePath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LogFileDetector("   "));
    }

    [Fact]
    public void IsDefinitive_ReturnsTrue()
    {
        using var detector = new LogFileDetector(_logPath);
        Assert.True(detector.IsDefinitive);
    }

    // ────────────────────────────────────────────────────────────────
    //  Detection — trigger phrase
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void DetectsRewardTrigger_WhenPhraseAppended()
    {
        File.WriteAllText(_logPath, "");

        using var detector = new LogFileDetector(_logPath);
        using var detected = new ManualResetEventSlim(false);

        detector.RewardScreenDetected += () => detected.Set();
        detector.Start();

        File.AppendAllText(_logPath, "12345.678 Sys [Info]: GotRewards\n");

        Assert.True(detected.Wait(TimeSpan.FromSeconds(2)),
            "RewardScreenDetected should fire when 'GotRewards' is appended.");
    }

    [Theory]
    [InlineData("Got rewards")]
    [InlineData("got rewards")]
    [InlineData("GotRewards")]
    public void DetectsKnownRewardTriggerVariants(string triggerText)
    {
        File.WriteAllText(_logPath, "");

        using var detector = new LogFileDetector(_logPath);
        using var detected = new ManualResetEventSlim(false);

        detector.RewardScreenDetected += () => detected.Set();
        detector.Start();

        File.AppendAllText(_logPath, $"12345.678 Sys [Info]: {triggerText}\n");

        Assert.True(detected.Wait(TimeSpan.FromSeconds(2)),
            $"RewardScreenDetected should fire when '{triggerText}' is appended.");
    }

    [Fact]
    public void IgnoresUnrelatedContent()
    {
        File.WriteAllText(_logPath, "");

        using var detector = new LogFileDetector(_logPath);
        bool fired = false;

        detector.RewardScreenDetected += () => fired = true;
        detector.Start();

        File.AppendAllText(_logPath, "12345.678 Sys [Info]: Loading level\n");
        File.AppendAllText(_logPath, "12345.700 Sys [Info]: Player joined\n");

        Thread.Sleep(500);

        Assert.False(fired,
            "RewardScreenDetected should not fire for unrelated log content.");
    }

    [Fact]
    public void IgnoresPreExistingContent()
    {
        // Trigger phrase exists BEFORE Start() — should not fire.
        File.WriteAllText(_logPath, "GotRewards\n");

        using var detector = new LogFileDetector(_logPath);
        bool fired = false;

        detector.RewardScreenDetected += () => fired = true;
        detector.Start();

        Thread.Sleep(500);

        Assert.False(fired,
            "RewardScreenDetected should not fire for content that " +
            "existed before Start() was called.");
    }

    // ────────────────────────────────────────────────────────────────
    //  File truncation (game restart)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void HandlesFileTruncation_DetectsNewTrigger()
    {
        File.WriteAllText(_logPath, "old session content here\n");

        using var detector = new LogFileDetector(_logPath);
        using var detected = new ManualResetEventSlim(false);

        detector.RewardScreenDetected += () => detected.Set();
        detector.Start();

        // Truncate (simulates Warframe launch).
        File.WriteAllText(_logPath, "");
        Thread.Sleep(100);

        File.AppendAllText(_logPath, "GotRewards\n");

        Assert.True(detected.Wait(TimeSpan.FromSeconds(2)),
            "Should detect trigger after file truncation.");
    }

    // ────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Start_IsIdempotent()
    {
        File.WriteAllText(_logPath, "");

        using var detector = new LogFileDetector(_logPath);

        detector.Start();
        detector.Start();  // should not throw
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        using var detector = new LogFileDetector(_logPath);

        detector.Stop();
        detector.Stop();  // should not throw
    }

    [Fact]
    public void StopThenRestart_StillDetects()
    {
        File.WriteAllText(_logPath, "");

        using var detector = new LogFileDetector(_logPath);
        using var detected = new ManualResetEventSlim(false);

        detector.RewardScreenDetected += () => detected.Set();

        detector.Start();
        detector.Stop();

        detector.Start();
        File.AppendAllText(_logPath, "GotRewards\n");

        Assert.True(detected.Wait(TimeSpan.FromSeconds(2)),
            "Should detect after stop + restart.");
    }

    [Fact]
    public void DoesNotFireAfterStop()
    {
        File.WriteAllText(_logPath, "");

        using var detector = new LogFileDetector(_logPath);
        bool fired = false;

        detector.RewardScreenDetected += () => fired = true;
        detector.Start();
        detector.Stop();

        File.AppendAllText(_logPath, "GotRewards\n");
        Thread.Sleep(500);

        Assert.False(fired,
            "RewardScreenDetected should not fire after Stop().");
    }

    [Fact]
    public void Dispose_StopsDetection()
    {
        File.WriteAllText(_logPath, "");

        var detector = new LogFileDetector(_logPath);
        bool fired = false;

        detector.RewardScreenDetected += () => fired = true;
        detector.Start();
        detector.Dispose();

        File.AppendAllText(_logPath, "GotRewards\n");
        Thread.Sleep(500);

        Assert.False(fired,
            "RewardScreenDetected should not fire after Dispose().");
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        var detector = new LogFileDetector(_logPath);
        detector.Dispose();

        Assert.Throws<ObjectDisposedException>(() => detector.Start());
    }

    // ────────────────────────────────────────────────────────────────
    //  Missing file (but directory exists)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Start_WhenFileDoesNotExist_DoesNotThrow()
    {
        string missingPath = Path.Combine(_tempDir, "nonexistent.log");

        using var detector = new LogFileDetector(missingPath);

        // Should not throw — the file may appear later.
        detector.Start();
    }

    [Fact]
    public void DetectsWhenFileCreatedAfterStart()
    {
        string futurePath = Path.Combine(_tempDir, "future.log");

        using var detector = new LogFileDetector(futurePath);
        using var detected = new ManualResetEventSlim(false);

        detector.RewardScreenDetected += () => detected.Set();
        detector.Start();

        File.WriteAllText(futurePath, "GotRewards\n");

        Assert.True(detected.Wait(TimeSpan.FromSeconds(2)),
            "Should detect trigger in a file created after Start().");
    }

    // ────────────────────────────────────────────────────────────────
    //  Does not fire for process lifecycle triggers
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void IgnoresProcessLifecyclePhrases()
    {
        // LogFileDetector only cares about "GotRewards", not the
        // process start/stop phrases that WarframeProcessTracker
        // handles via its own FileTriggerWatcher instance.
        File.WriteAllText(_logPath, "");

        using var detector = new LogFileDetector(_logPath);
        bool fired = false;

        detector.RewardScreenDetected += () => fired = true;
        detector.Start();

        File.AppendAllText(_logPath,
            "===[ Entering main loop ]===\n" +
            "===[ Exiting main loop ]===\n");

        Thread.Sleep(500);

        Assert.False(fired,
            "LogFileDetector should not react to process lifecycle phrases.");
    }
}
