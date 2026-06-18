using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Tests.TestSupport;

/// <summary>
/// A hand-configurable <see cref="IReservationView"/> for SIPP tests: every resource is free except the busy
/// windows declared via <see cref="Reserve"/>. <see cref="FreeIntervals"/> returns the complement of the busy
/// windows over <c>[0, long.MaxValue)</c> (the same shape the real reservation table exposes — gaps plus an
/// open-ended tail), and <see cref="IsFree"/> answers from the busy windows directly.
/// <para>
/// Note: the real <c>SnapshotReservationView</c> additionally treats a directed lane and its reverse as
/// conflicting (so a held <c>B-A</c> makes <c>A-B</c> busy). That equivalence is the table's contract and is
/// tested there; here, reserving lane <c>A-B</c> directly models how such a conflict surfaces to the planner.
/// </para>
/// </summary>
internal sealed class FakeReservationView : IReservationView
{
    private readonly Dictionary<ResourceRef, List<TimeInterval>> _busy = new();

    /// <summary>Marks <paramref name="resource"/> reserved (busy) for the half-open window [<paramref name="startMs"/>, <paramref name="endMs"/>).</summary>
    public FakeReservationView Reserve(ResourceRef resource, long startMs, long endMs)
    {
        if (!_busy.TryGetValue(resource, out var windows))
            _busy[resource] = windows = new List<TimeInterval>();
        windows.Add(new TimeInterval(startMs, endMs));
        return this;
    }

    /// <inheritdoc />
    public bool IsFree(ResourceRef resource, TimeInterval interval)
        => !_busy.TryGetValue(resource, out var windows) || windows.TrueForAll(w => !w.Overlaps(interval));

    /// <inheritdoc />
    public IEnumerable<SafeInterval> FreeIntervals(ResourceRef resource)
    {
        if (!_busy.TryGetValue(resource, out var windows) || windows.Count == 0)
        {
            yield return new SafeInterval(resource, new TimeInterval(0, long.MaxValue));
            yield break;
        }

        var cursor = 0L;
        foreach (var window in windows.OrderBy(w => w.StartMs))
        {
            if (window.StartMs > cursor)
                yield return new SafeInterval(resource, new TimeInterval(cursor, window.StartMs));
            cursor = Math.Max(cursor, window.EndMs);
        }

        if (cursor < long.MaxValue)
            yield return new SafeInterval(resource, new TimeInterval(cursor, long.MaxValue));
    }
}
