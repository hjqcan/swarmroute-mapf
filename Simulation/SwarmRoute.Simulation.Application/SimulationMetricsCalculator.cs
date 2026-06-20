namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab) Computes a run's <see cref="SimulationMetricsDto"/> deterministically from the recorded
/// timeline — every agent's control point + motion state on every tick — plus the aggregate stats. A pure function
/// (no engine, no I/O, no clock): the same request always yields the same metrics, so two planners / policies / maps
/// are comparable on identical inputs. One pass over the frames derives the per-agent time-to-goal, the
/// stationary-not-arrived wait, and the per-cell congestion (the bottleneck heatmap); the aggregates (percentiles,
/// Jain fairness, throughput) are closed-form on top.
/// <para>
/// Units follow the run's clock: a frame's <c>Tick</c> is an integer tick for the discrete executors and a
/// fleet-clock millisecond for the continuous (SIPPwRT) executor, so the metrics are self-consistent within a run.
/// </para>
/// </summary>
public static class SimulationMetricsCalculator
{
    /// <summary>How many of the most-congested cells to surface as the bottleneck ranking.</summary>
    private const int TopBottlenecks = 8;

    public static SimulationMetricsDto Compute(
        FleetLoopResult loop,
        IReadOnlyList<FleetAgentSpec> specs,
        IReadOnlyDictionary<string, (double X, double Y)> positionById)
    {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(positionById);

        var frames = loop.Frames;
        var agvCount = specs.Count;

        var arrivalTick = new Dictionary<string, int>(StringComparer.Ordinal);   // agent → tick it reached its goal
        var waitTicks = new Dictionary<string, int>(StringComparer.Ordinal);     // agent → stationary-not-arrived ticks
        var occupied = new Dictionary<string, int>(StringComparer.Ordinal);      // cell → agent-ticks spent on it
        var waited = new Dictionary<string, int>(StringComparer.Ordinal);        // cell → stationary-not-arrived ticks on it
        var prevPos = new Dictionary<string, string>(StringComparer.Ordinal);    // agent → its cell on the previous frame

        // One pass over the timeline. "Wait" = an agent holding the same cell across consecutive frames without having
        // arrived (pending right-of-way, or en route but gate-blocked) — the honest no-forward-progress signal that a
        // simple Waiting/Moving state flag misses for a blocked en-route agent.
        foreach (var frame in frames)
            foreach (var p in frame.Positions)
            {
                occupied[p.SiteId] = occupied.GetValueOrDefault(p.SiteId) + 1;

                var isArrived = p.State == AgentMotionState.Arrived;
                if (isArrived && !arrivalTick.ContainsKey(p.AgentId))
                    arrivalTick[p.AgentId] = frame.Tick;

                if (!isArrived
                    && prevPos.TryGetValue(p.AgentId, out var prev)
                    && string.Equals(prev, p.SiteId, StringComparison.Ordinal))
                {
                    waitTicks[p.AgentId] = waitTicks.GetValueOrDefault(p.AgentId) + 1;
                    waited[p.SiteId] = waited.GetValueOrDefault(p.SiteId) + 1;
                }

                prevPos[p.AgentId] = p.SiteId;
            }

        var travelTimes = arrivalTick.Values.OrderBy(t => t).ToList();
        var arrived = travelTimes.Count;
        var makespan = frames.Count > 0 ? frames[^1].Tick : 0;

        // Fleet-wide effective-completion times for fairness: every agent contributes, an un-arrived one counted at the
        // worst observed completion (the makespan) so starvation lowers the index instead of hiding behind the arrivals.
        // Deterministic (ordinal-stable specs; constant makespan), independent of the arrived-only travel-time list.
        var effectiveCompletion = specs
            .Select(s => arrivalTick.TryGetValue(s.Id, out var at) ? at : makespan)
            .ToList();

        var meanWaitRatio = agvCount == 0
            ? 0d
            : specs.Average(s =>
            {
                // Normalise each agent's wait by its own run length (its arrival, or the makespan if it never arrived).
                var span = arrivalTick.TryGetValue(s.Id, out var at) && at > 0 ? at : Math.Max(1, makespan);
                return (double)waitTicks.GetValueOrDefault(s.Id) / span;
            });

        var heatmap = occupied
            .Select(kv => new CellCongestionDto(
                kv.Key,
                positionById.TryGetValue(kv.Key, out var xy) ? xy.X : 0d,
                positionById.TryGetValue(kv.Key, out xy) ? xy.Y : 0d,
                kv.Value,
                waited.GetValueOrDefault(kv.Key)))
            .OrderByDescending(c => c.OccupiedTicks + c.WaitTicks)
            .ThenBy(c => c.SiteId, StringComparer.Ordinal)
            .ToList();

        return new SimulationMetricsDto(
            AgvCount: agvCount,
            Arrived: arrived,
            CompletionRate: agvCount == 0 ? 0d : (double)arrived / agvCount,
            MakespanTicks: makespan,
            ThroughputPerThousandTicks: makespan == 0 ? 0d : arrived * 1000d / makespan,
            TravelTime: TravelStats(travelTimes),
            MeanWaitRatio: meanWaitRatio,
            TotalWaitTicks: waitTicks.Values.Sum(),
            TotalReplans: loop.Stats.Replans,
            MaxConcurrent: loop.MaxConcurrentEnRoute,
            Collisions: loop.Stats.Collisions,
            Status: loop.Stats.Status.ToString(),
            FairnessIndex: JainFairness(effectiveCompletion),
            Heatmap: heatmap,
            BottleneckSiteIds: heatmap.Take(TopBottlenecks).Select(c => c.SiteId).ToList());
    }

    /// <summary>Mean + nearest-rank percentiles + max over an ascending-sorted travel-time list (empty → all zero).</summary>
    private static TravelTimeStatsDto TravelStats(IReadOnlyList<int> sortedAscending)
    {
        if (sortedAscending.Count == 0)
            return new TravelTimeStatsDto(0d, 0, 0, 0, 0);

        return new TravelTimeStatsDto(
            Mean: sortedAscending.Average(),
            P50: Percentile(sortedAscending, 0.50),
            P95: Percentile(sortedAscending, 0.95),
            P99: Percentile(sortedAscending, 0.99),
            Max: sortedAscending[^1]);
    }

    /// <summary>Nearest-rank percentile on an ascending-sorted list (deterministic; no interpolation).</summary>
    private static int Percentile(IReadOnlyList<int> sortedAscending, double q)
    {
        var rank = (int)Math.Ceiling(q * sortedAscending.Count);
        return sortedAscending[Math.Clamp(rank - 1, 0, sortedAscending.Count - 1)];
    }

    /// <summary>Jain's fairness index over the values (1 = perfectly even, →1/n = one value dominates). Empty or
    /// all-zero → 1 (no unfairness to report). Computed over the WHOLE fleet's effective-completion times — every
    /// agent contributes, an un-arrived one counted at the makespan — so a fleet with many starved agents reports a
    /// lower, honest index rather than an arrived-only one that hides the starvation. (Outright starvation is most
    /// directly read from <c>CompletionRate</c>; this index measures spread across the whole fleet.)</summary>
    private static double JainFairness(IReadOnlyList<int> values)
    {
        if (values.Count == 0)
            return 1d;

        double sum = 0d, sumSq = 0d;
        foreach (var v in values)
        {
            sum += v;
            sumSq += (double)v * v;
        }
        return sumSq == 0d ? 1d : sum * sum / (values.Count * sumSq);
    }
}
