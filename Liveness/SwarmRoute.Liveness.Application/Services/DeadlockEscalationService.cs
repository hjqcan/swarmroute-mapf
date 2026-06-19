using System.Collections.Generic;
using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Application.Contract.Services;
using SwarmRoute.Deadlock.Application.Resolution;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Deadlock.Application.Services;

/// <summary>
/// Default <see cref="IDeadlockEscalationService"/>. Looks up the open resolution for a victim and marks
/// its case <c>Escalated</c> as a livelock (raising <c>Deadlock.Case.Escalated</c>), then closes the
/// registry entry. Invoked by the fleet driver (via a delegate) when its local progress check fails.
/// </summary>
public sealed class DeadlockEscalationService : IDeadlockEscalationService
{
    private readonly IActiveResolutionRegistry _registry;
    private readonly IIntegrationEventPublisher _integrationEventPublisher;

    public DeadlockEscalationService(
        IActiveResolutionRegistry registry,
        IIntegrationEventPublisher integrationEventPublisher)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _integrationEventPublisher = integrationEventPublisher
            ?? throw new ArgumentNullException(nameof(integrationEventPublisher));
    }

    /// <inheritdoc />
    public async Task<bool> EscalateLivelockAsync(
        string victimAgentId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (!_registry.TryGet(victimAgentId, out var resolution))
            return false;

        resolution.Case.EscalateLivelock(reason);

        var events = new List<Event>();
        if (resolution.Case.DomainEvents is { Count: > 0 } domainEvents)
            events.AddRange(domainEvents);
        resolution.Case.ClearDomainEvents();

        _registry.Close(resolution.VictimAgentId);

        if (events.Count > 0)
            await _integrationEventPublisher.PublishAsync(events, cancellationToken).ConfigureAwait(false);

        return true;
    }
}
