using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Cbs;

/// <summary>
/// Overlays one agent's CBS constraints onto the rest-of-fleet reservation view, so the existing
/// <c>SippPathPlanner</c> can serve as the CBS low-level constrained single-agent search unchanged. The key
/// identity: a CBS constraint "agent X must not occupy/traverse R during <c>[t,t+1)</c>" is exactly "R is busy
/// during <c>[t,t+1)</c>" from the planner's point of view. <see cref="FreeIntervals"/> therefore subtracts the
/// constraint windows from the external safe intervals, and <see cref="IsFree"/> ANDs the external answer with
/// "no constraint covers this window". This is the bridge that makes CBS reuse the battle-tested space-time
/// search (no reimplementation, guaranteed axis-consistent and reservable output).
/// </summary>
public sealed class CbsConstraintView : IReservationView
{
    private readonly IReservationView _external;
    private readonly Dictionary<ResourceRef, List<TimeInterval>> _blocked;

    /// <summary>
    /// Wraps <paramref name="external"/> (the rest of the fleet's live reservations) with the busy windows implied
    /// by <paramref name="constraintsForAgent"/> (already filtered to the single agent being planned).
    /// </summary>
    public CbsConstraintView(IReservationView external, IEnumerable<CbsConstraint> constraintsForAgent)
    {
        ArgumentNullException.ThrowIfNull(external);
        ArgumentNullException.ThrowIfNull(constraintsForAgent);

        _external = external;
        _blocked = constraintsForAgent
            .GroupBy(c => c.Resource)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Interval).ToList());
    }

    /// <inheritdoc />
    public bool IsFree(ResourceRef resource, TimeInterval interval)
        => _external.IsFree(resource, interval)
           && (!_blocked.TryGetValue(resource, out var windows) || windows.TrueForAll(w => !w.Overlaps(interval)));

    /// <inheritdoc />
    public IEnumerable<SafeInterval> FreeIntervals(ResourceRef resource)
    {
        if (!_blocked.TryGetValue(resource, out var windows) || windows.Count == 0)
        {
            foreach (var safe in _external.FreeIntervals(resource))
                yield return safe;
            yield break;
        }

        foreach (var safe in _external.FreeIntervals(resource))
            foreach (var piece in Subtract(safe.Interval, windows))
                yield return new SafeInterval(resource, piece);
    }

    /// <summary>Yields the maximal sub-intervals of <paramref name="free"/> not covered by any blocked window.</summary>
    private static IEnumerable<TimeInterval> Subtract(TimeInterval free, List<TimeInterval> blocked)
    {
        var cursor = free.StartMs;
        foreach (var w in blocked.Where(w => w.Overlaps(free)).OrderBy(w => w.StartMs))
        {
            var clampStart = Math.Max(w.StartMs, free.StartMs);
            var clampEnd = Math.Min(w.EndMs, free.EndMs);
            if (clampStart > cursor)
                yield return new TimeInterval(cursor, clampStart);
            cursor = Math.Max(cursor, clampEnd);
            if (cursor >= free.EndMs)
                yield break;
        }
        if (cursor < free.EndMs)
            yield return new TimeInterval(cursor, free.EndMs);
    }
}
