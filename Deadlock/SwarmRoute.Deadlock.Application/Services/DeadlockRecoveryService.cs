using System.Collections.Generic;
using System.Linq;
using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Application.Contract.Services;
using SwarmRoute.Deadlock.Application.Resolution;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Deadlock.Application.Services;

/// <summary>
/// Default <see cref="IDeadlockRecoveryService"/>. Iterates the open resolutions in
/// <see cref="IActiveResolutionRegistry"/> and drives each through <c>IDeadlockResolver.Recover</c> (which
/// checks <c>IClearanceConfirmer</c> internally). On success it drains the case's domain events and
/// publishes the integration-flagged subset (<c>Deadlock.Case.Resolved</c>), then closes the registry
/// entry. Mirrors the publish behaviour of <see cref="DeadlockAppService"/> (the Deadlock context has no
/// <c>BaseDbContext.Commit()</c> to drain for it).
/// </summary>
public sealed class DeadlockRecoveryService : IDeadlockRecoveryService
{
    private readonly IActiveResolutionRegistry _registry;
    private readonly Domain.Services.IDeadlockResolver _resolver;
    private readonly IIntegrationEventPublisher _integrationEventPublisher;

    public DeadlockRecoveryService(
        IActiveResolutionRegistry registry,
        Domain.Services.IDeadlockResolver resolver,
        IIntegrationEventPublisher integrationEventPublisher)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _integrationEventPublisher = integrationEventPublisher
            ?? throw new ArgumentNullException(nameof(integrationEventPublisher));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<string>> TryRecoverAllAsync(CancellationToken cancellationToken = default)
    {
        var open = _registry.SnapshotOpen();
        if (open.Count == 0)
            return Array.Empty<string>();

        var recovered = new List<string>();
        var events = new List<Event>();

        foreach (var resolution in open)
        {
            // Recover checks IClearanceConfirmer.IsCleared internally; returns false while still blocked.
            if (!_resolver.Recover(resolution.Case, resolution.Plan))
                continue;

            if (resolution.Case.DomainEvents is { Count: > 0 } domainEvents)
                events.AddRange(domainEvents);
            resolution.Case.ClearDomainEvents();

            _registry.Close(resolution.VictimAgentId);
            recovered.Add(resolution.VictimAgentId);
        }

        if (events.Count > 0)
            await _integrationEventPublisher.PublishAsync(events, cancellationToken).ConfigureAwait(false);

        if (recovered.Count > 0)
            SwarmRouteMetrics.DeadlocksResolved.Add(recovered.Count);

        return recovered;
    }
}
