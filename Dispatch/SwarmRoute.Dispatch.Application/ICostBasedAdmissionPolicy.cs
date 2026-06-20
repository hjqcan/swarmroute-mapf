using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The FMS-V3 opt-in cost-based admission policy seam (成本式准入策略). Where FMS-V2's transit gate is a binary
/// rule (sever the core → defer; soft impact → priority override), this policy scores the trade-off numerically so
/// a high-urgency service can go first over low-priority followers while a low-urgency service yields to even one
/// high-priority follower.
/// <para>
/// Supplied to the <see cref="StationScheduler"/> via an appended optional constructor parameter; when not supplied
/// the scheduler keeps its exact V2 (or V1) behaviour. Implementations are expected to be deterministic and
/// side-effect-free.
/// </para>
/// </summary>
public interface ICostBasedAdmissionPolicy
{
    /// <summary>
    /// Scores admitting <paramref name="request"/> given the <paramref name="impact"/> its blocking closure would
    /// have, and decides whether to admit now or defer.
    /// </summary>
    /// <param name="request">The service-admission request being weighed.</param>
    /// <param name="impact">The traffic impact of holding the station's closure for the service window.</param>
    /// <param name="fleetPlan">
    /// Optional fleet snapshot used to read affected vehicles' priorities (to count the high-priority blocked).
    /// When <see langword="null"/> no affected vehicle is treated as high-priority.
    /// </param>
    /// <returns>The <see cref="AdmissionCostScore"/>: the score, the admit/defer verdict, and the clear-first batch.</returns>
    AdmissionCostScore Score(
        ServiceAdmissionRequest request,
        TrafficImpact impact,
        IFleetPlanProvider? fleetPlan);
}
