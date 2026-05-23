namespace WarframeRelicOverlay.Tests.Infrastructure.Platform;

using System.Diagnostics;
using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.Platform;
using Xunit;

public sealed class ProcessPollingTests
{
    // ── AttachSilently ──────────────────────────────────────────────

    [Fact]
    public void AttachSilently_SetsPid_WithoutRaisingStartedEvent()
    {
        var polling = new ProcessPolling("Warframe.x64");
        bool startedFired = false;
        polling.ProcessStarted += _ => startedFired = true;

        polling.AttachSilently(1234);

        polling.TrackedPid.Should().Be(1234);
        startedFired.Should().BeFalse();
    }

    // ── DetachSilently ──────────────────────────────────────────────

    [Fact]
    public void DetachSilently_ClearsPid_WithoutRaisingStoppedEvent()
    {
        var polling = new ProcessPolling("Warframe.x64");
        bool stoppedFired = false;
        polling.ProcessStopped += _ => stoppedFired = true;

        polling.AttachSilently(1234);
        polling.DetachSilently();

        polling.TrackedPid.Should().BeNull();
        stoppedFired.Should().BeFalse();
    }

    // ── IsProcessRunning ────────────────────────────────────────────

    [Fact]
    public void IsProcessRunning_ReturnsFalse_WhenNoPidTracked()
    {
        var polling = new ProcessPolling("Warframe.x64");
        polling.IsProcessRunning().Should().BeFalse();
    }

    [Fact]
    public void IsProcessRunning_ReturnsFalse_WhenPidDoesNotExist()
    {
        var polling = new ProcessPolling("Warframe.x64");
        polling.AttachSilently(int.MaxValue); // a PID that will never be allocated

        polling.IsProcessRunning().Should().BeFalse();
    }

    [Fact]
    public void IsProcessRunning_ReturnsTrue_WhenTrackingLiveProcess()
    {
        using var self = Process.GetCurrentProcess();
        var polling = new ProcessPolling("Warframe.x64");
        polling.AttachSilently(self.Id);

        polling.IsProcessRunning().Should().BeTrue();
    }

    // ── Poll — no process ───────────────────────────────────────────

    [Fact]
    public void Poll_ReturnsFalse_WhenNamedProcessDoesNotExist()
    {
        var polling = new ProcessPolling("__process_that_definitely_does_not_exist__");

        bool result = polling.Poll();

        result.Should().BeFalse();
        polling.TrackedPid.Should().BeNull();
    }

    [Fact]
    public void Poll_DoesNotRaiseStarted_WhenNoProcessFound()
    {
        var polling = new ProcessPolling("__process_that_definitely_does_not_exist__");
        bool fired = false;
        polling.ProcessStarted += _ => fired = true;

        polling.Poll();

        fired.Should().BeFalse();
    }

    // ── Poll — process found ────────────────────────────────────────

    [Fact]
    public void Poll_ReturnsTrue_WhenProcessExists()
    {
        string name = Process.GetCurrentProcess().ProcessName;
        var polling = new ProcessPolling(name);

        bool result = polling.Poll();

        result.Should().BeTrue();
    }

    [Fact]
    public void Poll_RaisesProcessStarted_WithPositivePid_WhenProcessFound()
    {
        string name = Process.GetCurrentProcess().ProcessName;
        var polling = new ProcessPolling(name);

        int? startedPid = null;
        polling.ProcessStarted += pid => startedPid = pid;

        polling.Poll();

        startedPid.Should().NotBeNull();
        startedPid.Should().BePositive();
    }

    [Fact]
    public void Poll_SetsTrackedPid_ToReportedPid_WhenProcessFound()
    {
        string name = Process.GetCurrentProcess().ProcessName;
        var polling = new ProcessPolling(name);

        int? startedPid = null;
        polling.ProcessStarted += pid => startedPid = pid;

        polling.Poll();

        polling.TrackedPid.Should().Be(startedPid);
    }

    [Fact]
    public void Poll_DoesNotRaiseStartedTwice_WhenCalledConsecutivelyWithSameProcess()
    {
        string name = Process.GetCurrentProcess().ProcessName;
        var polling = new ProcessPolling(name);

        int startedCount = 0;
        polling.ProcessStarted += _ => startedCount++;

        polling.Poll();
        polling.Poll(); // process still alive — should be a no-op for events

        startedCount.Should().Be(1);
    }

    // ── Poll — process stopped ──────────────────────────────────────

    [Fact]
    public void Poll_RaisesProcessStopped_WhenTrackedPidNoLongerExists()
    {
        // AttachSilently bypasses ProcessStarted; we simulate a process that
        // was previously found but has since exited (PID int.MaxValue never exists).
        var polling = new ProcessPolling("Warframe.x64");
        polling.AttachSilently(int.MaxValue);

        int? stoppedPid = null;
        polling.ProcessStopped += pid => stoppedPid = pid;

        polling.Poll();

        stoppedPid.Should().Be(int.MaxValue);
    }

    [Fact]
    public void Poll_ClearsTrackedPid_AfterDetectingStop()
    {
        var polling = new ProcessPolling("Warframe.x64");
        polling.AttachSilently(int.MaxValue);

        polling.Poll();

        polling.TrackedPid.Should().BeNull();
    }

    [Fact]
    public void Poll_ReturnsFalse_AfterDetectingStop()
    {
        var polling = new ProcessPolling("Warframe.x64");
        polling.AttachSilently(int.MaxValue);

        bool result = polling.Poll();

        result.Should().BeFalse();
    }
}
