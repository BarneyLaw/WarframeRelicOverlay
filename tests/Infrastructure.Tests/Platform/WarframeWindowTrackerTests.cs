namespace WarframeRelicOverlay.Tests.Infrastructure.Platform;

using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.Platform;
using Xunit;

/// <summary>
/// Unit tests for <see cref="WarframeWindowTracker"/>.
///
/// The tracker delegates directly to Win32 P/Invoke, so tests are limited
/// to sentinel-value inputs that bypass the native call (zero handle) and
/// to invalid-handle inputs where the Win32 call returns false, causing the
/// tracker to return null/false gracefully.
///
/// Full integration tests (real window geometry, DPI) require a live window
/// and belong in Integration.Tests.
/// </summary>
public sealed class WarframeWindowTrackerTests
{
    private readonly WarframeWindowTracker _tracker = new();

    // ── TryGetBounds ────────────────────────────────────────────────

    [Fact]
    public void TryGetBounds_ReturnsNull_ForZeroHandle()
    {
        // The method returns null immediately without touching Win32.
        WindowSnapshot? result = _tracker.TryGetBounds(nint.Zero);
        result.Should().BeNull();
    }

    [Fact]
    public void TryGetBounds_ReturnsNull_ForArbitraryInvalidHandle()
    {
        // GetClientRect will fail on a garbage handle; tracker must not throw.
        var fakeHandle = new nint(0x00ABCDEF);
        WindowSnapshot? result = _tracker.TryGetBounds(fakeHandle);
        result.Should().BeNull();
    }

    // ── IsForeground ────────────────────────────────────────────────

    [Fact]
    public void IsForeground_ReturnsFalse_ForZeroHandle()
    {
        // Zero handle is an early-exit sentinel — always false.
        bool result = _tracker.IsForeground(nint.Zero);
        result.Should().BeFalse();
    }

    [Fact]
    public void IsForeground_ReturnsFalse_WhenHandleIsNotForeground()
    {
        // An arbitrary non-zero handle that is almost certainly not the
        // current foreground window.
        var notForeground = new nint(0x00ABCDEF);
        bool result = _tracker.IsForeground(notForeground);
        result.Should().BeFalse();
    }
}
