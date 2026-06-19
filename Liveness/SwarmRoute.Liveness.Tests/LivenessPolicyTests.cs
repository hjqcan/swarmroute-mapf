using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.Liveness.Application.Policy;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using Xunit;

namespace SwarmRoute.Liveness.Tests;

/// <summary>
/// Unit tests for the pure <see cref="LivenessPolicy"/> — no engine, no I/O. They feed a hand-built
/// <see cref="LivenessSnapshot"/> at a given <see cref="LivenessPhase"/> and assert the directives, proving the
/// extracted PHYSICAL-standoff decisions in isolation: head-on yield, congestion-cluster handoff (PIBT vs CBS),
/// and the parked step-aside. Behaviour-parity with the executor is the integration suite's job; this pins the
/// decision logic itself.
/// </summary>
public sealed class LivenessPolicyTests
{
    // ── Small directed-graph builder ────────────────────────────────────────────────────────────────────────
    private static RoadmapGraph Graph(IEnumerable<string> sites, params (string From, string To)[] edges)
    {
        var siteEntities = sites
            .Select((id, i) => new MapSite(id, MapSiteType.RelaySite, new MapPosition(i, 0)))
            .ToList();
        var lineEntities = edges
            .Select(e => new MapLine($"{e.From}-{e.To}", e.From, e.To, distance: 1))
            .ToList();
        return RoadmapGraph.Build(siteEntities, lineEntities);
    }

    // A view with sensible defaults; tests override only the fields they exercise.
    private static AgentLivenessView View(
        string id, string position, string goal, int priority,
        string? enRouteNextCell = null, bool enRoute = false, bool done = false,
        bool inJointResolver = false, bool hasActiveRedirect = false, bool holdingAtAvoidSite = false,
        int blockedTicks = 0, int stuckTicks = 0, int yieldTicksRemaining = 0,
        int pibtHeldTicks = 0, int pibtEpisodeTicksLeft = 0,
        bool atRouteEnd = false, bool nextCellIsParked = false,
        bool scheduledToAdvance = false, bool scheduledToMoveThisTick = false) =>
        new(id, position, goal, EffectiveGoal: goal, priority, enRouteNextCell, enRoute, done,
            inJointResolver, hasActiveRedirect, holdingAtAvoidSite, blockedTicks, stuckTicks, yieldTicksRemaining,
            pibtHeldTicks, pibtEpisodeTicksLeft, atRouteEnd, nextCellIsParked, scheduledToAdvance, scheduledToMoveThisTick);

    private static LivenessPolicy PolicyOn(RoadmapGraph graph, LivenessOptions options) => new(graph, options);

    // ── (1) Head-on swap → exactly one YieldAndReplan, for the LOWER-priority agent ────────────────────────────
    [Fact]
    public void HeadOnSwap_YieldsTheLowerPriorityAgent_Once()
    {
        // A <-> B. agv-hi (prio 0) at A wants B; agv-lo (prio 1) at B wants A — a direct swap. Both are scheduled
        // to move this tick but neither advanced (the resolver refuses the swap), and both have stalled to the
        // head-on threshold. The lower-priority one must yield; the higher-priority one holds (its threshold is the
        // larger stall window, not yet reached).
        var graph = Graph(["A", "B"], ("A", "B"), ("B", "A"));
        var policy = PolicyOn(graph, new LivenessOptions()); // JointResolver = None, defaults

        var snapshot = new LivenessSnapshot(
            Tick: 5, LivenessPhase.Advance, ScheduleFaithful: true,
            new[]
            {
                View("agv-hi", "A", "B", priority: 0, enRouteNextCell: "B", enRoute: true,
                    blockedTicks: 3, scheduledToMoveThisTick: true),
                View("agv-lo", "B", "A", priority: 1, enRouteNextCell: "A", enRoute: true,
                    blockedTicks: 3, scheduledToMoveThisTick: true),
            },
            new HashSet<string>());

        var directives = policy.Evaluate(snapshot);

        var yields = directives.OfType<YieldAndReplan>().ToList();
        Assert.Single(yields);
        Assert.Equal("agv-lo", yields[0].AgentId);
        Assert.Equal(YieldAndReplan.HeadOnReason, yields[0].Reason);
    }

    // ── (2) Stuck cluster with JointResolver = Pibt → EnterJointResolver, then MoveTo on the drive phase ───────
    [Fact]
    public void StuckCluster_WithPibt_EntersJointResolver_ThenDrives()
    {
        // A <-> B with sidings (A<->S1, B<->S2) so PIBT has somewhere to shuffle. agv-1 at A wants B; agv-2 at B
        // wants A — mutually blocking (each one's intended next cell is the other's cell), stalled past the trigger.
        var graph = Graph(
            ["A", "B", "S1", "S2"],
            ("A", "B"), ("B", "A"), ("A", "S1"), ("S1", "A"), ("B", "S2"), ("S2", "B"));
        var policy = PolicyOn(graph, new LivenessOptions { JointResolver = JointResolverKind.Pibt });

        // ClusterFormation: both are en-route, mutually blocking, one stalled to the trigger threshold (8).
        var formation = policy.Evaluate(new LivenessSnapshot(
            Tick: 9, LivenessPhase.ClusterFormation, ScheduleFaithful: true,
            new[]
            {
                View("agv-1", "A", "B", priority: 0, enRouteNextCell: "B", enRoute: true, blockedTicks: 8),
                View("agv-2", "B", "A", priority: 1, enRouteNextCell: "A", enRoute: true, blockedTicks: 8),
            },
            new HashSet<string>()));

        var entered = formation.OfType<EnterJointResolver>().SelectMany(e => e.AgentIds).ToHashSet();
        Assert.Contains("agv-1", entered);
        Assert.Contains("agv-2", entered);
        Assert.Empty(formation.OfType<SolveClusterJointly>()); // PIBT, not CBS

        // JointDrive: the two are now joint-resolver-driven (released, holding no lease). Expect a MoveTo each.
        var drive = policy.Evaluate(new LivenessSnapshot(
            Tick: 10, LivenessPhase.JointDrive, ScheduleFaithful: true,
            new[]
            {
                View("agv-1", "A", "B", priority: 0, inJointResolver: true, pibtEpisodeTicksLeft: 8),
                View("agv-2", "B", "A", priority: 1, inJointResolver: true, pibtEpisodeTicksLeft: 8),
            },
            new HashSet<string>()));

        var moves = drive.OfType<MoveTo>().Select(m => m.AgentId).ToHashSet();
        Assert.Contains("agv-1", moves);
        Assert.Contains("agv-2", moves);
    }

    // ── (3) Stuck cluster with JointResolver = Cbs → SolveClusterJointly ──────────────────────────────────────
    [Fact]
    public void StuckCluster_WithCbs_EmitsSolveClusterJointly()
    {
        var graph = Graph(["A", "B"], ("A", "B"), ("B", "A"));
        var policy = PolicyOn(graph, new LivenessOptions { JointResolver = JointResolverKind.Cbs });

        var formation = policy.Evaluate(new LivenessSnapshot(
            Tick: 9, LivenessPhase.ClusterFormation, ScheduleFaithful: true,
            new[]
            {
                View("agv-1", "A", "B", priority: 0, enRouteNextCell: "B", enRoute: true, blockedTicks: 8),
                View("agv-2", "B", "A", priority: 1, enRouteNextCell: "A", enRoute: true, blockedTicks: 8),
            },
            new HashSet<string>()));

        var solves = formation.OfType<SolveClusterJointly>().ToList();
        Assert.Single(solves);
        Assert.Equal(new[] { "agv-1", "agv-2" }, solves[0].AgentIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Empty(formation.OfType<EnterJointResolver>()); // CBS, not PIBT
    }

    // ── (4) Parked-sealed goal with StepAside → RelocateParked to an off-path free neighbour ──────────────────
    [Fact]
    public void ParkedSealedGoal_WithStepAside_RelocatesTheBlockerOffPath()
    {
        // S -> P -> G is the only approach; P is sealed by a parked vehicle. P also has a free off-path neighbour F.
        // The walled agent (stuck past the gatekeeper threshold) should have the parked blocker sent aside to F.
        var graph = Graph(
            ["S", "P", "G", "F"],
            ("S", "P"), ("P", "G"), ("P", "F"));
        var policy = PolicyOn(graph, new LivenessOptions { StepAside = true });

        var snapshot = new LivenessSnapshot(
            Tick: 12, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                // Walled-out waiting agent: not en route, not done, stuck past the unblock threshold (10).
                View("agv-walled", "S", "G", priority: 0, stuckTicks: 10),
                // Finished vehicle parked on the only approach P (Done, not currently yielding).
                View("agv-parked", "P", "P", priority: 1, done: true),
            },
            new HashSet<string> { "P" });

        var directives = policy.Evaluate(snapshot);

        var relocations = directives.OfType<RelocateParked>().ToList();
        Assert.Single(relocations);
        Assert.Equal("agv-parked", relocations[0].BlockerId);
        Assert.Equal("F", relocations[0].Dest); // the only free, non-parked, non-goal neighbour of P
        Assert.Equal("agv-walled", relocations[0].WalledAgentId);
        Assert.Equal(new LivenessOptions().GatekeeperYieldWindow, relocations[0].YieldWindow);
    }

    // ── Off case: StepAside disabled ⇒ no relocation even with a walled agent ─────────────────────────────────
    [Fact]
    public void ParkedSealedGoal_WithoutStepAside_DoesNothing()
    {
        var graph = Graph(["S", "P", "G", "F"], ("S", "P"), ("P", "G"), ("P", "F"));
        var policy = PolicyOn(graph, new LivenessOptions { StepAside = false });

        var directives = policy.Evaluate(new LivenessSnapshot(
            Tick: 12, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-walled", "S", "G", priority: 0, stuckTicks: 10),
                View("agv-parked", "P", "P", priority: 1, done: true),
            },
            new HashSet<string> { "P" }));

        Assert.Empty(directives.OfType<RelocateParked>());
    }
}
