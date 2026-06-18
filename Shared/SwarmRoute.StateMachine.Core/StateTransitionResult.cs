namespace SwarmRoute.StateMachine.Core;

/// <summary>
/// Outcome of a state-machine transition attempt. Thin value type; richer behavior lives in concrete
/// state machines added later by the squads.
/// </summary>
/// <typeparam name="TState">The state type.</typeparam>
public sealed class StateTransitionResult<TState>
{
    /// <summary>Whether the transition succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>State the machine was in before the attempt.</summary>
    public TState FromState { get; init; } = default!;

    /// <summary>State the machine is in after the attempt (equals <see cref="FromState"/> on failure).</summary>
    public TState ToState { get; init; } = default!;

    /// <summary>Failure reason, when <see cref="Success"/> is false.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Name of the guard that blocked the transition, if any.</summary>
    public string? FailedGuardName { get; init; }

    public static StateTransitionResult<TState> Succeeded(TState from, TState to)
        => new() { Success = true, FromState = from, ToState = to };

    public static StateTransitionResult<TState> Failed(TState current, string errorMessage, string? failedGuardName = null)
        => new()
        {
            Success = false,
            FromState = current,
            ToState = current,
            ErrorMessage = errorMessage,
            FailedGuardName = failedGuardName
        };
}
