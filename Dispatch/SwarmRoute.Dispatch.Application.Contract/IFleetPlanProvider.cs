using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Application.Contract;

/// <summary>
/// A read-only snapshot of the fleet's current plan, the optional input the FMS-V2 station scheduler feeds the
/// <see cref="ITrafficImpactAnalyzer"/> and consults for priority-aware admission (車隊計畫快照).
/// <para>
/// Supplied to the scheduler via its constructor (never on the frozen request signature). When absent the
/// scheduler falls back to exact V1 first-come-first-served admission. Implementations are expected to return a
/// consistent, side-effect-free snapshot per call.
/// </para>
/// </summary>
public interface IFleetPlanProvider
{
    /// <summary>
    /// The resources each vehicle currently plans to traverse, keyed by agent id. Consumed by the impact
    /// analyser to decide which vehicles a closure would affect.
    /// </summary>
    /// <returns>A per-agent map of ordered planned resources; empty when no plan is known.</returns>
    IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> GetPlannedResources();

    /// <summary>
    /// The admission priority currently associated with <paramref name="agentId"/> (higher wins contention),
    /// or <see langword="null"/> when the vehicle's priority is unknown.
    /// </summary>
    /// <param name="agentId">The vehicle whose priority to look up.</param>
    /// <returns>The vehicle's priority, or <see langword="null"/> if unknown.</returns>
    int? GetPriority(string agentId);
}
