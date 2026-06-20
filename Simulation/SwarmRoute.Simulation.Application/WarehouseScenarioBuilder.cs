using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Endpoints;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Dispatch.Domain.Topology;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (FMS-V2) Builds a <b>well-formed warehouse</b> closed-loop scenario over a plain grid (良構倉儲情境). Unlike the
/// fixed <see cref="MF1ScenarioBuilder"/> demo, this is generated for an arbitrary <c>width × height × agvCount</c>
/// from a seed.
/// <para>
/// <b>Safe-rest set.</b> The <see cref="WellFormedEndpointGenerator"/> carves a well-formed endpoint set — cells
/// that can be occupied indefinitely without disconnecting the transit core — and this builder then GREEDILY
/// EXTENDS that set with extra cells, re-checking after each that the transit core (all cells minus the safe set)
/// stays connected. The result is a <em>collectively</em> safe rest set: a vehicle may park on ANY subset of it and
/// the core remains navigable, so a parked vehicle can never wall off another agent's goal. EVERY AGV goal is drawn
/// from this set — that is the scenario-semantics fix that removes the permanent goal-blocking which dominates a
/// random-stress run (M-F2).
/// </para>
/// <para>
/// <b>Fleet.</b> Each AGV starts on a transit-core cell (it departs at once, leaving the core clear). A small number
/// of AGVs are given a <em>workstation</em> task goal — modelled as a bufferless
/// <see cref="StationType.NonBlocking"/> station (service occupies only the dock CP) — so the executor drives the AGV
/// onto the workstation, runs a short service, then clears it to a real parking slot via the injected
/// <see cref="Dispatch.Application.Contract.IParkingManager"/> (proving clear-to-parking). Every other AGV is a plain
/// transit mover whose goal is a distinct parking endpoint from the safe set. No AGV ever permanently parks on an
/// unsafe (core) cell, so the run never permanently blocks a goal.
/// </para>
/// </summary>
public static class WarehouseScenarioBuilder
{
    /// <summary>Service duration (ms == ticks; HopMs == 1) at a warehouse workstation. Deliberately ONE tick — the
    /// warehouse point is throughput + that a serviced AGV clears to parking, not a long blocking service (that is
    /// M-F1's job). A longer service only adds dock-occupancy congestion on a dense grid.</summary>
    public const long ServiceDurationMs = 1;

    /// <summary>The assembled warehouse scenario.</summary>
    /// <param name="Field">The generated grid field (graph + render metadata).</param>
    /// <param name="Agents">The fleet (workstation AGVs first, then transit AGVs), in priority order.</param>
    /// <param name="Fms">The FMS overlay: site roles (workstations / parkings / transit) + the stations + ClearToParking.</param>
    /// <param name="Catalog">The station catalog the engine wires the dock-admission scheduler over.</param>
    /// <param name="Endpoints">The safe rest partition actually used (the chosen workstations as
    /// <see cref="EndpointSet.Workstations"/>, every other safe-rest cell as <see cref="EndpointSet.Parkings"/>).</param>
    /// <param name="StationAgentIds">The AGVs assigned a workstation goal (which must end on a parking/buffer after service).</param>
    public sealed record Scenario(
        GridField Field,
        IReadOnlyList<FleetAgentSpec> Agents,
        FmsScenario Fms,
        IStationCatalog Catalog,
        EndpointSet Endpoints,
        IReadOnlyList<string> StationAgentIds);

    /// <summary>
    /// Builds a well-formed warehouse scenario for a <paramref name="width"/>×<paramref name="height"/> grid and
    /// <paramref name="agvCount"/> AGVs, seeded by <paramref name="seed"/> for reproducibility.
    /// </summary>
    /// <exception cref="ArgumentNullException">If <paramref name="gridFactory"/> is null.</exception>
    /// <exception cref="ArgumentException">If the grid cannot host the fleet (fewer free cells than AGVs need, or no
    /// safe rest cell can be carved for a task goal).</exception>
    public static Scenario Build(GridFieldFactory gridFactory, int width, int height, int agvCount, int seed)
    {
        ArgumentNullException.ThrowIfNull(gridFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(agvCount, 1);

        var field = gridFactory.BuildGrid(width, height);
        var graph = field.Graph;
        var allCells = field.Sites.Select(s => s.Id).OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (allCells.Count < agvCount + 1)
            throw new ArgumentException(
                $"A {width}x{height} grid has {allCells.Count} free cell(s), too few for {agvCount} AGV(s) " +
                "(each needs a start, plus a safe cell to rest on).", nameof(agvCount));

        // 1. The collectively-safe rest set: start from the well-formed endpoint set, then greedily extend it with
        //    more cells (re-checking the core stays connected after each) so it is large enough to give EVERY AGV a
        //    distinct safe goal where possible. Parking on any subset of this set keeps the core navigable.
        var safe = BuildSafeRestSet(graph, allCells, targetSize: agvCount, seed: seed);
        if (safe.Count == 0)
            throw new ArgumentException(
                $"No safe rest cell could be carved from the {width}x{height} grid for a task goal.", nameof(agvCount));
        var safeSet = new HashSet<string>(safe, StringComparer.Ordinal);

        // 2. Partition the safe set: a small number of workstations (task goals that trigger a service + clear), the
        //    rest parkings (resting slots / transit goals). The workstation budget is deliberately small — enough to
        //    exercise + prove clear-to-parking, not so many that the service detours congest the dense grid.
        Shuffle(safe, seed);
        var workstationBudget = WorkstationBudget(agvCount, safe.Count);
        var workstations = safe.Take(workstationBudget).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var parkings = safe.Skip(workstationBudget).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var workstationSet = new HashSet<string>(workstations, StringComparer.Ordinal);
        var parkingSet = new HashSet<string>(parkings, StringComparer.Ordinal);

        // 3. Site roles: every safe-rest cell is a workstation or a parking; all other cells stay Transit (unmapped
        //    ⇒ Transit). Both kinds are safe to park on, so any parked vehicle keeps the core connected.
        var roles = new Dictionary<string, SiteRole>(StringComparer.Ordinal);
        foreach (var w in workstations)
            roles[w] = SiteRole.Workstation;
        foreach (var p in parkings)
            roles[p] = SiteRole.Parking;

        // 4. Stations: one bufferless NonBlocking station per workstation AGV. NonBlocking + empty closure ⇒ service
        //    occupies only the dock CP, never severs transit; bufferless ⇒ the AGV docks directly (admission skipped)
        //    and on completion clears to a free parking via the parking manager. A workstation AGV needs a distinct
        //    workstation goal AND a distinct free parking to clear into, so its count is bounded by both pools.
        var stationAgvCount = Math.Min(agvCount, Math.Min(workstations.Count, parkings.Count));
        var stationDefs = new List<StationDefinition>(stationAgvCount);
        for (var i = 0; i < stationAgvCount; i++)
            stationDefs.Add(new StationDefinition(
                StationId: $"ws-{workstations[i]}",
                DockPoint: workstations[i],
                PreDockBuffers: Array.Empty<string>(),
                BlockingClosure: EmptyClosure,
                ServiceDurationMs: ServiceDurationMs,
                StationType: StationType.NonBlocking));

        var catalog = new InMemoryStationCatalog(stationDefs);
        var fms = new FmsScenario(roles, stationDefs, ArrivalPolicy.ClearToParking);

        // 5. Goals. Workstation AGVs take the distinct workstation dock points (held back are `stationAgvCount`
        //    parkings for them to clear into). Every other AGV is a transit mover whose goal is a distinct parking
        //    endpoint — all safe, so no goal is ever permanently blocked. With more AGVs than safe cells, the
        //    leftover AGVs are still given the nearest spare safe cell (shared parking is impossible without
        //    collision, so any genuine overflow simply gets a parking goal and may not converge — never a blocker).
        var goals = new List<string>(agvCount);
        var usedGoal = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < stationAgvCount; i++)
        {
            goals.Add(workstations[i]);
            usedGoal.Add(workstations[i]);
        }

        // Transit AGVs get DISTINCT parking goals from the whole parking pool — maximising distinct safe goals so the
        // fleet spreads across the perimeter rather than piling onto a few cells (concentration is what breeds live
        // standoffs). `stationAgvCount` parkings are held back as clear-to destinations so a serviced AGV always has
        // a free slot to clear into (keeping requirement (b): a serviced AGV never ends on its workstation). Only the
        // genuine overflow (more AGVs than safe cells) cycles back over the pool — a duplicate goal that is still a
        // SAFE cell, so it can never wall off the core; that AGV may simply not converge.
        var transitCount = agvCount - stationAgvCount;
        var distinctTransitGoals = parkings.Skip(stationAgvCount).ToList(); // hold back the clear-to slots
        for (var i = 0; i < transitCount; i++)
        {
            string pick;
            if (i < distinctTransitGoals.Count)
                pick = distinctTransitGoals[i];                                // distinct safe parking
            else
            {
                // Overflow: reuse a safe cell (parkings first, then workstations) — duplicated but always safe.
                var pool = parkings.Count > 0 ? parkings : workstations;
                pick = pool[(i - distinctTransitGoals.Count) % pool.Count];
            }
            goals.Add(pick);
            usedGoal.Add(pick);
        }

        // 6. Starts: distinct transit-core cells (clear of the safe-rest set and of every goal), so a vehicle departs
        //    its start at once and never begins life sitting on a workstation/parking. Fall back to any free non-goal
        //    cell only if the transit core is too small.
        var goalSet = new HashSet<string>(goals, StringComparer.Ordinal);
        var startPool = allCells
            .Where(c => !goalSet.Contains(c) && !safeSet.Contains(c))
            .Concat(allCells.Where(c => !goalSet.Contains(c)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Shuffle(startPool, seed + 1);
        var starts = startPool.Take(agvCount).ToList();
        if (starts.Count < agvCount)
            throw new ArgumentException(
                $"A {width}x{height} grid cannot seat {agvCount} distinct AGV starts clear of their goals.", nameof(agvCount));

        // 7. Assemble the fleet. Workstation AGVs first (lower priority value = planned first) so they claim their
        //    service windows ahead of the transit movers; ids encode the role for the test + diagnostics.
        var agents = new List<FleetAgentSpec>(agvCount);
        var stationIds = new List<string>(stationAgvCount);
        for (var i = 0; i < agvCount; i++)
        {
            var isStation = i < stationAgvCount;
            var id = isStation ? $"ws-agv-{i + 1}" : $"transit-agv-{i + 1}";
            if (isStation)
                stationIds.Add(id);
            agents.Add(new FleetAgentSpec(id, starts[i], goals[i], Priority: i));
        }

        return new Scenario(
            field,
            agents,
            fms,
            catalog,
            new EndpointSet(workstationSet, parkingSet, EmptyEndpointSet, EmptyEndpointSet),
            stationIds);
    }

    /// <summary>How many AGVs (and so how many safe-rest cells) become workstation service AGVs — the rest of the
    /// fleet does light point-to-point onto parking endpoints. Deliberately a SMALL fraction (about a quarter, hard
    /// capped) so the service detours never congest the dense grid: enough to exercise and prove clear-to-parking,
    /// not enough to dominate. Always leaves the bulk of the safe-rest cells as parkings (resting + transit goals),
    /// and at least one workstation when the fleet and the safe set allow it.</summary>
    private static int WorkstationBudget(int agvCount, int safeCount)
    {
        if (safeCount <= 2 || agvCount <= 2)
            return Math.Min(1, Math.Min(agvCount, safeCount));
        // ~a quarter of the fleet, hard-capped at MaxStationAgvs, and never more than half the safe cells (so a
        // workstation AGV always has a distinct free parking to clear into).
        var quarter = Math.Max(1, agvCount / 4);
        return Math.Clamp(Math.Min(quarter, MaxStationAgvs), 1, safeCount / 2);
    }

    /// <summary>Hard ceiling on warehouse service AGVs. Clear-to-parking is proven by a couple; more only adds
    /// service-detour congestion on a dense grid (and risks tanking completion below the random-stress baseline).</summary>
    private const int MaxStationAgvs = 2;

    /// <summary>
    /// Builds the collectively-safe rest set: cells on which a parked vehicle cannot disconnect the transit core,
    /// even with many parked at once. It starts from the <see cref="WellFormedEndpointGenerator"/>'s well-formed
    /// endpoint set (an independent set of perimeter-like cells, each with core egress, none a roadmap cut vertex)
    /// and, only if that is too small to give every AGV a distinct goal, GREEDILY extends it — adding a cell (in a
    /// seed-shuffled order) only when it is not a roadmap cut vertex AND the core (all cells minus the running set)
    /// stays connected with the new cell still reachable. So parking on ANY subset keeps the core navigable: no
    /// parked vehicle ever walls off another agent's goal. Deterministic for a given <c>(graph, seed)</c>.
    /// </summary>
    private static List<string> BuildSafeRestSet(
        RoadmapGraph graph, IReadOnlyList<string> allCells, int targetSize, int seed)
    {
        var vertices = new HashSet<string>(allCells, StringComparer.Ordinal);
        var generator = new WellFormedEndpointGenerator();
        var carved = generator.BuildEndpoints(graph, Math.Max(1, targetSize), seed);
        var safe = new HashSet<string>(AllEndpoints(carved), StringComparer.Ordinal);

        // Extend toward targetSize so the fleet gets distinct safe goals rather than piling onto a few cells (which
        // breeds live standoffs). Only NON-cut cells that keep the core connected qualify, so the set stays
        // collectively safe. Visited in a deterministic seed-shuffled order.
        if (safe.Count < targetSize)
        {
            var candidates = allCells.Where(c => !safe.Contains(c)).ToList();
            Shuffle(candidates, seed + 7);
            foreach (var candidate in candidates)
            {
                if (safe.Count >= targetSize)
                    break;
                if (TransitCoreTopology.IsArticulationPoint(graph, vertices, candidate))
                    continue; // parking on a cut vertex alone could disconnect the core
                safe.Add(candidate);
                var core = vertices.Where(v => !safe.Contains(v)).ToHashSet(StringComparer.Ordinal);
                if (TransitCoreTopology.IsConnected(graph, core) && HasNeighbourIn(graph, candidate, core))
                    continue;
                safe.Remove(candidate); // backed out: not collectively safe
            }
        }

        return safe.OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    /// <summary>True when <paramref name="site"/> has at least one undirected neighbour inside <paramref name="core"/>.</summary>
    private static bool HasNeighbourIn(RoadmapGraph graph, string site, IReadOnlySet<string> core)
    {
        foreach (var successor in graph.Neighbours(site))
            if (core.Contains(successor))
                return true;
        foreach (var candidate in core)
            foreach (var successor in graph.Neighbours(candidate))
                if (string.Equals(successor, site, StringComparison.Ordinal))
                    return true;
        return false;
    }

    /// <summary>The union of all four endpoint roles as one set.</summary>
    private static IReadOnlySet<string> AllEndpoints(EndpointSet endpoints)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        all.UnionWith(endpoints.Workstations);
        all.UnionWith(endpoints.Parkings);
        all.UnionWith(endpoints.Buffers);
        all.UnionWith(endpoints.Chargers);
        return all;
    }

    /// <summary>In-place deterministic Fisher–Yates shuffle seeded by <paramref name="seed"/>.</summary>
    private static void Shuffle(List<string> items, int seed)
    {
        var rng = new Random(seed);
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static readonly IReadOnlySet<ResourceRef> EmptyClosure =
        new HashSet<ResourceRef>();

    private static readonly IReadOnlySet<string> EmptyEndpointSet =
        new HashSet<string>(StringComparer.Ordinal);
}
