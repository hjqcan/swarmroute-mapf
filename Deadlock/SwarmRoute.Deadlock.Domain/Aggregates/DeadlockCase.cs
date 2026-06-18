using System.Collections.Generic;
using System.Linq;
using NetDevPack.Domain;
using SwarmRoute.Deadlock.Domain.Events;
using SwarmRoute.Deadlock.Domain.Shared;
using SwarmRoute.Deadlock.Domain.Shared.Enums;
using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Domain.Aggregates;

/// <summary>
/// Aggregate root for one detected deadlock. Holds the circular-wait agent set and, once resolution
/// starts, the chosen victim + <see cref="ResolutionStrategy"/>. Lifecycle:
/// <c>Detected → Resolving → Resolved | Escalated</c>.
/// <para>
/// Although the Deadlock context persists no EF state in v0 (deadlocks are transient), the case is
/// modelled as a proper aggregate so it can raise domain/integration events through the normal
/// <c>Entity.DomainEvents</c> channel and carry optimistic-concurrency state, per grukirbs convention.
/// </para>
/// </summary>
public class DeadlockCase : Entity, IAggregateRoot
{
    private readonly List<string> _agentIds = [];

    // EF Core parameterless constructor
    private DeadlockCase()
    {
        Kind = DeadlockKind.Cyclic;
        Status = DeadlockCaseStatus.Detected;
        VictimAgentId = null;
        Strategy = null;
        SuggestedAvoidTarget = null;
        StateVersion = 0;
        StateChangedAtUtc = null;
    }

    private DeadlockCase(Guid id, DeadlockKind kind, IEnumerable<string> agentIds)
    {
        if (id == Guid.Empty)
            throw new ArgumentException(DeadlockErrorCodes.EmptyCycle, nameof(id));

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var a in agentIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(a))
                throw new ArgumentException(DeadlockErrorCodes.InvalidAgentId, nameof(agentIds));
            normalized.Add(a.Trim());
        }

        if (normalized.Count == 0)
            throw new ArgumentException(DeadlockErrorCodes.EmptyCycle, nameof(agentIds));

        Id = id;
        Kind = kind;
        Status = DeadlockCaseStatus.Detected;
        _agentIds.AddRange(normalized);
        VictimAgentId = null;
        Strategy = null;
        SuggestedAvoidTarget = null;
        StateVersion = 1;
        StateChangedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Opens a new case from a detected <see cref="DeadlockCycle"/> and raises
    /// <see cref="DeadlockCaseDetectedEvent"/>.
    /// </summary>
    public static DeadlockCase Detect(DeadlockCycle cycle, DeadlockKind kind = DeadlockKind.Cyclic)
    {
        ArgumentNullException.ThrowIfNull(cycle);

        var @case = new DeadlockCase(Guid.NewGuid(), kind, cycle.AgentIds);
        @case.AddDomainEvent(new DeadlockCaseDetectedEvent(@case.Id, kind, @case.AgentIds));
        return @case;
    }

    /// <summary>The kind of deadlock (v0: <see cref="DeadlockKind.Cyclic"/>).</summary>
    public DeadlockKind Kind { get; private set; }

    /// <summary>Current lifecycle state.</summary>
    public DeadlockCaseStatus Status { get; private set; }

    /// <summary>The agents in the circular wait (sorted ordinal).</summary>
    public IReadOnlyList<string> AgentIds => _agentIds.AsReadOnly();

    /// <summary>The chosen victim, once resolution has begun.</summary>
    public string? VictimAgentId { get; private set; }

    /// <summary>The chosen resolution strategy, once resolution has begun.</summary>
    public ResolutionStrategy? Strategy { get; private set; }

    /// <summary>The avoid target suggested to consumers (site id), if any.</summary>
    public string? SuggestedAvoidTarget { get; private set; }

    /// <summary>Optimistic-concurrency version.</summary>
    public long StateVersion { get; private set; }

    /// <summary>Last state-change timestamp (UTC).</summary>
    public DateTimeOffset? StateChangedAtUtc { get; private set; }

    /// <summary>
    /// Detected → Resolving. Records the victim + strategy (+ optional suggested avoid target) and raises
    /// <see cref="DeadlockCaseResolutionRequestedEvent"/> so the fleet acts.
    /// </summary>
    public void RequestResolution(
        string victimAgentId,
        ResolutionStrategy strategy,
        string? suggestedAvoidTarget = null)
    {
        if (Status != DeadlockCaseStatus.Detected)
            throw new InvalidOperationException(
                $"{DeadlockErrorCodes.InvalidTransition}: cannot request resolution from {Status}.");
        if (string.IsNullOrWhiteSpace(victimAgentId))
            throw new ArgumentException(DeadlockErrorCodes.NoVictim, nameof(victimAgentId));
        if (!_agentIds.Contains(victimAgentId.Trim()))
            throw new ArgumentException(DeadlockErrorCodes.NoVictim, nameof(victimAgentId));

        VictimAgentId = victimAgentId.Trim();
        Strategy = strategy;
        SuggestedAvoidTarget = string.IsNullOrWhiteSpace(suggestedAvoidTarget) ? null : suggestedAvoidTarget.Trim();
        Status = DeadlockCaseStatus.Resolving;
        IncrementStateVersion();

        AddDomainEvent(new DeadlockCaseResolutionRequestedEvent(Id, VictimAgentId, strategy, SuggestedAvoidTarget));
    }

    /// <summary>
    /// Resolving → Resolved. Raises <see cref="DeadlockCaseResolvedEvent"/>.
    /// </summary>
    public void MarkResolved()
    {
        if (Status != DeadlockCaseStatus.Resolving)
            throw new InvalidOperationException(
                $"{DeadlockErrorCodes.InvalidTransition}: cannot resolve from {Status}.");

        Status = DeadlockCaseStatus.Resolved;
        IncrementStateVersion();

        AddDomainEvent(new DeadlockCaseResolvedEvent(Id, VictimAgentId ?? string.Empty));
    }

    /// <summary>
    /// Detected/Resolving → Escalated. Used when automatic resolution is not possible (e.g. no avoidance
    /// site / detour denied). Idempotent if already escalated.
    /// </summary>
    public void Escalate(string? reason = null)
    {
        if (Status is DeadlockCaseStatus.Resolved or DeadlockCaseStatus.Escalated)
        {
            if (Status == DeadlockCaseStatus.Escalated)
                return;
            throw new InvalidOperationException(
                $"{DeadlockErrorCodes.InvalidTransition}: cannot escalate from {Status}.");
        }

        Status = DeadlockCaseStatus.Escalated;
        IncrementStateVersion();
    }

    /// <summary>
    /// Detected/Resolving → Escalated, classified as <see cref="DeadlockKind.Livelock"/>, raising
    /// <see cref="DeadlockCaseEscalatedEvent"/>. Used by the anti-livelock guard when a redirected victim
    /// makes no progress (its distance to the original goal did not strictly decrease, or the only
    /// avoidance point would repeat). Unlike <see cref="Escalate"/> this carries the victim + raises an
    /// integration event so the fleet stops redirecting the victim. Idempotent if already escalated.
    /// </summary>
    public void EscalateLivelock(string? reason = null)
    {
        if (Status == DeadlockCaseStatus.Escalated)
            return;
        if (Status == DeadlockCaseStatus.Resolved)
            throw new InvalidOperationException(
                $"{DeadlockErrorCodes.InvalidTransition}: cannot escalate from {Status}.");

        Kind = DeadlockKind.Livelock;
        Status = DeadlockCaseStatus.Escalated;
        IncrementStateVersion();

        AddDomainEvent(new DeadlockCaseEscalatedEvent(Id, VictimAgentId ?? string.Empty, Kind, reason));
    }

    private void IncrementStateVersion()
    {
        checked { StateVersion++; }
        StateChangedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Optimistic-concurrency check.</summary>
    public bool CheckVersion(long expectedVersion) => StateVersion == expectedVersion;
}
