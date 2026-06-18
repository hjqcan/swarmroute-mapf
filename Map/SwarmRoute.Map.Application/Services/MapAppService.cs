using AutoMapper;
using SwarmRoute.Map.Application.Contract.Dtos;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Application.Mapping;
using SwarmRoute.Map.Domain.Repositories;
using SwarmRoute.Map.Domain.Services;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Application.Services;

/// <summary>
/// Default <see cref="IMapAppService"/>. Imports/persists roadmap topologies through the repository and
/// unit of work, raising Map integration events (dispatched by the unit-of-work commit).
/// </summary>
public sealed class MapAppService : IMapAppService
{
    private readonly IRoadmapRepository _repository;
    private readonly IRoadmapGraphFactory _graphFactory;
    private readonly IMapper _mapper;

    public MapAppService(IRoadmapRepository repository, IRoadmapGraphFactory graphFactory, IMapper mapper)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _graphFactory = graphFactory ?? throw new ArgumentNullException(nameof(graphFactory));
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
    }

    /// <inheritdoc />
    public async Task<RoadmapDto> ImportAsync(ImportRoadmapRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = request.Id.HasValue && request.Id.Value != Guid.Empty ? request.Id.Value : Guid.NewGuid();

        // Builds entities + aggregate via validating constructors (throws ArgumentException on invalid topology).
        var roadmap = RoadmapFactory.FromRequest(id, request);

        roadmap.MarkImported();
        if (request.PublishOnImport)
            roadmap.MarkPublished();

        _repository.Add(roadmap);
        await _repository.UnitOfWork.Commit();

        return _mapper.Map<RoadmapDto>(roadmap);
    }

    /// <inheritdoc />
    public async Task<RoadmapDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var roadmap = await _repository.GetWithTopologyAsync(id, cancellationToken);
        return roadmap is null ? null : _mapper.Map<RoadmapDto>(roadmap);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RoadmapDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var roadmaps = await _repository.GetAllAsync(cancellationToken);
        return roadmaps.Select(r => _mapper.Map<RoadmapDto>(r)).ToList();
    }

    /// <inheritdoc />
    public async Task<RoadmapGraphSummaryDto?> GetGraphSummaryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var roadmap = await _repository.GetWithTopologyAsync(id, cancellationToken);
        if (roadmap is null)
            return null;

        var graph = _graphFactory.Build(roadmap);

        return new RoadmapGraphSummaryDto
        {
            RoadmapId = roadmap.Id,
            RoadmapName = roadmap.Name,
            StateVersion = roadmap.StateVersion,
            VertexCount = graph.VertexCount,
            EdgeCount = graph.EdgeCount,
            Vertices = graph.Vertices.OrderBy(v => v, StringComparer.Ordinal).ToList(),
            Edges = graph.Graph.Edges
                .Select(e => new GraphEdgeDto(e.Source, e.Destination, e.Weight))
                .OrderBy(e => e.From, StringComparer.Ordinal)
                .ThenBy(e => e.To, StringComparer.Ordinal)
                .ToList()
        };
    }

    /// <inheritdoc />
    public async Task<bool> PublishAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var roadmap = await _repository.GetWithTopologyAsync(id, cancellationToken);
        if (roadmap is null)
            return false;

        roadmap.MarkPublished();
        _repository.Update(roadmap);
        await _repository.UnitOfWork.Commit();
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var roadmap = await _repository.GetByIdAsync(id, cancellationToken);
        if (roadmap is null)
            return false;

        _repository.Remove(roadmap);
        await _repository.UnitOfWork.Commit();
        return true;
    }
}
