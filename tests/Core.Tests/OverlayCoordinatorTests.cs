namespace WarframeRelicOverlay.Tests.OverlayApp.Core;

using System.Drawing;
using FluentAssertions;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.OverlayApp.Detection;
using WarframeRelicOverlay.OverlayApp.Pipeline;
using WarframeRelicOverlay.OverlayApp.StateMachine;
using Xunit;

public class OverlayCoordinatorTests : IDisposable
{
    // ── Fakes ───────────────────────────────────────────────────

    /// <summary>
    /// Controllable process tracker that lets tests fire start/stop
    /// events manually.  Does not spawn real processes.
    /// </summary>
    private sealed class FakeProcessTracker : IProcessTracker
    {
        public bool IsRunning { get; set; }
        public int? ProcessId { get; set; }
        public nint MainWindowHandle { get; set; } = 1;  // non-zero default
        public event Action<int>? Started;
        public event Action<int>? Stopped;

        public void Start() { }  // no-op; tests fire events explicitly

        public void SimulateStart(int pid = 1234)
        {
            IsRunning = true;
            ProcessId = pid;
            Started?.Invoke(pid);
        }

        public void SimulateStop(int pid = 1234)
        {
            IsRunning = false;
            ProcessId = null;
            Stopped?.Invoke(pid);
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Returns a fixed <see cref="WindowSnapshot"/> for any handle.
    /// </summary>
    private sealed class FakeWindowTracker : IWindowTracker
    {
        public WindowSnapshot? SnapshotToReturn { get; set; } = new(
            ClientX: 0, ClientY: 0,
            ClientWidth: 1920, ClientHeight: 1080,
            DpiScaleX: 1.0, DpiScaleY: 1.0);

        public WindowSnapshot? TryGetBounds(nint windowHandle) => SnapshotToReturn;
        public WindowSnapshot? TryGetMonitorBounds(nint windowHandle) => null;
        public bool IsForeground(nint windowHandle) => true;
    }

    /// <summary>
    /// Controllable reward detector.  Tests call <c>SimulateX()</c>
    /// methods to fire events.  Tracks Start/Stop calls.
    /// </summary>
    private sealed class FakeDetector : IRewardDetector
    {
        public event Action? RewardDetected;
        public event Action? RewardLost;
        public event Action? RewardScreenExited;

        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public bool IsRunning { get; private set; }

        public void Start() { StartCount++; IsRunning = true; }
        public void Stop() { StopCount++; IsRunning = false; }

        public void SimulateRewardDetected() => RewardDetected?.Invoke();
        public void SimulateRewardLost() => RewardLost?.Invoke();
        public void SimulateRewardScreenExited() => RewardScreenExited?.Invoke();

        public void Dispose() { }
    }

    /// <summary>
    /// Pipeline that completes via a <see cref="TaskCompletionSource"/>
    /// so tests control exactly when it finishes and what it returns.
    /// </summary>
    private sealed class FakePipeline : IRewardPipeline
    {
        private TaskCompletionSource<PipelineResult>? _tcs;

        /// <summary>
        /// Sets the result that <see cref="ExecuteAsync"/> will return
        /// the next time the coordinator calls it.
        /// </summary>
        public void PrepareResult(PipelineResult result)
        {
            _tcs = new TaskCompletionSource<PipelineResult>();
            _tcs.SetResult(result);
        }

        /// <summary>
        /// Prepare a pipeline that completes immediately with cards.
        /// </summary>
        public void PrepareSuccessResult(int cardCount = 4)
        {
            var cards = Enumerable.Range(0, cardCount)
                .Select(i => new CardResult
                {
                    Index = i,
                    BoundsInWindow = new Rectangle(100 + i * 200, 400, 200, 60),
                    MatchedItem = new Domain.Models.RewardItem($"Item {i}"),
                    PricePlatinum = 10 + i,
                })
                .ToArray();

            PrepareResult(new PipelineResult
            {
                Cards = cards,
                Window = DefaultWindow,
                Elapsed = TimeSpan.FromMilliseconds(500),
            });
        }

        /// <summary>
        /// Prepare a pipeline that completes immediately with no cards.
        /// </summary>
        public void PrepareEmptyResult()
        {
            PrepareResult(PipelineResult.Empty(
                DefaultWindow, TimeSpan.FromMilliseconds(50)));
        }

        /// <summary>
        /// Prepare a pipeline that will block until <paramref name="tcs"/>
        /// is completed externally, allowing tests to control timing.
        /// </summary>
        public void PreparePending(out TaskCompletionSource<PipelineResult> tcs)
        {
            _tcs = new TaskCompletionSource<PipelineResult>();
            tcs = _tcs;
        }

        public int ExecutionCount { get; private set; }

        public Task<PipelineResult> ExecuteAsync(
            WindowSnapshot window, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            cancellationToken.ThrowIfCancellationRequested();

            if (_tcs is not null)
                return _tcs.Task;

            // Fallback: return empty result.
            return Task.FromResult(PipelineResult.Empty(
                window, TimeSpan.FromMilliseconds(1)));
        }

        private static readonly WindowSnapshot DefaultWindow = new(
            0, 0, 1920, 1080, 1.0, 1.0);
    }

    /// <summary>
    /// Records all output calls and signals when specific methods are
    /// invoked so tests can synchronize with async pipeline completion.
    /// </summary>
    private sealed class FakeOutput : IOverlayOutput
    {
        public readonly ManualResetEventSlim PricesShown = new(false);
        public readonly ManualResetEventSlim PricesCleared = new(false);
        public readonly ManualResetEventSlim LoadingShown = new(false);
        public readonly ManualResetEventSlim LoadingHidden = new(false);

        public int ShowPricesCalls { get; private set; }
        public int ClearPricesCalls { get; private set; }
        public int ShowLoadingCalls { get; private set; }
        public int HideLoadingCalls { get; private set; }
        public PipelineResult? LastResult { get; private set; }

        public void ShowPrices(PipelineResult result)
        {
            ShowPricesCalls++;
            LastResult = result;
            PricesShown.Set();
        }

        public void ClearPrices()
        {
            ClearPricesCalls++;
            PricesCleared.Set();
        }

        public void ShowLoading()
        {
            ShowLoadingCalls++;
            LoadingShown.Set();
        }

        public void HideLoading()
        {
            HideLoadingCalls++;
            LoadingHidden.Set();
        }

        /// <summary>Reset all signals for reuse across test steps.</summary>
        public void Reset()
        {
            PricesShown.Reset();
            PricesCleared.Reset();
            LoadingShown.Reset();
            LoadingHidden.Reset();
        }
    }

    // ── Shared setup ────────────────────────────────────────────

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static AppSettings MakeSettings(string mode = "EELog") => new()
    {
        DetectionMode = mode,
        DetectionStreak = 3,
        StabilizationDelayMs = 0,  // no delay in tests
        PriceCacheTtlMinutes = 5,
    };

    private readonly FakeProcessTracker _processTracker = new();
    private readonly FakeWindowTracker _windowTracker = new();
    private readonly FakeDetector _detector = new();
    private readonly FakePipeline _pipeline = new();
    private readonly FakeOutput _output = new();

    private OverlayCoordinator CreateCoordinator(
        AppSettings? settings = null,
        OverlayStateMachine? sm = null)
    {
        return new OverlayCoordinator(
            sm ?? new OverlayStateMachine(),
            _processTracker,
            _windowTracker,
            _detector,
            _pipeline,
            _output,
            settings ?? MakeSettings());
    }

    public void Dispose()
    {
        _output.PricesShown.Dispose();
        _output.PricesCleared.Dispose();
        _output.LoadingShown.Dispose();
        _output.LoadingHidden.Dispose();
    }

    // ── Process lifecycle ───────────────────────────────────────

    [Fact]
    public void WarframeStart_TransitionsToTracking_StartsDetector()
    {
        var sm = new OverlayStateMachine();
        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();

        sm.Current.Should().Be(OverlayState.Tracking);
        _detector.StartCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void WarframeStop_TransitionsToIdle_ClearsPrices()
    {
        var sm = new OverlayStateMachine();
        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        sm.Current.Should().Be(OverlayState.Tracking);

        _processTracker.SimulateStop();

        sm.Current.Should().Be(OverlayState.Idle);
        _output.ClearPricesCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── EE.log mode — instant confirmation ──────────────────────

    [Fact]
    public void EELogMode_RewardDetected_GoesToPricingDirectly()
    {
        var sm = new OverlayStateMachine();
        _pipeline.PrepareSuccessResult();

        using var coordinator = CreateCoordinator(settings: MakeSettings("EELog"), sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        sm.Current.Should().Be(OverlayState.Tracking);

        _detector.SimulateRewardDetected();

        // Pipeline runs asynchronously — wait for output.
        _output.PricesShown.Wait(WaitTimeout).Should()
            .BeTrue("pipeline should complete and show prices");

        sm.Current.Should().Be(OverlayState.Displaying);
        _output.LastResult.Should().NotBeNull();
        _output.LastResult!.HasCards.Should().BeTrue();
    }

    // ── OCR mode — streak management ────────────────────────────

    [Fact]
    public void OcrMode_SingleHint_StaysInDetecting()
    {
        var settings = MakeSettings("OCR");
        settings.DetectionStreak = 3;

        var sm = new OverlayStateMachine();
        using var coordinator = CreateCoordinator(settings: settings, sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        _detector.SimulateRewardDetected();  // hint 1 of 3

        sm.Current.Should().Be(OverlayState.Detecting);
    }

    [Fact]
    public void OcrMode_StreakComplete_GoesToPricing()
    {
        var settings = MakeSettings("OCR");
        settings.DetectionStreak = 3;

        var sm = new OverlayStateMachine();
        _pipeline.PrepareSuccessResult();

        using var coordinator = CreateCoordinator(settings: settings, sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();

        _detector.SimulateRewardDetected();  // hint 1
        _detector.SimulateRewardDetected();  // hint 2
        _detector.SimulateRewardDetected();  // hint 3 → confirmed

        // Pipeline fires asynchronously.
        _output.PricesShown.Wait(WaitTimeout).Should().BeTrue();

        sm.Current.Should().Be(OverlayState.Displaying);
    }

    [Fact]
    public void OcrMode_StreakBroken_ReturnsToTracking()
    {
        var settings = MakeSettings("OCR");
        settings.DetectionStreak = 3;

        var sm = new OverlayStateMachine();
        using var coordinator = CreateCoordinator(settings: settings, sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();

        _detector.SimulateRewardDetected();  // hint 1
        _detector.SimulateRewardDetected();  // hint 2
        sm.Current.Should().Be(OverlayState.Detecting);

        _detector.SimulateRewardLost();      // streak broken

        sm.Current.Should().Be(OverlayState.Tracking);
    }

    [Fact]
    public void OcrMode_StreakRestartsAfterBroken()
    {
        var settings = MakeSettings("OCR");
        settings.DetectionStreak = 2;

        var sm = new OverlayStateMachine();
        _pipeline.PrepareSuccessResult();

        using var coordinator = CreateCoordinator(settings: settings, sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();

        // First attempt — broken.
        _detector.SimulateRewardDetected();
        _detector.SimulateRewardLost();
        sm.Current.Should().Be(OverlayState.Tracking);

        // Second attempt — succeeds.
        _detector.SimulateRewardDetected();  // hint 1
        _detector.SimulateRewardDetected();  // hint 2 → confirmed

        _output.PricesShown.Wait(WaitTimeout).Should().BeTrue();
        sm.Current.Should().Be(OverlayState.Displaying);
    }

    // ── Pipeline failure ────────────────────────────────────────

    [Fact]
    public void PipelineReturnsNoCards_TransitionsToTracking()
    {
        var sm = new OverlayStateMachine();
        _pipeline.PrepareEmptyResult();

        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        _detector.SimulateRewardDetected();

        // Pipeline returns empty → PricingFailed → Tracking.
        // Wait for the loading indicator to appear and then hide.
        _output.LoadingShown.Wait(WaitTimeout).Should().BeTrue();

        // Give the pipeline task time to fire PricingFailed.
        Thread.Sleep(200);

        sm.Current.Should().Be(OverlayState.Tracking);
    }

    [Fact]
    public void WindowBoundsUnavailable_PricingFails()
    {
        var sm = new OverlayStateMachine();
        _windowTracker.SnapshotToReturn = null;

        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        _detector.SimulateRewardDetected();

        // Give the async task time to run.
        Thread.Sleep(200);

        sm.Current.Should().Be(OverlayState.Tracking);
    }

    // ── Reward screen exit ──────────────────────────────────────

    [Fact]
    public void DetectorReportsScreenExit_TransitionsToTracking()
    {
        var sm = new OverlayStateMachine();
        _pipeline.PrepareSuccessResult();

        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        // Get to Displaying.
        _processTracker.SimulateStart();
        _detector.SimulateRewardDetected();
        _output.PricesShown.Wait(WaitTimeout).Should().BeTrue();
        sm.Current.Should().Be(OverlayState.Displaying);

        // Detector says the reward screen is gone.
        _output.Reset();
        _detector.SimulateRewardScreenExited();

        sm.Current.Should().Be(OverlayState.Tracking);
        _output.ClearPricesCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── Cancellation on Warframe exit ───────────────────────────

    [Fact]
    public void WarframeExitsDuringPipeline_PipelineCancelled()
    {
        var sm = new OverlayStateMachine();
        _pipeline.PreparePending(out var tcs);

        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        _detector.SimulateRewardDetected();

        // Pipeline is now running (blocking on tcs).
        Thread.Sleep(100);
        sm.Current.Should().Be(OverlayState.Pricing);

        // Warframe exits while pipeline is in-flight.
        _processTracker.SimulateStop();

        sm.Current.Should().Be(OverlayState.Idle);

        // Complete the pipeline task to avoid unobserved exception.
        tcs.TrySetResult(PipelineResult.Empty(
            new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0),
            TimeSpan.Zero));
    }

    // ── Loading indicator ───────────────────────────────────────

    [Fact]
    public void EnteringPricing_ShowsLoading()
    {
        var sm = new OverlayStateMachine();
        _pipeline.PrepareSuccessResult();

        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        _detector.SimulateRewardDetected();

        _output.LoadingShown.Wait(WaitTimeout).Should().BeTrue();
        _output.ShowLoadingCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void EnteringDisplaying_HidesLoading()
    {
        var sm = new OverlayStateMachine();
        _pipeline.PrepareSuccessResult();

        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        _detector.SimulateRewardDetected();

        _output.PricesShown.Wait(WaitTimeout).Should().BeTrue();
        _output.HideLoadingCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── Detector lifecycle ──────────────────────────────────────

    [Fact]
    public void DetectorStoppedDuringPricing()
    {
        var sm = new OverlayStateMachine();
        _pipeline.PreparePending(out var tcs);

        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        int stopCountBefore = _detector.StopCount;

        _detector.SimulateRewardDetected();

        // Give async task a moment to start.
        Thread.Sleep(100);

        _detector.StopCount.Should().BeGreaterThan(stopCountBefore,
            "detector should be stopped while pipeline is running");

        // Clean up.
        tcs.TrySetResult(PipelineResult.Empty(
            new WindowSnapshot(0, 0, 1920, 1080, 1.0, 1.0),
            TimeSpan.Zero));
    }

    [Fact]
    public void DetectorStoppedWhenWarframeExits()
    {
        var sm = new OverlayStateMachine();
        using var coordinator = CreateCoordinator(sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        int stopBefore = _detector.StopCount;

        _processTracker.SimulateStop();

        _detector.StopCount.Should().BeGreaterThan(stopBefore);
    }

    // ── Manual mode — instant confirmation ──────────────────────

    [Fact]
    public void ManualMode_RewardDetected_GoesToPricing()
    {
        var sm = new OverlayStateMachine();
        _pipeline.PrepareSuccessResult();

        using var coordinator = CreateCoordinator(
            settings: MakeSettings("Manual"), sm: sm);
        coordinator.Start();

        _processTracker.SimulateStart();
        _detector.SimulateRewardDetected();

        _output.PricesShown.Wait(WaitTimeout).Should().BeTrue();
        sm.Current.Should().Be(OverlayState.Displaying);
    }

    // ── Constructor null guards ─────────────────────────────────

    [Fact]
    public void NullStateMachine_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new OverlayCoordinator(null!, _processTracker, _windowTracker,
                _detector, _pipeline, _output, MakeSettings()));

    [Fact]
    public void NullProcessTracker_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new OverlayCoordinator(new OverlayStateMachine(), null!,
                _windowTracker, _detector, _pipeline, _output, MakeSettings()));

    [Fact]
    public void NullWindowTracker_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new OverlayCoordinator(new OverlayStateMachine(), _processTracker,
                null!, _detector, _pipeline, _output, MakeSettings()));

    [Fact]
    public void NullDetector_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new OverlayCoordinator(new OverlayStateMachine(), _processTracker,
                _windowTracker, null!, _pipeline, _output, MakeSettings()));

    [Fact]
    public void NullPipeline_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new OverlayCoordinator(new OverlayStateMachine(), _processTracker,
                _windowTracker, _detector, null!, _output, MakeSettings()));

    [Fact]
    public void NullOutput_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new OverlayCoordinator(new OverlayStateMachine(), _processTracker,
                _windowTracker, _detector, _pipeline, null!, MakeSettings()));

    [Fact]
    public void NullSettings_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            new OverlayCoordinator(new OverlayStateMachine(), _processTracker,
                _windowTracker, _detector, _pipeline, _output, null!));
}