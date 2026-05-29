namespace WarframeRelicOverlay;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WarframeRelicOverlay.Core;
using WarframeRelicOverlay.Domain.Matching;
using WarframeRelicOverlay.Domain.Pricing;
using WarframeRelicOverlay.Infrastructure.Logging;
using WarframeRelicOverlay.Infrastructure.Market;
using WarframeRelicOverlay.Infrastructure.OCR;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.Infrastructure.RewardData;
using WarframeRelicOverlay.Infrastructure.ScreenCapture;
using WarframeRelicOverlay.OverlayApp.Detection;
using WarframeRelicOverlay.OverlayApp.Layout;
using WarframeRelicOverlay.OverlayApp.Pipeline;
using WarframeRelicOverlay.OverlayApp.StateMachine;
using WarframeRelicOverlay.Presentation;

/// <summary>
/// Application entry point.  Supports two launch modes:
///
///   <b>Normal</b> (default) — builds the full DI container with
///   real infrastructure (OCR, screen capture, market API) and starts
///   the coordinator + process tracker.
///
///   <b>Debug</b> (<c>--debug</c> flag or <c>DebugMode = true</c>
///   in settings) — skips all real infrastructure, attaches a
///   <see cref="DebugSimulator"/> that lets you trigger fake reward
///   cycles with F5/F6 to test the overlay visuals.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private OverlayCoordinator? _coordinator;
    private OverlayViewModel? _viewModel;
    private IProcessTracker? _processTracker;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        bool debugMode = e.Args.Contains("--debug", StringComparer.OrdinalIgnoreCase);

        // ── Load settings ───────────────────────────────────────────
        string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        string settingsPath = Path.Combine(dataDir, "settings.json");
        var settings = AppSettings.Load(settingsPath);

        debugMode = debugMode || settings.DebugMode;
        Debug.WriteLine($"[App] DebugMode={debugMode}, DetectionMode={settings.DetectionMode}");

        if (debugMode)
            StartDebugMode(settings);
        else
            StartNormalMode(settings);
    }

    // ── Debug mode ──────────────────────────────────────────────────
    // Only needs the state machine, view model, and simulator.
    // No OCR, no screen capture, no market API, no Tesseract.

    private void StartDebugMode(AppSettings settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<OverlayStateMachine>();
        services.AddSingleton<OverlayViewModel>(sp =>
        {
            var sm = sp.GetRequiredService<OverlayStateMachine>();
            // OverlayViewModel needs IWindowTracker and IProcessTracker
            // but in debug mode they're never called (position tracking
            // is replaced by ForceWindowGeometry).  Register stubs.
            return new OverlayViewModel(sm, NullWindowTracker.Instance, NullProcessTracker.Instance);
        });

        _serviceProvider = services.BuildServiceProvider();

        var stateMachine = _serviceProvider.GetRequiredService<OverlayStateMachine>();
        _viewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();

        var overlayWindow = new OverlayWindow { DataContext = _viewModel };
        MainWindow = overlayWindow;
        overlayWindow.Show();

        // Attach the simulator — it drives the state machine via
        // keyboard shortcuts and fakes pipeline results.
        var simulator = new DebugSimulator(stateMachine, _viewModel);
        simulator.Attach(overlayWindow);

        Debug.WriteLine("[App] Debug mode started. F5 = rewards, F6 = exit, Esc = quit.");
    }

    // ── Normal mode ─────────────────────────────────────────────────
    // Full DI container with all real services.

    private void StartNormalMode(AppSettings settings)
    {
        string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        string itemsPath = Path.Combine(dataDir, "items.json");
        string tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");

        var services = new ServiceCollection();

        // Settings
        services.AddSingleton(settings);

        // Logging
        services.AddSingleton<ILogger, FileLogger>();

        // Infrastructure: platform
        services.AddSingleton<IProcessTracker, WarframeProcessTracker>();
        services.AddSingleton<IWindowTracker, WarframeWindowTracker>();

        // Infrastructure: screen capture
        services.AddSingleton<IScreenCapturer, GdiScreenCapturer>();

        // Infrastructure: OCR (pooled Tesseract engines)
        services.AddSingleton<IOcrEngine>(
            _ => new TesseractOcrEngine(tessDataPath, poolSize: 4));

        // Infrastructure: market API
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IWarframeMarketAPI>(
            sp => new WarframeMarketClient(sp.GetRequiredService<HttpClient>()));

        // Domain: reward data
        services.AddSingleton<IRewardRepository>(
            _ => new JsonRewardRepository(itemsPath));

        // Domain: matching
        services.AddSingleton<IRewardMatcher, FuzzyRewardMatcher>();

        // Domain: pricing (cached)
        services.AddSingleton<IPriceProvider>(sp =>
            new RewardPriceCache(
                sp.GetRequiredService<IWarframeMarketAPI>(),
                TimeSpan.FromMinutes(settings.PriceCacheTtlMinutes)));

        // Application: layout detection
        services.AddSingleton<IRewardLayoutDetector, IntensityProfileDetector>();

        // Application: reward screen detection (mode-dependent)
        services.AddSingleton<IRewardScreenDetector>(sp => settings.DetectionMode switch
        {
            "OCR" => new OcrFallbackDetector(
                        sp.GetRequiredService<IScreenCapturer>(),
                        sp.GetRequiredService<IOcrEngine>(),
                        sp.GetRequiredService<IProcessTracker>(),
                        sp.GetRequiredService<IWindowTracker>(),
                        settings),
            _ => new LogFileDetector(settings),
        });

        // Application: adapter from IRewardScreenDetector → IRewardDetector
        services.AddSingleton<IRewardDetector>(sp =>
            new RewardDetectorAdapter(
                sp.GetRequiredService<IRewardScreenDetector>()));

        // Application: pipeline
        services.AddSingleton<IRewardPipeline, RewardPricingPipeline>();

        // Application: state machine
        services.AddSingleton<OverlayStateMachine>();

        // Presentation: ViewModel (implements IOverlayOutput)
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<IOverlayOutput>(sp =>
            sp.GetRequiredService<OverlayViewModel>());

        // Application: coordinator
        services.AddSingleton<OverlayCoordinator>();

        _serviceProvider = services.BuildServiceProvider();

        _viewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();
        _coordinator = _serviceProvider.GetRequiredService<OverlayCoordinator>();

        var overlayWindow = new OverlayWindow { DataContext = _viewModel };
        MainWindow = overlayWindow;
        overlayWindow.Show();

        // Start services
        _processTracker = _serviceProvider.GetRequiredService<IProcessTracker>();
        _processTracker.Start();
        _viewModel.StartPositionTracking(overlayWindow);
        _coordinator.Start();

        Debug.WriteLine("[App] Normal mode started.");
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        Debug.WriteLine("[App] Shutting down...");

        _viewModel?.StopPositionTracking();
        _coordinator?.Dispose();

        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();
    }
}

// ── Null stubs for debug mode ───────────────────────────────────────
// These satisfy the OverlayViewModel constructor without pulling in
// real Win32 infrastructure.

file sealed class NullProcessTracker : IProcessTracker
{
    public static readonly NullProcessTracker Instance = new();
    public event Action<int>? Started { add { } remove { } }
    public event Action<int>? Stopped { add { } remove { } }
    public nint MainWindowHandle => nint.Zero;
    public int? ProcessId => null;
    public bool IsRunning => false;
    public void Start() { }
    public void Dispose() { }
}

file sealed class NullWindowTracker : IWindowTracker
{
    public static readonly NullWindowTracker Instance = new();
    public WindowSnapshot? TryGetBounds(nint hwnd) => null;
    public bool IsForeground(nint hwnd) => false;
}