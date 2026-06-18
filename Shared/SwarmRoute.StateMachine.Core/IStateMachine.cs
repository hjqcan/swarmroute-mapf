namespace SwarmRoute.StateMachine.Core;

/// <summary>
/// Minimal generic state-machine abstraction. Thin by design — concrete implementations
/// (e.g. a Stateless-backed machine) are provided later by the owning squads.
/// </summary>
/// <typeparam name="TState">The state type.</typeparam>
/// <typeparam name="TTrigger">The trigger/event type that drives transitions.</typeparam>
public interface IStateMachine<TState, TTrigger>
{
    /// <summary>The current state.</summary>
    TState CurrentState { get; }

    /// <summary>Whether <paramref name="trigger"/> is permitted from the current state.</summary>
    bool CanFire(TTrigger trigger);

    /// <summary>Attempts to fire <paramref name="trigger"/>, returning the transition outcome.</summary>
    Task<StateTransitionResult<TState>> FireAsync(TTrigger trigger, object? context = null, CancellationToken ct = default);
}
