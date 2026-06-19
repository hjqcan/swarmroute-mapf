using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// End-to-end validation of v3 zone-local PIBT (Priority Inheritance with Backtracking) driven through the REAL
/// engine. The contract is twofold: (1) <b>regression lock</b> — with PIBT off, or on but with no standoff, the
/// timeline is byte-identical to v2; (2) <b>value</b> — PIBT converges (or arrives strictly more) on dense seeds
/// where SIPP + StepAside reports <c>DidNotConverge</c>, and never collides or crashes. The value/hard-floor
/// cases are added once the episode is activated (Phase 4); this file starts with the regression locks.
/// </summary>
public sealed class PibtClosedLoopTests
{
    private static SimulationResultDto Run(SimulationRequest request) =>
        new SimulationService(
                new GridFieldFactory(),
                new FleetLoopDriver(),
                new InMemorySimulationEngineFactory(),
                NullLogger<SimulationService>.Instance)
            .RunAsync(request).GetAwaiter().GetResult();

    private static SimulationRequest Req(int w, int h, int agv, int seed, bool usePibt) =>
        new(w, h, agv, seed, PlannerKind.Sipp, Starts: null, HorizonWindowMs: long.MaxValue,
            StepAside: true, PreventDeadlockCycles: false, UsePibt: usePibt);

    // ── Regression lock: an explicit UsePibt:false run is byte-identical to the default (field-omitted) request ─
    [Theory]
    [InlineData(5, 5, 5)]
    [InlineData(6, 6, 8)]
    [InlineData(7, 7, 16)]
    public void Pibt_off_is_byte_identical_to_default(int w, int h, int agv)
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            var explicitOff = Run(Req(w, h, agv, seed, usePibt: false));
            var defaulted = Run(new SimulationRequest(w, h, agv, seed, PlannerKind.Sipp, StepAside: true)); // UsePibt defaults false

            Assert.Equal(SerializeTimeline(explicitOff), SerializeTimeline(defaulted));
        }
    }

    // ── Regression lock: PIBT is inert at sparse densities where no congestion cluster ever forms ─────────────
    [Theory]
    [InlineData(4, 4, 3)]
    [InlineData(5, 5, 5)]
    public void Pibt_is_inert_when_no_standoff_forms(int w, int h, int agv)
    {
        for (var seed = 1; seed <= 4; seed++)
        {
            var off = Run(Req(w, h, agv, seed, usePibt: false));
            var on = Run(Req(w, h, agv, seed, usePibt: true));

            Assert.Equal(SerializeTimeline(off), SerializeTimeline(on)); // no cluster → PIBT never engages
            Assert.Equal(0, on.Stats.Collisions);
        }
    }

    // ── Value: PIBT converges/arrives strictly more on dense seeds SIPP + StepAside cannot, never colliding ───
    [Fact]
    public void Pibt_improves_dense_convergence_without_collisions()
    {
        const int w = 7, h = 7, agv = 16;
        int offTotal = 0, onTotal = 0;
        var anyStrictlyBetter = false;
        var anyNewlyCompleted = false;

        for (var seed = 1; seed <= 6; seed++)
        {
            var off = Run(Req(w, h, agv, seed, usePibt: false)); // SIPP + StepAside (today's best)
            var on = Run(Req(w, h, agv, seed, usePibt: true));    // + zone-local PIBT

            Assert.Equal(0, on.Stats.Collisions);
            Assert.NotEqual("CollisionDetected", on.Stats.Status);
            Assert.True(on.Stats.Arrived >= off.Stats.Arrived,
                $"seed {seed}: PIBT arrived {on.Stats.Arrived} < StepAside {off.Stats.Arrived} (regression).");

            if (on.Stats.Arrived > off.Stats.Arrived) anyStrictlyBetter = true;
            if (on.Stats.Status == "Completed" && off.Stats.Status != "Completed") anyNewlyCompleted = true;
            offTotal += off.Stats.Arrived;
            onTotal += on.Stats.Arrived;
        }

        Assert.True(anyStrictlyBetter, "PIBT should strictly increase arrivals on at least one dense seed.");
        Assert.True(anyNewlyCompleted, "PIBT should fully converge at least one dense seed StepAside could not.");
        Assert.True(onTotal > offTotal, $"PIBT total arrivals {onTotal} should exceed StepAside {offTotal}.");
    }

    // ── Hard floor: PIBT is collision-free and never crashes across densities — at worst it reports
    //    DidNotConverge (never a collision), which is the empirical evidence for the cross-tick safety argument ──
    [Theory]
    [InlineData(5, 5, 5)]
    [InlineData(6, 6, 8)]
    [InlineData(7, 7, 16)]
    [InlineData(10, 8, 12)]
    public void Pibt_is_collision_free_and_never_crashes(int w, int h, int agv)
    {
        for (var seed = 1; seed <= 6; seed++)
        {
            var on = Run(Req(w, h, agv, seed, usePibt: true));

            Assert.Equal(0, on.Stats.Collisions);
            Assert.NotEqual("CollisionDetected", on.Stats.Status); // only Completed or DidNotConverge — never a collision
        }
    }

    // ── Independence: PIBT's value is its own — with StepAside OFF it still improves convergence collision-free
    //    (and with StepAside ON the value test above shows it adds on top). The two recoveries compose, and PIBT
    //    engages a few ticks before StepAside so it leads inside its cluster. ─────────────────────────────────
    [Fact]
    public void Pibt_helps_independently_of_stepaside()
    {
        const int w = 7, h = 7, agv = 16;
        int baseTotal = 0, pibtTotal = 0;

        for (var seed = 1; seed <= 6; seed++)
        {
            var raw = Run(new SimulationRequest(w, h, agv, seed, PlannerKind.Sipp)); // no StepAside, no PIBT
            var pibtOnly = Run(new SimulationRequest(w, h, agv, seed, PlannerKind.Sipp, UsePibt: true)); // PIBT only

            Assert.Equal(0, pibtOnly.Stats.Collisions);
            Assert.True(pibtOnly.Stats.Arrived >= raw.Stats.Arrived,
                $"seed {seed}: PIBT-only arrived {pibtOnly.Stats.Arrived} < raw SIPP {raw.Stats.Arrived}.");
            baseTotal += raw.Stats.Arrived;
            pibtTotal += pibtOnly.Stats.Arrived;
        }

        Assert.True(pibtTotal > baseTotal, $"PIBT alone (StepAside off) should improve arrivals: {pibtTotal} vs {baseTotal}.");
    }

    // ── Determinism: same seed ⇒ identical timeline, even through a PIBT episode ──────────────────────────────
    [Fact]
    public void Pibt_is_deterministic_for_a_fixed_seed()
    {
        var first = Run(Req(7, 7, 16, seed: 3, usePibt: true));  // seed 3 exercises a full PIBT convergence
        var second = Run(Req(7, 7, 16, seed: 3, usePibt: true));

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
