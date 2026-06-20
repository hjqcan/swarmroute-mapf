using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (FMS-V2 — M-F2) End-to-end validation of the well-formed warehouse scenario through the REAL engine
/// (Coordination + PathPlanning + TrafficControl + the Dispatch dock-admission scheduler + the parking manager, all
/// over the same reservation table). The WarehouseWellFormed scenario carves a well-formed endpoint set out of the
/// grid (parking/workstation cells around a connected transit core), draws every AGV's goal from that safe set,
/// drives a handful through a service + clear-to-parking lifecycle, and leaves the rest as point-to-point movers.
/// The contract proven here:
/// <list type="bullet">
///   <item><b>(a)</b> the run is collision-free, and never falsely reported as a collision;</item>
///   <item><b>(b)</b> every AGV that serviced a workstation and finished ends on a parking/buffer cell, NOT on its
///     workstation goal — clear-to-parking is real (and at least one workstation AGV demonstrably clears);</item>
///   <item><b>(c)</b> on any non-convergence the DOMINANT reason is NOT <c>ParkedGoalBlocker</c> — the scenario
///     semantics (every goal a well-formed endpoint) removed the permanent goal-blocking that a random-stress run can
///     suffer; meanwhile RandomStress at the same scale DOES exhibit <c>ParkedGoalBlocker</c>;</item>
///   <item><b>(d)</b> completion is at least as good as the SAME seed under RandomStress (an improvement, not
///     necessarily 100%) — the well-formed structure keeps parked vehicles off the transit core, so the fleet
///     converges as well as or better than random goals on these seeds (dramatically so where random stress
///     goal-blocks);</item>
///   <item><b>(e)</b> the <c>ScenarioMode</c> default (RandomStress) is byte-identical to a plain run with no
///     <c>ScenarioMode</c> set — the FMS-V2 lever is fully opt-in.</item>
/// </list>
/// </summary>
public sealed class WarehouseWellFormedClosedLoopTests
{
    private const int Width = 7;
    private const int Height = 7;
    private const int AgvCount = 16;

    // Seeds verified to exercise the contract: WarehouseWellFormed completion >= RandomStress completion, with at
    // least one workstation AGV clearing to parking and none ending parked on its workstation. Seed 16 is the strong
    // case (well-formed converges far more than the goal-blocked random run). The SIPP planner is used (the M-F1
    // finding: the warehouse drives SIPP + the schedule-faithful executor, whose tick axis the service window lives
    // on). The warehouse path forces SIPP internally; RandomStress is run with SIPP too for an apples-to-apples
    // completion comparison.
    private static readonly int[] Seeds = [2, 16, 18, 25, 26];

    private static SimulationService Svc() =>
        new(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
            NullLogger<SimulationService>.Instance);

    private static SimulationResultDto RunWarehouse(int seed) =>
        Svc().RunAsync(new SimulationRequest(
                Width: Width, Height: Height, AgvCount: AgvCount, Seed: seed,
                Planner: PlannerKind.Sipp, ScenarioMode: ScenarioMode.WarehouseWellFormed))
            .GetAwaiter().GetResult();

    private static SimulationResultDto RunRandomStress(int seed, PlannerKind planner = PlannerKind.Sipp) =>
        Svc().RunAsync(new SimulationRequest(
                Width: Width, Height: Height, AgvCount: AgvCount, Seed: seed,
                Planner: planner, ScenarioMode: ScenarioMode.RandomStress))
            .GetAwaiter().GetResult();

    // A "plain" run: NO ScenarioMode set at all (so it defaults to RandomStress) — used to prove the default is inert.
    private static SimulationResultDto RunPlain(int seed) =>
        Svc().RunAsync(new SimulationRequest(
                Width: Width, Height: Height, AgvCount: AgvCount, Seed: seed, Planner: PlannerKind.Sipp))
            .GetAwaiter().GetResult();

    private static (string SiteId, string State) Final(SimulationResultDto r, string agentId)
    {
        var p = r.Timeline.Frames[^1].Positions.First(x => x.AgentId == agentId);
        return (p.SiteId, p.State);
    }

    private static bool IsWorkstationAgv(string id) => id.StartsWith("ws-agv-", StringComparison.Ordinal);

    // ── (a) Collision-free, and never falsely reported as a collision ────────────────────────────────────────────
    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(25)]
    [InlineData(26)]
    public void Warehouse_run_is_collision_free(int seed)
    {
        var result = RunWarehouse(seed);

        Assert.Equal(0, result.Stats.Collisions);
        Assert.NotEqual("CollisionDetected", result.Stats.Status);
        Assert.Null(result.Stats.CollisionTick);
    }

    // ── (b) Every serviced-and-finished AGV clears to a parking/buffer, never parks on its workstation goal ───────
    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(25)]
    [InlineData(26)]
    public void Serviced_agvs_clear_to_parking_and_never_finish_on_their_workstation(int seed)
    {
        var result = RunWarehouse(seed);

        // Identify which workstation each station AGV was bound to (its goal), and which cells are parking/buffer.
        var workstationGoals = result.Agents
            .Where(a => IsWorkstationAgv(a.Id))
            .ToDictionary(a => a.Id, a => a.GoalSiteId, StringComparer.Ordinal);
        Assert.NotEmpty(workstationGoals); // the warehouse always assigns at least one service AGV

        var clearedCount = 0;
        foreach (var (agentId, workstation) in workstationGoals)
        {
            var (siteId, state) = Final(result, agentId);
            if (state != "Arrived")
                continue; // still en route to parking (a stuck mid-clear AGV is not "finished on its workstation")

            // A FINISHED station AGV must have cleared OFF its workstation to a different cell (its parking slot).
            Assert.NotEqual(workstation, siteId);
            clearedCount++;
        }

        // And the mechanism genuinely fires: at least one station AGV completed service and cleared to parking.
        Assert.True(clearedCount > 0,
            $"seed {seed}: at least one workstation AGV should service then clear to parking (cleared {clearedCount}).");
    }

    // ── (c) Non-convergence is never dominated by ParkedGoalBlocker (the goal-blocking fix) ───────────────────────
    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(25)]
    [InlineData(26)]
    public void Warehouse_nonconvergence_is_never_dominated_by_parked_goal_blocking(int seed)
    {
        var result = RunWarehouse(seed);

        // If it converged, there is no diagnostic to check (and nothing was goal-blocked).
        if (result.Stats.Status != "DidNotConverge")
        {
            Assert.Null(result.Stats.NonConvergence);
            return;
        }

        Assert.NotNull(result.Stats.NonConvergence);
        Assert.NotEqual(
            NonConvergenceReason.ParkedGoalBlocker.ToString(),
            result.Stats.NonConvergence!.DominantReason);

        // And no individual stranded AGV is a parked-goal-blocker victim either: the well-formed endpoints keep every
        // goal reachable through the (endpoint-free) transit core, so a parked vehicle can never wall one off.
        Assert.DoesNotContain(
            NonConvergenceReason.ParkedGoalBlocker.ToString(),
            result.Stats.NonConvergence.PerAgentReasons.Values);
    }

    // ── (c-contrast) RandomStress at the same scale DOES suffer permanent goal-blocking that the warehouse removes ─
    [Fact]
    public void RandomStress_can_be_dominated_by_parked_goal_blocking_which_warehouse_eliminates()
    {
        // Across a sweep, at least one random-stress seed is dominated by ParkedGoalBlocker (a parked vehicle walls
        // off another's goal — the failure mode the well-formed warehouse is designed to remove). The warehouse
        // never exhibits it (proven per-seed above). This contrast is the point of the M-F2 scenario-semantics fix.
        var randomGoalBlockedSeeds = new List<int>();
        var warehouseGoalBlockedSeeds = new List<int>();
        foreach (var seed in Enumerable.Range(1, 25))
        {
            var rs = RunRandomStress(seed);
            if (rs.Stats.NonConvergence?.DominantReason == NonConvergenceReason.ParkedGoalBlocker.ToString())
                randomGoalBlockedSeeds.Add(seed);

            var wh = RunWarehouse(seed);
            if (wh.Stats.NonConvergence?.DominantReason == NonConvergenceReason.ParkedGoalBlocker.ToString())
                warehouseGoalBlockedSeeds.Add(seed);
        }

        Assert.NotEmpty(randomGoalBlockedSeeds); // RandomStress genuinely goal-blocks at this scale
        Assert.Empty(warehouseGoalBlockedSeeds); // the well-formed warehouse never does
    }

    // ── (d) Completion >= the SAME seed under RandomStress (improvement, not necessarily 100%) ────────────────────
    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(25)]
    [InlineData(26)]
    public void Warehouse_completion_is_at_least_as_good_as_random_stress(int seed)
    {
        var warehouse = RunWarehouse(seed);
        var randomStress = RunRandomStress(seed);

        Assert.True(
            warehouse.Stats.Arrived >= randomStress.Stats.Arrived,
            $"seed {seed}: WarehouseWellFormed arrived {warehouse.Stats.Arrived} should be >= " +
            $"RandomStress arrived {randomStress.Stats.Arrived}.");
    }

    [Fact]
    public void Warehouse_completion_strongly_beats_random_stress_when_random_goal_blocks()
    {
        // Seed 16 is the headline case: RandomStress goal-scatters and stalls badly, while the well-formed warehouse
        // keeps the transit core clear and converges far more of the fleet — a several-AGV improvement.
        var warehouse = RunWarehouse(16);
        var randomStress = RunRandomStress(16);

        Assert.True(
            warehouse.Stats.Arrived > randomStress.Stats.Arrived + 2,
            $"seed 16: WarehouseWellFormed ({warehouse.Stats.Arrived}) should clearly beat " +
            $"RandomStress ({randomStress.Stats.Arrived}).");
    }

    // ── (e) ScenarioMode default (RandomStress) is byte-identical to a plain run ──────────────────────────────────
    [Theory]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(42)]
    public void RandomStress_default_is_byte_identical_to_a_plain_run(int seed)
    {
        // A request with NO ScenarioMode set (the default is RandomStress) must produce exactly the same run as a
        // request that explicitly sets ScenarioMode.RandomStress — the FMS-V2 lever is fully opt-in and inert by
        // default. Compare the full serialized timeline (positions + motion state per tick).
        var plain = RunPlain(seed);
        var explicitRandom = RunRandomStress(seed);

        Assert.Equal(Serialize(plain), Serialize(explicitRandom));
        Assert.Equal(plain.Stats.Arrived, explicitRandom.Stats.Arrived);
        Assert.Equal(plain.Stats.Status, explicitRandom.Stats.Status);
    }

    // ── Determinism: the same seed yields an identical warehouse run (layout + timeline) ──────────────────────────
    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    public void Warehouse_run_is_deterministic_for_a_given_seed(int seed)
    {
        var first = RunWarehouse(seed);
        var second = RunWarehouse(seed);

        // Same generated layout (each AGV's start/goal) and same tick-by-tick timeline.
        Assert.Equal(
            string.Join(";", first.Agents.OrderBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => $"{a.Id}:{a.StartSiteId}->{a.GoalSiteId}")),
            string.Join(";", second.Agents.OrderBy(a => a.Id, StringComparer.Ordinal)
                .Select(a => $"{a.Id}:{a.StartSiteId}->{a.GoalSiteId}")));
        Assert.Equal(Serialize(first), Serialize(second));
    }

    private static string Serialize(SimulationResultDto r) =>
        string.Join(";", r.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions
                .OrderBy(p => p.AgentId, StringComparer.Ordinal)
                .Select(p => $"{p.AgentId}@{p.SiteId}/{p.State}"))));
}
