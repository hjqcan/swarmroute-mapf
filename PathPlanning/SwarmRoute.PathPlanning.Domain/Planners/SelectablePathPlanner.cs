using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Domain.Planners;

/// <summary>
/// The registered <see cref="IPathPlanner"/>: a thin dispatcher that forwards each <see cref="Plan"/> call to the
/// concrete planner chosen by <see cref="PlannerOptions.Default"/> (v0 <see cref="DijkstraPathPlanner"/> or v1
/// <see cref="SippPathPlanner"/>). Keeping the choice here means the frozen <see cref="IPathPlanner.Plan"/> seam
/// and the coordination cycle that calls it never change as the rollout flips the default from Dijkstra to SIPP.
/// </summary>
public sealed class SelectablePathPlanner : IPathPlanner
{
    private readonly DijkstraPathPlanner _dijkstra;
    private readonly SippPathPlanner _sipp;
    private readonly SippwrtPathPlanner _sippwrt;
    private readonly PlannerOptions _options;

    public SelectablePathPlanner(DijkstraPathPlanner dijkstra, SippPathPlanner sipp, SippwrtPathPlanner sippwrt, PlannerOptions options)
    {
        _dijkstra = dijkstra ?? throw new ArgumentNullException(nameof(dijkstra));
        _sipp = sipp ?? throw new ArgumentNullException(nameof(sipp));
        _sippwrt = sippwrt ?? throw new ArgumentNullException(nameof(sippwrt));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>The planner currently selected (for diagnostics / status reporting).</summary>
    public PlannerKind Active => _options.Default;

    /// <inheritdoc />
    public PlanResult Plan(RoadmapGraph graph, PlanRequest request, IReservationView reservations)
        => Select().Plan(graph, request, reservations);

    private IPathPlanner Select() => _options.Default switch
    {
        PlannerKind.Sippwrt => _sippwrt,
        PlannerKind.Sipp => _sipp,
        _ => _dijkstra,
    };
}
