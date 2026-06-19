using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Domain.Cbs;
using SwarmRoute.PathPlanning.Domain.Planners;
using SwarmRoute.PathPlanning.Domain.ValueObjects;
using SwarmRoute.PathPlanning.Tests.TestSupport;
using SwarmRoute.SpatioTemporal.Kernel;
using Xunit;

namespace SwarmRoute.PathPlanning.Tests.Cbs;

/// <summary>
/// Verifies <see cref="CbsConstraintView"/> both as a pure <see cref="IReservationView"/> (constraint windows are
/// subtracted from the external safe intervals) and end-to-end through <see cref="SippPathPlanner"/> — which is
/// the whole point: a CBS constraint is just a busy window, so the existing low level honours it unchanged.
/// </summary>
public sealed class CbsConstraintViewTests
{
    private static readonly ResourceRef CpB = RoadmapGraph.SiteRef("B");

    private static CbsConstraint VertexConstraint(string agentId, string cp, long tick)
        => new(agentId, CbsConstraintKind.Vertex, RoadmapGraph.SiteRef(cp), new TimeInterval(tick, tick + 1));

    // ── Pure view: a single-tick vertex constraint punches exactly one hole in the resource's free time ───────
    [Fact]
    public void Constraint_window_is_subtracted_from_free_intervals()
    {
        var external = new FakeReservationView(); // everything free
        var view = new CbsConstraintView(external, new[] { VertexConstraint("a1", "B", tick: 1) });

        Assert.False(view.IsFree(CpB, new TimeInterval(1, 2)));   // the constrained tick
        Assert.True(view.IsFree(CpB, new TimeInterval(0, 1)));    // before
        Assert.True(view.IsFree(CpB, new TimeInterval(2, 3)));    // after

        var free = view.FreeIntervals(CpB).ToList();
        Assert.Equal(new TimeInterval(0, 1), free[0].Interval);
        Assert.Equal(new TimeInterval(2, long.MaxValue), free[1].Interval);
    }

    // ── A resource with no constraint passes the external view through untouched ──────────────────────────────
    [Fact]
    public void Unconstrained_resource_passes_external_intervals_through()
    {
        var external = new FakeReservationView().Reserve(CpB, 5, 8);
        var view = new CbsConstraintView(external, Array.Empty<CbsConstraint>());

        Assert.Equal(external.FreeIntervals(CpB).Select(s => s.Interval),
                     view.FreeIntervals(CpB).Select(s => s.Interval));
    }

    // ── End-to-end: SIPP plans around a CBS vertex constraint exactly as it would a reservation ───────────────
    [Fact]
    public void Sipp_routes_around_a_cbs_vertex_constraint()
    {
        // A simple corridor A-B-C; the only way C is to pass through B.
        var graph = new RoadmapGraphBuilder()
            .Edge("A", "B").Edge("B", "A")
            .Edge("B", "C").Edge("C", "B")
            .Build();
        var request = new PlanRequest(Guid.Empty, "a1", "A", "C", releaseTimeMs: 0);

        // Forbid a1 from occupying B at tick 1 (its natural arrival). It must wait at A and pass B later.
        var view = new CbsConstraintView(new FakeReservationView(), new[] { VertexConstraint("a1", "B", tick: 1) });

        var result = new SippPathPlanner().Plan(graph, request, view);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Path!.Cells, c =>
            c.Resource.Kind == ResourceKind.CP && c.Resource.Id == "B" && c.Interval.Overlaps(new TimeInterval(1, 2)));
        // And it still reaches the goal (just later than the unconstrained tick-2 arrival).
        Assert.Contains(result.Path!.Cells, c => c.Resource.Kind == ResourceKind.CP && c.Resource.Id == "C");
    }
}
