using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Domain.Shared.Events;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Coordination.Application.Deadlock;

/// <summary>
/// Coordination's consumer of the Deadlock context's resolution lifecycle events
/// (<c>Deadlock.Case.ResolutionRequested / Resolved / Escalated</c>). It is the seam the design's contract
/// table calls for ("Deadlock → Coordination: ResolutionRequested → Coordination re-plans the victim").
/// <para>
/// It does the minimum, allocation-light work: project the event into the
/// <see cref="IFleetRedirectSink"/> so the fleet driver can act on it between ticks. It must NOT call back
/// into TrafficControl or the coordination cycle, because it fires synchronously inside the contended
/// <c>TryReserve</c> publish (re-entrancy is guarded only at the Deadlock scan; this handler stays a pure
/// store write).
/// </para>
/// </summary>
public sealed class DeadlockResolutionRequestedConsumer : IIntegrationEventHandler
{
    private readonly IFleetRedirectSink _sink;

    public DeadlockResolutionRequestedConsumer(IFleetRedirectSink sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <inheritdoc />
    public bool CanHandle(Event domainEvent)
        => domainEvent is IDeadlockResolutionRequested or IDeadlockResolved or IDeadlockEscalated;

    /// <inheritdoc />
    public Task HandleAsync(Event domainEvent, CancellationToken cancellationToken = default)
    {
        switch (domainEvent)
        {
            case IDeadlockResolutionRequested requested
                when !string.IsNullOrWhiteSpace(requested.SuggestedAvoidTarget):
                _sink.PublishRedirect(new RedirectIntent(
                    requested.CaseId,
                    requested.VictimAgentId,
                    requested.SuggestedAvoidTarget!));
                break;

            case IDeadlockResolved resolved:
                _sink.MarkRecovered(resolved.VictimAgentId);
                break;

            case IDeadlockEscalated escalated:
                _sink.MarkEscalated(escalated.VictimAgentId);
                break;
        }

        return Task.CompletedTask;
    }
}
