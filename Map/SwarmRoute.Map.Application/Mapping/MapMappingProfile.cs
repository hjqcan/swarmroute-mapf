using AutoMapper;
using SwarmRoute.Map.Application.Contract.Dtos;
using SwarmRoute.Map.Domain.Aggregates;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Application.Mapping;

/// <summary>
/// AutoMapper profile for the Map context: domain aggregate/entities/VOs → transport DTOs.
/// (Domain construction from DTOs is done explicitly via validating constructors, not AutoMapper,
/// so aggregate invariants are always enforced.)
/// </summary>
public sealed class MapMappingProfile : Profile
{
    public MapMappingProfile()
    {
        CreateMap<MapPosition, MapPositionDto>();

        CreateMap<MapSite, MapSiteDto>()
            .ForMember(d => d.InterferenceSiteIds, o => o.MapFrom(s => s.InterferenceSiteIds))
            .ForMember(d => d.InterferenceLineIds, o => o.MapFrom(s => s.InterferenceLineIds));

        CreateMap<MapLine, MapLineDto>()
            .ForMember(d => d.InterferenceSiteIds, o => o.MapFrom(s => s.InterferenceSiteIds))
            .ForMember(d => d.InterferenceLineIds, o => o.MapFrom(s => s.InterferenceLineIds));

        CreateMap<MapBlock, MapBlockDto>()
            .ForMember(d => d.ContainedSiteIds, o => o.MapFrom(s => s.ContainedSiteIds))
            .ForMember(d => d.ContainedLineIds, o => o.MapFrom(s => s.ContainedLineIds));

        CreateMap<Roadmap, RoadmapDto>();
    }
}
