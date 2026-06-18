using DotNetCore.CAP;
using SwarmRoute.Coordination.Application.Deadlock;
using SwarmRoute.Deadlock.Application.Subscribers;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Host-level CAP bindings for cross-context integration events. Application handlers stay transport-agnostic;
/// this adapter only converts CAP topic deliveries back into the existing app seams.
/// </summary>
public sealed class CapIntegrationEventSubscriber : ICapSubscribe
{
    private readonly AllocationContendedSubscriber _allocationContendedSubscriber;
    private readonly IFleetRedirectSink _fleetRedirectSink;

    public CapIntegrationEventSubscriber(
        AllocationContendedSubscriber allocationContendedSubscriber,
        IFleetRedirectSink fleetRedirectSink)
    {
        _allocationContendedSubscriber = allocationContendedSubscriber
            ?? throw new ArgumentNullException(nameof(allocationContendedSubscriber));
        _fleetRedirectSink = fleetRedirectSink ?? throw new ArgumentNullException(nameof(fleetRedirectSink));
    }

    [CapSubscribe("TrafficControl.Allocation.Contended")]
    public Task OnAllocationContendedAsync(
        AllocationContendedMessage _,
        CancellationToken cancellationToken = default)
        => _allocationContendedSubscriber.HandleAsync(cancellationToken);

    [CapSubscribe("Deadlock.Case.ResolutionRequested")]
    public Task OnDeadlockResolutionRequestedAsync(
        DeadlockResolutionRequestedMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(message.VictimAgentId)
            && !string.IsNullOrWhiteSpace(message.SuggestedAvoidTarget))
        {
            _fleetRedirectSink.PublishRedirect(new RedirectIntent(
                message.CaseId,
                message.VictimAgentId,
                message.SuggestedAvoidTarget!));
        }

        return Task.CompletedTask;
    }

    [CapSubscribe("Deadlock.Case.Resolved")]
    public Task OnDeadlockResolvedAsync(
        DeadlockTerminalMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fleetRedirectSink.MarkRecovered(message.CaseId, message.VictimAgentId);
        return Task.CompletedTask;
    }

    [CapSubscribe("Deadlock.Case.Escalated")]
    public Task OnDeadlockEscalatedAsync(
        DeadlockTerminalMessage message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fleetRedirectSink.MarkEscalated(message.CaseId, message.VictimAgentId);
        return Task.CompletedTask;
    }

    public sealed record AllocationContendedMessage(
        Guid ReservationTableId,
        string AgentId,
        int ContendedRequestCount);

    public sealed record DeadlockResolutionRequestedMessage(
        Guid CaseId,
        string VictimAgentId,
        string? SuggestedAvoidTarget);

    public sealed record DeadlockTerminalMessage(Guid CaseId, string VictimAgentId);
}
