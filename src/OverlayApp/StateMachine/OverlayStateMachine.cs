namespace WarframeRelicOverlay.OverlayApp.StateMachine;

using System;
using System.Collections.Generic;

/// <summary>
/// A deterministic state machine that governs the overlay lifecycle.
///
/// Thread safety: all public methods acquire <see cref="_lock"/> so the
/// machine can be driven from the UI thread, a background task, or an
/// event handler without external synchronization.
///
/// Usage:
///   var sm = new OverlayStateMachine();
///   sm.StateChanged += (prev, curr, trigger) => { ... };
///   sm.Fire(OverlayTrigger.WarframeStarted);
/// </summary>
public sealed class OverlayStateMachine
{
    // ── Transition table ────────────────────────────────────────────

    private static readonly Dictionary<(OverlayState From, OverlayTrigger Trigger), OverlayState> Transitions = new()
    {
        // Idle
        { (OverlayState.Idle, OverlayTrigger.WarframeStarted),       OverlayState.Tracking },

        // Tracking
        { (OverlayState.Tracking, OverlayTrigger.RewardHintDetected), OverlayState.Detecting },
        { (OverlayState.Tracking, OverlayTrigger.RewardConfirmed),    OverlayState.Pricing },   // EE.log instant confirm
        { (OverlayState.Tracking, OverlayTrigger.WarframeStopped),    OverlayState.Idle },

        // Detecting (OCR streak in progress)
        { (OverlayState.Detecting, OverlayTrigger.RewardHintDetected),    OverlayState.Detecting },  // streak increments, stays
        { (OverlayState.Detecting, OverlayTrigger.DetectionStreakBroken),  OverlayState.Tracking },
        { (OverlayState.Detecting, OverlayTrigger.RewardConfirmed),       OverlayState.Pricing },
        { (OverlayState.Detecting, OverlayTrigger.WarframeStopped),       OverlayState.Idle },

        // Pricing (pipeline running)
        { (OverlayState.Pricing, OverlayTrigger.PricingCompleted), OverlayState.Displaying },
        { (OverlayState.Pricing, OverlayTrigger.PricingFailed),    OverlayState.Tracking },
        { (OverlayState.Pricing, OverlayTrigger.WarframeStopped),  OverlayState.Idle },

        // Displaying (prices shown)
        { (OverlayState.Displaying, OverlayTrigger.RewardScreenExited), OverlayState.Tracking },
        { (OverlayState.Displaying, OverlayTrigger.WarframeStopped),    OverlayState.Idle },
    };

    // ── State ───────────────────────────────────────────────────────

    private readonly object _lock = new();
    private OverlayState _current;

    /// <summary>Current state (thread-safe read).</summary>
    public OverlayState Current
    {
        get { lock (_lock) return _current; }
    }

    // ── Events ──────────────────────────────────────────────────────

    /// <summary>
    /// Raised after every successful transition. Subscribers receive the
    /// previous state, new state, and the trigger that caused the change.
    /// Raised inside the lock — keep handlers fast and non-blocking.
    /// </summary>
    public event Action<OverlayState, OverlayState, OverlayTrigger>? StateChanged;

    /// <summary>
    /// Raised when <see cref="Fire"/> is called with a trigger that has
    /// no entry in the transition table for the current state — i.e. the
    /// trigger is simply not valid right now. This is informational, not
    /// an error (e.g. WarframeStopped while already Idle is a no-op).
    /// </summary>
    public event Action<OverlayState, OverlayTrigger>? TriggerIgnored;

    // ── Construction ────────────────────────────────────────────────

    public OverlayStateMachine(OverlayState initial = OverlayState.Idle)
    {
        _current = initial;
    }

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Attempt to fire a trigger. If the (current state, trigger) pair
    /// exists in the transition table the machine moves to the target
    /// state and <see cref="StateChanged"/> is raised.
    ///
    /// Returns <c>true</c> if a transition occurred, <c>false</c> if
    /// the trigger was ignored (no valid transition).
    /// </summary>
    public bool Fire(OverlayTrigger trigger)
    {
        lock (_lock)
        {
            var key = (_current, trigger);

            if (!Transitions.TryGetValue(key, out var target))
            {
                TriggerIgnored?.Invoke(_current, trigger);
                return false;
            }

            var previous = _current;
            _current = target;

            // Self-transitions (Detecting → Detecting) still raise the
            // event so subscribers can react (e.g. increment a counter).
            StateChanged?.Invoke(previous, _current, trigger);

            return true;
        }
    }

    /// <summary>
    /// Check whether a trigger would cause a transition from the current
    /// state without actually firing it. Useful for UI to enable/disable
    /// buttons or for guards that need to pre-check.
    /// </summary>
    public bool CanFire(OverlayTrigger trigger)
    {
        lock (_lock)
        {
            return Transitions.ContainsKey((_current, trigger));
        }
    }

    /// <summary>
    /// Force the machine into a specific state without going through a
    /// trigger. Use only for initialization or crash recovery — never
    /// for normal flow.
    /// </summary>
    public void Reset(OverlayState state = OverlayState.Idle)
    {
        lock (_lock)
        {
            var previous = _current;
            _current = state;

            if (previous != state)
            {
                // No trigger to report — pass the same trigger enum value
                // that would most naturally explain the transition, but
                // since this is a forced reset we fire a dedicated event.
                StateChanged?.Invoke(previous, _current, default);
            }
        }
    }

    /// <summary>
    /// Returns true when the machine is in one of the "active" states
    /// (anything other than Idle) — handy for deciding whether the
    /// overlay window should be visible at all.
    /// </summary>
    public bool IsActive
    {
        get { lock (_lock) return _current != OverlayState.Idle; }
    }

    /// <summary>
    /// Returns true when the machine is in a state where detection
    /// polling should be running (Tracking or Detecting).
    /// </summary>
    public bool IsDetecting
    {
        get
        {
            lock (_lock)
                return _current == OverlayState.Tracking ||
                        _current == OverlayState.Detecting;
        }
    }

    /// <summary>
    /// Returns a snapshot of all valid triggers for the current state.
    /// Useful for debugging and the Log tab.
    /// </summary>
    public List<OverlayTrigger> GetValidTriggers()
    {
        lock (_lock)
        {
            var valid = new List<OverlayTrigger>();
            foreach (var trigger in Enum.GetValues<OverlayTrigger>())
            {
                if (Transitions.ContainsKey((_current, trigger)))
                    valid.Add(trigger);
            }
            return valid;
        }
    }
}
