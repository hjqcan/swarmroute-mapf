using NetDevPack.Domain;
using SwarmRoute.PathPlanning.Domain.Events;
using SwarmRoute.PathPlanning.Domain.Shared;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Aggregates;

/// <summary>
/// The plan aggregate root for a single agent: its goal, the current space-time route and its
/// <see cref="PlanStatus"/>. Although PathPlanning is a non-persisted compute context (no EF), the plan is
/// modelled as a proper aggregate so its state transitions are explicit, validated and event-raising — the
/// coordinator owns an <see cref="AgentPlan"/> per vehicle and re-plans it across ticks.
/// <para>
/// Behaviours (<see cref="Replan"/>, <see cref="Invalidate"/>) mutate state, bump
/// <see cref="StateVersion"/> (optimistic concurrency) and raise the corresponding integration event,
/// mirroring the grukirbs tactical conventions.
/// </para>
/// </summary>
public class AgentPlan : Entity, IAggregateRoot
{
    // Parameterless ctor kept for symmetry with the grukirbs convention (EF is not used in this context).
    private AgentPlan()
    {
        AgentId = string.Empty;
        FromSiteId = string.Empty;
        ToSiteId = string.Empty;
        PlannerKind = PlannerKind.Dijkstra;
        Status = PlanStatus.Failed;
    }

    /// <summary>
    /// Creates an <see cref="AgentPlan"/> from the outcome of an initial planning attempt.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when ids are empty or the result is inconsistent.</exception>
    public AgentPlan(
        Guid id,
        Guid roadmapId,
        string agentId,
        string fromSiteId,
        string toSiteId,
        PlanResult result,
        PlannerKind plannerKind = PlannerKind.Dijkstra)
    {
        if (id == Guid.Empty)
            throw new ArgumentException($"[{PathPlanningErrorCodes.MissingIdentifier}] AgentPlan id must not be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException($"[{PathPlanningErrorCodes.MissingIdentifier}] Agent id must not be empty.", nameof(agentId));
        if (string.IsNullOrWhiteSpace(fromSiteId))
            throw new ArgumentException($"[{PathPlanningErrorCodes.MissingIdentifier}] From-site id must not be empty.", nameof(fromSiteId));
        if (string.IsNullOrWhiteSpace(toSiteId))
            throw new ArgumentException($"[{PathPlanningErrorCodes.MissingIdentifier}] To-site id must not be empty.", nameof(toSiteId));
        ArgumentNullException.ThrowIfNull(result);

        Id = id;
        RoadmapId = roadmapId;
        AgentId = agentId.Trim();
        FromSiteId = fromSiteId.Trim();
        ToSiteId = toSiteId.Trim();
        PlannerKind = plannerKind;
        StateVersion = 1;
        StateChangedAtUtc = DateTimeOffset.UtcNow;

        Apply(result);
    }

    /// <summary>The roadmap the plan is computed against.</summary>
    public Guid RoadmapId { get; private set; }

    /// <summary>The agent (vehicle) this plan is for.</summary>
    public string AgentId { get; private set; }

    /// <summary>The start site of the current goal.</summary>
    public string FromSiteId { get; private set; }

    /// <summary>The goal site.</summary>
    public string ToSiteId { get; private set; }

    /// <summary>Which planner produced the current state.</summary>
    public PlannerKind PlannerKind { get; private set; }

    /// <summary>Lifecycle status of the plan.</summary>
    public PlanStatus Status { get; private set; }

    /// <summary>The current space-time route, or <c>null</c> when the plan is failed / superseded with no route.</summary>
    public SpaceTimePath? Path { get; private set; }

    /// <summary>The cost of the current route, or <c>null</c> when there is none.</summary>
    public PlanCost? Cost { get; private set; }

    /// <summary>The reason the plan is in a non-computed state, or <c>null</c> when computed.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Optimistic-concurrency version, incremented on every behaviour.</summary>
    public long StateVersion { get; private set; }

    /// <summary>UTC timestamp of the last state change.</summary>
    public DateTimeOffset? StateChangedAtUtc { get; private set; }

    /// <summary>True when the plan currently carries a valid route.</summary>
    public bool HasPath => Status == PlanStatus.Computed && Path is not null;

    /// <summary>
    /// Re-plans the agent toward a (possibly new) goal from the outcome of a fresh planning attempt, bumping
    /// the version and raising <c>PathPlanning.AgentPlan.Computed</c> or <c>.Failed</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when site ids are empty or the result is inconsistent.</exception>
    public void Replan(string fromSiteId, string toSiteId, PlanResult result)
    {
        if (string.IsNullOrWhiteSpace(fromSiteId))
            throw new ArgumentException($"[{PathPlanningErrorCodes.MissingIdentifier}] From-site id must not be empty.", nameof(fromSiteId));
        if (string.IsNullOrWhiteSpace(toSiteId))
            throw new ArgumentException($"[{PathPlanningErrorCodes.MissingIdentifier}] To-site id must not be empty.", nameof(toSiteId));
        ArgumentNullException.ThrowIfNull(result);

        FromSiteId = fromSiteId.Trim();
        ToSiteId = toSiteId.Trim();
        IncrementStateVersion();
        Apply(result);
    }

    /// <summary>
    /// Invalidates the current plan (e.g. topology change, contended resource), marking it
    /// <see cref="PlanStatus.Superseded"/>, dropping any route and raising <c>PathPlanning.AgentPlan.Failed</c>
    /// with the supplied <paramref name="reason"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reason"/> is null/whitespace.</exception>
    public void Invalidate(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException($"[{PathPlanningErrorCodes.InvalidPlanTransition}] Invalidation reason must not be empty.", nameof(reason));

        Status = PlanStatus.Superseded;
        Path = null;
        Cost = null;
        FailureReason = reason;
        IncrementStateVersion();

        AddDomainEvent(new AgentPlanFailedEvent(Id, RoadmapId, AgentId, FromSiteId, ToSiteId, StateVersion, reason));
    }

    /// <summary>Applies a <see cref="PlanResult"/> to the aggregate state and raises the matching event.</summary>
    private void Apply(PlanResult result)
    {
        if (result.Success)
        {
            // Success implies a path + cost (guaranteed by PlanResult.Succeeded).
            Status = PlanStatus.Computed;
            Path = result.Path;
            Cost = result.Cost;
            FailureReason = null;

            var siteSequence = ExtractSiteSequence(result.Path!);
            var cost = result.Cost!;
            AddDomainEvent(new AgentPlanComputedEvent(
                Id, RoadmapId, AgentId, FromSiteId, ToSiteId, StateVersion,
                siteSequence, cost.DistanceUnits, cost.HopCount, cost.DurationMs));
        }
        else
        {
            Status = PlanStatus.Failed;
            Path = null;
            Cost = null;
            FailureReason = result.FailureReason;

            AddDomainEvent(new AgentPlanFailedEvent(
                Id, RoadmapId, AgentId, FromSiteId, ToSiteId, StateVersion,
                result.FailureReason ?? "Planning failed."));
        }
    }

    /// <summary>Projects a space-time path back to its ordered control-point (site) id sequence.</summary>
    private static IReadOnlyList<string> ExtractSiteSequence(SpaceTimePath path)
        => path.Cells
            .Where(c => c.Resource.Kind == ResourceKind.CP)
            .Select(c => c.Resource.Id)
            .ToList();

    private void IncrementStateVersion()
    {
        checked { StateVersion++; }
        StateChangedAtUtc = DateTimeOffset.UtcNow;
    }
}
