using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.Liveness.Application.Policy;
using SwarmRoute.Liveness.Domain.Resolution;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using Xunit;

namespace SwarmRoute.Liveness.Tests;

/// <summary>
/// The FMS-V1 "in-service" gate: a vehicle whose <see cref="MobilityClass"/> is
/// <see cref="MobilityClass.ImmovableUntilServiceComplete"/> (docked and mid-service) is a HARD immovable obstacle.
/// It must never be relocated (step-aside), PIBT-driven, CBS-driven, or yielded by the policy — even when it blocks
/// other agents. Each test pairs the immovable scenario with the SAME scenario where the blocker is
/// <see cref="MobilityClass.Movable"/>, which DOES trigger relocation / joint-resolution — proving the gate (not an
/// inert fixture) is what suppresses it, and that the default <see cref="MobilityClass.Movable"/> path is unchanged.
/// </summary>
public sealed class InServiceGateTests
{
    // ── Small directed-graph builder (mirrors LivenessPolicyTests) ──────────────────────────────────────────────
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

    // A view with sensible defaults; tests override only the fields they exercise (incl. the new Mobility).
    private static AgentLivenessView View(
        string id, string position, string goal, int priority,
        string? enRouteNextCell = null, bool enRoute = false, bool done = false,
        bool inJointResolver = false, bool hasActiveRedirect = false, bool holdingAtAvoidSite = false,
        int blockedTicks = 0, int stuckTicks = 0, int yieldTicksRemaining = 0,
        int pibtHeldTicks = 0, int pibtEpisodeTicksLeft = 0,
        bool atRouteEnd = false, bool nextCellIsParked = false,
        bool scheduledToAdvance = false, bool scheduledToMoveThisTick = false,
        MobilityClass mobility = MobilityClass.Movable) =>
        new(id, position, goal, EffectiveGoal: goal, priority, enRouteNextCell, enRoute, done,
            inJointResolver, hasActiveRedirect, holdingAtAvoidSite, blockedTicks, stuckTicks, yieldTicksRemaining,
            pibtHeldTicks, pibtEpisodeTicksLeft, atRouteEnd, nextCellIsParked, scheduledToAdvance,
            scheduledToMoveThisTick, mobility);

    // A <-> B head-on swap with sidings so a joint resolver has room to shuffle; agent "blocker" sits at A wanting B,
    // "mover" sits at B wanting A. Both en-route and stalled to the trigger threshold (mutually obstructing).
    private static LivenessSnapshot HeadOnCluster(LivenessPhase phase, MobilityClass blockerMobility) =>
        new(
            Tick: 9, phase, ScheduleFaithful: true,
            new[]
            {
                View("blocker", "A", "B", priority: 0, enRouteNextCell: "B", enRoute: true, blockedTicks: 8,
                    mobility: blockerMobility),
                View("mover", "B", "A", priority: 1, enRouteNextCell: "A", enRoute: true, blockedTicks: 8),
            },
            new HashSet<string>());

    private static RoadmapGraph SwapGraphWithSidings() => Graph(
        ["A", "B", "S1", "S2"],
        ("A", "B"), ("B", "A"), ("A", "S1"), ("S1", "A"), ("B", "S2"), ("S2", "B"));

    // ── (1) PIBT cluster: an in-service blocker is never entered, and never pulls its blocked peer into PIBT ──────
    [Fact]
    public void ClusterFormation_Pibt_NeverEntersAnInServiceVehicle()
    {
        var graph = SwapGraphWithSidings();
        var policy = new LivenessPolicy(graph, new LivenessOptions { JointResolver = JointResolverKind.Pibt });

        var directives = policy.Evaluate(
            HeadOnCluster(LivenessPhase.ClusterFormation, MobilityClass.ImmovableUntilServiceComplete));

        // The immovable vehicle is a non-candidate ⇒ "mover" is a lone singleton ⇒ the cluster is dropped:
        // nobody is handed to PIBT (not the immovable blocker, and not the agent it walls in).
        var entered = directives.OfType<EnterJointResolver>().SelectMany(e => e.AgentIds).ToHashSet();
        Assert.Empty(entered);
    }

    // Control: the SAME scenario with a MOVABLE blocker DOES form a cluster and enters both agents into PIBT.
    [Fact]
    public void ClusterFormation_Pibt_MovableBlocker_StillEntersBoth()
    {
        var graph = SwapGraphWithSidings();
        var policy = new LivenessPolicy(graph, new LivenessOptions { JointResolver = JointResolverKind.Pibt });

        var directives = policy.Evaluate(
            HeadOnCluster(LivenessPhase.ClusterFormation, MobilityClass.Movable));

        var entered = directives.OfType<EnterJointResolver>().SelectMany(e => e.AgentIds).ToHashSet();
        Assert.Contains("blocker", entered);
        Assert.Contains("mover", entered);
    }

    // ── (2) CBS cluster: an in-service blocker is never handed to CBS, and its peer is left to prioritized SIPP ───
    [Fact]
    public void ClusterFormation_Cbs_NeverSolvesAnInServiceVehicle()
    {
        var graph = Graph(["A", "B"], ("A", "B"), ("B", "A"));
        var policy = new LivenessPolicy(graph, new LivenessOptions { JointResolver = JointResolverKind.Cbs });

        var directives = policy.Evaluate(
            HeadOnCluster(LivenessPhase.ClusterFormation, MobilityClass.ImmovableUntilServiceComplete));

        // Immovable ⇒ non-candidate ⇒ "mover" is a singleton ⇒ no joint CBS solve is emitted at all.
        Assert.Empty(directives.OfType<SolveClusterJointly>());
    }

    [Fact]
    public void ClusterFormation_Cbs_MovableBlocker_StillSolves()
    {
        var graph = Graph(["A", "B"], ("A", "B"), ("B", "A"));
        var policy = new LivenessPolicy(graph, new LivenessOptions { JointResolver = JointResolverKind.Cbs });

        var directives = policy.Evaluate(
            HeadOnCluster(LivenessPhase.ClusterFormation, MobilityClass.Movable));

        var solves = directives.OfType<SolveClusterJointly>().ToList();
        Assert.Single(solves);
        Assert.Equal(new[] { "blocker", "mover" }, solves[0].AgentIds.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ── (3) Step-aside: an in-service vehicle sealing a goal approach is never relocated (it is a hard obstacle) ──
    [Fact]
    public void BeforePlanning_StepAside_NeverRelocatesAnInServiceVehicle()
    {
        // S -> P -> G is the only approach; P is sealed. P also has a free off-path neighbour F to step to. The
        // sealing vehicle is docked & in service, so it must NOT be sent aside even though "agv-walled" is walled out.
        var graph = Graph(["S", "P", "G", "F"], ("S", "P"), ("P", "G"), ("P", "F"));
        var policy = new LivenessPolicy(graph, new LivenessOptions { StepAside = true });

        var snapshot = new LivenessSnapshot(
            Tick: 12, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-walled", "S", "G", priority: 0, stuckTicks: 10),
                // A docked, in-service vehicle holding P (NOT done — it is mid-service, not parked at its goal).
                View("agv-service", "P", "P", priority: 1, mobility: MobilityClass.ImmovableUntilServiceComplete),
            },
            new HashSet<string> { "P" });

        Assert.Empty(policy.Evaluate(snapshot).OfType<RelocateParked>());
    }

    // Control: a finished MOVABLE vehicle sealing the same approach IS relocated off-path (the pre-FMS behaviour).
    [Fact]
    public void BeforePlanning_StepAside_MovableParkedBlocker_IsRelocated()
    {
        var graph = Graph(["S", "P", "G", "F"], ("S", "P"), ("P", "G"), ("P", "F"));
        var policy = new LivenessPolicy(graph, new LivenessOptions { StepAside = true });

        var snapshot = new LivenessSnapshot(
            Tick: 12, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-walled", "S", "G", priority: 0, stuckTicks: 10),
                View("agv-parked", "P", "P", priority: 1, done: true), // Movable (default), finished, parked on P
            },
            new HashSet<string> { "P" });

        var relocations = policy.Evaluate(snapshot).OfType<RelocateParked>().ToList();
        Assert.Single(relocations);
        Assert.Equal("agv-parked", relocations[0].BlockerId);
        Assert.Equal("F", relocations[0].Dest);
    }

    // ── (4) The pure selector itself never picks an in-service vehicle as a blocker to step aside ────────────────
    [Fact]
    public void ParkedRelocationSelector_SkipsImmovableBlocker()
    {
        var graph = Graph(["S", "P", "G", "F"], ("S", "P"), ("P", "G"), ("P", "F"));

        // An in-service vehicle on P (modelled as "parked" on the approach so the selector would otherwise pick it).
        var immovable = new[]
        {
            new RelocationAgentState("agv-walled", "S", "G", StuckTicks: 10, YieldTicksRemaining: 0,
                IsWalledCandidate: true, IsParked: false),
            new RelocationAgentState("agv-service", "P", "P", StuckTicks: 0, YieldTicksRemaining: 0,
                IsWalledCandidate: false, IsParked: true,
                Mobility: MobilityClass.ImmovableUntilServiceComplete),
        };
        var withImmovable = ParkedRelocationSelector.Select(
            immovable, new HashSet<string> { "P" }, graph, gatekeeperUnblockThreshold: 10);
        Assert.Empty(withImmovable);

        // Control: the same blocker as a default-Movable parked vehicle IS selected and sent aside to F.
        var movable = new[]
        {
            new RelocationAgentState("agv-walled", "S", "G", StuckTicks: 10, YieldTicksRemaining: 0,
                IsWalledCandidate: true, IsParked: false),
            new RelocationAgentState("agv-parked", "P", "P", StuckTicks: 0, YieldTicksRemaining: 0,
                IsWalledCandidate: false, IsParked: true),
        };
        var withMovable = ParkedRelocationSelector.Select(
            movable, new HashSet<string> { "P" }, graph, gatekeeperUnblockThreshold: 10);
        var r = Assert.Single(withMovable);
        Assert.Equal("agv-parked", r.BlockerId);
        Assert.Equal("F", r.Dest);
        Assert.Equal("agv-walled", r.WalledAgentId);
    }

    // ── (5) Advance phase: an in-service en-route-flagged vehicle is never told to yield / re-plan ────────────────
    [Fact]
    public void Advance_NeverYieldsAnInServiceVehicle()
    {
        // A <-> B head-on at the yield threshold. The would-be lower-priority yielder is in service ⇒ it must hold;
        // the higher-priority partner is not past ITS (larger) stall window, so NObody yields this tick.
        var graph = Graph(["A", "B"], ("A", "B"), ("B", "A"));
        var policy = new LivenessPolicy(graph, new LivenessOptions()); // JointResolver = None

        var snapshot = new LivenessSnapshot(
            Tick: 5, LivenessPhase.Advance, ScheduleFaithful: true,
            new[]
            {
                View("agv-hi", "A", "B", priority: 0, enRouteNextCell: "B", enRoute: true,
                    blockedTicks: 3, scheduledToMoveThisTick: true),
                // Lower priority, would normally yield the swap — but it is in service, so it is skipped entirely.
                View("agv-service", "B", "A", priority: 1, enRouteNextCell: "A", enRoute: true,
                    blockedTicks: 3, scheduledToMoveThisTick: true,
                    mobility: MobilityClass.ImmovableUntilServiceComplete),
            },
            new HashSet<string>());

        Assert.Empty(policy.Evaluate(snapshot).OfType<YieldAndReplan>());
    }

    // Control: with the lower-priority partner MOVABLE, it yields the swap (the pre-FMS head-on behaviour).
    [Fact]
    public void Advance_MovableLowerPriority_YieldsTheSwap()
    {
        var graph = Graph(["A", "B"], ("A", "B"), ("B", "A"));
        var policy = new LivenessPolicy(graph, new LivenessOptions());

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

        var yields = policy.Evaluate(snapshot).OfType<YieldAndReplan>().ToList();
        Assert.Single(yields);
        Assert.Equal("agv-lo", yields[0].AgentId);
    }
}
