using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using SwarmRoute.Map.Application.Contract.Services;
using SwarmRoute.Map.Domain.Repositories;
using SwarmRoute.Map.Domain.Services;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Application.Services;

/// <summary>
/// Singleton implementation of <see cref="IRoadmapQueryService"/>. Loads a roadmap on cache miss, builds
/// its <see cref="RoadmapGraph"/> and caches it keyed by roadmap id. The cache is invalidated by
/// <see cref="Events.RoadmapPublishedCacheInvalidator"/> in response to <c>Map.Roadmap.Published</c>.
/// <para>
/// Because graph building must read the persistence layer (a scoped <see cref="IRoadmapRepository"/>) from a
/// singleton, repositories are resolved through a fresh DI scope per build.
/// </para>
/// </summary>
public sealed class RoadmapGraphProvider : IRoadmapQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    // Cache the built graph keyed by roadmap id. ConcurrentDictionary gives lock-free reads; a value of
    // null is never stored (misses are simply absent), so presence == cached.
    private readonly ConcurrentDictionary<Guid, RoadmapGraph> _cache = new();

    public RoadmapGraphProvider(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    /// <inheritdoc />
    public async Task<RoadmapGraph> GetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default)
    {
        var graph = await TryGetGraphAsync(roadmapId, cancellationToken);
        return graph ?? throw new KeyNotFoundException($"Roadmap '{roadmapId}' was not found.");
    }

    /// <inheritdoc />
    public async Task<RoadmapGraph?> TryGetGraphAsync(Guid roadmapId, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(roadmapId, out var cached))
            return cached;

        var built = await BuildAsync(roadmapId, cancellationToken);
        if (built is null)
            return null;

        // GetOrAdd keeps a single instance even under concurrent first-access.
        return _cache.GetOrAdd(roadmapId, built);
    }

    /// <inheritdoc />
    public void Invalidate(Guid roadmapId) => _cache.TryRemove(roadmapId, out _);

    private async Task<RoadmapGraph?> BuildAsync(Guid roadmapId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRoadmapRepository>();
        var factory = scope.ServiceProvider.GetRequiredService<IRoadmapGraphFactory>();

        var roadmap = await repository.GetWithTopologyAsync(roadmapId, cancellationToken);
        return roadmap is null ? null : factory.Build(roadmap);
    }
}
