using System.Collections.Generic;
using System.Linq;
using NetDevPack.Domain;
using SwarmRoute.Liveness.Domain.Shared;

namespace SwarmRoute.Liveness.Domain.ValueObjects;

/// <summary>
/// The set of agent ids that participate in a single circular wait (a cycle in the
/// Resource-Allocation-Graph). Agent ids are stored without the <c>agent_</c> vertex prefix and are
/// kept ordered (ordinal) so the value object has a deterministic, stable identity regardless of the
/// order detection happened to discover them in.
/// </summary>
public sealed class DeadlockCycle : ValueObject
{
    private readonly IReadOnlyList<string> _agentIds;

    private DeadlockCycle(IReadOnlyList<string> agentIds)
    {
        _agentIds = agentIds;
    }

    /// <summary>The distinct agent ids in the circular wait, sorted ordinal-ascending.</summary>
    public IReadOnlyList<string> AgentIds => _agentIds;

    /// <summary>Number of agents in the cycle.</summary>
    public int Size => _agentIds.Count;

    /// <summary>
    /// Builds a cycle from raw agent ids (already stripped of any vertex prefix). Blank ids are rejected;
    /// duplicates are collapsed; the result is sorted ordinal.
    /// </summary>
    public static DeadlockCycle FromAgentIds(IEnumerable<string> agentIds)
    {
        ArgumentNullException.ThrowIfNull(agentIds);

        var normalized = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in agentIds)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException(DeadlockErrorCodes.InvalidAgentId, nameof(agentIds));
            normalized.Add(id.Trim());
        }

        if (normalized.Count == 0)
            throw new ArgumentException(DeadlockErrorCodes.EmptyCycle, nameof(agentIds));

        return new DeadlockCycle(normalized.ToList());
    }

    /// <summary>
    /// Builds a cycle from RAG vertex names, stripping the supplied <paramref name="agentPrefix"/>
    /// (default <see cref="ResourceAllocationGraph.AgentPrefix"/>) from each.
    /// </summary>
    public static DeadlockCycle FromVertices(
        IEnumerable<string> vertices,
        string agentPrefix = ResourceAllocationGraph.AgentPrefix)
    {
        ArgumentNullException.ThrowIfNull(vertices);

        var stripped = vertices.Select(v =>
            !string.IsNullOrEmpty(agentPrefix) && v is not null && v.StartsWith(agentPrefix, StringComparison.Ordinal)
                ? v[agentPrefix.Length..]
                : v ?? string.Empty);

        return FromAgentIds(stripped);
    }

    /// <summary>True if <paramref name="agentId"/> participates in this circular wait.</summary>
    public bool Contains(string agentId) =>
        !string.IsNullOrWhiteSpace(agentId) && _agentIds.Contains(agentId.Trim());

    protected override IEnumerable<object> GetEqualityComponents()
    {
        foreach (var id in _agentIds)
            yield return id;
    }

    public override string ToString() => $"DeadlockCycle[{string.Join(",", _agentIds)}]";
}
