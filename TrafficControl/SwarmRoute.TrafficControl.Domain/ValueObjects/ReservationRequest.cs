using NetDevPack.Domain;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.TrafficControl.Domain.ValueObjects;

/// <summary>
/// A request to reserve a single resource, recorded when a grant is contended (denied/queued). Ports
/// <c>AJR.MAPF.Map.ResourceRequest</c> (<see cref="AgentId"/>, <see cref="Resource"/>,
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
        : this(
            agentId,
            new ResourceRef(ResourceKind.CP, resourceId),
            requestTime,
            estimateTime,
            hadWaitedTime,
            requested,
            priority)
    {
    }

    /// <summary>Creates a contended request for <paramref name="resource"/> by <paramref name="agentId"/>.</summary>
    /// <exception cref="ArgumentException">Thrown when ids are null/whitespace.</exception>
    public ReservationRequest(
        string agentId,
        ResourceRef resource,
        DateTime requestTime,
        int estimateTime,
        int hadWaitedTime,
        TimeInterval requested,
        int priority)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Request agentId must be provided.", nameof(agentId));
        if (string.IsNullOrWhiteSpace(resource.Id))
            throw new ArgumentException("Request resource id must be provided.", nameof(resource));

        AgentId = agentId;
        Resource = new ResourceRef(resource.Kind, resource.Id.Trim());
        RequestTime = requestTime;
        EstimateTime = estimateTime;
        HadWaitedTime = hadWaitedTime;
        Requested = requested;
        Priority = priority;
    }

    /// <summary>The requesting agent / vehicle id (ports <c>ResourceRequest.AgentId</c>).</summary>
    public string AgentId { get; }

    /// <summary>The requested resource, including its kind so CP/Lane/Block ids cannot collide.</summary>
    public ResourceRef Resource { get; }

    /// <summary>The requested resource id (ports <c>ResourceRequest.ResourceId</c>); kind lives in <see cref="Resource"/>.</summary>
    public string ResourceId => Resource.Id;

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
        => new(AgentId, Resource, RequestTime, EstimateTime, HadWaitedTime + seconds, Requested, Priority);

    /// <summary>
    /// Returns a single request edge for repeated waits by the same agent on the same resource.
    /// </summary>
    public ReservationRequest MergedWith(ReservationRequest other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(AgentId, other.AgentId, StringComparison.Ordinal) || !Resource.Equals(other.Resource))
            throw new ArgumentException("Only requests for the same agent and resource can be merged.", nameof(other));

        var requestTime = RequestTime <= other.RequestTime ? RequestTime : other.RequestTime;
        var requested = new TimeInterval(
            Math.Min(Requested.StartMs, other.Requested.StartMs),
            Math.Max(Requested.EndMs, other.Requested.EndMs));

        return new ReservationRequest(
            AgentId,
            Resource,
            requestTime,
            Math.Max(EstimateTime, other.EstimateTime),
            Math.Max(HadWaitedTime, other.HadWaitedTime),
            requested,
            Math.Max(Priority, other.Priority));
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return AgentId;
        yield return Resource;
        yield return RequestTime;
        yield return EstimateTime;
        yield return HadWaitedTime;
        yield return Requested;
        yield return Priority;
    }
}
