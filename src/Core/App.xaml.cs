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
    private ILogger? _logger;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Build the logger first thing so every step below is captured,
        // even if startup throws. Constructed explicitly (not via DI) so
        // the log file is guaranteed to exist from boot — a lazily
        // resolved singleton might never be instantiated.
        var logger = new FileLogger();
        _logger = logger;
        logger.LogInfo("==================== App starting ====================");
        logger.LogInfo($"Log file: {logger.LogFilePath}");

        try
        {
            // Belt-and-suspenders: the app.manifest already declares
            // PerMonitorV2, but if it was stripped (e.g. some single-file
            // publish configs) this ensures we still get true physical
            // pixels from the Win32 geometry/capture APIs. Must run before
            // any HWND is created.
            Infrastructure.Platform.Win32Interop.TryEnablePerMonitorV2();
            logger.LogInfo("Per-Monitor-V2 DPI awareness requested.");

            bool debugMode = e.Args.Contains("--debug", StringComparer.OrdinalIgnoreCase);

            // ── Load settings ───────────────────────────────────────────
            string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
            string settingsPath = Path.Combine(dataDir, "settings.json");
            var settings = AppSettings.Load(settingsPath);
            logger.LogInfo(
                $"Settings loaded from '{settingsPath}': " +
                $"DetectionMode={settings.DetectionMode}, DebugMode={settings.DebugMode}.");

            debugMode = debugMode || settings.DebugMode;
            logger.LogInfo($"Effective launch mode: {(debugMode ? "DEBUG" : "NORMAL")}.");

            if (debugMode)
                StartDebugMode(settings, logger);
            else
                StartNormalMode(settings, logger);

            logger.LogInfo("Startup complete.");
        }
        catch (Exception ex)
        {
            logger.LogError("Fatal error during startup.", ex);
            throw;
        }
    }

    // ── Debug mode ──────────────────────────────────────────────────
    // Only needs the state machine, view model, and simulator.
    // No OCR, no screen capture, no market API, no Tesseract.

    private void StartDebugMode(AppSettings settings, ILogger logger)
    {
        logger.LogInfo("Starting DEBUG mode (no real OCR / capture / market).");

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<ILogger>(logger);
        services.AddSingleton<OverlayStateMachine>();
        services.AddSingleton<OverlayViewModel>(sp =>
        {
            var sm = sp.GetRequiredService<OverlayStateMachine>();
            // OverlayViewModel needs IWindowTracker and IProcessTracker
            // but in debug mode they're never called (position tracking
            // is replaced by ForceWindowGeometry).  Register stubs.
            return new OverlayViewModel(sm, NullWindowTracker.Instance, NullProcessTracker.Instance, logger);
        });

        _serviceProvider = services.BuildServiceProvider();

        var stateMachine = _serviceProvider.GetRequiredService<OverlayStateMachine>();
        _viewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();

        var overlayWindow = new OverlayWindow(logger) { DataContext = _viewModel };
        _viewModel.PhysicalBoundsChanged += overlayWindow.SetPhysicalBounds;
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

    private void StartNormalMode(AppSettings settings, ILogger logger)
    {
        logger.LogInfo("Starting NORMAL mode.");

        string dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        string itemsPath = Path.Combine(dataDir, "items.json");
        string tessDataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");

        var services = new ServiceCollection();

        // Settings
        services.AddSingleton(settings);

        // Logging — register the instance created at boot so every
        // component shares the same log file.
        services.AddSingleton<ILogger>(logger);

        // Infrastructure: platform
        services.AddSingleton<IProcessTracker, WarframeProcessTracker>();
        services.AddSingleton<IWindowTracker, WarframeWindowTracker>();

        // Infrastructure: focus monitoring (alt-tab → hide overlay / idle)
        // services.AddSingleton<FocusWatcher>();

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
        logger.LogInfo("DI container built.");

        _viewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();
        _coordinator = _serviceProvider.GetRequiredService<OverlayCoordinator>();

        var overlayWindow = new OverlayWindow(logger) { DataContext = _viewModel };
        _viewModel.PhysicalBoundsChanged += overlayWindow.SetPhysicalBounds;
        MainWindow = overlayWindow;
        overlayWindow.Show();
        logger.LogInfo("Overlay window shown.");

        // Start services
        _processTracker = _serviceProvider.GetRequiredService<IProcessTracker>();
        _processTracker.Start();
        logger.LogInfo("Process tracker started.");
        _viewModel.StartPositionTracking();
        logger.LogInfo("Position tracking started.");
        _coordinator.Start();
        logger.LogInfo("Coordinator started. Normal mode ready.");
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        _logger?.LogInfo($"Application exiting (code {e.ApplicationExitCode}).");

        _viewModel?.StopPositionTracking();
        _coordinator?.Dispose();

        if (_serviceProvider is IDisposable disposable)
            disposable.Dispose();

        _logger?.LogInfo("==================== App stopped ====================");
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
    public WindowSnapshot? TryGetMonitorBounds(nint hwnd) => null;
    public bool IsForeground(nint hwnd) => false;
}