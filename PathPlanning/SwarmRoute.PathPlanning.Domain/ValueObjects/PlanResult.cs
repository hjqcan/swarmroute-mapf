using NetDevPack.Domain;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.ValueObjects;

/// <summary>
/// The outcome of a single planning attempt: either success — carrying the computed
/// <see cref="SpaceTimePath"/> and its <see cref="PlanCost"/> — or failure, carrying a human-readable
/// <see cref="FailureReason"/>. A discriminated result returned by <c>IPathPlanner.Plan</c>.
/// <para>
/// Mirrors the two branches of <c>CBS.SearchPath</c>: a non-null Dijkstra path → <see cref="Succeeded"/>;
/// a null path (endpoint blocked / no route) → <see cref="Failed"/>.
/// </para>
/// </summary>
public sealed class PlanResult : ValueObject
{
    private PlanResult(bool success, SpaceTimePath? path, PlanCost? cost, string? failureReason, bool reachesGoal)
    {
        Success = success;
        Path = path;
        Cost = cost;
        FailureReason = failureReason;
        ReachesGoal = reachesGoal;
    }

    /// <summary>True when a valid path was found.</summary>
    public bool Success { get; }

    /// <summary>The computed space-time path, or <c>null</c> on failure.</summary>
    public SpaceTimePath? Path { get; }

    /// <summary>The cost of the computed path, or <c>null</c> on failure.</summary>
    public PlanCost? Cost { get; }

    /// <summary>The failure reason, or <c>null</c> on success.</summary>
    public string? FailureReason { get; }

    /// <summary>
    /// True when the returned <see cref="Path"/> reaches the requested goal site. Always true for a horizon-free
    /// (whole-path) plan and false on failure. Under a rolling horizon (RHCR) a successful plan may be a partial
    /// route truncated at the window frontier — then this is false, signalling the executor to re-plan the next
    /// window on arrival instead of parking. Lets callers branch without re-deriving goal-equality from the path.
    /// </summary>
    public bool ReachesGoal { get; }

    /// <summary>Creates a successful result for <paramref name="path"/> at <paramref name="cost"/>.</summary>
    /// <param name="reachesGoal">
    /// Whether <paramref name="path"/> reaches the requested goal (true) or is a horizon-truncated partial route
    /// toward it (false). Defaults to true so existing whole-path planners are unaffected.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> or <paramref name="cost"/> is null.</exception>
    public static PlanResult Succeeded(SpaceTimePath path, PlanCost cost, bool reachesGoal = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(cost);
        return new PlanResult(true, path, cost, failureReason: null, reachesGoal: reachesGoal);
    }

    /// <summary>Creates a failed result with the given <paramref name="reason"/>.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reason"/> is null/whitespace.</exception>
    public static PlanResult Failed(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Failure reason must not be empty.", nameof(reason));
        return new PlanResult(false, path: null, cost: null, failureReason: reason, reachesGoal: false);
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Success;
        yield return Path is null ? "<null>" : string.Join("|", Path.Cells.Select(c => $"{c.Resource.Kind}:{c.Resource.Id}@[{c.Interval.StartMs},{c.Interval.EndMs})"));
        yield return Cost ?? PlanCost.Zero;
        yield return FailureReason ?? "<null>";
        yield return ReachesGoal;
    }
}
