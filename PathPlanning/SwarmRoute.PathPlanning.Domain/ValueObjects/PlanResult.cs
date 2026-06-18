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
    private PlanResult(bool success, SpaceTimePath? path, PlanCost? cost, string? failureReason)
    {
        Success = success;
        Path = path;
        Cost = cost;
        FailureReason = failureReason;
    }

    /// <summary>True when a valid path was found.</summary>
    public bool Success { get; }

    /// <summary>The computed space-time path, or <c>null</c> on failure.</summary>
    public SpaceTimePath? Path { get; }

    /// <summary>The cost of the computed path, or <c>null</c> on failure.</summary>
    public PlanCost? Cost { get; }

    /// <summary>The failure reason, or <c>null</c> on success.</summary>
    public string? FailureReason { get; }

    /// <summary>Creates a successful result for <paramref name="path"/> at <paramref name="cost"/>.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="path"/> or <paramref name="cost"/> is null.</exception>
    public static PlanResult Succeeded(SpaceTimePath path, PlanCost cost)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(cost);
        return new PlanResult(true, path, cost, failureReason: null);
    }

    /// <summary>Creates a failed result with the given <paramref name="reason"/>.</summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reason"/> is null/whitespace.</exception>
    public static PlanResult Failed(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Failure reason must not be empty.", nameof(reason));
        return new PlanResult(false, path: null, cost: null, failureReason: reason);
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Success;
        yield return Path is null ? "<null>" : string.Join("|", Path.Cells.Select(c => $"{c.Resource.Kind}:{c.Resource.Id}@[{c.Interval.StartMs},{c.Interval.EndMs})"));
        yield return Cost ?? PlanCost.Zero;
        yield return FailureReason ?? "<null>";
    }
}
