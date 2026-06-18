using NetDevPack.Domain;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Domain.ValueObjects;

/// <summary>
/// A single granted (or in-flight) hold of one roadmap <see cref="ResourceRef"/> by one agent over one
/// half-open <see cref="TimeInterval"/>. This is the time-interval successor to the original engine's
/// <c>MapResource.OccupiedBy</c> + <c>MapResource.Status</c> (Locked/Unlocked): instead of a binary lock,
/// a lease carries <em>who</em> holds <em>what</em> for <em>which time window</em> in <em>which lifecycle state</em>.
/// </summary>
/// <remarks>
/// Immutable value object (grukirbs convention). State changes (e.g. <see cref="WithState"/>) return a new
/// instance; the owning <c>ReservationTable</c> aggregate swaps leases in its indices. Equality is by all
/// four components so two leases on the same resource by different agents (or different windows) are distinct.
/// </remarks>
public sealed class ResourceLease : ValueObject
{
    /// <summary>Creates a lease of <paramref name="resource"/> for <paramref name="agentId"/> over <paramref name="interval"/>.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="agentId"/> is null or whitespace.</exception>
    public ResourceLease(ResourceRef resource, string agentId, TimeInterval interval, LeaseState state)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Lease agentId must be provided.", nameof(agentId));

        Resource = resource;
        AgentId = agentId;
        Interval = interval;
        State = state;
    }

    /// <summary>The roadmap resource held by this lease.</summary>
    public ResourceRef Resource { get; }

    /// <summary>The agent that holds the lease.</summary>
    public string AgentId { get; }

    /// <summary>The half-open time window the lease covers.</summary>
    public TimeInterval Interval { get; }

    /// <summary>The lifecycle state of the lease.</summary>
    public LeaseState State { get; }

    /// <summary>
    /// True when this lease conflicts with <paramref name="other"/>: same resource, overlapping interval,
    /// but a <em>different</em> agent. The same agent may hold overlapping windows of one resource (e.g. a
    /// dwell that spans entry and exit), which is not a conflict.
    /// </summary>
    public bool ConflictsWith(ResourceLease other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Resource.Equals(other.Resource)
               && !string.Equals(AgentId, other.AgentId, StringComparison.Ordinal)
               && Interval.Overlaps(other.Interval);
    }

    /// <summary>Returns a copy of this lease in <paramref name="state"/> (value-object "with" semantics).</summary>
    public ResourceLease WithState(LeaseState state) => new(Resource, AgentId, Interval, state);

    /// <summary>True when the lease's window has fully elapsed at fleet-clock instant <paramref name="nowMs"/>.</summary>
    public bool HasExpiredAt(long nowMs) => Interval.EndMs <= nowMs;

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Resource;
        yield return AgentId;
        yield return Interval;
        yield return State;
    }
}
