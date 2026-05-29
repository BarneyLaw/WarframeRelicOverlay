namespace WarframeRelicOverlay.Presentation;

using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using WarframeRelicOverlay.Domain.Models;
using WarframeRelicOverlay.Infrastructure.Platform;
using WarframeRelicOverlay.OverlayApp.Pipeline;
using WarframeRelicOverlay.OverlayApp.StateMachine;

/// <summary>
/// Debug harness that simulates the full overlay lifecycle without
/// Warframe running.  Hooks into the <see cref="OverlayStateMachine"/>
/// and <see cref="OverlayViewModel"/> to exercise every visual state.
///
/// <b>Usage:</b> Launch the app with <c>--debug</c> or set
/// <c>AppSettings.DebugMode = true</c>.  The overlay window spans
/// the primary monitor.  Press <b>F5</b> to trigger a fake reward
/// detection cycle (Tracking → Detecting → Pricing → Displaying).
/// Press <b>F6</b> to simulate exit back to Tracking.
/// Press <b>Escape</b> to quit.
///
/// The simulator fabricates a <see cref="PipelineResult"/> with 4
/// mock reward cards positioned across the window, using realistic
/// prices and one untradeable (Forma) to verify all label styles.
/// </summary>
public sealed class DebugSimulator
{
    private readonly OverlayStateMachine _stateMachine;
    private readonly OverlayViewModel _viewModel;

    public DebugSimulator(
        OverlayStateMachine stateMachine,
        OverlayViewModel viewModel)
    {
        _stateMachine = stateMachine;
        _viewModel = viewModel;
    }

    /// <summary>
    /// Attaches keyboard hooks to the overlay window and puts the
    /// state machine into Tracking mode immediately (no process
    /// tracker needed).
    /// </summary>
    public void Attach(Window overlayWindow)
    {
        overlayWindow.KeyDown += OnKeyDown;

        // Disable click-through so the window can receive keyboard
        // input in debug mode.
        if (overlayWindow is OverlayWindow ow)
            ow.SetClickThrough(false);

        // Position the overlay over the full primary screen so we
        // have room to show mock card positions.
        var screen = SystemParameters.PrimaryScreenWidth;
        var screenH = SystemParameters.PrimaryScreenHeight;
        _viewModel.ForceWindowGeometry(0, 0, screen, screenH);

        // Kick the state machine straight into Tracking — normally
        // the process tracker fires WarframeStarted, but there's no
        // Warframe in debug mode.
        _stateMachine.Fire(OverlayTrigger.WarframeStarted);

        Debug.WriteLine("[DebugSim] Attached. F5 = simulate rewards, F6 = exit rewards, Esc = quit.");
    }

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F5:
                await SimulateRewardCycleAsync();
                break;

            case Key.F6:
                SimulateExit();
                break;

            case Key.Escape:
                Application.Current.Shutdown();
                break;
        }
    }

    /// <summary>
    /// Walks through Tracking → Detecting → Pricing → Displaying
    /// with realistic delays so you can see each visual state.
    /// </summary>
    private async Task SimulateRewardCycleAsync()
    {
        Debug.WriteLine("[DebugSim] F5 pressed — starting reward simulation.");

        // 1. Hint detected → Detecting state
        _stateMachine.Fire(OverlayTrigger.RewardHintDetected);
        await Task.Delay(400);

        // 2. Confirmed → Pricing state
        _stateMachine.Fire(OverlayTrigger.RewardConfirmed);
        _viewModel.ShowLoading();
        await Task.Delay(1200); // simulate OCR + API latency

        // 3. Build mock pipeline result
        var result = BuildMockResult();
        _viewModel.ShowPrices(result);
        _viewModel.HideLoading();

        // 4. Pricing completed → Displaying state
        _stateMachine.Fire(OverlayTrigger.PricingCompleted);

        Debug.WriteLine("[DebugSim] Displaying mock prices.");
    }

    private void SimulateExit()
    {
        Debug.WriteLine("[DebugSim] F6 pressed — simulating reward screen exit.");
        _viewModel.ClearPrices();
        _stateMachine.Fire(OverlayTrigger.RewardScreenExited);
    }

    /// <summary>
    /// Fabricates a 4-card pipeline result with cards spread evenly
    /// across the window.  Uses the current overlay window dimensions
    /// to calculate plausible card positions.
    /// </summary>
    private PipelineResult BuildMockResult()
    {
        // Simulate a 1920×1080 Warframe window at 1x DPI
        int windowW = 1920;
        int windowH = 1080;
        double dpiX = 1.0;
        double dpiY = 1.0;

        // Card geometry: 4 cards centered, each ~200px wide, spaced
        // evenly across ~60% of window width, vertically at ~42%
        int cardW = 200;
        int cardH = 60;
        int totalCardsW = 4 * cardW + 3 * 30; // 30px gaps
        int startX = (windowW - totalCardsW) / 2;
        int cardY = (int)(windowH * 0.42);

        var mockItems = new (string Name, int? Price, bool Untradeable)[]
        {
            ("Ash Prime Chassis Blueprint",     45,   false),
            ("Forma Blueprint",                 null, true),
            ("Kavasa Prime Buckle",             12,   false),
            ("Valkyr Prime Neuroptics Blueprint", 8,  false),
        };

        var cards = new List<CardResult>();
        for (int i = 0; i < 4; i++)
        {
            var (name, price, untradeable) = mockItems[i];
            int x = startX + i * (cardW + 30);

            cards.Add(new CardResult
            {
                Index = i,
                BoundsInWindow = new Rectangle(x, cardY, cardW, cardH),
                MatchedItem = new RewardItem(name, untradeable),
                PricePlatinum = price,
                RawOcrText = name.ToLowerInvariant(),
            });
        }

        var window = new WindowSnapshot(
            ClientX: 0,
            ClientY: 0,
            ClientWidth: windowW,
            ClientHeight: windowH,
            DpiScaleX: dpiX,
            DpiScaleY: dpiY);

        return new PipelineResult
        {
            Cards = cards,
            Window = window,
            Elapsed = TimeSpan.FromMilliseconds(1150),
        };
    }
}