namespace SwarmRoute.StateMachine.Core;

/// <summary>
/// A guard condition that may block a state transition. Thin abstraction; concrete guards are added
/// by the owning squads alongside their state machines.
/// </summary>
/// <typeparam name="TState">The state type.</typeparam>
/// <typeparam name="TTrigger">The trigger/event type.</typeparam>
public interface IStateGuard<TState, TTrigger>
{
    /// <summary>Guard name, surfaced in <see cref="StateTransitionResult{TState}.FailedGuardName"/>.</summary>
    string Name { get; }

    /// <summary>Returns true when the transition is allowed.</summary>
    Task<bool> CanTransitionAsync(TState fromState, TState toState, TTrigger trigger, object? context = null);

    /// <summary>Human-readable reason used when the guard blocks the transition.</summary>
    string GetFailureReason();
}
