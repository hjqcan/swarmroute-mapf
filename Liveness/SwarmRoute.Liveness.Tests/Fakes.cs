using System.Collections.Generic;
using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Deadlock.Tests;

/// <summary>Captures published integration events for assertions.</summary>
internal sealed class CapturingIntegrationEventPublisher : IIntegrationEventPublisher
{
    public List<Event> Published { get; } = [];

    public Task PublishAsync(IEnumerable<Event> domainEvents, CancellationToken cancellationToken = default)
    {
        Published.AddRange(domainEvents);
        return Task.CompletedTask;
    }
}

/// <summary>Avoidance-point selector that always returns a fixed site (simulates an integrated map).</summary>
internal sealed class FixedAvoidancePointSelector(string siteId) : IAvoidancePointSelector
{
    public string? SelectAvoidancePoint(string victimAgentId, IReadOnlySet<string>? excludedSiteIds = null)
        => excludedSiteIds is not null && excludedSiteIds.Contains(siteId) ? null : siteId;
}

/// <summary>Detour reservation that always succeeds (simulates an integrated TrafficControl).</summary>
internal sealed class AlwaysGrantDetourReservationService : IDetourReservationService
{
    public Task<bool> TryReserveDetourAsync(
        string victimAgentId,
        string avoidanceSiteId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}

/// <summary>Clearance confirmer with a settable outcome.</summary>
internal sealed class StubClearanceConfirmer(bool cleared) : IClearanceConfirmer
{
    public bool IsCleared(string victimAgentId) => cleared;
}
