using SwarmRoute.PathPlanning.Application.Contract.Dtos;

namespace SwarmRoute.PathPlanning.Application.Contract.Services;

/// <summary>
/// Application service for single-agent path planning. Resolves the roadmap graph (via the Map context's
/// <c>IRoadmapQueryService</c>) and the current reservation view (via <c>IReservationQuery</c>), runs the
/// configured <c>IPathPlanner</c>, raises the plan integration event and returns a transport
/// <see cref="PlanResultDto"/>.
/// </summary>
public interface IPathPlanningAppService
{
    /// <summary>
    /// Plans a route for <paramref name="agentId"/> from <paramref name="fromSiteId"/> to
    /// <paramref name="toSiteId"/> on roadmap <paramref name="roadmapId"/>, departing at the fleet clock's
    /// current zero (release time 0 in v0).
    /// </summary>
    /// <returns>
    /// A <see cref="PlanResultDto"/> — on success, <see cref="PlanResultDto.Success"/> is <c>true</c> and
    /// <see cref="PlanResultDto.SiteSequence"/> carries the ordered route; on failure (unreachable goal /
    /// blocked endpoint / unknown site) it is <c>false</c> with a populated
    /// <see cref="PlanResultDto.FailureReason"/>.
    /// </returns>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">
    /// Thrown when <paramref name="roadmapId"/> does not resolve to a roadmap.
    /// </exception>
    Task<PlanResultDto> PlanForAsync(
        Guid roadmapId,
        string agentId,
        string fromSiteId,
        string toSiteId,
        CancellationToken cancellationToken = default);
}
