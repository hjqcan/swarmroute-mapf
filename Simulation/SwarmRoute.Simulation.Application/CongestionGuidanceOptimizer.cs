namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab) Turns a run's measured congestion into a <see cref="GuidanceGraph"/>: the congestion-feedback
/// step of the optimization loop the doc describes (Telemetry → Congestion Analyzer → GuidanceGraph → re-plan). Each
/// directed lane is penalised in proportion to how contested the cell it ENTERS was over the baseline run, so a
/// weight-aware planner steers the next pass away from the hotspots and the load spreads. A pure function of the
/// metrics + the field — deterministic, so the guided pass is reproducible.
/// </summary>
public static class CongestionGuidanceOptimizer
{
    /// <summary>Default penalty strength: the hottest cell's incoming lanes cost up to <c>1 + Strength</c>× their base.</summary>
    public const double DefaultStrength = 2.5;

    public static GuidanceGraph Derive(SimulationMetricsDto metrics, GridField field, double strength = DefaultStrength)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(field);

        // Congestion per cell (occupied + waited agent-ticks), normalised by the worst cell.
        var loadByCell = metrics.Heatmap.ToDictionary(c => c.SiteId, c => c.OccupiedTicks + c.WaitTicks, StringComparer.Ordinal);
        var maxLoad = loadByCell.Count == 0 ? 0 : loadByCell.Values.Max();
        if (maxLoad <= 0)
            return GuidanceGraph.Identity; // nothing was congested → no steer

        // Penalise each lane by the congestion of the cell it enters: entering a hot cell costs more, so the planner
        // prefers routes through cooler cells. Only lanes into a congested cell get a multiplier (the rest stay 1.0).
        var multipliers = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var from in field.Graph.Vertices)
            foreach (var to in field.Graph.Neighbours(from))
            {
                var load = loadByCell.GetValueOrDefault(to, 0);
                if (load <= 0)
                    continue;
                multipliers[$"{from}-{to}"] = 1.0 + strength * load / maxLoad;
            }

        return new GuidanceGraph(multipliers);
    }
}
