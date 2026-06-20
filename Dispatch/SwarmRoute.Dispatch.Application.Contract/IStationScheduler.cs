using SwarmRoute.Dispatch.Domain;

namespace SwarmRoute.Dispatch.Application.Contract;

/// <summary>
/// The FMS station scheduler seam: decides whether a vehicle queued in a pre-dock buffer may be admitted to a
/// station's dock point, and when (停靠准入排程器).
/// <para>
/// <b>FMS semantics.</b> A station service is not a single-tick reservation but a long
/// <see cref="StationDefinition.ServiceDurationMs"/> interval-lease over the station's dock-point control point
/// <em>and</em> its <see cref="StationDefinition.BlockingClosure"/> zone (ADR-F2 — modelled on the frozen Kernel
/// vocabulary, no <c>ResourceKind.Station</c>). Admission is therefore granted only when that whole closure can be
/// held free for the window, weighing <see cref="ServiceAdmissionRequest.Priority"/>,
/// <see cref="ServiceAdmissionRequest.EarliestStartMs"/> and the optional
/// <see cref="ServiceAdmissionRequest.DeadlineMs"/>. The scheduler is the producer of
/// <see cref="ServiceAdmissionDecision"/> and, downstream (Round 2), feeds the dock-admission controller that
/// blocks station resources at the coordination loop via its existing <c>blockedResources</c> parameter (ADR-F3).
/// </para>
/// <para>
/// This contract is deliberately goal-agnostic in V1: it does not reference <c>AgentGoal</c>, so the
/// Coordination and Dispatch contexts do not form a reference cycle. The goal-filtering
/// <c>IDockAdmissionController</c> is introduced in Round 2.
/// </para>
/// </summary>
public interface IStationScheduler
{
    /// <summary>
    /// Evaluates a single vehicle's request for a dock-point service window and returns the admission verdict.
    /// Implementations are expected to be deterministic and side-effect-free with respect to the request itself
    /// (any reservation is taken via <see cref="IStationResourceCalendar"/>).
    /// </summary>
    /// <param name="request">The vehicle's service-admission request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="ServiceAdmissionDecision"/>: granted with a service start instant, or denied with a reason and
    /// (optionally) the blocking-closure occupants that must clear first.
    /// </returns>
    Task<ServiceAdmissionDecision> RequestDockAdmissionAsync(
        ServiceAdmissionRequest request,
        CancellationToken ct = default);
}
