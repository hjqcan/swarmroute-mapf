namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (v4 SwarmRoute Lab) A <b>guidance overlay</b> on the roadmap: per-directed-lane weight multipliers that bias the
/// planner away from congested corridors without changing the topology (no edge is removed, so connectivity — and
/// every goal's reachability — is preserved). A multiplier of <c>1.0</c> leaves a lane at its base cost; <c>&gt;1.0</c>
/// makes it look longer, so a <b>weight-aware</b> planner (Dijkstra's weighted shortest path, SIPPwRT's
/// length-proportional kinematic duration) routes around it and the fleet spreads out. Hop-uniform discrete SIPP
/// ignores edge weight, so guidance is inert under it — an honest limitation surfaced by the Lab's baseline-vs-guided
/// comparison, not hidden. Applied at grid-build time by <see cref="GridFieldFactory"/>.
/// </summary>
public sealed record GuidanceGraph(IReadOnlyDictionary<string, double> LaneWeightMultipliers)
{
    /// <summary>The no-op overlay — every lane at its base weight (a guided build with this is byte-identical).</summary>
    public static readonly GuidanceGraph Identity = new(new Dictionary<string, double>(StringComparer.Ordinal));

    /// <summary>The multiplier for lane <paramref name="laneId"/> (<c>"from-to"</c>), or <c>1.0</c> when unguided.</summary>
    public double MultiplierFor(string laneId)
        => LaneWeightMultipliers.TryGetValue(laneId, out var m) ? m : 1.0;

    /// <summary>How many lanes were actually re-weighted (multiplier ≠ 1).</summary>
    public int AdjustedLaneCount => LaneWeightMultipliers.Count(kv => kv.Value != 1.0);

    /// <summary>The heaviest multiplier applied (1.0 when nothing was guided) — the strength of the steer.</summary>
    public double MaxMultiplier
        => LaneWeightMultipliers.Count == 0 ? 1.0 : Math.Max(1.0, LaneWeightMultipliers.Values.Max());
}
