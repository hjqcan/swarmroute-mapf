using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Simulation.Application.Pibt;
using SwarmRoute.Simulation.Tests.TestSupport;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for the pure <see cref="PibtZoneResolver"/> joint-step resolver: head-on yielding, multi-agent
/// rotation, priority inheritance, deterministic tie-break, the all-hold deadlock floor, the directed-lane
/// guard, and the non-cluster blocked-cell gate. No engine, no I/O — the resolver is a pure function.
/// </summary>
public sealed class PibtZoneResolverTests
{
    private static readonly IReadOnlySet<string> NoneBlocked = new HashSet<string>(StringComparer.Ordinal);

    private static PibtAgentView Agent(string id, string cell, string goal, int priority, int held = 0)
        => new(id, cell, goal, priority, held);

    /// <summary>Memoizing hop-distance oracle, one BFS per distinct goal.</summary>
    private static Func<string, IReadOnlyDictionary<string, int>> Hops(RoadmapGraph g)
    {
        var cache = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        return goal => cache.TryGetValue(goal, out var d) ? d : cache[goal] = HopDistances.To(g, goal);
    }

    // ── Head-on: the higher-priority agent advances; the lower-priority one vacates without swapping ──────────
    [Fact]
    public void Head_on_pair_resolves_without_swap()
    {
        var g = new RoadmapGraphBuilder().Bidi("A", "B").Bidi("B", "C").Bidi("C", "D").Build();
        var cluster = new List<PibtAgentView>
        {
            Agent("agv-1", "B", "D", priority: 0), // higher priority, heading toward D → wants C
            Agent("agv-2", "C", "A", priority: 1), // heading toward A → wants B (would swap)
        };

        var move = PibtZoneResolver.Resolve(cluster, NoneBlocked, g, Hops(g));

        Assert.Equal("C", move["agv-1"]);                 // advanced toward its goal
        Assert.NotEqual("C", move["agv-2"]);              // vacated the contested cell
        Assert.NotEqual("B", move["agv-2"]);              // did NOT swap into the higher-priority agent's origin
        Assert.NotEqual(move["agv-1"], move["agv-2"]);    // distinct end cells
    }

    // ── Rotation: three agents on a directed ring each step one around (allowed; not a swap) ─────────────────
    [Fact]
    public void Three_agent_ring_rotates()
    {
        var g = new RoadmapGraphBuilder().Edge("A", "B").Edge("B", "C").Edge("C", "A").Build();
        var cluster = new List<PibtAgentView>
        {
            Agent("agv-1", "A", "C", priority: 0), // wants B
            Agent("agv-2", "B", "A", priority: 1), // wants C
            Agent("agv-3", "C", "B", priority: 2), // wants A
        };

        var move = PibtZoneResolver.Resolve(cluster, NoneBlocked, g, Hops(g));

        Assert.Equal("B", move["agv-1"]);
        Assert.Equal("C", move["agv-2"]);
        Assert.Equal("A", move["agv-3"]);
        Assert.Equal(3, new HashSet<string>(move.Values).Count); // all distinct
    }

    // ── Priority inheritance: a high-priority agent forces a goal-sitting low-priority agent off its cell ────
    [Fact]
    public void Inheritance_pushes_blocking_agent_off_its_cell()
    {
        var g = new RoadmapGraphBuilder().Bidi("A", "B").Bidi("B", "C").Build();
        var cluster = new List<PibtAgentView>
        {
            Agent("agv-1", "A", "C", priority: 0), // must pass through B
            Agent("agv-2", "B", "B", priority: 1), // sitting on its own goal, blocking B
        };

        var move = PibtZoneResolver.Resolve(cluster, NoneBlocked, g, Hops(g));

        Assert.Equal("B", move["agv-1"]);    // advanced into B
        Assert.Equal("C", move["agv-2"]);    // inheritance forced it off B
        Assert.NotEqual("B", move["agv-2"]);
    }

    // ── Determinism: symmetric contention is broken by (priority, ordinal id), reproducibly ──────────────────
    [Fact]
    public void Symmetric_contention_is_deterministic_and_ordinal_broken()
    {
        var g = new RoadmapGraphBuilder().Edge("A", "C").Edge("B", "C").Edge("C", "D").Build();
        var cluster = new List<PibtAgentView>
        {
            Agent("agv-1", "A", "D", priority: 0), // same priority as agv-2
            Agent("agv-2", "B", "D", priority: 0), // ties broken by ordinal id → agv-1 wins C
        };

        var first = PibtZoneResolver.Resolve(cluster, NoneBlocked, g, Hops(g));
        var second = PibtZoneResolver.Resolve(cluster, NoneBlocked, g, Hops(g));

        Assert.Equal("C", first["agv-1"]); // lower ordinal id wins the contested cell
        Assert.Equal("B", first["agv-2"]); // loser holds
        Assert.Equal(Serialize(first), Serialize(second));
    }

    // ── Deadlock floor: a one-wide head-on with no siding cannot be solved → both hold, no crash ─────────────
    [Fact]
    public void Unsolvable_corridor_holds_both_agents()
    {
        var g = new RoadmapGraphBuilder().Bidi("A", "B").Bidi("B", "C").Build(); // path A-B-C, no siding
        var cluster = new List<PibtAgentView>
        {
            Agent("agv-1", "B", "C", priority: 0),
            Agent("agv-2", "C", "B", priority: 1),
        };

        var move = PibtZoneResolver.Resolve(cluster, NoneBlocked, g, Hops(g));

        Assert.Equal("B", move["agv-1"]); // held (swap refused, no room to pass)
        Assert.Equal("C", move["agv-2"]); // held
    }

    // ── Directed lanes: on a one-way ring the resolver only ever proposes existing out-edges ────────────────
    [Fact]
    public void One_way_ring_never_proposes_a_missing_reverse_edge()
    {
        var g = new RoadmapGraphBuilder().Edge("A", "B").Edge("B", "C").Edge("C", "A").Build();
        var cluster = new List<PibtAgentView> { Agent("agv-1", "B", "A", priority: 0) };

        var move = PibtZoneResolver.Resolve(cluster, NoneBlocked, g, Hops(g));

        Assert.Equal("C", move["agv-1"]);    // the only directed move toward A is B→C→A
        Assert.NotEqual("A", move["agv-1"]); // there is no B→A edge to take
    }

    // ── A cell occupied/claimed by the rest of the fleet is off-limits to the cluster this tick ──────────────
    [Fact]
    public void Cell_blocked_by_non_cluster_agent_is_not_entered()
    {
        var g = new RoadmapGraphBuilder().Bidi("A", "B").Bidi("B", "C").Build();
        var cluster = new List<PibtAgentView> { Agent("agv-1", "B", "C", priority: 0) };
        var blocked = new HashSet<string>(StringComparer.Ordinal) { "C" }; // a non-cluster agent holds C

        var move = PibtZoneResolver.Resolve(cluster, blocked, g, Hops(g));

        Assert.Equal("B", move["agv-1"]); // cannot enter the blocked goal cell → holds
    }

    private static string Serialize(IReadOnlyDictionary<string, string> move)
        => string.Join(";", move.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}->{kv.Value}"));
}
