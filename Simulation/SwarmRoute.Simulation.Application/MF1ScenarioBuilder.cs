using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (FMS-V1 R2) Builds the fixed <b>M-F1</b> station demo: a single-row transit corridor crossed by transit AGVs,
/// plus one <see cref="StationType.HardBlocking"/> station whose dock hangs off the corridor on a dead-end stub
/// behind a pre-dock buffer, with a blocking closure over the corridor cell the servicing vehicle straddles.
/// <para>
/// Geometry on a 5×3 grid (ids <c>r{row}c{col}</c>); rows 1–2 are <b>carved</b> down to an L-shaped stub
/// (<c>r1c1 — r1c2 — r2c2</c>) so the corridor cell <c>r0c2</c> is the unique east–west cut — a transit AGV cannot
/// bypass the dock, and the dock AGV cannot leak onto the corridor while it waits:
/// </para>
/// <code>
///   row 0 (corridor):  r0c0 — r0c1 — r0c2 — r0c3 — r0c4    (transit AGVs, left→right; r0c2 = the only E-W path)
///                                      |
///   row 1 (stub)    :         r1c1 — r1c2                   (r1c1 = dock-AGV lane / parking; r1c2 = pre-dock buffer)
///                                      |
///   row 2 (dock)    :               r2c2                    (dock point D — a dead end)
/// </code>
/// (Obstacle cells, carved away: <c>r1c0, r1c3, r1c4, r2c0, r2c1, r2c3, r2c4</c>.)
/// <list type="bullet">
///   <item><b>Transit AGVs</b> run the corridor left→right (staggered, same direction so they never deadlock head-on);
///     because rows 1–2 are a dead-end stub, every transit AGV MUST cross the dock's corridor cell <c>r0c2</c>.</item>
///   <item><b>Dock AGV</b> starts on the row-1 stub (<c>r1c1</c>) and is bound for dock <c>r2c2</c>; its effective goal
///     starts at the buffer <c>r1c2</c> (the executor stages it there for admission), one hop away and never touching
///     the corridor — so it genuinely WAITS at the buffer rather than racing the transit AGVs for the corridor.</item>
///   <item><b>Blocking closure</b> = { CP <c>r0c2</c> }. While the dock AGV is in service the closure (plus the dock CP)
///     is leased, so a transit AGV cannot use <c>r0c2</c>; admission is therefore withheld until every transit AGV has
///     crossed the corridor (clearance-before-service, ADR-F3).</item>
/// </list>
/// The <see cref="StationDefinition.ServiceDurationMs"/> is long enough that, had the dock AGV docked immediately, the
/// in-service corridor block would have stalled the transit AGVs — so a passing test genuinely exercises the admission
/// hold rather than a coincidental ordering.
/// </summary>
public static class MF1ScenarioBuilder
{
    /// <summary>Grid width (corridor length in CPs).</summary>
    public const int Width = 5;

    /// <summary>Grid height (corridor row 0, buffer/lane row 1, dock row 2).</summary>
    public const int Height = 3;

    /// <summary>The station's dock-point control-point id (off the corridor, a dead end).</summary>
    public const string DockPoint = "r2c2";

    /// <summary>The station's pre-dock buffer control-point id (between corridor and dock).</summary>
    public const string PreDockBuffer = "r1c2";

    /// <summary>The corridor control point the servicing vehicle's footprint blocks (the unique E-W cut).</summary>
    public const string CorridorBlockedCell = "r0c2";

    /// <summary>The dock AGV's start / post-service parking slot on the row-1 stub.</summary>
    public const string DockAgvLane = "r1c1";

    /// <summary>The station id.</summary>
    public const string StationId = "MF1-station";

    /// <summary>The id of the single AGV assigned to the station (its goal is the dock point).</summary>
    public const string DockAgentIdConst = "dock-1";

    /// <summary>The nominal service duration in fleet-clock ms (== ticks; HopMs == 1). Long enough to outlast the
    /// transit AGVs' corridor crossing, so an immediate dock would have blocked them.</summary>
    public const long ServiceDurationMs = 40;

    /// <summary>The carved-away obstacle cells: rows 1–2 reduced to the L-shaped dock stub r1c1—r1c2—r2c2.</summary>
    private static readonly IReadOnlySet<string> Obstacles = new HashSet<string>(StringComparer.Ordinal)
    {
        "r1c0", "r1c3", "r1c4",
        "r2c0", "r2c1", "r2c3", "r2c4",
    };

    /// <summary>The assembled M-F1 scenario.</summary>
    /// <param name="Field">The carved 5×3 grid field (graph + render metadata).</param>
    /// <param name="Agents">The fleet: one dock AGV (goal = dock point) + transit AGVs crossing the corridor.</param>
    /// <param name="Fms">The FMS overlay (site roles + the single station + <see cref="ArrivalPolicy.ClearToParking"/>).</param>
    /// <param name="Catalog">The station catalog the engine wires the dock-admission scheduler over.</param>
    /// <param name="DockAgentId">The id of the AGV assigned to the station.</param>
    /// <param name="TransitAgentIds">The ids of the transit AGVs crossing the corridor.</param>
    public sealed record Scenario(
        GridField Field,
        IReadOnlyList<FleetAgentSpec> Agents,
        FmsScenario Fms,
        IStationCatalog Catalog,
        string DockAgentId,
        IReadOnlyList<string> TransitAgentIds);

    /// <summary>
    /// Builds the M-F1 scenario. <paramref name="transitAgvCount"/> transit AGVs (clamped to ≥ 2) cross the corridor
    /// left→right, staggered; the dock AGV is given the highest ordinal priority so the transit AGVs reserve the
    /// corridor first (the dock AGV then genuinely has to wait at its buffer for them to clear).
    /// </summary>
    public static Scenario Build(GridFieldFactory gridFactory, int transitAgvCount = 2)
    {
        ArgumentNullException.ThrowIfNull(gridFactory);
        // ≥ 2 transit AGVs (M-F1 requires "2+ transit AGVs"), and not so many that distinct corridor goals run out.
        var transit = Math.Clamp(transitAgvCount, 2, Width - 1);

        var field = gridFactory.BuildGrid(Width, Height, obstacles: Obstacles);

        // Blocking closure: the corridor cell the dock straddles, modelled as a CP lease over the frozen Kernel
        // vocabulary (ADR-F2 — no ResourceKind.Station). The dock CP itself is held implicitly by the service window
        // (StationResourceCalendar always leases the dock point), so the closure adds the corridor cell.
        var closure = new HashSet<ResourceRef> { new(ResourceKind.CP, CorridorBlockedCell) };

        var station = new StationDefinition(
            StationId: StationId,
            DockPoint: DockPoint,
            PreDockBuffers: new[] { PreDockBuffer },
            BlockingClosure: closure,
            ServiceDurationMs: ServiceDurationMs,
            StationType: StationType.HardBlocking);

        var catalog = new InMemoryStationCatalog(new[] { station });

        // Site roles: the corridor is transit, the buffer/dock carry their FMS roles, and the row-1 lane is a parking
        // slot so the serviced vehicle has somewhere to clear to under ClearToParking (r2c2 → r1c2 → r1c1).
        var roles = new Dictionary<string, SiteRole>(StringComparer.Ordinal)
        {
            [DockPoint] = SiteRole.DockPoint,
            [PreDockBuffer] = SiteRole.PreDockBuffer,
            [DockAgvLane] = SiteRole.Parking,
        };

        var fms = new FmsScenario(roles, new[] { station }, ArrivalPolicy.ClearToParking);

        // Fleet. Transit AGVs cross the corridor left→right (staggered starts/goals so they all pass r0c2 but never
        // meet head-on), with the lowest priorities so they plan/reserve the corridor first. The dock AGV starts on
        // the row-1 stub (so its approach to the buffer never touches the corridor) and is bound for the dock
        // (goal = r2c2), routed via the buffer; it gets the highest ordinal priority so it is planned last and
        // genuinely waits for the transit AGVs to clear the closure.
        var agents = new List<FleetAgentSpec>(transit + 1);
        var transitIds = new List<string>(transit);
        for (var i = 0; i < transit; i++)
        {
            // Staggered same-direction crossings: start at column i, finish packed against the right end. Each starts
            // on or left of r0c2 and finishes right of it, so every transit AGV traverses the corridor cell r0c2.
            var start = $"r0c{i}";
            var goal = $"r0c{Width - 1 - (transit - 1 - i)}";
            var id = $"transit-{i + 1}";
            transitIds.Add(id);
            agents.Add(new FleetAgentSpec(id, start, goal, Priority: i));
        }
        agents.Add(new FleetAgentSpec(DockAgentIdConst, DockAgvLane, DockPoint, Priority: transit));

        return new Scenario(field, agents, fms, catalog, DockAgentIdConst, transitIds);
    }
}
