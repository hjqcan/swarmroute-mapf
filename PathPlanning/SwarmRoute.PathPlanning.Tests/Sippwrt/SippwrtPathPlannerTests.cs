using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.Shared;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Tests.TestSupport;
using SwarmRoute.SpatioTemporal.Kernel;
using Xunit;

namespace SwarmRoute.PathPlanning.Tests.Sippwrt;

/// <summary>
/// SIPPwRT continuous-time planning: on a NON-uniform map it prefers the time-optimal route (which min-hop SIPP
/// cannot see), sizes cells by the kinematic closed-form, waits the REAL traversal time for a busy lane, is
/// deterministic, and on a uniform map returns the same site sequence as SIPP (only interval widths scale).
/// </summary>
public sealed class SippwrtPathPlannerTests
{
    private static IReadOnlyList<string> Sites(PlanResult r) =>
        r.Path!.Cells.Where(c => c.Resource.Kind == ResourceKind.CP).Select(c => c.Resource.Id).ToList();

    private static long TerminalArrival(PlanResult r) =>
        r.Path!.Cells.Last(c => c.Resource.Kind == ResourceKind.CP).Interval.StartMs;

    // ── Public API contract: with the continuous executor closed (v3 Stage C), SIPPwRT is now a selectable
    //    planner with the stable wire value 3 (appended after Dijkstra=1/Sipp=2 — never renumbered). ────────────
    [Fact]
    public void Planner_kind_exposes_sippwrt_as_value_three()
    {
        Assert.Contains("Sippwrt", Enum.GetNames<PlannerKind>());
        Assert.Equal(3, (int)PlannerKind.Sippwrt);
    }

    // ── Time-optimal route ≠ min-hop route: one long lane vs two short lanes ──────────────────────────────────
    [Fact]
    public void Prefers_time_optimal_route_over_min_hop()
    {
        // S→T direct is 1 hop but a long (5 m) lane; S→M→T is 2 hops of short (1 m) lanes — faster in real time.
        var graph = new RoadmapGraphBuilder()
            .Edge("S", "T", 5.0)
            .Edge("S", "M", 1.0).Edge("M", "T", 1.0)
            .Build();
        var request = new PlanRequest(Guid.Empty, "a1", "S", "T", releaseTimeMs: 0);
        var view = new FakeReservationView();

        var sipp = new SippPathPlanner().Plan(graph, request, view);
        var sippwrt = new SippwrtPathPlanner().Plan(graph, request, view);

        Assert.True(sipp.Success && sippwrt.Success);
        Assert.Equal(new[] { "S", "T" }, Sites(sipp));          // min-hop discrete planner takes the direct lane
        Assert.Equal(new[] { "S", "M", "T" }, Sites(sippwrt));  // SIPPwRT takes the kinematically faster 2-hop route
    }

    // ── Cell intervals are sized by the kinematic closed-form ─────────────────────────────────────────────────
    [Fact]
    public void Lane_cell_width_equals_the_kinematic_duration()
    {
        var graph = new RoadmapGraphBuilder().Edge("S", "M", 1.0).Edge("M", "T", 1.0).Build();
        var result = new SippwrtPathPlanner().Plan(graph, new PlanRequest(Guid.Empty, "a1", "S", "T"), new FakeReservationView());

        var laneSM = result.Path!.Cells.First(c => c.Resource is { Kind: ResourceKind.Lane, Id: "S-M" });
        Assert.Equal(EdgeKinematics.DurationMs(1000, KinematicProfile.Default), laneSM.Interval.Duration); // 2000 ms
        Assert.Equal(4000, TerminalArrival(result)); // arrive T at 4000 ms (two 2000 ms hops)
    }

    // ── A busy lane forces a wait of the REAL traversal time, not one tick ───────────────────────────────────
    [Fact]
    public void Waits_the_real_traversal_time_for_a_busy_lane()
    {
        var graph = new RoadmapGraphBuilder().Edge("S", "T", 5.0).Build(); // only route is the 5 m lane (dur 6000 ms)
        var view = new FakeReservationView().Reserve(RoadmapGraph.LaneRef("S", "T"), 0, 6000); // lane busy until 6000

        var result = new SippwrtPathPlanner().Plan(graph, new PlanRequest(Guid.Empty, "a2", "S", "T"), view);

        Assert.True(result.Success);
        Assert.Equal(12000, TerminalArrival(result)); // waits 6000 for the lane to clear, then traverses 6000 more
    }

    // ── Determinism: identical inputs ⇒ byte-identical path + cost ───────────────────────────────────────────
    [Fact]
    public void Is_deterministic_for_identical_inputs()
    {
        var graph = new RoadmapGraphBuilder()
            .Edge("S", "T", 5.0).Edge("S", "M", 1.0).Edge("M", "T", 1.0)
            .Edge("S", "N", 1.0).Edge("N", "T", 1.0)
            .Build();
        var request = new PlanRequest(Guid.Empty, "a1", "S", "T");

        var first = new SippwrtPathPlanner().Plan(graph, request, new FakeReservationView());
        var second = new SippwrtPathPlanner().Plan(graph, request, new FakeReservationView());

        Assert.Equal(Serialize(first), Serialize(second));
        Assert.Equal(first.Cost!.DurationMs, second.Cost!.DurationMs);
    }

    // ── On a uniform graph, SIPPwRT picks the same site sequence as SIPP (only interval widths scale) ─────────
    [Fact]
    public void Uniform_graph_matches_sipp_site_sequence()
    {
        var graph = new RoadmapGraphBuilder() // diamond, all unit edges
            .Edge("A", "B").Edge("B", "D")
            .Edge("A", "C").Edge("C", "D")
            .Build();
        var request = new PlanRequest(Guid.Empty, "a1", "A", "D");

        var sipp = Sites(new SippPathPlanner().Plan(graph, request, new FakeReservationView()));
        var sippwrt = Sites(new SippwrtPathPlanner().Plan(graph, request, new FakeReservationView()));

        Assert.Equal(sipp, sippwrt);
    }

    // ── Failure branches mirror SIPP ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Unreachable_goal_fails_with_no_route()
    {
        var graph = new RoadmapGraphBuilder().Edge("A", "B").Build(); // no edge into A from B
        var result = new SippwrtPathPlanner().Plan(graph, new PlanRequest(Guid.Empty, "a1", "B", "A"), new FakeReservationView());

        Assert.False(result.Success);
        Assert.Contains(PathPlanningErrorCodes.NoRoute, result.FailureReason);
    }

    private static string Serialize(PlanResult r) =>
        string.Join(",", r.Path!.Cells.Select(c => $"{c.Resource.Kind}:{c.Resource.Id}@[{c.Interval.StartMs},{c.Interval.EndMs})"));
}
