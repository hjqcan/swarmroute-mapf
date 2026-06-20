using SwarmRoute.Dispatch.Domain;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Application.Contract;

/// <summary>
/// The FMS-V2 traffic-impact analyser seam: given a station's blocking closure and the service window it would be
/// held for, estimates the effect on the rest of the fleet (交通影響評估器).
/// <para>
/// This is the producer of <see cref="TrafficImpact"/> the upgraded station scheduler weighs before admitting a
/// service. It is deliberately a <em>pure read</em> over a roadmap snapshot and the fleet's currently-planned
/// resources — it takes no reservations and mutates nothing, so the same inputs always yield the same impact.
/// </para>
/// </summary>
public interface ITrafficImpactAnalyzer
{
    /// <summary>
    /// Analyses the traffic impact of holding <paramref name="blockingClosure"/> free for
    /// <paramref name="serviceWindow"/>.
    /// </summary>
    /// <param name="blockingClosure">The station resources that must be held idle for the whole window.</param>
    /// <param name="serviceWindow">The half-open window the closure would be held for.</param>
    /// <param name="fleetPlannedResources">
    /// Per-agent ordered lists of the resources each vehicle currently plans to traverse. A vehicle is
    /// <em>affected</em> when any of its planned resources lies in the closure within the window. Pass an empty
    /// map when no fleet plan is known.
    /// </param>
    /// <returns>
    /// The <see cref="TrafficImpact"/>: the affected agents, whether the closure severs the transit core,
    /// whether a bypass route survives, and an estimated aggregate wait in ticks.
    /// </returns>
    TrafficImpact AnalyzeBlockingImpact(
        IReadOnlySet<ResourceRef> blockingClosure,
        TimeInterval serviceWindow,
        IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> fleetPlannedResources);
}
