using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// End-to-end validation of v3 zone-local CBS driven through the REAL engine. Contract: (1) <b>regression lock</b>
/// — with CBS off, or on but with no standoff, the timeline is byte-identical to v2; (2) <b>value</b> — CBS
/// converges (or arrives strictly more) on dense seeds the greedy PIBT resolver cannot crack, never colliding.
/// The value/determinism cases are added once the executor integration lands; this file starts with the locks.
/// </summary>
public sealed class CbsClosedLoopTests
{
    private static SimulationResultDto Run(SimulationRequest request) =>
        new SimulationService(
                new GridFieldFactory(),
                new FleetLoopDriver(),
                new InMemorySimulationEngineFactory(),
                NullLogger<SimulationService>.Instance)
            .RunAsync(request).GetAwaiter().GetResult();

    // CBS in isolation (UsePibt:false) so the value tests below attribute convergence to CBS, not PIBT.
    private static SimulationRequest Req(int w, int h, int agv, int seed, bool useCbs) =>
        new(w, h, agv, seed, PlannerKind.Sipp, Starts: null, HorizonWindowMs: long.MaxValue,
            StepAside: true, PreventDeadlockCycles: false, UsePibt: false, UseCbs: useCbs);

    [Fact]
    public void Cbs_requires_sipp_schedule_faithful_execution()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Run(new SimulationRequest(4, 4, 3, Seed: 1, Planner: PlannerKind.Dijkstra, UseCbs: true)));

        Assert.Contains("UseCbs requires Planner=Sipp", ex.Message);
    }

    [Fact]
    public void Cbs_and_pibt_are_explicitly_mutually_exclusive()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Run(new SimulationRequest(4, 4, 3, Seed: 1, Planner: PlannerKind.Sipp, UsePibt: true, UseCbs: true)));

        Assert.Contains("UseCbs and UsePibt are mutually exclusive", ex.Message);
    }

    // ── Regression lock: an explicit UseCbs:false run is byte-identical to the default (field-omitted) request ─
    [Theory]
    [InlineData(5, 5, 5)]
    [InlineData(6, 6, 8)]
    [InlineData(7, 7, 16)]
    public void Cbs_off_is_byte_identical_to_default(int w, int h, int agv)
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            var explicitOff = Run(Req(w, h, agv, seed, useCbs: false));
            var defaulted = Run(new SimulationRequest(w, h, agv, seed, PlannerKind.Sipp, StepAside: true)); // UseCbs defaults false

            Assert.Equal(SerializeTimeline(explicitOff), SerializeTimeline(defaulted));
        }
    }

    // ── Regression lock: CBS is inert at sparse densities where no congestion cluster ever forms ──────────────
    [Theory]
    [InlineData(4, 4, 3)]
    [InlineData(5, 5, 5)]
    public void Cbs_is_inert_when_no_standoff_forms(int w, int h, int agv)
    {
        for (var seed = 1; seed <= 4; seed++)
        {
            var off = Run(Req(w, h, agv, seed, useCbs: false));
            var on = Run(Req(w, h, agv, seed, useCbs: true));

            Assert.Equal(SerializeTimeline(off), SerializeTimeline(on)); // no cluster → CBS never engages
            Assert.Equal(0, on.Stats.Collisions);
        }
    }

    // ── Value: CBS adds arrivals and fully converges a dense seed the StepAside baseline cannot, never colliding.
    //    (It is the complete/optimal LOCAL solver — proven on PIBT-hard static cases in the unit tests — so it
    //    cracks some standoffs; in the dynamic loop it does NOT dominate the lock-free PIBT resolver, which is an
    //    honest boundary, so this asserts value over the baseline, not over PIBT.) ──────────────────────────────
    [Fact]
    public void Cbs_adds_value_and_cracks_a_baseline_did_not_converge_seed()
    {
        const int w = 7, h = 7, agv = 16;
        int baseTotal = 0, cbsTotal = 0;
        var anyNewlyCompleted = false;

        for (var seed = 1; seed <= 6; seed++)
        {
            var baseline = Run(new SimulationRequest(w, h, agv, seed, PlannerKind.Sipp, StepAside: true)); // no CBS/PIBT
            var cbs = Run(Req(w, h, agv, seed, useCbs: true));

            Assert.Equal(0, cbs.Stats.Collisions);
            Assert.NotEqual("CollisionDetected", cbs.Stats.Status);
            Assert.True(cbs.Stats.Arrived >= baseline.Stats.Arrived,
                $"seed {seed}: CBS arrived {cbs.Stats.Arrived} < baseline {baseline.Stats.Arrived} (regression).");

            if (cbs.Stats.Status == "Completed" && baseline.Stats.Status != "Completed") anyNewlyCompleted = true;
            baseTotal += baseline.Stats.Arrived;
            cbsTotal += cbs.Stats.Arrived;
        }

        Assert.True(cbsTotal > baseTotal, $"CBS total arrivals {cbsTotal} should exceed the StepAside baseline {baseTotal}.");
        Assert.True(anyNewlyCompleted, "CBS should fully converge at least one dense seed the baseline could not.");
    }

    // ── Hard floor: CBS is collision-free and never crashes across densities (Completed or DidNotConverge only) ─
    [Theory]
    [InlineData(5, 5, 5)]
    [InlineData(6, 6, 8)]
    [InlineData(7, 7, 16)]
    [InlineData(10, 8, 12)]
    public void Cbs_is_collision_free_and_never_crashes(int w, int h, int agv)
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            var on = Run(Req(w, h, agv, seed, useCbs: true));

            Assert.Equal(0, on.Stats.Collisions);
            Assert.NotEqual("CollisionDetected", on.Stats.Status);
        }
    }

    // ── No regression: seeds the baseline already converges stay converged under CBS ─────────────────────────
    [Fact]
    public void Cbs_does_not_regress_already_converging_seeds()
    {
        for (var seed = 2; seed <= 6; seed++)
        {
            var baseline = Run(new SimulationRequest(10, 8, 12, seed, PlannerKind.Sipp, StepAside: true));
            if (baseline.Stats.Status != "Completed")
                continue;

            var cbs = Run(Req(10, 8, 12, seed, useCbs: true));
            Assert.Equal("Completed", cbs.Stats.Status);
            Assert.Equal(baseline.Stats.Arrived, cbs.Stats.Arrived);
        }
    }

    // ── Determinism: same seed ⇒ identical timeline, even through a CBS cluster solve ─────────────────────────
    [Fact]
    public void Cbs_is_deterministic_for_a_fixed_seed()
    {
        var first = Run(Req(7, 7, 16, seed: 5, useCbs: true));  // seed 5 exercises a full CBS convergence
        var second = Run(Req(7, 7, 16, seed: 5, useCbs: true));

        Assert.Equal(first.Stats.Status, second.Stats.Status);
        Assert.Equal(first.Stats.Arrived, second.Stats.Arrived);
        Assert.Equal(SerializeTimeline(first), SerializeTimeline(second));
    }

    private static string SerializeTimeline(SimulationResultDto result)
        => string.Join(";", result.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions
                .OrderBy(p => p.AgentId, StringComparer.Ordinal)
                .Select(p => $"{p.AgentId}@{p.SiteId}/{p.State}"))));
}
