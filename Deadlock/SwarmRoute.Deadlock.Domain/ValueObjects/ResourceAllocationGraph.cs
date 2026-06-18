using System.Collections.Generic;
using System.Linq;
using AJR.Platform.Algorithms.DataStructures.Graphs;
using NetDevPack.Domain;
using SwarmRoute.Deadlock.Domain.Shared;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Deadlock.Domain.ValueObjects;

/// <summary>
/// The Resource-Allocation-Graph (RAG) for a point-in-time snapshot of who holds and who waits on
/// which resources. This value object adapts the frozen Kernel
/// <see cref="ResourceAllocationGraphSnapshot"/> into a directed graph that cycle detection runs over.
/// <para>
/// Ported from <c>AJR.MAPF.ConflictDetect.MapResourceAllocationGraph.GenerateGraph</c>. The original
/// builds a directed sparse graph with three vertex families — <c>agent_</c>, <c>occupySite_</c> and
/// <c>applySite_</c> — and two edge families:
/// </para>
/// <list type="bullet">
/// <item><description><b>Ownership</b>: <c>occupySite_&lt;resource&gt; → agent_&lt;owner&gt;</c>
/// ("this resource is held by that agent").</description></item>
/// <item><description><b>Wait-for</b>: <c>agent_&lt;waiter&gt; → occupySite_&lt;resource&gt;</c>
/// ("this agent is blocked on that resource").</description></item>
/// </list>
/// <para>
/// Note: the original also adds standalone <c>applySite_</c> vertices, but its wait edges point at the
/// shared <c>occupySite_</c> vertex (so that a circular wait closes a cycle that passes through the
/// agent nodes). We reproduce that working behaviour here — both ownership and wait-for edges pivot on
/// a single resource vertex (<see cref="ResourcePrefix"/>) per resource so that
/// <c>agent → resource → agent → …</c> cycles can form. <c>applySite_</c> marker vertices are added for
/// fidelity/observability but, as in the source, carry no edges.
/// </para>
/// <para>This is an immutable value object: <see cref="Build"/> materialises a fresh
/// <see cref="DirectedSparseGraph{T}"/> on demand; equality is by the (sorted) owns/waits edge sets.</para>
/// </summary>
public sealed class ResourceAllocationGraph : ValueObject
{
    /// <summary>Vertex-name prefix for agent nodes.</summary>
    public const string AgentPrefix = "agent_";

    /// <summary>Vertex-name prefix for the (shared) resource nodes that ownership and wait edges pivot on.</summary>
    public const string ResourcePrefix = "occupySite_";

    /// <summary>Vertex-name prefix for the (edge-less) "applied for" marker nodes, kept for fidelity.</summary>
    public const string ApplyPrefix = "applySite_";

    private readonly IReadOnlyList<(string AgentId, string ResourceId)> _owns;
    private readonly IReadOnlyList<(string AgentId, string ResourceId)> _waits;

    private ResourceAllocationGraph(
        IReadOnlyList<(string AgentId, string ResourceId)> owns,
        IReadOnlyList<(string AgentId, string ResourceId)> waits)
    {
        _owns = owns;
        _waits = waits;
    }

    /// <summary>"Held" edges: agent currently owns resource.</summary>
    public IReadOnlyList<(string AgentId, string ResourceId)> Owns => _owns;

    /// <summary>"Request" edges: agent is blocked waiting on resource.</summary>
    public IReadOnlyList<(string AgentId, string ResourceId)> Waits => _waits;

    /// <summary>An empty graph (no ownership, no waits).</summary>
    public static ResourceAllocationGraph Empty { get; } = new([], []);

    /// <summary>
    /// Creates a RAG value object from the frozen Kernel snapshot, validating that every agent/resource
    /// id is non-blank.
    /// </summary>
    public static ResourceAllocationGraph FromSnapshot(ResourceAllocationGraphSnapshot snapshot)
    {
        if (snapshot is null)
            throw new ArgumentException(DeadlockErrorCodes.NullSnapshot, nameof(snapshot));

        var owns = Normalize(snapshot.Owns, nameof(snapshot.Owns));
        var waits = Normalize(snapshot.Waits, nameof(snapshot.Waits));
        return new ResourceAllocationGraph(owns, waits);
    }

    private static IReadOnlyList<(string AgentId, string ResourceId)> Normalize(
        IReadOnlyList<(string AgentId, string ResourceId)>? edges,
        string paramName)
    {
        if (edges is null || edges.Count == 0)
            return [];

        var result = new List<(string AgentId, string ResourceId)>(edges.Count);
        foreach (var (agentId, resourceId) in edges)
        {
            if (string.IsNullOrWhiteSpace(agentId))
                throw new ArgumentException(DeadlockErrorCodes.InvalidAgentId, paramName);
            if (string.IsNullOrWhiteSpace(resourceId))
                throw new ArgumentException(DeadlockErrorCodes.InvalidResourceId, paramName);

            result.Add((agentId.Trim(), resourceId.Trim()));
        }

        return result;
    }

    /// <summary>
    /// Materialises the directed sparse graph used by cycle detection. Vertices are added before edges
    /// (the underlying <see cref="DirectedSparseGraph{T}.AddEdge"/> silently no-ops if an endpoint is
    /// missing), mirroring the source's two-phase build.
    /// </summary>
    public DirectedSparseGraph<string> Build()
    {
        var graph = new DirectedSparseGraph<string>();

        // ----- vertices -----
        var vertices = new List<string>();

        // agent vertices (from both owns and waits endpoints)
        var agentIds = _owns.Select(e => e.AgentId)
            .Concat(_waits.Select(e => e.AgentId))
            .Distinct();
        vertices.AddRange(agentIds.Select(a => AgentPrefix + a));

        // shared resource vertices that ownership and wait edges pivot on
        var ownedResourceIds = _owns.Select(e => e.ResourceId).Distinct().ToList();
        vertices.AddRange(ownedResourceIds.Select(r => ResourcePrefix + r));

        // also add resource vertices for resources that are only waited-on (so wait edges have an endpoint)
        var waitedResourceIds = _waits.Select(e => e.ResourceId).Distinct();
        foreach (var r in waitedResourceIds)
        {
            var vertex = ResourcePrefix + r;
            if (!vertices.Contains(vertex))
                vertices.Add(vertex);
        }

        // applySite_ marker vertices (edge-less, kept for source fidelity / observability)
        vertices.AddRange(_waits.Select(e => ApplyPrefix + e.ResourceId).Distinct());

        graph.AddVertices(vertices.Distinct().ToList());

        // ----- edges -----
        // ownership: resource -> owner agent
        foreach (var (agentId, resourceId) in _owns)
            graph.AddEdge(ResourcePrefix + resourceId, AgentPrefix + agentId);

        // wait-for: waiter agent -> resource
        foreach (var (agentId, resourceId) in _waits)
            graph.AddEdge(AgentPrefix + agentId, ResourcePrefix + resourceId);

        return graph;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        foreach (var edge in _owns.OrderBy(e => e.AgentId).ThenBy(e => e.ResourceId))
            yield return $"O:{edge.AgentId}->{edge.ResourceId}";
        // separator so owns/waits with swapped roles are not considered equal
        yield return "|";
        foreach (var edge in _waits.OrderBy(e => e.AgentId).ThenBy(e => e.ResourceId))
            yield return $"W:{edge.AgentId}->{edge.ResourceId}";
    }
}
