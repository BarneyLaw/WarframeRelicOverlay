namespace WarframeRelicOverlay.Tests.OverlayApp.Detection;

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Infrastructure.OCR;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.Infrastructure.ScreenCapture;
using WarframeRelicOverlay.OverlayApp.Detection;
using Xunit;

public class OcrFallbackDetectorTests : IDisposable
{
    private readonly StubProcessTracker _processTracker = new();
    private readonly StubWindowTracker _windowTracker = new();
    private readonly StubScreenCapturer _capturer = new();
    private readonly StubOcrEngine _ocr = new();

    private readonly AppSettings _settings = new()
    {
        DetectionIntervalMs = 100,  // fast polling for tests
    };

    public void Dispose()
    {
        // Stubs have no resources to release.
    }

    private OcrFallbackDetector CreateDetector()
    {
        return new OcrFallbackDetector(
            _capturer, _ocr, _processTracker, _windowTracker, _settings);
    }

    // ────────────────────────────────────────────────────────────────
    //  Properties
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void IsDefinitive_ReturnsFalse()
    {
        using var detector = CreateDetector();
        Assert.False(detector.IsDefinitive);
    }

    // ────────────────────────────────────────────────────────────────
    //  Detection — positive polls
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void FiresRewardScreenDetected_WhenOcrFindsRewardText()
    {
        _processTracker.SimulateRunning(1234, new nint(0xBEEF));
        _windowTracker.SetBounds(new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0));
        _capturer.SetCaptureResult(CreateDummyBitmap());
        _ocr.SetResult("VOID RELIC REWARDS");

        using var detector = CreateDetector();
        using var detected = new ManualResetEventSlim(false);

        detector.RewardScreenDetected += () => detected.Set();
        detector.Start();

        Assert.True(detected.Wait(TimeSpan.FromSeconds(2)),
            "RewardScreenDetected should fire when OCR finds 'REWARD'.");
    }

    [Fact]
    public void MatchesCaseInsensitively()
    {
        _processTracker.SimulateRunning(1234, new nint(0xBEEF));
        _windowTracker.SetBounds(new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0));
        _capturer.SetCaptureResult(CreateDummyBitmap());
        _ocr.SetResult("Void Relic Rewards");  // mixed case

        using var detector = CreateDetector();
        using var detected = new ManualResetEventSlim(false);

        detector.RewardScreenDetected += () => detected.Set();
        detector.Start();

        Assert.True(detected.Wait(TimeSpan.FromSeconds(2)),
            "Should match 'Reward' case-insensitively.");
    }

    [Fact]
    public void ToleratesPartialMatch()
    {
        _processTracker.SimulateRunning(1234, new nint(0xBEEF));
        _windowTracker.SetBounds(new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0));
        _capturer.SetCaptureResult(CreateDummyBitmap());
        _ocr.SetResult("REWARD5");  // OCR artifact: 'S' misread as '5'

        using var detector = CreateDetector();
        using var detected = new ManualResetEventSlim(false);

        detector.RewardScreenDetected += () => detected.Set();
        detector.Start();

        Assert.True(detected.Wait(TimeSpan.FromSeconds(2)),
            "Should match on 'REWARD' prefix even with trailing OCR noise.");
    }

    // ────────────────────────────────────────────────────────────────
    //  Detection — negative polls
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void DoesNotFire_WhenOcrReturnsUnrelatedText()
    {
        _processTracker.SimulateRunning(1234, new nint(0xBEEF));
        _windowTracker.SetBounds(new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0));
        _capturer.SetCaptureResult(CreateDummyBitmap());
        _ocr.SetResult("MISSION COMPLETE");

        using var detector = CreateDetector();
        bool fired = false;

        detector.RewardScreenDetected += () => fired = true;
        detector.Start();

        Thread.Sleep(400);

        Assert.False(fired,
            "Should not fire for unrelated OCR text.");
    }

    [Fact]
    public void DoesNotFire_WhenProcessNotRunning()
    {
        // Process tracker returns no handle.
        _capturer.SetCaptureResult(CreateDummyBitmap());
        _ocr.SetResult("REWARDS");

        using var detector = CreateDetector();
        bool fired = false;

        detector.RewardScreenDetected += () => fired = true;
        detector.Start();

        Thread.Sleep(400);

        Assert.False(fired,
            "Should not fire when Warframe is not running.");
    }

    [Fact]
    public void DoesNotFire_WhenCaptureReturnsNull()
    {
        _processTracker.SimulateRunning(1234, new nint(0xBEEF));
        _windowTracker.SetBounds(new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0));
        _capturer.SetCaptureResult(null);
        _ocr.SetResult("REWARDS");

        using var detector = CreateDetector();
        bool fired = false;

        detector.RewardScreenDetected += () => fired = true;
        detector.Start();

        Thread.Sleep(400);

        Assert.False(fired,
            "Should not fire when screen capture fails.");
    }

    // ────────────────────────────────────────────────────────────────
    //  Exit detection
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void FiresRewardScreenExited_OnTransitionFromPositiveToNegative()
    {
        _processTracker.SimulateRunning(1234, new nint(0xBEEF));
        _windowTracker.SetBounds(new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0));
        _capturer.SetCaptureResult(CreateDummyBitmap());

        // Start with positive OCR result.
        _ocr.SetResult("REWARDS");

        using var detector = CreateDetector();
        using var detected = new ManualResetEventSlim(false);
        using var exited = new ManualResetEventSlim(false);

        detector.RewardScreenDetected += () => detected.Set();
        detector.RewardScreenExited += () => exited.Set();
        detector.Start();

        // Wait for at least one positive detection.
        Assert.True(detected.Wait(TimeSpan.FromSeconds(2)));

        // Now switch to negative.
        _ocr.SetResult("MISSION COMPLETE");

        Assert.True(exited.Wait(TimeSpan.FromSeconds(2)),
            "RewardScreenExited should fire on positive → negative transition.");
    }

    [Fact]
    public void DoesNotFireExited_WhenNeverDetected()
    {
        _processTracker.SimulateRunning(1234, new nint(0xBEEF));
        _windowTracker.SetBounds(new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0));
        _capturer.SetCaptureResult(CreateDummyBitmap());
        _ocr.SetResult("MISSION COMPLETE");  // always negative

        using var detector = CreateDetector();
        bool exitFired = false;

        detector.RewardScreenExited += () => exitFired = true;
        detector.Start();

        Thread.Sleep(400);

        Assert.False(exitFired,
            "Should not fire RewardScreenExited if there was never a positive detection.");
    }

    // ────────────────────────────────────────────────────────────────
    //  Lifecycle
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Start_IsIdempotent()
    {
        using var detector = CreateDetector();
        detector.Start();
        detector.Start();  // should not throw
    }

    [Fact]
    public void Stop_IsIdempotent()
    {
        using var detector = CreateDetector();
        detector.Stop();
        detector.Stop();  // should not throw
    }

    [Fact]
    public void DoesNotFire_AfterStop()
    {
        _processTracker.SimulateRunning(1234, new nint(0xBEEF));
        _windowTracker.SetBounds(new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0));
        _capturer.SetCaptureResult(CreateDummyBitmap());
        _ocr.SetResult("REWARDS");

        using var detector = CreateDetector();
        bool firedAfterStop = false;

        detector.Start();
        detector.Stop();

        detector.RewardScreenDetected += () => firedAfterStop = true;
        Thread.Sleep(400);

        Assert.False(firedAfterStop,
            "Should not fire after Stop().");
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        var detector = CreateDetector();
        detector.Dispose();

        Assert.Throws<ObjectDisposedException>(() => detector.Start());
    }

    [Fact]
    public void Constructor_NullCapturer_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new OcrFallbackDetector(null!, _ocr, _processTracker, _windowTracker, _settings));
    }

    [Fact]
    public void Constructor_NullOcr_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new OcrFallbackDetector(_capturer, null!, _processTracker, _windowTracker, _settings));
    }

    // ────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────

    private static Bitmap CreateDummyBitmap()
    {
        return new Bitmap(100, 20, PixelFormat.Format24bppRgb);
    }

    // ────────────────────────────────────────────────────────────────
    //  Stubs
    // ────────────────────────────────────────────────────────────────

    private sealed class StubProcessTracker : IProcessTracker
    {
        private int? _pid;
        private nint _handle;

        public bool IsRunning => _pid.HasValue;
        public int? ProcessId => _pid;
        public nint MainWindowHandle => _handle;

        public event Action<int>? Started;
        public event Action<int>? Stopped;

        public void SimulateRunning(int pid, nint handle)
        {
            _pid = pid;
            _handle = handle;
        }

        public void Start() { }
        public void Dispose() { }

        // Suppress unused-event warnings — stubs don't fire them.
        internal void SuppressWarnings()
        {
            Started?.Invoke(0);
            Stopped?.Invoke(0);
        }
    }

    private sealed class StubWindowTracker : IWindowTracker
    {
        private WindowSnapshot? _bounds;

        public void SetBounds(WindowSnapshot bounds) => _bounds = bounds;

        public WindowSnapshot? TryGetBounds(nint windowHandle) => _bounds;
        public bool IsForeground(nint windowHandle) => true;
    }

    private sealed class StubScreenCapturer : IScreenCapturer
    {
        private Bitmap? _result;

        public void SetCaptureResult(Bitmap? result) => _result = result;

        public Bitmap? CaptureWindow(WindowSnapshot window) => _result;

        public Bitmap? CaptureRegion(Rectangle physicalRegion)
        {
            // Return a copy so each call gets its own disposable bitmap.
            if (_result is null) return null;
            return new Bitmap(_result);
        }
    }

    private sealed class StubOcrEngine : IOcrEngine
    {
        private string _text = "";

        public void SetResult(string text) => _text = text;

        public string Recognize(Bitmap image) => _text;
    }
}