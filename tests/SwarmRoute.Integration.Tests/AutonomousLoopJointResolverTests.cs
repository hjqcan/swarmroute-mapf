using SwarmRoute.Coordination.Application;
using SwarmRoute.Integration.Tests.TestSupport;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Shared;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// The autonomous loop's joint standoff resolver (the v3 host-seam) driven through the REAL engine. After a cycle
/// leaves agents contended, <see cref="IFleetCoordinationCycle.ResolveStandoffsAsync"/> forms the mutually-blocking
/// clusters and resolves each with the configured resolver: <b>CBS</b> solves + reserves the cluster jointly;
/// <b>PIBT</b> commits the cluster's next collision-free joint single hop atomically through the reservation table
/// (<c>TryGrantJointStep</c>, the table as the single authority). Opt-in — <see cref="JointResolverKind.None"/> is a
/// no-op. The standoff is a head-on on the corridor A-B-C with a passing bay D off B: agv-1 (A→C) intends B, where
/// agv-2 (B→A) sits, and agv-2 intends A, where agv-1 sits — a mutually-blocking pair. PIBT cracks it by shuffling
/// agv-2 aside (to C); CBS solves it using the bay (agv-2 waits in D while agv-1 passes).
/// </summary>
public sealed class AutonomousLoopJointResolverTests
{
    private sealed class FixedClock(long nowMs) : IFleetClock
    {
        public long NowMs { get; } = nowMs;
    }

    // A-B-C corridor with a passing bay D off B, so the adjacent head-on is solvable (by CBS via the bay; PIBT shuffles).
    private static RoadmapGraph Corridor() =>
        FakeRoadmapQueryService.Graph(["A", "B", "C", "D"], ("A", "B"), ("B", "C"), ("B", "D"));

    private static IReadOnlyList<AgentGoal> HeadOnCluster() =>
    [
        new AgentGoal("agv-1", "A", "C", 0), // at A, heading to C → intends B (held by agv-2)
        new AgentGoal("agv-2", "B", "A", 1), // at B, heading to A → intends A (held by agv-1)
    ];

    private static CycleReport Resolve(JointResolverKind resolver)
    {
        using var host = CoordinationTestHost.Build(
            Corridor(), new FixedClock(0), planner: PlannerKind.Sipp, jointResolver: resolver);
        return host.Cycle.ResolveStandoffsAsync(host.RoadmapId, HeadOnCluster()).GetAwaiter().GetResult();
    }

    [Fact]
    public void None_resolver_is_a_no_op()
    {
        var report = Resolve(JointResolverKind.None);
        Assert.Empty(report.Results); // lever off → byte-identical: no cluster formed, no reservation attempted
    }

    [Fact]
    public void Pibt_commits_a_collision_free_joint_step_atomically()
    {
        var report = Resolve(JointResolverKind.Pibt);

        Assert.Equal(2, report.Results.Count);                                       // the cluster was formed + driven
        Assert.All(report.Results, r => Assert.Equal(AllocationOutcome.Granted, r.Outcome)); // the joint step committed
        Assert.Equal(2, report.ReservedAgentIds.Count);                              // both advanced one hop (priority shuffle)
    }

    [Fact]
    public void Cbs_solves_and_reserves_the_cluster_jointly()
    {
        var report = Resolve(JointResolverKind.Cbs);

        Assert.Equal(2, report.Results.Count);                  // the cluster was formed + handed to CBS (PlanClusterAsync)
        Assert.True(report.Results.Any(r => r.Reserved),        // a conflict-free joint solution was reserved
            "no member reserved: " + string.Join("; ", report.Results.Select(r =>
                $"{r.AgentId} planned={r.Planned} reserved={r.Reserved} outcome={r.Outcome} reason={r.FailureReason}")));
    }

    [Fact]
    public void Resolver_is_deterministic_for_a_fixed_standoff()
    {
        Assert.Equal(
            string.Join(";", Resolve(JointResolverKind.Pibt).Results.OrderBy(r => r.AgentId, StringComparer.Ordinal).Select(r => $"{r.AgentId}/{r.Reserved}/{r.Outcome}")),
            string.Join(";", Resolve(JointResolverKind.Pibt).Results.OrderBy(r => r.AgentId, StringComparer.Ordinal).Select(r => $"{r.AgentId}/{r.Reserved}/{r.Outcome}")));
    }
}
