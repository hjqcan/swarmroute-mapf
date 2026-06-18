using AutoMapper;
using SwarmRoute.PathPlanning.Application.Contract.Dtos;
using SwarmRoute.PathPlanning.Domain.Aggregates;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.PathPlanning.Application.Mapping;

/// <summary>
/// AutoMapper profile for the PathPlanning context: the <c>AgentPlan</c> aggregate → the transport
/// <see cref="PlanResultDto"/>. The route's ordered site ids are projected from the aggregate's
/// <c>SpaceTimePath</c> control-point cells; cost fields flatten the <c>PlanCost</c> VO.
/// </summary>
public sealed class PathPlanningMappingProfile : Profile
{
    public PathPlanningMappingProfile()
    {
        CreateMap<AgentPlan, PlanResultDto>()
            .ForMember(d => d.Success, o => o.MapFrom(s => s.HasPath))
            .ForMember(d => d.SiteSequence, o => o.MapFrom(s => SiteSequenceOf(s)))
            .ForMember(d => d.DistanceUnits, o => o.MapFrom(s => s.Cost == null ? 0L : s.Cost.DistanceUnits))
            .ForMember(d => d.HopCount, o => o.MapFrom(s => s.Cost == null ? 0 : s.Cost.HopCount))
            .ForMember(d => d.DurationMs, o => o.MapFrom(s => s.Cost == null ? 0L : s.Cost.DurationMs))
            .ForMember(d => d.FailureReason, o => o.MapFrom(s => s.FailureReason));
    }

    /// <summary>Projects an <c>AgentPlan</c>'s current path to its ordered control-point site ids (empty when none).</summary>
    private static IReadOnlyList<string> SiteSequenceOf(AgentPlan plan)
        => plan.Path is null
            ? Array.Empty<string>()
            : plan.Path.Cells
                .Where(c => c.Resource.Kind == ResourceKind.CP)
                .Select(c => c.Resource.Id)
                .ToList();
}
