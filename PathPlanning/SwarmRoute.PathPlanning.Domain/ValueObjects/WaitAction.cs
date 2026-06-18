using NetDevPack.Domain;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.ValueObjects;

/// <summary>
/// A planned "wait in place" segment: the agent holds station <see cref="SiteId"/> for the half-open
/// <see cref="Interval"/> rather than moving. This is the dual of a move and is what a reservation-aware
/// planner inserts to let another vehicle pass (SIPP, v1).
/// <para>
/// v0's Dijkstra planner never emits waits (its timeline is move-only), but the value object is defined now
/// so the v1 planner and the coordinator share a stable vocabulary. <see cref="AsCell"/> projects the wait
/// onto a Kernel <see cref="SpaceTimeCell"/> on the held control point.
/// </para>
/// </summary>
public sealed class WaitAction : ValueObject
{
    /// <summary>
    /// Creates a wait at <paramref name="siteId"/> spanning <paramref name="interval"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="siteId"/> is null/whitespace.</exception>
    public WaitAction(string siteId, TimeInterval interval)
    {
        if (string.IsNullOrWhiteSpace(siteId))
            throw new ArgumentException("Wait site id must not be empty.", nameof(siteId));

        SiteId = siteId.Trim();
        Interval = interval;
    }

    /// <summary>The control point held during the wait.</summary>
    public string SiteId { get; }

    /// <summary>The half-open window of the wait, in fleet-clock milliseconds.</summary>
    public TimeInterval Interval { get; }

    /// <summary>Duration of the wait in milliseconds.</summary>
    public long DurationMs => Interval.Duration;

    /// <summary>Projects the wait onto a Kernel space-time cell on the held control point.</summary>
    public SpaceTimeCell AsCell() => new(new ResourceRef(ResourceKind.CP, SiteId), Interval);

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return SiteId;
        yield return Interval;
    }
}
