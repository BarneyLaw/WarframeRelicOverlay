namespace WarframeRelicOverlay.Tests.Integration;
 
using System.Diagnostics;
using System.Drawing;
using System.Text;
using FluentAssertions;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Domain.Matching;
using WarframeRelicOverlay.Domain.Pricing;
using WarframeRelicOverlay.Infrastructure.Market;
using WarframeRelicOverlay.Infrastructure.OCR;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.Infrastructure.RewardData;
using WarframeRelicOverlay.Infrastructure.ScreenCapture;
using WarframeRelicOverlay.OverlayApp.Detection;
using WarframeRelicOverlay.OverlayApp.Layout;
using WarframeRelicOverlay.OverlayApp.Pipeline;
using WarframeRelicOverlay.OverlayApp.StateMachine;
using Xunit;
using Xunit.Abstractions;

// ─────────────────────────────────────────────────────────────────────────────
// END-TO-END INTEGRATION TEST
//
// Proves the full pipeline works from EE.log trigger to priced output:
//
//   1. Create a temp EE.log file.
//   2. Start a real LogFileDetector watching it.
//   3. Append "GotRewards" — the detector fires.
//   4. The OverlayCoordinator reacts: enters Pricing state.
//   5. The real pipeline runs against whole_screen.png:
//        IntensityProfileDetector → TesseractOcrEngine → FuzzyRewardMatcher
//        → MarketSlugConverter → WarframeMarketClient (live API)
//   6. Results are written to test_output/pipeline_report.txt for
//      human verification — no hardcoded expected values.
//
// Run with:  dotnet test --filter "Category=Integration"
// ─────────────────────────────────────────────────────────────────────────────

[Trait("Category", "Integration")]
public sealed class EndToEndPipelineTests : IDisposable
{
    //  Paths
 
    private static readonly string BaseDir = AppContext.BaseDirectory;
    private static readonly string TestImagesDir = Path.Combine(BaseDir, "test-images");
    private static readonly string TessDataDir = Path.Combine(BaseDir, "tessdata");
    private static readonly string DataDir = Path.Combine(BaseDir, "data");
    private static readonly string OutputDir = Path.Combine(BaseDir, "test_output");
    private static readonly string ReportPath = Path.Combine(OutputDir, "pipeline_report.txt");
 
    private readonly ITestOutputHelper _testOutput;
    private readonly string _tempDir;
    private readonly string _eeLogPath;

    public EndToEndPipelineTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
 
        // Create a temp directory for this test run
        _tempDir = Path.Combine(Path.GetTempPath(), $"WfOverlayTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
 
        // Create a temp EE.log file
        _eeLogPath = Path.Combine(_tempDir, "EE.log");
        File.WriteAllText(_eeLogPath, ""); // start with empty log;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (Exception ex)
        {
            _testOutput.WriteLine($"Failed to clean up temp directory: {ex}");
        }
    }

    /// <summary>
    /// Return whole_screen.png instead of a real GDI capture.
    /// <summary>
    private class TestScreenCapturer : IScreenCapturer
    {
        private readonly string _imagePath;
        public int CaptureCount { get; private set; }
 
        public TestScreenCapturer(string imagePath)
        {
            _imagePath = imagePath;
        }
 
        public Bitmap? CaptureWindow(WindowSnapshot window)
        {
            CaptureCount++;
            return File.Exists(_imagePath) ? new Bitmap(_imagePath) : null;
        }
 
        public Bitmap? CaptureRegion(Rectangle physicalRegion) => null;

    }

        /// <summary>
    /// Fake process tracker — always "running" with a valid handle.
    /// </summary>
    private sealed class StubProcessTracker : IProcessTracker
    {
        public bool IsRunning => true;
        public int? ProcessId => 9999;
        public nint MainWindowHandle => 1;
        public event Action<int>? Started;
        public event Action<int>? Stopped;
        public void Start() { }
        public void SimulateStart() => Started?.Invoke(9999);
        public void Dispose() { }
    }
 
    /// <summary>
    /// Returns a snapshot matching the test image dimensions.
    /// </summary>
    private sealed class StubWindowTracker : IWindowTracker
    {
        private readonly WindowSnapshot _snapshot;
        public StubWindowTracker(int width, int height)
        {
            _snapshot = new WindowSnapshot(0, 0, width, height, 1.0, 1.0);
        }
        public WindowSnapshot? TryGetBounds(nint windowHandle) => _snapshot;
        public bool IsForeground(nint windowHandle) => true;
    }

    /// <summary>
    /// Adapts <see cref="LogFileDetector"/> (which implements
    /// <see cref="IRewardScreenDetector"/>) to the
    /// <see cref="IRewardDetector"/> interface expected by
    /// <see cref="OverlayCoordinator"/>.
    /// </summary>
    private sealed class LogDetectorAdapter : IRewardDetector
    {
        private readonly LogFileDetector _inner;
 
        public event Action? RewardDetected;
        public event Action? RewardLost;       // never fires for EE.log
        public event Action? RewardScreenExited;
 
        public LogDetectorAdapter(LogFileDetector inner)
        {
            _inner = inner;
            _inner.RewardScreenDetected += () => RewardDetected?.Invoke();
            _inner.RewardScreenExited += () => RewardScreenExited?.Invoke();
        }
 
        public void Start() => _inner.Start();
        public void Stop() => _inner.Stop();
        public void Dispose() => _inner.Dispose();
    }


    /// <summary>
    /// Records pipeline output and signals completion so the test
    /// can synchronize with the async pipeline.
    /// </summary>
    private sealed class RecordingOutput : IOverlayOutput
    {
        public readonly ManualResetEventSlim PricesReady = new(false);
        public PipelineResult? CapturedResult { get; private set; }
        public bool LoadingWasShown { get; private set; }
 
        public void ShowPrices(PipelineResult result)
        {
            CapturedResult = result;
            PricesReady.Set();
        }
        public void ClearPrices() { }
        public void ShowLoading() { LoadingWasShown = true; }
        public void HideLoading() { }
    }

    // TEST BEGINS

    [Fact]
    public void FullPipeline_EELogTrigger_ProducesReport()
    {
        // Precondition checks
 
        string imagePath = Path.Combine(TestImagesDir, "whole_screen.png");
        File.Exists(imagePath).Should().BeTrue(
            $"test image not found at {imagePath}");
        Directory.Exists(TessDataDir).Should().BeTrue(
            $"tessdata not found at {TessDataDir}");
 
        string itemsJsonPath = Path.Combine(DataDir, "items.json");
        File.Exists(itemsJsonPath).Should().BeTrue(
            $"items.json not found at {itemsJsonPath}");
 
        // Wire up real components
 
        // Load the test image once to get dimensions for the window snapshot.
        int imgWidth, imgHeight;
        using (var probe = new Bitmap(imagePath))
        {
            imgWidth = probe.Width;
            imgHeight = probe.Height;
        }
 
        var settings = new AppSettings
        {
            DetectionMode = "EELog",
            EeLogPathOverride = _eeLogPath,
            StabilizationDelayMs = 100, // short delay — image is already stable
            PriceCacheTtlMinutes = 5,
        };
 
        // Infrastructure — all real implementations
        var capturer = new TestScreenCapturer(imagePath);
        var layoutDetector = new IntensityProfileDetector();
        using var ocrEngine = new TesseractOcrEngine(TessDataDir, poolSize: 4);
        var rewardRepo = new JsonRewardRepository(itemsJsonPath);
        var matcher = new FuzzyRewardMatcher(rewardRepo);
 
        using var http = new HttpClient
        {
            BaseAddress = new Uri("https://api.warframe.market/v2/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.Add("User-Agent", "WarframeRelicOverlay/1.0-integration-test");
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        http.DefaultRequestHeaders.Add("Platform", "pc");
        http.DefaultRequestHeaders.Add("Language", "en");
 
        var marketClient = new WarframeMarketClient(http);
        var priceCache = new RewardPriceCache(marketClient, TimeSpan.FromMinutes(5));
 
        // Pipeline — real
        var pipeline = new RewardPricingPipeline(
            capturer, layoutDetector, ocrEngine, matcher, priceCache);
 
        // Detection — real LogFileDetector on the temp EE.log
        var logDetector = new LogFileDetector(_eeLogPath);
        var detectorAdapter = new LogDetectorAdapter(logDetector);
 
        // Coordinator wiring
        var stateMachine = new OverlayStateMachine();
        var processTracker = new StubProcessTracker();
        var windowTracker = new StubWindowTracker(imgWidth, imgHeight);
        var output = new RecordingOutput();
 
        using var coordinator = new OverlayCoordinator(
            stateMachine, processTracker, windowTracker,
            detectorAdapter, pipeline, output, settings);

        // Start the detector (which starts the whole system)
        var totalStopwatch = Stopwatch.StartNew();
        coordinator.Start();
 
        // Simulate Warframe being detected as running.
        processTracker.SimulateStart();
        stateMachine.Current.Should().Be(OverlayState.Tracking,
            "after Warframe starts, the state machine should be Tracking");

                // ── Inject the EE.log trigger ────────────────────────────
 
        // Append the trigger phrase. The LogFileDetector polls every
        // ~200ms, so the event should fire within a few hundred ms.
        File.AppendAllText(_eeLogPath,
            $"[{DateTime.Now:HH:mm:ss}] Script [Info]: GotRewards\n");
 
        _testOutput.WriteLine("[Test] Wrote 'GotRewards' to temp EE.log.");
 
        // ── Wait for pipeline completion ─────────────────────────
 
        bool completed = output.PricesReady.Wait(TimeSpan.FromSeconds(30));
        totalStopwatch.Stop();
 
        // ── Structural assertions (minimal — human verifies content) ──
 
        completed.Should().BeTrue(
            "the pipeline should complete within 30 seconds");
 
        output.LoadingWasShown.Should().BeTrue(
            "the coordinator should have shown the loading indicator");
 
        output.CapturedResult.Should().NotBeNull(
            "the output should have received a PipelineResult");
 
        var result = output.CapturedResult!;
        result.HasCards.Should().BeTrue(
            "the pipeline should detect at least one reward card");
 
        capturer.CaptureCount.Should().BeGreaterThanOrEqualTo(1,
            "the pipeline should have captured the screen at least once");
 
        stateMachine.Current.Should().Be(OverlayState.Displaying,
            "after pricing completes, state should be Displaying");
 
        // ── Generate human-readable report ───────────────────────
 
        Directory.CreateDirectory(OutputDir);
        var report = BuildReport(result, totalStopwatch.Elapsed, imgWidth, imgHeight);
 
        File.WriteAllText(ReportPath, report);
        _testOutput.WriteLine(report);
        _testOutput.WriteLine($"\nReport written to: {ReportPath}");
 
        File.Exists(ReportPath).Should().BeTrue();
    }


        // ── Report builder ────────────────────────────────────────────
 
    private static string BuildReport(
        PipelineResult result, TimeSpan totalElapsed, int imgW, int imgH)
    {
        var sb = new StringBuilder();
        sb.AppendLine("╔══════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║     WARFRAME RELIC OVERLAY — END-TO-END PIPELINE REPORT     ║");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");
        sb.AppendLine();
        sb.AppendLine($"  Generated:          {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  Screenshot:         whole_screen.png ({imgW}×{imgH})");
        sb.AppendLine($"  Cards detected:     {result.Cards.Count}");
        sb.AppendLine($"  Pipeline time:      {result.Elapsed.TotalMilliseconds:F0} ms");
        sb.AppendLine($"  Total test time:    {totalElapsed.TotalMilliseconds:F0} ms  (includes EE.log poll + stabilization)");
        sb.AppendLine($"  All matched:        {result.AllMatched}");
        sb.AppendLine();
        sb.AppendLine("──────────────────────────────────────────────────────────────");
        sb.AppendLine();
 
        for (int i = 0; i < result.Cards.Count; i++)
        {
            var card = result.Cards[i];
            sb.AppendLine($"  ┌─ Card {i} ─────────────────────────────────────────────");
            sb.AppendLine($"  │  Bounds:        x={card.BoundsInWindow.X}, y={card.BoundsInWindow.Y}, " +
                          $"w={card.BoundsInWindow.Width}, h={card.BoundsInWindow.Height}");
            sb.AppendLine($"  │  Raw OCR text:  \"{card.RawOcrText}\"");
            sb.AppendLine($"  │  Matched item:  {card.MatchedItem?.CanonicalName ?? "(none)"}");
 
            if (card.MatchedItem is not null)
            {
                string slug = MarketSlugConverter.ToSlug(card.MatchedItem.CanonicalName);
                sb.AppendLine($"  │  Market slug:   {slug}");
                sb.AppendLine($"  │  Untradeable:   {card.MatchedItem.IsUntradeable}");
            }
 
            sb.AppendLine($"  │  Price:         {card.DisplayText}");
            sb.AppendLine($"  │  Successful:    {card.IsSuccessful}");
            sb.AppendLine($"  └──────────────────────────────────────────────────────");
            sb.AppendLine();
        }
 
        sb.AppendLine("──────────────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("  EXPECTED REWARDS (from the screenshot):");
        sb.AppendLine("    Card 0: Forma Blueprint          → Untradeable (or low price)");
        sb.AppendLine("    Card 1: Paris Prime String        → tradeable, expect a platinum price");
        sb.AppendLine("    Card 2: Fang Prime Blueprint      → tradeable, expect a platinum price");
        sb.AppendLine("    Card 3: Boltor Prime Stock        → tradeable, expect a platinum price");
        sb.AppendLine();
        sb.AppendLine("  ▸ Verify each card's OCR text is reasonable.");
        sb.AppendLine("  ▸ Verify each matched item matches the expected reward.");
        sb.AppendLine("  ▸ Verify prices are plausible (not null for tradeable items).");
        sb.AppendLine("  ▸ Verify pipeline time is under 4 seconds.");
        sb.AppendLine();
 
        return sb.ToString();
    }

}