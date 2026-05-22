namespace WarframeRelicOverlay.Tests.OverlayApp;

using WarframeRelicOverlay.OverlayApp.StateMachine;
using Xunit;

public class OverlayStateMachineTests
{
    // ────────────────────────────────────────────────────────────────
    //  Initial state
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultInitialState_IsIdle()
    {
        var sm = new OverlayStateMachine();
        Assert.Equal(OverlayState.Idle, sm.Current);
    }

    [Theory]
    [InlineData(OverlayState.Idle)]
    [InlineData(OverlayState.Tracking)]
    [InlineData(OverlayState.Detecting)]
    [InlineData(OverlayState.Pricing)]
    [InlineData(OverlayState.Displaying)]
    public void CanConstructWithAnyInitialState(OverlayState initial)
    {
        var sm = new OverlayStateMachine(initial);
        Assert.Equal(initial, sm.Current);
    }

    // ────────────────────────────────────────────────────────────────
    //  Valid transitions — every edge in the graph
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Idle_WarframeStarted_MovesToTracking()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        Assert.True(sm.Fire(OverlayTrigger.WarframeStarted));
        Assert.Equal(OverlayState.Tracking, sm.Current);
    }

    [Fact]
    public void Tracking_RewardHintDetected_MovesToDetecting()
    {
        var sm = new OverlayStateMachine(OverlayState.Tracking);
        Assert.True(sm.Fire(OverlayTrigger.RewardHintDetected));
        Assert.Equal(OverlayState.Detecting, sm.Current);
    }

    [Fact]
    public void Tracking_RewardConfirmed_MovesToPricing()
    {
        // EE.log instant confirmation skips the Detecting state
        var sm = new OverlayStateMachine(OverlayState.Tracking);
        Assert.True(sm.Fire(OverlayTrigger.RewardConfirmed));
        Assert.Equal(OverlayState.Pricing, sm.Current);
    }

    [Fact]
    public void Detecting_RewardHintDetected_StaysInDetecting()
    {
        var sm = new OverlayStateMachine(OverlayState.Detecting);
        Assert.True(sm.Fire(OverlayTrigger.RewardHintDetected));
        Assert.Equal(OverlayState.Detecting, sm.Current);
    }

    [Fact]
    public void Detecting_DetectionStreakBroken_ReturnsToTracking()
    {
        var sm = new OverlayStateMachine(OverlayState.Detecting);
        Assert.True(sm.Fire(OverlayTrigger.DetectionStreakBroken));
        Assert.Equal(OverlayState.Tracking, sm.Current);
    }

    [Fact]
    public void Detecting_RewardConfirmed_MovesToPricing()
    {
        var sm = new OverlayStateMachine(OverlayState.Detecting);
        Assert.True(sm.Fire(OverlayTrigger.RewardConfirmed));
        Assert.Equal(OverlayState.Pricing, sm.Current);
    }

    [Fact]
    public void Pricing_PricingCompleted_MovesToDisplaying()
    {
        var sm = new OverlayStateMachine(OverlayState.Pricing);
        Assert.True(sm.Fire(OverlayTrigger.PricingCompleted));
        Assert.Equal(OverlayState.Displaying, sm.Current);
    }

    [Fact]
    public void Pricing_PricingFailed_FallsBackToTracking()
    {
        var sm = new OverlayStateMachine(OverlayState.Pricing);
        Assert.True(sm.Fire(OverlayTrigger.PricingFailed));
        Assert.Equal(OverlayState.Tracking, sm.Current);
    }

    [Fact]
    public void Displaying_RewardScreenExited_ReturnsToTracking()
    {
        var sm = new OverlayStateMachine(OverlayState.Displaying);
        Assert.True(sm.Fire(OverlayTrigger.RewardScreenExited));
        Assert.Equal(OverlayState.Tracking, sm.Current);
    }

    // ────────────────────────────────────────────────────────────────
    //  WarframeStopped from every non-Idle state
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OverlayState.Tracking)]
    [InlineData(OverlayState.Detecting)]
    [InlineData(OverlayState.Pricing)]
    [InlineData(OverlayState.Displaying)]
    public void WarframeStopped_AlwaysReturnsToIdle(OverlayState from)
    {
        var sm = new OverlayStateMachine(from);
        Assert.True(sm.Fire(OverlayTrigger.WarframeStopped));
        Assert.Equal(OverlayState.Idle, sm.Current);
    }

    [Fact]
    public void WarframeStopped_WhenAlreadyIdle_IsIgnored()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        Assert.False(sm.Fire(OverlayTrigger.WarframeStopped));
        Assert.Equal(OverlayState.Idle, sm.Current);
    }

    // ────────────────────────────────────────────────────────────────
    //  Invalid transitions — triggers that don't apply
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OverlayState.Idle, OverlayTrigger.RewardHintDetected)]
    [InlineData(OverlayState.Idle, OverlayTrigger.RewardConfirmed)]
    [InlineData(OverlayState.Idle, OverlayTrigger.PricingCompleted)]
    [InlineData(OverlayState.Idle, OverlayTrigger.RewardScreenExited)]
    [InlineData(OverlayState.Tracking, OverlayTrigger.PricingCompleted)]
    [InlineData(OverlayState.Tracking, OverlayTrigger.RewardScreenExited)]
    [InlineData(OverlayState.Detecting, OverlayTrigger.PricingCompleted)]
    [InlineData(OverlayState.Detecting, OverlayTrigger.RewardScreenExited)]
    [InlineData(OverlayState.Pricing, OverlayTrigger.RewardHintDetected)]
    [InlineData(OverlayState.Pricing, OverlayTrigger.DetectionStreakBroken)]
    [InlineData(OverlayState.Pricing, OverlayTrigger.RewardScreenExited)]
    [InlineData(OverlayState.Displaying, OverlayTrigger.RewardHintDetected)]
    [InlineData(OverlayState.Displaying, OverlayTrigger.PricingCompleted)]
    public void InvalidTrigger_ReturnsFalse_StateUnchanged(
        OverlayState initial, OverlayTrigger trigger)
    {
        var sm = new OverlayStateMachine(initial);
        Assert.False(sm.Fire(trigger));
        Assert.Equal(initial, sm.Current);
    }

    // ────────────────────────────────────────────────────────────────
    //  Full lifecycle: happy paths
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void FullLifecycle_EELogPath()
    {
        // EE.log mode: Idle → Tracking → Pricing → Displaying → Tracking → Idle
        var sm = new OverlayStateMachine();

        sm.Fire(OverlayTrigger.WarframeStarted);
        Assert.Equal(OverlayState.Tracking, sm.Current);

        // EE.log fires RewardConfirmed directly (no Detecting phase)
        sm.Fire(OverlayTrigger.RewardConfirmed);
        Assert.Equal(OverlayState.Pricing, sm.Current);

        sm.Fire(OverlayTrigger.PricingCompleted);
        Assert.Equal(OverlayState.Displaying, sm.Current);

        sm.Fire(OverlayTrigger.RewardScreenExited);
        Assert.Equal(OverlayState.Tracking, sm.Current);

        sm.Fire(OverlayTrigger.WarframeStopped);
        Assert.Equal(OverlayState.Idle, sm.Current);
    }

    [Fact]
    public void FullLifecycle_OcrFallbackPath()
    {
        // OCR mode: Idle → Tracking → Detecting (×N) → Pricing → Displaying → Tracking
        var sm = new OverlayStateMachine();

        sm.Fire(OverlayTrigger.WarframeStarted);
        Assert.Equal(OverlayState.Tracking, sm.Current);

        // First OCR hint
        sm.Fire(OverlayTrigger.RewardHintDetected);
        Assert.Equal(OverlayState.Detecting, sm.Current);

        // Streak building — additional hits stay in Detecting
        sm.Fire(OverlayTrigger.RewardHintDetected);
        Assert.Equal(OverlayState.Detecting, sm.Current);

        // Streak threshold reached — external code fires RewardConfirmed
        sm.Fire(OverlayTrigger.RewardConfirmed);
        Assert.Equal(OverlayState.Pricing, sm.Current);

        sm.Fire(OverlayTrigger.PricingCompleted);
        Assert.Equal(OverlayState.Displaying, sm.Current);

        sm.Fire(OverlayTrigger.RewardScreenExited);
        Assert.Equal(OverlayState.Tracking, sm.Current);
    }

    [Fact]
    public void FullLifecycle_OcrFalsePositiveThenRecovery()
    {
        var sm = new OverlayStateMachine();
        sm.Fire(OverlayTrigger.WarframeStarted);

        // Start detecting
        sm.Fire(OverlayTrigger.RewardHintDetected);
        Assert.Equal(OverlayState.Detecting, sm.Current);

        // False positive — streak broken
        sm.Fire(OverlayTrigger.DetectionStreakBroken);
        Assert.Equal(OverlayState.Tracking, sm.Current);

        // Real reward screen now
        sm.Fire(OverlayTrigger.RewardHintDetected);
        Assert.Equal(OverlayState.Detecting, sm.Current);

        sm.Fire(OverlayTrigger.RewardConfirmed);
        Assert.Equal(OverlayState.Pricing, sm.Current);
    }

    [Fact]
    public void FullLifecycle_PricingFailureRecovery()
    {
        var sm = new OverlayStateMachine();
        sm.Fire(OverlayTrigger.WarframeStarted);
        sm.Fire(OverlayTrigger.RewardConfirmed);
        Assert.Equal(OverlayState.Pricing, sm.Current);

        // Pipeline fails (no cards detected, network error, etc.)
        sm.Fire(OverlayTrigger.PricingFailed);
        Assert.Equal(OverlayState.Tracking, sm.Current);

        // Can re-enter the pipeline on next detection
        sm.Fire(OverlayTrigger.RewardConfirmed);
        Assert.Equal(OverlayState.Pricing, sm.Current);
    }

    [Fact]
    public void WarframeCrashDuringPricing_ReturnsToIdle()
    {
        var sm = new OverlayStateMachine();
        sm.Fire(OverlayTrigger.WarframeStarted);
        sm.Fire(OverlayTrigger.RewardConfirmed);
        Assert.Equal(OverlayState.Pricing, sm.Current);

        sm.Fire(OverlayTrigger.WarframeStopped);
        Assert.Equal(OverlayState.Idle, sm.Current);

        // Can restart cleanly
        sm.Fire(OverlayTrigger.WarframeStarted);
        Assert.Equal(OverlayState.Tracking, sm.Current);
    }

    // ────────────────────────────────────────────────────────────────
    //  StateChanged event
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void StateChanged_FiresWithCorrectArguments()
    {
        var sm = new OverlayStateMachine();
        OverlayState? capturedPrev = null;
        OverlayState? capturedCurr = null;
        OverlayTrigger? capturedTrigger = null;

        sm.StateChanged += (prev, curr, trigger) =>
        {
            capturedPrev = prev;
            capturedCurr = curr;
            capturedTrigger = trigger;
        };

        sm.Fire(OverlayTrigger.WarframeStarted);

        Assert.Equal(OverlayState.Idle, capturedPrev);
        Assert.Equal(OverlayState.Tracking, capturedCurr);
        Assert.Equal(OverlayTrigger.WarframeStarted, capturedTrigger);
    }

    [Fact]
    public void StateChanged_FiresOnSelfTransition()
    {
        // Detecting + RewardHintDetected → Detecting is a self-transition
        // that should still raise the event (so streak counter can increment)
        var sm = new OverlayStateMachine(OverlayState.Detecting);
        int fireCount = 0;

        sm.StateChanged += (_, _, _) => fireCount++;

        sm.Fire(OverlayTrigger.RewardHintDetected);
        sm.Fire(OverlayTrigger.RewardHintDetected);
        sm.Fire(OverlayTrigger.RewardHintDetected);

        Assert.Equal(3, fireCount);
    }

    [Fact]
    public void StateChanged_DoesNotFireOnInvalidTrigger()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        int fireCount = 0;

        sm.StateChanged += (_, _, _) => fireCount++;

        sm.Fire(OverlayTrigger.PricingCompleted); // invalid from Idle
        Assert.Equal(0, fireCount);
    }

    [Fact]
    public void StateChanged_TracksFullSequence()
    {
        var sm = new OverlayStateMachine();
        var transitions = new List<(OverlayState From, OverlayState To, OverlayTrigger Trigger)>();

        sm.StateChanged += (prev, curr, trigger) =>
            transitions.Add((prev, curr, trigger));

        sm.Fire(OverlayTrigger.WarframeStarted);
        sm.Fire(OverlayTrigger.RewardConfirmed);
        sm.Fire(OverlayTrigger.PricingCompleted);
        sm.Fire(OverlayTrigger.RewardScreenExited);

        Assert.Equal(4, transitions.Count);
        Assert.Equal((OverlayState.Idle, OverlayState.Tracking, OverlayTrigger.WarframeStarted), transitions[0]);
        Assert.Equal((OverlayState.Tracking, OverlayState.Pricing, OverlayTrigger.RewardConfirmed), transitions[1]);
        Assert.Equal((OverlayState.Pricing, OverlayState.Displaying, OverlayTrigger.PricingCompleted), transitions[2]);
        Assert.Equal((OverlayState.Displaying, OverlayState.Tracking, OverlayTrigger.RewardScreenExited), transitions[3]);
    }

    // ────────────────────────────────────────────────────────────────
    //  TriggerIgnored event
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void TriggerIgnored_FiresOnInvalidTrigger()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        OverlayState? capturedState = null;
        OverlayTrigger? capturedTrigger = null;

        sm.TriggerIgnored += (state, trigger) =>
        {
            capturedState = state;
            capturedTrigger = trigger;
        };

        sm.Fire(OverlayTrigger.PricingCompleted);

        Assert.Equal(OverlayState.Idle, capturedState);
        Assert.Equal(OverlayTrigger.PricingCompleted, capturedTrigger);
    }

    [Fact]
    public void TriggerIgnored_DoesNotFireOnValidTrigger()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        int fireCount = 0;

        sm.TriggerIgnored += (_, _) => fireCount++;

        sm.Fire(OverlayTrigger.WarframeStarted);
        Assert.Equal(0, fireCount);
    }

    // ────────────────────────────────────────────────────────────────
    //  CanFire
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void CanFire_ReturnsTrueForValidTransitions()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        Assert.True(sm.CanFire(OverlayTrigger.WarframeStarted));
    }

    [Fact]
    public void CanFire_ReturnsFalseForInvalidTransitions()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        Assert.False(sm.CanFire(OverlayTrigger.PricingCompleted));
        Assert.False(sm.CanFire(OverlayTrigger.RewardScreenExited));
    }

    [Fact]
    public void CanFire_DoesNotMutateState()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        sm.CanFire(OverlayTrigger.WarframeStarted);
        Assert.Equal(OverlayState.Idle, sm.Current);
    }

    // ────────────────────────────────────────────────────────────────
    //  Reset
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_SetsStateDirectly()
    {
        var sm = new OverlayStateMachine(OverlayState.Displaying);
        sm.Reset(OverlayState.Idle);
        Assert.Equal(OverlayState.Idle, sm.Current);
    }

    [Fact]
    public void Reset_DefaultsToIdle()
    {
        var sm = new OverlayStateMachine(OverlayState.Pricing);
        sm.Reset();
        Assert.Equal(OverlayState.Idle, sm.Current);
    }

    [Fact]
    public void Reset_FiresStateChangedWhenStateDiffers()
    {
        var sm = new OverlayStateMachine(OverlayState.Pricing);
        int fireCount = 0;

        sm.StateChanged += (_, _, _) => fireCount++;

        sm.Reset(OverlayState.Idle);
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void Reset_DoesNotFireStateChangedWhenSameState()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        int fireCount = 0;

        sm.StateChanged += (_, _, _) => fireCount++;

        sm.Reset(OverlayState.Idle);
        Assert.Equal(0, fireCount);
    }

    // ────────────────────────────────────────────────────────────────
    //  Convenience properties
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(OverlayState.Idle, false)]
    [InlineData(OverlayState.Tracking, true)]
    [InlineData(OverlayState.Detecting, true)]
    [InlineData(OverlayState.Pricing, true)]
    [InlineData(OverlayState.Displaying, true)]
    public void IsActive_TrueForAllNonIdleStates(OverlayState state, bool expected)
    {
        var sm = new OverlayStateMachine(state);
        Assert.Equal(expected, sm.IsActive);
    }

    [Theory]
    [InlineData(OverlayState.Idle, false)]
    [InlineData(OverlayState.Tracking, true)]
    [InlineData(OverlayState.Detecting, true)]
    [InlineData(OverlayState.Pricing, false)]
    [InlineData(OverlayState.Displaying, false)]
    public void IsDetecting_TrueOnlyForTrackingAndDetecting(
        OverlayState state, bool expected)
    {
        var sm = new OverlayStateMachine(state);
        Assert.Equal(expected, sm.IsDetecting);
    }

    // ────────────────────────────────────────────────────────────────
    //  GetValidTriggers
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void GetValidTriggers_Idle_OnlyWarframeStarted()
    {
        var sm = new OverlayStateMachine(OverlayState.Idle);
        var valid = sm.GetValidTriggers();

        Assert.Single(valid);
        Assert.Contains(OverlayTrigger.WarframeStarted, valid);
    }

    [Fact]
    public void GetValidTriggers_Tracking_HasExpectedTriggers()
    {
        var sm = new OverlayStateMachine(OverlayState.Tracking);
        var valid = sm.GetValidTriggers();

        Assert.Contains(OverlayTrigger.RewardHintDetected, valid);
        Assert.Contains(OverlayTrigger.RewardConfirmed, valid);
        Assert.Contains(OverlayTrigger.WarframeStopped, valid);
        Assert.Equal(3, valid.Count);
    }

    [Fact]
    public void GetValidTriggers_Detecting_HasExpectedTriggers()
    {
        var sm = new OverlayStateMachine(OverlayState.Detecting);
        var valid = sm.GetValidTriggers();

        Assert.Contains(OverlayTrigger.RewardHintDetected, valid);
        Assert.Contains(OverlayTrigger.DetectionStreakBroken, valid);
        Assert.Contains(OverlayTrigger.RewardConfirmed, valid);
        Assert.Contains(OverlayTrigger.WarframeStopped, valid);
        Assert.Equal(4, valid.Count);
    }

    [Fact]
    public void GetValidTriggers_Pricing_HasExpectedTriggers()
    {
        var sm = new OverlayStateMachine(OverlayState.Pricing);
        var valid = sm.GetValidTriggers();

        Assert.Contains(OverlayTrigger.PricingCompleted, valid);
        Assert.Contains(OverlayTrigger.PricingFailed, valid);
        Assert.Contains(OverlayTrigger.WarframeStopped, valid);
        Assert.Equal(3, valid.Count);
    }

    [Fact]
    public void GetValidTriggers_Displaying_HasExpectedTriggers()
    {
        var sm = new OverlayStateMachine(OverlayState.Displaying);
        var valid = sm.GetValidTriggers();

        Assert.Contains(OverlayTrigger.RewardScreenExited, valid);
        Assert.Contains(OverlayTrigger.WarframeStopped, valid);
        Assert.Equal(2, valid.Count);
    }

    // ────────────────────────────────────────────────────────────────
    //  Thread safety — basic concurrent stress test
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ConcurrentFire_DoesNotCorruptState()
    {
        // Hammer the machine from multiple threads. The state should
        // always be a valid OverlayState value and the machine should
        // never throw.
        var sm = new OverlayStateMachine(OverlayState.Tracking);
        var triggers = Enum.GetValues<OverlayTrigger>();

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var rng = new Random();
            for (int i = 0; i < 1000; i++)
            {
                var trigger = triggers[rng.Next(triggers.Length)];
                sm.Fire(trigger); // must never throw
            }
        }));

        Task.WhenAll(tasks).GetAwaiter().GetResult();

        // State must be one of the valid enum values
        Assert.True(Enum.IsDefined(sm.Current));
    }

    // ────────────────────────────────────────────────────────────────
    //  Repeated start/stop cycles
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void RepeatedStartStop_CyclesCleanly()
    {
        var sm = new OverlayStateMachine();

        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(OverlayState.Idle, sm.Current);

            sm.Fire(OverlayTrigger.WarframeStarted);
            Assert.Equal(OverlayState.Tracking, sm.Current);

            sm.Fire(OverlayTrigger.WarframeStopped);
            Assert.Equal(OverlayState.Idle, sm.Current);
        }
    }

    [Fact]
    public void MultipleRewardCyclesWithoutRestart()
    {
        var sm = new OverlayStateMachine();
        sm.Fire(OverlayTrigger.WarframeStarted);

        // Run three reward cycles in a row without restarting Warframe
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(OverlayState.Tracking, sm.Current);

            sm.Fire(OverlayTrigger.RewardConfirmed);
            Assert.Equal(OverlayState.Pricing, sm.Current);

            sm.Fire(OverlayTrigger.PricingCompleted);
            Assert.Equal(OverlayState.Displaying, sm.Current);

            sm.Fire(OverlayTrigger.RewardScreenExited);
            Assert.Equal(OverlayState.Tracking, sm.Current);
        }
    }
}
