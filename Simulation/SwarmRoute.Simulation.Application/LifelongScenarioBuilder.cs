using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Endpoints;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Dispatch.Domain.Topology;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (FMS-V3) Builds a <b>lifelong dispatch</b> closed-loop scenario over a larger, sparser grid (終身派工情境). Unlike the
/// one-shot <see cref="WarehouseScenarioBuilder"/>, this carves MANY safe workstation/parking endpoints and a STREAM of
/// transport tasks the runtime dispatcher hands out continuously, so the fleet runs forever (until the horizon).
/// <para>
/// <b>Safe-rest set.</b> As in the warehouse builder, the <see cref="WellFormedEndpointGenerator"/> carves a
/// collectively-safe endpoint set (cells a vehicle may park on indefinitely without disconnecting the transit core),
/// which is then greedily extended toward a target size. Every workstation dock AND every parking slot is drawn from
/// this set, so a parked or docked vehicle never walls off the core — the lifelong loop never permanently goal-blocks.
/// </para>
/// <para>
/// <b>Sizing (the M-F2 finding).</b> A lifelong run needs MORE endpoints than AGVs so an AGV always finds a free
/// workstation to serve and a free parking to rest in between tasks (otherwise the fleet saturates parking or contends
/// on docks). The caller is expected to pass a large, sparse grid (e.g. 12×12 / 14×10) with a modest fleet.
/// </para>
/// <para>
/// <b>Stations &amp; tasks.</b> Each workstation endpoint becomes a bufferless <see cref="StationType.NonBlocking"/>
/// station (service occupies only its dock CP, briefly) so an AGV docks directly, services, then clears to a parking
/// slot — and is immediately re-tasked. The task pool draws goals uniformly across the workstation docks and releases
/// them over the horizon, so the dispatcher faces a genuine, growing-then-draining backlog.
/// </para>
/// </summary>
public static class LifelongScenarioBuilder
{
    /// <summary>Service duration (ms == ticks; HopMs == 1) at a lifelong workstation. ONE tick — the lifelong point is
    /// continuous throughput + clear-to-parking re-tasking, not a long blocking service (that is M-F1's job).</summary>
    public const long ServiceDurationMs = 1;

    /// <summary>The assembled lifelong scenario.</summary>
    /// <param name="Field">The generated grid field (graph + render metadata).</param>
    /// <param name="Agents">The fleet, in priority order. Every AGV is a lifelong dispatch AGV (re-tasked continuously).</param>
    /// <param name="Fms">The FMS overlay: site roles (workstations / parkings / transit) + the stations + ClearToParking.</param>
    /// <param name="Catalog">The station catalog the engine wires the dock-admission scheduler over.</param>
    /// <param name="StationByDock">Maps each workstation dock CP to its station (the re-task arm's lookup).</param>
    /// <param name="Tasks">The released-over-time transport-task pool (goals at workstation docks).</param>
    /// <param name="WorkstationDocks">The workstation dock CPs (task goals).</param>
    /// <param name="ParkingSlots">The parking-slot CPs (resting + saturation denominator).</param>
    /// <param name="FleetPlan">A fleet-plan snapshot (per-agent priorities) for cost-based admission.</param>
    public sealed record Scenario(
        GridField Field,
        IReadOnlyList<FleetAgentSpec> Agents,
        FmsScenario Fms,
        IStationCatalog Catalog,
        IReadOnlyDictionary<string, StationDefinition> StationByDock,
        IReadOnlyList<TransportTask> Tasks,
        IReadOnlyList<string> WorkstationDocks,
        IReadOnlyList<string> ParkingSlots,
        IFleetPlanProvider FleetPlan);

    /// <summary>
    /// Builds a lifelong scenario for a <paramref name="width"/>×<paramref name="height"/> grid and
    /// <paramref name="agvCount"/> AGVs, generating <paramref name="taskCount"/> transport tasks released uniformly
    /// across <paramref name="horizonTicks"/>, seeded by <paramref name="seed"/> for reproducibility.
    /// </summary>
    /// <exception cref="ArgumentNullException">If <paramref name="gridFactory"/> is null.</exception>
    /// <exception cref="ArgumentException">If the grid cannot host the fleet plus at least one workstation and one parking.</exception>
    public static Scenario Build(
        GridFieldFactory gridFactory, int width, int height, int agvCount, int taskCount, long horizonTicks, int seed)
    {
        ArgumentNullException.ThrowIfNull(gridFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(agvCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(taskCount, 1);

        var field = gridFactory.BuildGrid(width, height);
        var graph = field.Graph;
        var allCells = field.Sites.Select(s => s.Id).OrderBy(c => c, StringComparer.Ordinal).ToList();

        // 1. A LARGE collectively-safe rest set — target well over the fleet size so there are plenty of distinct
        //    workstations AND plenty of distinct parkings (lifelong needs more endpoints than AGVs). Aim for roughly a
        //    third of the grid, but at least 4*agvCount where the core allows it.
        var targetSafe = Math.Max(agvCount * 4, allCells.Count / 3);
        var safe = BuildSafeRestSet(graph, allCells, targetSafe, seed);
        if (safe.Count < 2)
            throw new ArgumentException(
                $"A {width}x{height} grid yields {safe.Count} safe endpoint(s); a lifelong scenario needs at least a " +
                "workstation and a parking. Use a larger / sparser grid.", nameof(agvCount));

        // 2. Partition the safe set into workstations (task goals) and parkings (rest slots). Split roughly in half,
        //    but keep enough parkings that every AGV can rest at once (>= agvCount where possible) so parking never
        //    saturates, and keep at least one of each.
        Shuffle(safe, seed + 3);
        var parkingTarget = Math.Max(agvCount, safe.Count / 2);
        parkingTarget = Math.Clamp(parkingTarget, 1, safe.Count - 1); // leave at least one workstation
        var parkings = safe.Take(parkingTarget).OrderBy(c => c, StringComparer.Ordinal).ToList();
        var workstations = safe.Skip(parkingTarget).OrderBy(c => c, StringComparer.Ordinal).ToList();

        // 3. Site roles: every safe cell is a workstation or a parking; all other cells stay Transit (unmapped).
        var roles = new Dictionary<string, SiteRole>(StringComparer.Ordinal);
        foreach (var w in workstations)
            roles[w] = SiteRole.Workstation;
        foreach (var p in parkings)
            roles[p] = SiteRole.Parking;

        // 4. One bufferless NonBlocking station per workstation: service occupies only the dock CP (empty closure ⇒
        //    never severs transit), bufferless ⇒ dock directly (admission skipped for the NonBlocking case), and on
        //    completion clear to a free parking. A re-tasked AGV can be sent to ANY of these docks.
        var stationDefs = new List<StationDefinition>(workstations.Count);
        var stationByDock = new Dictionary<string, StationDefinition>(StringComparer.Ordinal);
        foreach (var dock in workstations)
        {
            var station = new StationDefinition(
                StationId: $"ws-{dock}",
                DockPoint: dock,
                PreDockBuffers: Array.Empty<string>(),
                BlockingClosure: EmptyClosure,
                ServiceDurationMs: ServiceDurationMs,
                StationType: StationType.NonBlocking);
            stationDefs.Add(station);
            stationByDock[dock] = station;
        }

        var catalog = new InMemoryStationCatalog(stationDefs);
        var fms = new FmsScenario(roles, stationDefs, ArrivalPolicy.ClearToParking);

        // 5. The transport-task stream. Goals are drawn round-robin across the workstation docks (so the backlog is not
        //    all the same goal), released uniformly across the horizon so the dispatcher faces a real arriving backlog.
        var tasks = BuildTaskStream(workstations, taskCount, horizonTicks, seed + 11);

        // 6. Starts: distinct transit-core cells clear of the safe-rest set (so an AGV begins idle-parked on a transit
        //    cell, not on a workstation/parking it would block). Fall back to any free cell if the core is too small.
        var safeSet = new HashSet<string>(safe, StringComparer.Ordinal);
        var startPool = allCells.Where(c => !safeSet.Contains(c))
            .Concat(allCells)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Shuffle(startPool, seed + 1);
        var starts = startPool.Take(agvCount).ToList();
        if (starts.Count < agvCount)
            throw new ArgumentException(
                $"A {width}x{height} grid cannot seat {agvCount} distinct AGV starts.", nameof(agvCount));

        // 7. The fleet — every AGV is a lifelong dispatch AGV (it begins idle-parked at its start and is tasked by the
        //    dispatcher). Goal is a placeholder (the dock of the first workstation); the lifelong arm overwrites it with
        //    each assigned task's dock. Higher index ⇒ higher priority value (planned later) for a stable order.
        var agents = new List<FleetAgentSpec>(agvCount);
        var placeholderGoal = workstations.Count > 0 ? workstations[0] : parkings[0];
        var priorities = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < agvCount; i++)
        {
            var id = $"agv-{i + 1}";
            agents.Add(new FleetAgentSpec(id, starts[i], placeholderGoal, Priority: i));
            priorities[id] = i;
        }

        return new Scenario(
            field, agents, fms, catalog, stationByDock, tasks, workstations, parkings,
            new StaticFleetPlan(priorities));
    }

    /// <summary>
    /// Builds the released-over-time task pool: <paramref name="taskCount"/> tasks whose goals cycle through the
    /// workstation docks (round-robin so the backlog has varied goals), released at evenly-spaced ticks across
    /// <c>[0, horizonTicks)</c> — leaving the back third of the horizon release-free so the backlog can fully drain
    /// (proving second-half throughput, not a perpetual unservable queue). Deterministic for a given seed.
    /// </summary>
    private static IReadOnlyList<TransportTask> BuildTaskStream(
        IReadOnlyList<string> workstationDocks, int taskCount, long horizonTicks, int seed)
    {
        // Release window = the first two-thirds of the horizon, so the last third drains the backlog.
        var releaseWindow = Math.Max(1, horizonTicks * 2 / 3);
        var rng = new Random(seed);
        var tasks = new List<TransportTask>(taskCount);
        for (var i = 0; i < taskCount; i++)
        {
            var dock = workstationDocks[i % workstationDocks.Count];
            // Evenly spread releases across the window with a tiny seeded jitter so they are not perfectly periodic.
            var baseRelease = taskCount <= 1 ? 0 : (long)i * (releaseWindow - 1) / (taskCount - 1);
            var release = Math.Clamp(baseRelease + rng.Next(0, 2), 0, Math.Max(0, horizonTicks - 1));
            // A small priority spread so the cost-based admission / dispatcher ranking has something to order on.
            var priority = rng.Next(0, 3);
            tasks.Add(new TransportTask(
                TaskId: $"task-{i + 1:D4}",
                GoalSiteId: dock,
                Priority: priority,
                ReleaseMs: release,
                DeadlineMs: null));
        }
        return tasks;
    }

    /// <summary>
    /// Builds the collectively-safe rest set (identical strategy to <see cref="WarehouseScenarioBuilder"/>): start from
    /// the well-formed endpoint set, then greedily extend it toward <paramref name="targetSize"/> with non-cut cells
    /// that keep the core connected. Parking on ANY subset keeps the core navigable. Deterministic for <c>(graph, seed)</c>.
    /// </summary>
    private static List<string> BuildSafeRestSet(
        RoadmapGraph graph, IReadOnlyList<string> allCells, int targetSize, int seed)
    {
        var vertices = new HashSet<string>(allCells, StringComparer.Ordinal);
        var generator = new WellFormedEndpointGenerator();
        var carved = generator.BuildEndpoints(graph, Math.Max(1, targetSize), seed);
        var safe = new HashSet<string>(AllEndpoints(carved), StringComparer.Ordinal);

        if (safe.Count < targetSize)
        {
            var candidates = allCells.Where(c => !safe.Contains(c)).ToList();
            Shuffle(candidates, seed + 7);
            foreach (var candidate in candidates)
            {
                if (safe.Count >= targetSize)
                    break;
                if (TransitCoreTopology.IsArticulationPoint(graph, vertices, candidate))
                    continue;
                safe.Add(candidate);
                var core = vertices.Where(v => !safe.Contains(v)).ToHashSet(StringComparer.Ordinal);
                if (TransitCoreTopology.IsConnected(graph, core) && HasNeighbourIn(graph, candidate, core))
                    continue;
                safe.Remove(candidate);
            }
        }

        return safe.OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

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

    private static IReadOnlySet<string> AllEndpoints(EndpointSet endpoints)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        all.UnionWith(endpoints.Workstations);
        all.UnionWith(endpoints.Parkings);
        all.UnionWith(endpoints.Buffers);
        all.UnionWith(endpoints.Chargers);
        return all;
    }

    private static void Shuffle(List<string> items, int seed)
    {
        var rng = new Random(seed);
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static readonly IReadOnlySet<ResourceRef> EmptyClosure = new HashSet<ResourceRef>();

    /// <summary>A fixed per-agent priority snapshot exposed as an <see cref="IFleetPlanProvider"/> for cost-based
    /// admission. The planned-resources map is empty (the cost policy only needs priorities here).</summary>
    private sealed class StaticFleetPlan(IReadOnlyDictionary<string, int> priorities) : IFleetPlanProvider
    {
        private static readonly IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> Empty =
            new Dictionary<string, IReadOnlyList<ResourceRef>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> GetPlannedResources() => Empty;

        public int? GetPriority(string agentId) => priorities.TryGetValue(agentId, out var p) ? p : null;
    }
}
