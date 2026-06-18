using NetDevPack.Domain;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.TrafficControl.Domain.ValueObjects;

/// <summary>
/// A request to reserve a single resource, recorded when a grant is contended (denied/queued). Ports
/// <c>AJR.MAPF.Map.ResourceRequest</c> (<see cref="AgentId"/>, <see cref="ResourceId"/>,
/// <see cref="RequestTime"/>, <see cref="EstimateTime"/>, <see cref="HadWaitedTime"/>) and augments it with
/// the v0+ time-interval machinery (<see cref="Requested"/>) and an explicit <see cref="Priority"/> so the
/// deterministic right-of-way tie-break has all the inputs it needs.
/// </summary>
/// <remarks>
/// The request is the "Waits" edge the <c>ResourceAllocationGraphSnapshot</c> exposes to Deadlock, and the
/// thing <c>StaleRequestEscalationJob</c> ages (incrementing <see cref="HadWaitedTime"/>) to guarantee
/// liveness / no-starvation (invariant I7).
/// </remarks>
public sealed class ReservationRequest : ValueObject
{
    /// <summary>Creates a contended request for <paramref name="resourceId"/> by <paramref name="agentId"/>.</summary>
    /// <exception cref="ArgumentException">Thrown when ids are null/whitespace.</exception>
    public ReservationRequest(
        string agentId,
        string resourceId,
        DateTime requestTime,
        int estimateTime,
        int hadWaitedTime,
        TimeInterval requested,
        int priority)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Request agentId must be provided.", nameof(agentId));
        if (string.IsNullOrWhiteSpace(resourceId))
            throw new ArgumentException("Request resourceId must be provided.", nameof(resourceId));

        AgentId = agentId;
        ResourceId = resourceId;
        RequestTime = requestTime;
        EstimateTime = estimateTime;
        HadWaitedTime = hadWaitedTime;
        Requested = requested;
        Priority = priority;
    }

    /// <summary>The requesting agent / vehicle id (ports <c>ResourceRequest.AgentId</c>).</summary>
    public string AgentId { get; }

    /// <summary>The requested resource id (ports <c>ResourceRequest.ResourceId</c>).</summary>
    public string ResourceId { get; }

    /// <summary>Wall-clock time the request was made (ports <c>ResourceRequest.RequestTime</c>).</summary>
    public DateTime RequestTime { get; }

    /// <summary>Estimated occupancy duration, seconds (ports <c>ResourceRequest.EstimateTime</c>).</summary>
    public int EstimateTime { get; }

    /// <summary>Accumulated time already spent waiting, seconds (ports <c>ResourceRequest.HadWaitedTime</c>); aged for fairness.</summary>
    public int HadWaitedTime { get; }

    /// <summary>The half-open fleet-clock window the agent wanted the resource for (v0+ time machinery).</summary>
    public TimeInterval Requested { get; }

    /// <summary>The agent's scheduling priority; higher wins the right-of-way tie-break first.</summary>
    public int Priority { get; }

    /// <summary>Returns a copy with <see cref="HadWaitedTime"/> increased by <paramref name="seconds"/> (used by escalation).</summary>
    public ReservationRequest AgedBy(int seconds)
        => new(AgentId, ResourceId, RequestTime, EstimateTime, HadWaitedTime + seconds, Requested, Priority);

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return AgentId;
        yield return ResourceId;
        yield return RequestTime;
        yield return EstimateTime;
        yield return HadWaitedTime;
        yield return Requested;
        yield return Priority;
    }
}
