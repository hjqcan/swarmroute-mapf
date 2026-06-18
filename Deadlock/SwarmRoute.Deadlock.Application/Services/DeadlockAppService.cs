using System.Collections.Generic;
using System.Linq;
using NetDevPack.Messaging;
using SwarmRoute.Deadlock.Application.Contract.Dtos;
using SwarmRoute.Deadlock.Application.Contract.Services;
using SwarmRoute.Deadlock.Application.Resolution;
using SwarmRoute.Deadlock.Domain.Aggregates;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Deadlock.Application.Services;

/// <summary>
/// Default <see cref="IDeadlockAppService"/>. Orchestrates the v0 deadlock flow:
/// <list type="number">
/// <item><description>detect circular waits from the snapshot (<see cref="IDeadlockDetector"/>);</description></item>
/// <item><description>open a <see cref="DeadlockCase"/> per cycle (raising <c>Deadlock.Case.Detected</c>);</description></item>
/// <item><description>run the resolver (<see cref="IDeadlockResolver"/>) which selects a victim and requests
/// resolution (raising <c>Deadlock.Case.ResolutionRequested</c>) — escalating if no avoidance site is
/// available;</description></item>
/// <item><description>publish all accumulated integration events
/// (<see cref="IIntegrationEventPublisher"/>) and return a <see cref="DeadlockReportDto"/>.</description></item>
/// </list>
/// <para>
/// Because the Deadlock context has no EF/DbContext (deadlocks are transient), this service plays the
/// role <c>BaseDbContext.Commit()</c> plays elsewhere: it drains <see cref="Entity.DomainEvents"/> from
/// the cases and publishes the integration-flagged subset itself.
/// </para>
/// </summary>
public sealed class DeadlockAppService : IDeadlockAppService
{
    private readonly IDeadlockDetector _detector;
    private readonly IDeadlockResolver _resolver;
    private readonly IIntegrationEventPublisher _integrationEventPublisher;
    private readonly IActiveResolutionRegistry _registry;

    public DeadlockAppService(
        IDeadlockDetector detector,
        IDeadlockResolver resolver,
        IIntegrationEventPublisher integrationEventPublisher,
        IActiveResolutionRegistry registry)
    {
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _integrationEventPublisher = integrationEventPublisher
            ?? throw new ArgumentNullException(nameof(integrationEventPublisher));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <inheritdoc />
    public async Task<DeadlockReportDto> ScanAsync(
        ResourceAllocationGraphSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var cycles = _detector.Detect(snapshot);
        if (cycles.Count == 0)
            return DeadlockReportDto.Empty;

        var cases = new List<DeadlockCase>(cycles.Count);
        var events = new List<Event>();
        var cycleDtos = new List<DeadlockCycleDto>(cycles.Count);

        foreach (var cycle in cycles)
        {
            // Skip a cycle whose victim is already being resolved: re-detecting the same circular wait on a
            // later tick must NOT open a duplicate case, re-emit Deadlock.Case.ResolutionRequested, or
            // re-reserve a detour. The resolution stays open in the registry until recovery closes it.
            if (_registry.HasOpenForAny(cycle.AgentIds))
                continue;

            var deadlockCase = DeadlockCase.Detect(cycle);

            // Select victim + request resolution (and reserve detour if integrated). In a standalone
            // build the Null seams cause this to escalate, but the victim/strategy + ResolutionRequested
            // event are still produced.
            var plan = await _resolver.SolveAsync(deadlockCase, cancellationToken).ConfigureAwait(false);

            // A real resolution (avoid site found + detour reserved) parks the plan at ConfirmCleared. Hand
            // the LIVE aggregates to the registry so the recovery driver can later drive
            // ConfirmCleared → Recover → Resolved on these same instances. Escalated/aborted plans are not
            // registered (nothing to recover).
            if (plan.CurrentStep == AvoidancePlanStep.ConfirmCleared)
                _registry.Open(deadlockCase, plan);

            cases.Add(deadlockCase);
            cycleDtos.Add(new DeadlockCycleDto
            {
                CaseId = deadlockCase.Id,
                AgentIds = deadlockCase.AgentIds,
                VictimAgentId = deadlockCase.VictimAgentId,
                SuggestedAvoidTarget = deadlockCase.SuggestedAvoidTarget,
            });
        }

        // Drain and publish the integration-flagged domain events from every case.
        foreach (var deadlockCase in cases)
        {
            if (deadlockCase.DomainEvents is { Count: > 0 } domainEvents)
                events.AddRange(domainEvents);
            deadlockCase.ClearDomainEvents();
        }

        if (events.Count > 0)
            await _integrationEventPublisher.PublishAsync(events, cancellationToken);

        return new DeadlockReportDto { Cycles = cycleDtos };
    }
}
