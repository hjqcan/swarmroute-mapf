using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab — Order/Dispatch context) The <b>lifelong / online</b> dispatch layer that sits ABOVE the MAPF
/// layer — the warehouse-operations question module #5's one-shot <see cref="TaskDispatcher"/> deferred: orders
/// <i>release over time</i>, queue, and are continuously assigned to vehicles that pick the next job the moment they
/// finish the last one, with pickup/dropoff/charging <b>stations</b>, a <b>battery</b> budget, and per-order <b>SLA</b>
/// deadlines. It is a self-contained discrete-event simulation over the same <see cref="RoadmapGraph"/> (it never
/// touches the fleet executor); legs use free-flow shortest-path travel-time estimates, so this answers operations
/// questions — throughput, on-time rate, queue depth, utilization, charging — at the dispatch layer, with per-leg
/// collision-free <i>execution</i> being the MAPF layer proven elsewhere. A pure, deterministic function of the field,
/// fleet, seed and policy.
/// </summary>
public static class OrderDispatchSimulator
{
    /// <summary>Free-flow speed: 1 graph unit (mm) per ms = 1 m/s, matching <c>KinematicProfile.Default</c>'s cruise.
    /// The dispatch layer estimates leg time as distance at cruise (congestion is the MAPF layer's concern).</summary>
    private static long DurationMs(long distanceMm) => distanceMm;

    private const long Unreachable = 1_000_000_000L;

    /// <summary>One transport order: move a unit from a pickup station to a dropoff station, released at
    /// <paramref name="ReleaseMs"/> and due by <paramref name="DeadlineMs"/>.</summary>
    internal sealed record Order(string Id, string Pickup, string Dropoff, long ReleaseMs, long DeadlineMs);

    /// <summary>The dispatch scenario's deterministic knobs. Derived from the field + fleet by <see cref="Options.Derive"/>
    /// so the public request needs a single opt-in flag; tests construct their own.</summary>
    public sealed record Options(
        int OrderCount, long InterArrivalMs, long SlaMs, bool BatteryEnabled, long BatteryRangeMm, long RechargeMs)
    {
        /// <summary>Sensible defaults that build a real queue (orders release faster than a leg completes) so the
        /// assignment policy visibly matters, with a generous battery that rarely trips (kept as a separate dimension).</summary>
        public static Options Derive(GridField field, int fleetSize)
        {
            var span = (long)(field.Width + field.Height) * 1000L;  // ≈ a full traversal in mm/ms
            var fleet = Math.Max(1, fleetSize);
            // A leg is ≈ empty-to-pickup + loaded-to-dropoff ≈ 2·span; the fleet clears one every ≈ 2·span/fleet. Release
            // orders ≈ 4× faster than that so a real backlog forms — the regime where assignment quality compounds.
            return new Options(
                OrderCount: Math.Max(10, fleetSize * 4),
                InterArrivalMs: Math.Max(150, span / (fleet * 2)),
                SlaMs: span * 3,                            // tight enough that the backlog makes a poor policy miss
                BatteryEnabled: true,
                BatteryRangeMm: span * 12,                  // many legs between charges
                RechargeMs: span);
        }
    }

    private sealed class Vehicle
    {
        public required string Id;
        public required string Site;
        public long FreeAtMs;
        public long BatteryMm;
        public long BusyMs;
    }

    /// <summary>Runs the lifelong dispatch simulation and returns its operations summary. <paramref name="vehicleStarts"/>
    /// are the fleet's current sites (the same poses the MAPF run used).</summary>
    public static OrderDispatchReportDto Run(
        GridField field, IReadOnlyList<string> vehicleStarts, int seed, AssignmentPolicy policy, Options options)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(vehicleStarts);
        ArgumentNullException.ThrowIfNull(options);
        if (vehicleStarts.Count == 0 || options.OrderCount == 0)
            return Empty(options, policy);

        var (orders, chargers) = GenerateStream(field, seed, options);
        if (orders.Count == 0)
            return Empty(options, policy);

        long Dist(string a, string b) => string.Equals(a, b, StringComparison.Ordinal) ? 0 : field.Graph.DistanceTo(a, b) ?? Unreachable;

        var vehicles = vehicleStarts
            .Select((s, i) => new Vehicle { Id = $"agv-{i + 1}", Site = s, BatteryMm = options.BatteryRangeMm })
            .ToList();

        var assigned = new bool[orders.Count];
        var deliveredMs = new long[orders.Count];
        var onTime = new bool[orders.Count];
        int completed = 0, chargingStops = 0, maxQueueDepth = 0, remaining = orders.Count;

        while (remaining > 0)
        {
            // Soonest moment something can happen: a vehicle is free AND an order has released.
            var tFree = vehicles.Min(v => v.FreeAtMs);
            var tRelease = long.MaxValue;
            for (var o = 0; o < orders.Count; o++)
                if (!assigned[o] && orders[o].ReleaseMs < tRelease)
                    tRelease = orders[o].ReleaseMs;
            var t = Math.Max(tFree, tRelease);

            var idle = vehicles.Where(v => v.FreeAtMs <= t)
                .OrderBy(v => v.FreeAtMs).ThenBy(v => v.Id, StringComparer.Ordinal).ToList();
            var pend = Enumerable.Range(0, orders.Count).Where(o => !assigned[o] && orders[o].ReleaseMs <= t)
                .OrderBy(o => orders[o].ReleaseMs).ThenBy(o => orders[o].Id, StringComparer.Ordinal).ToList();
            maxQueueDepth = Math.Max(maxQueueDepth, pend.Count);

            foreach (var (vi, oi) in Match(idle, pend, orders, Dist, policy))
            {
                var veh = idle[vi];
                var ord = orders[oi];
                var start = t;

                var leg = Dist(veh.Site, ord.Pickup) + Dist(ord.Pickup, ord.Dropoff);
                if (options.BatteryEnabled && veh.BatteryMm < leg)
                {
                    // Detour to the nearest charger, refill to full, then run the leg from there.
                    var charger = chargers.OrderBy(c => Dist(veh.Site, c)).ThenBy(c => c, StringComparer.Ordinal).First();
                    start += DurationMs(Dist(veh.Site, charger)) + options.RechargeMs;
                    veh.Site = charger;
                    veh.BatteryMm = options.BatteryRangeMm;
                    chargingStops++;
                    leg = Dist(veh.Site, ord.Pickup) + Dist(ord.Pickup, ord.Dropoff);
                }

                var done = start + DurationMs(leg);
                veh.BusyMs += done - t;
                veh.Site = ord.Dropoff;
                veh.FreeAtMs = done;
                veh.BatteryMm -= leg;

                assigned[oi] = true;
                deliveredMs[oi] = done;
                onTime[oi] = done <= ord.DeadlineMs;
                completed++;
                remaining--;
            }
        }

        var latencies = new long[completed];
        var k = 0;
        long makespan = 0, onTimeCount = 0;
        for (var o = 0; o < orders.Count; o++)
        {
            latencies[k++] = deliveredMs[o] - orders[o].ReleaseMs;
            makespan = Math.Max(makespan, deliveredMs[o]);
            if (onTime[o]) onTimeCount++;
        }
        Array.Sort(latencies);

        var busyTotal = vehicles.Sum(v => v.BusyMs);
        var utilization = makespan > 0 ? busyTotal / (double)(vehicles.Count * makespan) : 0;

        return new OrderDispatchReportDto(
            OrdersTotal: orders.Count,
            OrdersCompleted: completed,
            OnTimeRate: completed > 0 ? onTimeCount / (double)completed : 0,
            MeanLatencyMs: completed > 0 ? (long)Math.Round(latencies.Average()) : 0,
            P95LatencyMs: Percentile(latencies, 0.95),
            MakespanMs: makespan,
            FleetUtilization: Math.Clamp(utilization, 0, 1),
            ChargingStops: chargingStops,
            MaxQueueDepth: maxQueueDepth,
            Policy: policy.ToString());
    }

    /// <summary>Matches idle vehicles to pending orders for one wave, returning (idleIndex, globalOrderIndex) pairs.
    /// <see cref="AssignmentPolicy.Optimal"/> is the exact Hungarian min-empty-travel matching (square-padded);
    /// <see cref="AssignmentPolicy.Nearest"/> is greedy nearest; <see cref="AssignmentPolicy.Random"/> keeps arrival order.</summary>
    private static IEnumerable<(int Vehicle, int Order)> Match(
        IReadOnlyList<Vehicle> idle, IReadOnlyList<int> pend, IReadOnlyList<Order> orders,
        Func<string, string, long> dist, AssignmentPolicy policy)
    {
        var k = Math.Min(idle.Count, pend.Count);
        if (k == 0)
            yield break;

        if (policy == AssignmentPolicy.Random)
        {
            for (var i = 0; i < k; i++)
                yield return (i, pend[i]);
            yield break;
        }

        if (policy == AssignmentPolicy.Nearest)
        {
            var taken = new bool[pend.Count];
            for (var i = 0; i < idle.Count; i++)
            {
                var best = -1;
                var bestCost = long.MaxValue;
                for (var j = 0; j < pend.Count; j++)
                    if (!taken[j])
                    {
                        var c = dist(idle[i].Site, orders[pend[j]].Pickup);
                        if (c < bestCost) { bestCost = c; best = j; }
                    }
                if (best < 0) yield break; // all orders claimed
                taken[best] = true;
                yield return (i, pend[best]);
            }
            yield break;
        }

        // Optimal: pad the shorter side to a square cost matrix (dummy rows/cols cost 0 ⇒ Hungarian optimally matches
        // the real min-side, leaving the surplus to wait), then keep only the real-real pairs.
        var n = Math.Max(idle.Count, pend.Count);
        var cost = new long[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                cost[i, j] = i < idle.Count && j < pend.Count ? dist(idle[i].Site, orders[pend[j]].Pickup) : 0;

        var assignment = TaskDispatcher.Hungarian(cost);
        for (var i = 0; i < idle.Count; i++)
        {
            var j = assignment[i];
            if (j < pend.Count)
                yield return (i, pend[j]);
        }
    }

    /// <summary>Deterministically builds the order stream + charging stations from the field and seed. Pickup stations
    /// are the left edge, dropoff the right edge, chargers the corners (falling back to any sites if a scenario carved
    /// an edge away), so orders cross the field and exercise the dispatch.</summary>
    private static (IReadOnlyList<Order> Orders, IReadOnlyList<string> Chargers) GenerateStream(
        GridField field, int seed, Options options)
    {
        var sites = field.Sites;
        if (sites.Count == 0)
            return (Array.Empty<Order>(), Array.Empty<string>());

        var minX = sites.Min(s => s.X);
        var maxX = sites.Max(s => s.X);
        var pickups = sites.Where(s => s.X == minX).Select(s => s.Id).ToList();
        var dropoffs = sites.Where(s => s.X == maxX).Select(s => s.Id).ToList();
        if (pickups.Count == 0) pickups = sites.Select(s => s.Id).ToList();
        if (dropoffs.Count == 0) dropoffs = sites.Select(s => s.Id).ToList();

        // Chargers: the four corner-most sites (by Manhattan distance to each grid corner).
        var corners = new[] { (0, 0), (field.Width - 1, 0), (0, field.Height - 1), (field.Width - 1, field.Height - 1) };
        var chargers = corners
            .Select(c => sites.OrderBy(s => Math.Abs(s.X - c.Item1) + Math.Abs(s.Y - c.Item2)).ThenBy(s => s.Id, StringComparer.Ordinal).First().Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rng = new Random(seed);
        var orders = new List<Order>(options.OrderCount);
        for (var i = 0; i < options.OrderCount; i++)
        {
            var release = i * options.InterArrivalMs;
            var pickup = pickups[rng.Next(pickups.Count)];
            var dropoff = dropoffs[rng.Next(dropoffs.Count)];
            orders.Add(new Order($"ord-{i + 1}", pickup, dropoff, release, release + options.SlaMs));
        }
        return (orders, chargers);
    }

    private static long Percentile(long[] sortedAsc, double q)
    {
        if (sortedAsc.Length == 0)
            return 0;
        var idx = Math.Clamp((int)Math.Ceiling(q * sortedAsc.Length) - 1, 0, sortedAsc.Length - 1);
        return sortedAsc[idx];
    }

    private static OrderDispatchReportDto Empty(Options options, AssignmentPolicy policy) =>
        new(options.OrderCount, 0, 0, 0, 0, 0, 0, 0, 0, policy.ToString());
}
