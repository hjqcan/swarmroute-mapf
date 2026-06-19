using System.Collections.Generic;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Liveness.Tests;

/// <summary>
/// Small fluent helper to fabricate <see cref="ResourceAllocationGraphSnapshot"/>s in tests.
/// </summary>
internal sealed class SnapshotBuilder
{
    private readonly List<(string AgentId, ResourceRef Resource)> _owns = [];
    private readonly List<(string AgentId, ResourceRef Resource)> _waits = [];

    public SnapshotBuilder Owns(string agentId, string resourceId)
        => Owns(agentId, new ResourceRef(ResourceKind.CP, resourceId));

    public SnapshotBuilder Owns(string agentId, ResourceRef resource)
    {
        _owns.Add((agentId, resource));
        return this;
    }

    public SnapshotBuilder Waits(string agentId, string resourceId)
        => Waits(agentId, new ResourceRef(ResourceKind.CP, resourceId));

    public SnapshotBuilder Waits(string agentId, ResourceRef resource)
    {
        _waits.Add((agentId, resource));
        return this;
    }

    public ResourceAllocationGraphSnapshot Build() => new(_owns, _waits);

    /// <summary>
    /// A canonical n-agent circular wait: agent i owns resource r_i and waits on r_{(i+1)%n}.
    /// Agents are named "A","B","C",… ; resources "r1","r2",… .
    /// </summary>
    public static ResourceAllocationGraphSnapshot Cycle(int n)
    {
        var b = new SnapshotBuilder();
        for (var i = 0; i < n; i++)
        {
            var agent = ((char)('A' + i)).ToString();
            var ownRes = $"r{i + 1}";
            var wantRes = $"r{(i + 1) % n + 1}";
            b.Owns(agent, ownRes);
            b.Waits(agent, wantRes);
        }

        return b.Build();
    }
}
