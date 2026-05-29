namespace WarframeRelicOverlay;

using System;
using System.Diagnostics;
using System.IO;
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
/// Application composition root.
///
/// <para>
/// Wires up the entire dependency graph using
/// <see cref="Microsoft.Extensions.DependencyInjection"/>, then starts the
/// Warframe process tracker and the overlay coordinator.  The overlay is
/// hidden by default and only becomes visible when the state machine
/// transitions into <see cref="OverlayState.Pricing"/> or
/// <see cref="OverlayState.Displaying"/>, or when the user presses the
/// configured global hotkey.
/// </para>
///
/// <para>
/// The window <see cref="OverlayWindow"/> is itself click-through and
/// transparent.  All long-lived disposables are tracked here and released
/// in reverse order on shutdown.
/// </para>
/// </summary>
public partial class App : Application
{
    // ── Configuration ───────────────────────────────────────────────

    private const string SettingsFileName = "data/settings.json";
    private const string ItemsFileName = "data/items.json";
    private const string TessDataFolder = "tessdata";
    private const string MarketBaseUrl = "https://api.warframe.market/v2/";
    private const string UserAgent = "WarframeRelicOverlay/1.0";

    // ── State ───────────────────────────────────────────────────────

    private ServiceProvider? _services;
    private OverlayCoordinator? _coordinator;
    private OverlayWindow? _overlayWindow;
    private HotkeyManager? _hotkeyManager;
    private ILogger? _logger;

    // ── Lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Builds the dependency graph and starts the overlay engine.
    /// Any failure here is logged, surfaced as a single error dialog,
    /// and causes the process to exit with a non-zero exit code.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // No automatic shutdown when a window closes — we only shut
        // down when the coordinator says so or the user kills the
        // process from the tray / task manager.  Each window manages
        // its own visibility.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            BuildAndStart();
        }
        catch (Exception ex)
        {
            _logger?.LogError("Startup failed.", ex);
            Debug.WriteLine($"[App] Startup failed: {ex}");

            MessageBox.Show(
                $"Warframe Relic Overlay failed to start:\n\n{ex.Message}",
                "Warframe Relic Overlay",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            DisposeServices();
            Shutdown(exitCode: 1);
        }
    }

    /// <summary>
    /// Releases every long-lived resource registered with the DI
    /// container, in reverse order, on application exit.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        DisposeServices();
        base.OnExit(e);
    }

    // ── Composition ─────────────────────────────────────────────────

    /// <summary>
    /// Constructs the service collection, builds the provider, and
    /// kicks off the runtime components in the correct order.
    /// </summary>
    private void BuildAndStart()
    {
        AppSettings settings = LoadSettings();

        var services = new ServiceCollection();

        // Logger first so everything else can use it.
        services.AddSingleton<ILogger>(_ => new FileLogger());
        services.AddSingleton(settings);

        // Reward pool — graceful empty-pool fallback handled by the
        // repository itself (logs a warning if items.json is missing).
        services.AddSingleton<IRewardRepository>(_ =>
            new JsonRewardRepository(ItemsFileName));
        services.AddSingleton<IRewardMatcher, FuzzyRewardMatcher>();

        // Platform — process and window tracking.
        services.AddSingleton<IProcessTracker, WarframeProcessTracker>();
        services.AddSingleton<IWindowTracker, WarframeWindowTracker>();
        services.AddSingleton<IScreenCapturer, GdiScreenCapturer>();

        // OCR engine pool.
        services.AddSingleton(sp => new TesseractOcrEngine(TessDataFolder));
        services.AddSingleton<IOcrEngine>(sp => sp.GetRequiredService<TesseractOcrEngine>());

        // Market client + caching price provider.
        services.AddSingleton(_ => CreateHttpClient());
        services.AddSingleton<IWarframeMarketAPI>(sp =>
            new WarframeMarketClient(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new RewardPriceCache(
            sp.GetRequiredService<IWarframeMarketAPI>(),
            TimeSpan.FromMinutes(settings.PriceCacheTtlMinutes)));
        services.AddSingleton<IPriceProvider>(sp =>
            sp.GetRequiredService<RewardPriceCache>());

        // Pipeline.
        services.AddSingleton<IRewardLayoutDetector, IntensityProfileDetector>();
        services.AddSingleton<IRewardPipeline, RewardPricingPipeline>();

        // Detection — pick a screen detector based on AppSettings.DetectionMode
        // and adapt it to the IRewardDetector interface the coordinator uses.
        services.AddSingleton<IRewardScreenDetector>(sp =>
            CreateScreenDetector(sp, settings));
        services.AddSingleton<IRewardDetector>(sp =>
            new RewardScreenDetectorAdapter(
                sp.GetRequiredService<IRewardScreenDetector>()));

        // State machine.
        services.AddSingleton<OverlayStateMachine>();

        // Presentation — the WPF window and the IOverlayOutput that
        // pushes pipeline results into it.
        services.AddSingleton<OverlayWindow>();
        services.AddSingleton<IOverlayOutput>(sp =>
            new WpfOverlayOutput(
                sp.GetRequiredService<OverlayWindow>(),
                sp.GetRequiredService<IWindowTracker>(),
                sp.GetRequiredService<IProcessTracker>(),
                sp.GetRequiredService<OverlayStateMachine>(),
                sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<ILogger>()));

        // Top-level coordinator.
        services.AddSingleton<OverlayCoordinator>();

        _services = services.BuildServiceProvider();

        _logger = _services.GetRequiredService<ILogger>();
        _logger.LogOperationStart("Application startup");

        // Touch the overlay window early so it is created on the UI
        // thread and gets wired into the WpfOverlayOutput before any
        // pipeline result arrives.
        _overlayWindow = _services.GetRequiredService<OverlayWindow>();
        _ = _services.GetRequiredService<IOverlayOutput>();

        // Build the coordinator and start everything.  Order matters:
        // OverlayCoordinator.Start subscribes to the process tracker's
        // Started event before we kick the tracker into action.  Without
        // this ordering, IProcessTracker.Start fires Started(pid)
        // synchronously (via its initial poll) when Warframe is already
        // running, the coordinator hasn't subscribed yet, and the
        // WarframeStarted trigger never reaches the state machine.
        _coordinator = _services.GetRequiredService<OverlayCoordinator>();
        _coordinator.Start();

        var processTracker = _services.GetRequiredService<IProcessTracker>();
        processTracker.Start();

        // Hotkey toggle for force-show.
        _hotkeyManager = new HotkeyManager(
            _overlayWindow,
            settings.ToggleHotkey,
            OnHotkeyPressed,
            _logger);
        _hotkeyManager.TryRegister();

        _logger.LogOperationEnd("Application startup", success: true,
            details: $"DetectionMode={settings.DetectionMode}");
    }

    /// <summary>
    /// Loads <see cref="AppSettings"/> from <c>data/settings.json</c>,
    /// returning the defaults if the file is missing, unreadable, or
    /// corrupt.  Validation warnings are swallowed by
    /// <see cref="AppSettings.Load"/>; we log them via <see cref="Debug"/>
    /// because the file logger has not been constructed yet.
    /// </summary>
    private static AppSettings LoadSettings()
    {
        try
        {
            return AppSettings.Load(SettingsFileName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[App] AppSettings.Load failed: {ex.Message}. Using defaults.");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Constructs the shared <see cref="HttpClient"/> used by the
    /// Warframe Market client.  The base address, timeout, and required
    /// headers are configured here exactly once for the process lifetime.
    /// </summary>
    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(MarketBaseUrl),
            Timeout = TimeSpan.FromSeconds(5),
        };

        http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        http.DefaultRequestHeaders.Add("Accept", "application/json");
        http.DefaultRequestHeaders.Add("Platform", "pc");
        http.DefaultRequestHeaders.Add("Language", "en");

        return http;
    }

    /// <summary>
    /// Selects and constructs the screen detector that matches
    /// <see cref="AppSettings.DetectionMode"/>.  Unknown values and
    /// "Manual" fall back to <see cref="LogFileDetector"/> with a
    /// warning so the app stays useful even with a stale settings file.
    /// </summary>
    private static IRewardScreenDetector CreateScreenDetector(
        IServiceProvider sp, AppSettings settings)
    {
        var logger = sp.GetRequiredService<ILogger>();

        switch (settings.DetectionMode)
        {
            case "EELog":
                return new LogFileDetector(settings);

            case "OCR":
                return new OcrFallbackDetector(
                    sp.GetRequiredService<IScreenCapturer>(),
                    sp.GetRequiredService<IOcrEngine>(),
                    sp.GetRequiredService<IProcessTracker>(),
                    sp.GetRequiredService<IWindowTracker>(),
                    settings);

            default:
                logger.LogWarning(
                    $"DetectionMode '{settings.DetectionMode}' is not supported in this build. " +
                    "Falling back to EELog.");
                return new LogFileDetector(settings);
        }
    }

    // ── Hotkey ──────────────────────────────────────────────────────

    /// <summary>
    /// Forwards a global hotkey press to the overlay output so the
    /// user can force the overlay to appear (or hide it again) at any
    /// time, regardless of the current pipeline state.
    /// </summary>
    private void OnHotkeyPressed()
    {
        if (_services is null) return;

        var output = _services.GetService<IOverlayOutput>() as WpfOverlayOutput;
        output?.ToggleManualShown();
    }

    // ── Disposal ────────────────────────────────────────────────────

    /// <summary>
    /// Disposes the coordinator, the hotkey, and the service provider
    /// in reverse-registration order.  Each step is wrapped in its own
    /// try/catch so a misbehaving component cannot prevent the rest
    /// from being released.
    /// </summary>
    private void DisposeServices()
    {
        try { _coordinator?.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[App] Coordinator dispose failed: {ex.Message}"); }
        _coordinator = null;

        try { _hotkeyManager?.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[App] Hotkey dispose failed: {ex.Message}"); }
        _hotkeyManager = null;

        try { _services?.Dispose(); }
        catch (Exception ex) { Debug.WriteLine($"[App] Services dispose failed: {ex.Message}"); }
        _services = null;
    }
}
