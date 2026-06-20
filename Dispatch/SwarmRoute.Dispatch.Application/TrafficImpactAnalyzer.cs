using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Topology;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The FMS-V2 <see cref="ITrafficImpactAnalyzer"/>: a pure, deterministic estimate of what holding a station's
/// blocking closure does to fleet traffic, computed over a fixed <see cref="RoadmapGraph"/> snapshot
/// (交通影響評估器).
/// <para>
/// <b>Method.</b>
/// <list type="bullet">
///   <item><see cref="TrafficImpact.AffectedAgentIds"/> — every agent whose planned resource list intersects the
///   closure, while the window is actually held (a zero-duration window holds nothing, so affects no one).
///   Returned ordinal-sorted for reproducibility.</item>
///   <item><see cref="TrafficImpact.BlocksTransitCore"/> — true when removing the closure's site vertices
///   disconnects the roadmap (no through path survives), via <see cref="TransitCoreTopology"/>.</item>
///   <item><see cref="TrafficImpact.HasBypass"/> — true when the roadmap minus the closure is still connected,
///   so affected traffic can route around it. Mutually exclusive with <see cref="TrafficImpact.BlocksTransitCore"/>
///   (a severed core has no bypass).</item>
///   <item><see cref="TrafficImpact.EstWaitTicks"/> — a simple worst-case estimate: the largest number of
///   closure resources any single affected agent must still traverse (its remaining time stuck in the closure).</item>
/// </list>
/// Nothing here is reserved or mutated; the analyser is a side-effect-free read.
/// </para>
/// </summary>
public sealed class TrafficImpactAnalyzer : ITrafficImpactAnalyzer
{
    private readonly RoadmapGraph _graph;

    /// <summary>Creates the analyser over the roadmap snapshot its connectivity verdicts are computed against.</summary>
    /// <param name="graph">The roadmap whose transit-core connectivity the impact is measured on.</param>
    public TrafficImpactAnalyzer(RoadmapGraph graph)
        => _graph = graph ?? throw new ArgumentNullException(nameof(graph));

    /// <inheritdoc />
    public TrafficImpact AnalyzeBlockingImpact(
        IReadOnlySet<ResourceRef> blockingClosure,
        TimeInterval serviceWindow,
        IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> fleetPlannedResources)
    {
        ArgumentNullException.ThrowIfNull(blockingClosure);
        ArgumentNullException.ThrowIfNull(fleetPlannedResources);

        // A zero-length window holds nothing free, so it can affect no traffic and sever nothing.
        var windowHolds = serviceWindow.Duration > 0 && blockingClosure.Count > 0;

        var affected = new List<string>();
        var maxTraversal = 0;

        if (windowHolds)
        {
            // Deterministic: evaluate agents in ordinal id order, count each one's resources inside the closure.
            foreach (var agentId in OrderedKeys(fleetPlannedResources))
            {
                var planned = fleetPlannedResources[agentId];
                if (planned is null)
                    continue;

                var inClosure = 0;
                foreach (var resource in planned)
                {
                    if (blockingClosure.Contains(resource))
                        inClosure++;
                }

                if (inClosure > 0)
                {
                    affected.Add(agentId);
                    if (inClosure > maxTraversal)
                        maxTraversal = inClosure;
                }
            }
        }

        // Connectivity verdicts: project the closure onto site vertices and ask whether the core survives.
        var closureSites = TransitCoreTopology.ClosureSites(blockingClosure, _graph);
        var remainderConnected = TransitCoreTopology.RemainderConnected(_graph, closureSites);

        // The closure severs the core only if it actually removes a vertex and the remainder is disconnected.
        var blocksTransitCore = windowHolds && closureSites.Count > 0 && !remainderConnected;

        // A bypass exists when the core stays connected without the closure; never true once the core is severed.
        var hasBypass = !blocksTransitCore && remainderConnected;

        return new TrafficImpact(
            AffectedAgentIds: affected,
            BlocksTransitCore: blocksTransitCore,
            HasBypass: hasBypass,
            EstWaitTicks: maxTraversal);
    }

    private static IEnumerable<string> OrderedKeys(
        IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> fleetPlannedResources)
        => fleetPlannedResources.Keys.OrderBy(id => id, StringComparer.Ordinal);
}
