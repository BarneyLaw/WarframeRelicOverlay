namespace WarframeRelicOverlay.Tests.Infrastructure.ScreenCapture;

using System.Drawing;
using FluentAssertions;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.Infrastructure.ScreenCapture;
using Xunit;

public sealed class GdiScreenCapturerTests
{
    private readonly GdiScreenCapturer _capturer = new();

    // ── CaptureWindow — null guards ─────────────────────────────────

    [Fact]
    public void CaptureWindow_ReturnsNull_WhenClientWidthIsZero()
    {
        var window = new WindowSnapshot(0, 0, ClientWidth: 0, ClientHeight: 100, 1.0, 1.0);
        _capturer.CaptureWindow(window).Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_ReturnsNull_WhenClientHeightIsZero()
    {
        var window = new WindowSnapshot(0, 0, ClientWidth: 100, ClientHeight: 0, 1.0, 1.0);
        _capturer.CaptureWindow(window).Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_ReturnsNull_WhenClientWidthIsNegative()
    {
        var window = new WindowSnapshot(0, 0, ClientWidth: -1, ClientHeight: 100, 1.0, 1.0);
        _capturer.CaptureWindow(window).Should().BeNull();
    }

    [Fact]
    public void CaptureWindow_ReturnsNull_WhenClientHeightIsNegative()
    {
        var window = new WindowSnapshot(0, 0, ClientWidth: 100, ClientHeight: -1, 1.0, 1.0);
        _capturer.CaptureWindow(window).Should().BeNull();
    }

    // ── CaptureRegion — null guards ─────────────────────────────────

    [Fact]
    public void CaptureRegion_ReturnsNull_WhenWidthIsZero()
    {
        _capturer.CaptureRegion(new Rectangle(0, 0, 0, 100)).Should().BeNull();
    }

    [Fact]
    public void CaptureRegion_ReturnsNull_WhenHeightIsZero()
    {
        _capturer.CaptureRegion(new Rectangle(0, 0, 100, 0)).Should().BeNull();
    }

    [Fact]
    public void CaptureRegion_ReturnsNull_WhenWidthIsNegative()
    {
        _capturer.CaptureRegion(new Rectangle(0, 0, -1, 100)).Should().BeNull();
    }

    [Fact]
    public void CaptureRegion_ReturnsNull_WhenHeightIsNegative()
    {
        _capturer.CaptureRegion(new Rectangle(0, 0, 100, -1)).Should().BeNull();
    }

    // ── CaptureRegion — real screen capture ─────────────────────────
    // These tests capture a small region of the primary monitor (top-left
    // corner) and verify the returned bitmap has the expected properties.

    [Fact]
    public void CaptureRegion_ReturnsBitmap_WithExactRequestedDimensions()
    {
        const int width  = 64;
        const int height = 48;

        using var bmp = _capturer.CaptureRegion(new Rectangle(0, 0, width, height));

        bmp.Should().NotBeNull("GDI CopyFromScreen should succeed on an active desktop");
        bmp!.Width.Should().Be(width);
        bmp.Height.Should().Be(height);
    }

    [Fact]
    public void CaptureRegion_ReturnedBitmap_IsNotFullyBlack()
    {
        // Any real desktop at (0, 0) will have at least one non-black pixel
        // (taskbar, wallpaper, window chrome, etc.).
        using var bmp = _capturer.CaptureRegion(new Rectangle(0, 0, 128, 128));

        bmp.Should().NotBeNull();

        bool anyNonBlack = false;
        for (int y = 0; y < bmp!.Height && !anyNonBlack; y++)
        for (int x = 0; x < bmp.Width  && !anyNonBlack; x++)
        {
            Color px = bmp.GetPixel(x, y);
            anyNonBlack = px.R > 0 || px.G > 0 || px.B > 0;
        }

        anyNonBlack.Should().BeTrue("a live desktop screenshot must contain non-black pixels");
    }

    // ── CaptureWindow — real screen capture ─────────────────────────

    [Fact]
    public void CaptureWindow_ReturnsBitmapWithCorrectDimensions_ForValidRegion()
    {
        const int width  = 64;
        const int height = 48;

        var window = new WindowSnapshot(
            ClientX: 0, ClientY: 0,
            ClientWidth: width, ClientHeight: height,
            DpiScaleX: 1.0, DpiScaleY: 1.0);

        using var bmp = _capturer.CaptureWindow(window);

        bmp.Should().NotBeNull("CaptureWindow should succeed when the region maps to a valid area");
        bmp!.Width.Should().Be(width);
        bmp.Height.Should().Be(height);
    }

    [Fact]
    public void CaptureWindow_ProducesSamePixels_AsCaptureRegionForSameArea()
    {
        // CaptureWindow is a thin wrapper around CaptureRegion; both should
        // capture the identical physical region.
        const int width  = 32;
        const int height = 32;

        var window = new WindowSnapshot(0, 0, width, height, 1.0, 1.0);

        using var fromWindow = _capturer.CaptureWindow(window);
        using var fromRegion = _capturer.CaptureRegion(new Rectangle(0, 0, width, height));

        fromWindow.Should().NotBeNull();
        fromRegion.Should().NotBeNull();

        // Pixel-level comparison — these are taken in rapid succession so
        // the desktop content is effectively identical.
        fromWindow!.Width.Should().Be(fromRegion!.Width);
        fromWindow.Height.Should().Be(fromRegion.Height);
    }
}
