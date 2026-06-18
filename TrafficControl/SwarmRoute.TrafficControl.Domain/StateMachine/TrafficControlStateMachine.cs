using SwarmRoute.StateMachine.Core;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Domain.StateMachine;

/// <summary>
/// The lease lifecycle state machine over <see cref="LeaseState"/>, driven by <see cref="LeaseTrigger"/>:
/// <c>Requested →(Grant)→ Reserved →(Enter)→ InTransit →(Pass)→ Releasing →(Release)→ Free</c>.
/// The <c>Grant</c> transition is gated by the supplied guards
/// (<see cref="ResourceAvailableGuard"/> / <see cref="NoConflictGuard"/> / <see cref="NotBlacklistedGuard"/>),
/// which is how the original engine's "can I lock this?" predicate is expressed declaratively. Implements the
/// Shared <see cref="IStateMachine{TState,TTrigger}"/> contract.
/// </summary>
/// <remarks>
/// Deliberately thin and allocation-free on the happy path; one instance models one lease. The aggregate
/// remains the authority on the table-wide invariant — this machine guards a single lease's transitions.
/// </remarks>
public sealed class TrafficControlStateMachine : IStateMachine<LeaseState, LeaseTrigger>
{
    private static readonly Dictionary<(LeaseState, LeaseTrigger), LeaseState> Transitions = new()
    {
        { (LeaseState.Requested, LeaseTrigger.Grant), LeaseState.Reserved },
        { (LeaseState.Reserved, LeaseTrigger.Enter), LeaseState.InTransit },
        { (LeaseState.InTransit, LeaseTrigger.Pass), LeaseState.Releasing },
        { (LeaseState.Releasing, LeaseTrigger.Release), LeaseState.Free },
    };

    private readonly IReadOnlyList<IStateGuard<LeaseState, LeaseTrigger>> _grantGuards;

    /// <summary>
    /// Creates a machine starting in <paramref name="initialState"/> (default <see cref="LeaseState.Requested"/>),
    /// applying <paramref name="grantGuards"/> to the <see cref="LeaseTrigger.Grant"/> transition.
    /// </summary>
    public TrafficControlStateMachine(
        IEnumerable<IStateGuard<LeaseState, LeaseTrigger>>? grantGuards = null,
        LeaseState initialState = LeaseState.Requested)
    {
        _grantGuards = grantGuards?.ToList() ?? new List<IStateGuard<LeaseState, LeaseTrigger>>();
        CurrentState = initialState;
    }

    /// <inheritdoc />
    public LeaseState CurrentState { get; private set; }

    /// <inheritdoc />
    public bool CanFire(LeaseTrigger trigger) => Transitions.ContainsKey((CurrentState, trigger));

    /// <inheritdoc />
    public async Task<StateTransitionResult<LeaseState>> FireAsync(
        LeaseTrigger trigger, object? context = null, CancellationToken ct = default)
    {
        if (!Transitions.TryGetValue((CurrentState, trigger), out var next))
        {
            return StateTransitionResult<LeaseState>.Failed(
                CurrentState,
                $"{TrafficControlErrorCodes.InvalidLeaseTransition}: {trigger} not allowed from {CurrentState}.");
        }

        // Guards apply only to the Grant transition (the original "can I lock?" predicate).
        if (trigger == LeaseTrigger.Grant)
        {
            foreach (var guard in _grantGuards)
            {
                ct.ThrowIfCancellationRequested();
                if (!await guard.CanTransitionAsync(CurrentState, next, trigger, context).ConfigureAwait(false))
                {
                    return StateTransitionResult<LeaseState>.Failed(
                        CurrentState, guard.GetFailureReason(), guard.Name);
                }
            }
        }

        var from = CurrentState;
        CurrentState = next;
        return StateTransitionResult<LeaseState>.Succeeded(from, next);
    }
}
