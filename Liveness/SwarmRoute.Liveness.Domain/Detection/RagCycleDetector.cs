using AJR.Platform.Algorithms.Graphs;
using SwarmRoute.Liveness.Domain.Services;
using SwarmRoute.Liveness.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.Liveness.Domain.Detection;

/// <summary>
/// The single RAG cycle-detection primitive, serving BOTH liveness roles on byte-identical semantics
/// (one <see cref="ResourceAllocationGraph"/> builder, one vendored <see cref="CyclesDetector"/>, the same
/// <c>agent_</c> pivot + DFS):
/// <list type="bullet">
///   <item><see cref="IWouldCloseCycleDetector.WouldCloseCycle"/> — grant-time <b>prevention</b>: would adding a
///   candidate's would-be wait edges put it on a cycle? Consulted by <c>ReservationTable.TryGrant</c>.</item>
///   <item><see cref="IDeadlockDetector.Detect"/> — post-hoc <b>detection</b>: partition every cyclic agent into
///   independent circular waits.</item>
/// </list>
/// Merges the former <c>RagDeadlockDetector</c> (Liveness) and host <c>RagWouldCloseCycleDetector</c> into one
/// class so prevention and detection can never drift. Pure analyser — stateless, never mutates TrafficControl,
/// singleton-safe.
/// </summary>
public sealed class RagCycleDetector : IDeadlockDetector, IWouldCloseCycleDetector
{
    /// <inheritdoc />
    public bool WouldCloseCycle(
        ResourceAllocationGraphSnapshot currentEdges,
        string candidateAgentId,
        IReadOnlyCollection<(string OwnerAgentId, ResourceRef Resource)> candidateWaitEdges)
    {
        if (candidateWaitEdges is null || candidateWaitEdges.Count == 0 || string.IsNullOrWhiteSpace(candidateAgentId))
            return false;

        // Add the candidate's would-be wait edges (agent → resource) to the live graph, then ask whether the
        // candidate now lies on a cycle. The owner edges (resource → owner) already exist in currentEdges.Owns —
        // that is WHY the candidate is blocked — so its new wait edge is all that is needed to close a loop.
        var waits = new List<(string AgentId, ResourceRef Resource)>(currentEdges.Waits);
        foreach (var (_, resource) in candidateWaitEdges)
            waits.Add((candidateAgentId, resource));

        var rag = ResourceAllocationGraph.FromSnapshot(new ResourceAllocationGraphSnapshot(currentEdges.Owns, waits));
        var cyclic = CyclesDetector.CyclicVertices(rag.Build(), ResourceAllocationGraph.AgentPrefix);
        return cyclic.Contains(ResourceAllocationGraph.AgentPrefix + candidateAgentId.Trim());
    }

    /// <inheritdoc />
    public IReadOnlyList<DeadlockCycle> Detect(ResourceAllocationGraphSnapshot snapshot)
    {
        var rag = ResourceAllocationGraph.FromSnapshot(snapshot);
        var graph = rag.Build();

        // Port: agents that form a cycle in the RAG (vertices kept with the "agent_" prefix).
        var cyclicVertices = CyclesDetector.CyclicVertices(graph, ResourceAllocationGraph.AgentPrefix);
        if (cyclicVertices is null || cyclicVertices.Count == 0)
            return [];

        var cyclicAgents = new HashSet<string>(
            cyclicVertices
                .Where(v => v.StartsWith(ResourceAllocationGraph.AgentPrefix, StringComparison.Ordinal))
                .Select(v => v[ResourceAllocationGraph.AgentPrefix.Length..]),
            StringComparer.Ordinal);

        if (cyclicAgents.Count == 0)
            return [];

        var components = PartitionIntoCycles(rag, cyclicAgents);

        // Deterministic ordering of the reported cycles: by smallest member agent id.
        return components
            .Select(DeadlockCycle.FromAgentIds)
            .OrderBy(c => c.AgentIds[0], StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Splits the (over-approximated) set of cyclic agents into independent circular waits.
    /// <para>
    /// <c>CyclesDetector.CyclicVertices</c> (faithfully ported) flags every agent vertex *from which a
    /// cycle is reachable*, which over-includes agents that merely queue behind a deadlock without being
    /// part of any mutual wait. To report only genuine circular waits, we build the agent-blocking
    /// digraph — edge <c>a → b</c> when <c>a</c> waits on a resource that <c>b</c> owns — and return its
    /// non-trivial strongly-connected components (an SCC of size ≥ 2, or a singleton that has a self
    /// blocking-edge). A starving-but-not-circular waiter forms a trivial singleton SCC and is dropped.
    /// </para>
    /// </summary>
    private static List<List<string>> PartitionIntoCycles(
        ResourceAllocationGraph rag,
        HashSet<string> cyclicAgents)
    {
        // resource -> owning agents (any agent, restricted later to cyclic for edges)
        var ownersByResource = new Dictionary<ResourceRef, List<string>>();
        foreach (var (agentId, resource) in rag.Owns)
        {
            if (!ownersByResource.TryGetValue(resource, out var owners))
            {
                owners = [];
                ownersByResource[resource] = owners;
            }
            owners.Add(agentId);
        }

        // Adjacency over cyclic agents only: a -> b when a waits on a resource b owns.
        var adjacency = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var agent in cyclicAgents)
            adjacency[agent] = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var (agentId, resource) in rag.Waits)
        {
            if (!cyclicAgents.Contains(agentId))
                continue;
            if (!ownersByResource.TryGetValue(resource, out var owners))
                continue;
            foreach (var owner in owners)
            {
                if (cyclicAgents.Contains(owner))
                    adjacency[agentId].Add(owner); // self-edges allowed (owner == agentId)
            }
        }

        var sccs = StronglyConnectedComponents(adjacency);

        // Keep only non-trivial SCCs: size >= 2, or a singleton with a self blocking-edge.
        var result = new List<List<string>>();
        foreach (var scc in sccs)
        {
            if (scc.Count >= 2)
            {
                result.Add(scc);
            }
            else
            {
                var only = scc[0];
                if (adjacency[only].Contains(only))
                    result.Add(scc);
            }
        }

        return result;
    }

    /// <summary>
    /// Iterative Tarjan strongly-connected-components over the (deterministically-ordered) adjacency map.
    /// </summary>
    private static List<List<string>> StronglyConnectedComponents(
        Dictionary<string, SortedSet<string>> adjacency)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var nextIndex = 0;
        var components = new List<List<string>>();

        // Deterministic outer iteration order.
        foreach (var root in adjacency.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (index.ContainsKey(root))
                continue;

            // Iterative DFS frame: the node and an enumerator over its successors.
            var work = new Stack<(string Node, IEnumerator<string> Successors)>();
            index[root] = lowLink[root] = nextIndex++;
            onStack.Add(root);
            stack.Push(root);
            work.Push((root, adjacency[root].GetEnumerator()));

            while (work.Count > 0)
            {
                var (node, successors) = work.Peek();
                if (successors.MoveNext())
                {
                    var next = successors.Current;
                    if (!index.ContainsKey(next))
                    {
                        index[next] = lowLink[next] = nextIndex++;
                        onStack.Add(next);
                        stack.Push(next);
                        work.Push((next, adjacency[next].GetEnumerator()));
                    }
                    else if (onStack.Contains(next))
                    {
                        lowLink[node] = Math.Min(lowLink[node], index[next]);
                    }
                }
                else
                {
                    // Done with node: if it is a root of an SCC, pop the component.
                    if (lowLink[node] == index[node])
                    {
                        var component = new List<string>();
                        string w;
                        do
                        {
                            w = stack.Pop();
                            onStack.Remove(w);
                            component.Add(w);
                        }
                        while (!string.Equals(w, node, StringComparison.Ordinal));

                        component.Sort(StringComparer.Ordinal);
                        components.Add(component);
                    }

                    work.Pop();
                    if (work.Count > 0)
                    {
                        var parent = work.Peek().Node;
                        lowLink[parent] = Math.Min(lowLink[parent], lowLink[node]);
                    }
                }
            }
        }

        return components;
    }
}
