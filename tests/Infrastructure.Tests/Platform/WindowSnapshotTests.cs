namespace WarframeRelicOverlay.Tests.Infrastructure.Platform;

using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.Platform;
using Xunit;

public sealed class WindowSnapshotTests
{
    // ── IsValid ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(1920, 1080, true)]   // standard 1080p
    [InlineData(320,  240,  true)]   // minimum valid dimensions (inclusive)
    [InlineData(319,  240,  false)]  // one pixel below width threshold
    [InlineData(320,  239,  false)]  // one pixel below height threshold
    [InlineData(100,  100,  false)]  // clearly too small
    [InlineData(0,    0,    false)]  // zero area
    public void IsValid_ReflectsMinimumDimensions(int width, int height, bool expected)
    {
        var snap = new WindowSnapshot(0, 0, width, height, 1.0, 1.0);
        snap.IsValid.Should().Be(expected);
    }

    // ── Logical dimensions ──────────────────────────────────────────

    [Fact]
    public void LogicalDimensions_AreEqualToPhysical_AtUnitDpi()
    {
        var snap = new WindowSnapshot(50, 30, 1920, 1080, 1.0, 1.0);

        snap.LogicalWidth.Should().Be(1920);
        snap.LogicalHeight.Should().Be(1080);
        snap.LogicalX.Should().Be(50);
        snap.LogicalY.Should().Be(30);
    }

    [Fact]
    public void LogicalWidth_DividesByDpiScaleX()
    {
        var snap = new WindowSnapshot(0, 0, 3840, 2160, 2.0, 2.0);
        snap.LogicalWidth.Should().BeApproximately(1920.0, precision: 0.001);
    }

    [Fact]
    public void LogicalHeight_DividesByDpiScaleY()
    {
        var snap = new WindowSnapshot(0, 0, 3840, 2160, 2.0, 2.0);
        snap.LogicalHeight.Should().BeApproximately(1080.0, precision: 0.001);
    }

    [Fact]
    public void LogicalX_DividesByDpiScaleX()
    {
        var snap = new WindowSnapshot(200, 0, 1920, 1080, 1.25, 1.0);
        snap.LogicalX.Should().BeApproximately(160.0, precision: 0.001);
    }

    [Fact]
    public void LogicalY_DividesByDpiScaleY()
    {
        var snap = new WindowSnapshot(0, 100, 1920, 1080, 1.0, 1.25);
        snap.LogicalY.Should().BeApproximately(80.0, precision: 0.001);
    }

    [Theory]
    [InlineData(3840, 2160, 2.0,  2.0,  1920, 1080)]
    [InlineData(2560, 1440, 1.5,  1.5,  1706.667, 960)]
    [InlineData(1920, 1080, 1.25, 1.25, 1536, 864)]
    public void LogicalDimensions_ScaleCorrectly(
        int physW, int physH, double dpiX, double dpiY,
        double expectedW, double expectedH)
    {
        var snap = new WindowSnapshot(0, 0, physW, physH, dpiX, dpiY);
        snap.LogicalWidth.Should().BeApproximately(expectedW, precision: 0.5);
        snap.LogicalHeight.Should().BeApproximately(expectedH, precision: 0.5);
    }

    // ── AspectRatio ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1920, 1080, 16.0 / 9.0)]
    [InlineData(2560, 1080, 2560.0 / 1080.0)]
    [InlineData(1280, 1024, 1280.0 / 1024.0)]
    [InlineData(320,  240,  320.0  / 240.0)]
    public void AspectRatio_ComputedCorrectly(int width, int height, double expected)
    {
        var snap = new WindowSnapshot(0, 0, width, height, 1.0, 1.0);
        snap.AspectRatio.Should().BeApproximately(expected, precision: 0.0001);
    }

    [Fact]
    public void AspectRatio_ReturnsZero_WhenHeightIsZero()
    {
        var snap = new WindowSnapshot(0, 0, 1920, 0, 1.0, 1.0);
        snap.AspectRatio.Should().Be(0);
    }

    // ── Value equality (record struct) ──────────────────────────────

    [Fact]
    public void TwoSnapshots_AreEqual_WhenAllFieldsMatch()
    {
        var a = new WindowSnapshot(10, 20, 1920, 1080, 1.0, 1.0);
        var b = new WindowSnapshot(10, 20, 1920, 1080, 1.0, 1.0);
        a.Should().Be(b);
    }

    [Fact]
    public void TwoSnapshots_AreNotEqual_WhenAnyFieldDiffers()
    {
        var a = new WindowSnapshot(10, 20, 1920, 1080, 1.0, 1.0);
        var b = new WindowSnapshot(10, 20, 2560, 1440, 1.0, 1.0);
        a.Should().NotBe(b);
    }
}
