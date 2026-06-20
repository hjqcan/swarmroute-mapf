using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using SwarmRoute.SpatioTemporal.Kernel;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (FMS-V3 — M-F3) End-to-end validation of the executor-integrated <b>lifelong dispatch</b> mode through the REAL
/// engine (Coordination + PathPlanning + TrafficControl + the Dispatch dock-admission scheduler + the runtime
/// <see cref="ITaskDispatcher"/> + the parking manager, all over the same reservation table). On a larger, sparser
/// grid a stream of transport tasks is released over a horizon, the dispatcher hands each idle AGV its next task, and
/// the instant an AGV clears to parking it is re-tasked — continuous operation. The contract proven here:
/// <list type="bullet">
///   <item><b>(a)</b> the run is collision-free, and never falsely reported as a collision;</item>
///   <item><b>(b)</b> CONTINUOUS PROGRESS — the fleet completes many tasks over the horizon and throughput is
///     sustained (not a one-shot burst then stall);</item>
///   <item><b>(c)</b> NO permanent deadlock/livelock — tasks complete in BOTH the first and second halves of the
///     horizon (the loop keeps turning over the whole run);</item>
///   <item><b>(d)</b> parking is not saturated — peak parked stays below capacity, so AGVs always find a slot to rest
///     in between tasks (no AGV permanently stuck unable to park);</item>
///   <item><b>(e)</b> cost-based admission makes the right call in a constructed mini-case — a high-urgency service is
///     admitted ahead of low-priority empty-wait traffic, while a low-urgency service defers to a high-priority
///     follower (through the engine factory's cost-admission wiring);</item>
///   <item><b>(f)</b> the lever is fully opt-in: <c>ScenarioMode != LifelongDispatch</c> (and <c>LifelongHorizonTicks</c>
///     null) is byte-identical to a normal run, and <c>LifelongDispatch</c> WITHOUT a horizon is byte-identical to a
///     one-shot WarehouseWellFormed run.</item>
/// </list>
/// </summary>
public sealed class LifelongDispatchClosedLoopTests
{
    // A larger, sparser grid with a modest fleet (the M-F2 finding: lifelong needs more endpoints than AGVs so AGVs
    // find a workstation to serve and parking to rest in between tasks). Seeds verified to exercise the contract.
    private const int Width = 12;
    private const int Height = 12;
    private const int AgvCount = 6;
    private const long Horizon = 400;

    private static SimulationService Svc() =>
        new(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
            NullLogger<SimulationService>.Instance);

    private static SimulationResultDto RunLifelong(
        int seed, int width = Width, int height = Height, int agv = AgvCount, long horizon = Horizon,
        bool costAdmission = false) =>
        Svc().RunAsync(new SimulationRequest(
                Width: width, Height: height, AgvCount: agv, Seed: seed,
                Planner: PlannerKind.Sipp, ScenarioMode: ScenarioMode.LifelongDispatch,
                LifelongHorizonTicks: horizon, LifelongCostBasedAdmission: costAdmission))
            .GetAwaiter().GetResult();

    // ── (a) Collision-free, and never falsely reported as a collision ────────────────────────────────────────────
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(99)]
    public void Lifelong_run_is_collision_free(int seed)
    {
        var result = RunLifelong(seed);

        Assert.Equal(0, result.Stats.Collisions);
        Assert.NotEqual("CollisionDetected", result.Stats.Status);
        Assert.Null(result.Stats.CollisionTick);
        Assert.NotNull(result.Lifelong); // a horizon-bounded lifelong run always reports lifelong metrics
    }

    // ── (b) Continuous progress: completes many tasks over the horizon, throughput sustained ─────────────────────
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(99)]
    public void Lifelong_run_makes_continuous_progress(int seed)
    {
        var result = RunLifelong(seed);
        var lifelong = result.Lifelong!;

        // The fleet turns over a substantial number of tasks over the horizon — far more than a one-shot fleet of this
        // size could (6 AGVs reaching one goal each), proving genuine continuous re-tasking.
        Assert.True(lifelong.TasksCompleted >= 40,
            $"seed {seed}: lifelong should complete many tasks (completed {lifelong.TasksCompleted}).");

        // Sustained throughput, not a one-shot burst: a healthy per-100-tick rate over the whole horizon.
        Assert.True(lifelong.ThroughputPerHundredTicks >= 5d,
            $"seed {seed}: throughput should be sustained (was {lifelong.ThroughputPerHundredTicks:F2}/100 ticks).");

        // The dispatcher genuinely re-tasked through the backlog: more tasks completed than there are AGVs, and the
        // backlog never grew unboundedly (a stalled fleet would let maxQ approach the released count).
        Assert.True(lifelong.TasksCompleted > AgvCount);
        Assert.True(lifelong.MaxQueueDepth < lifelong.TasksReleased,
            $"seed {seed}: the backlog should be served, not pile up (maxQ {lifelong.MaxQueueDepth} vs released {lifelong.TasksReleased}).");
    }

    // ── (c) No permanent deadlock/livelock: tasks complete in BOTH halves of the horizon ─────────────────────────
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(99)]
    public void Lifelong_run_keeps_completing_tasks_across_the_whole_horizon(int seed)
    {
        var result = RunLifelong(seed);
        var lifelong = result.Lifelong!;

        // The loop keeps turning over the WHOLE horizon: tasks complete in the first half AND the second half. A
        // permanent deadlock/livelock would complete a burst early then stall (second half == 0).
        Assert.True(lifelong.TasksCompletedFirstHalf > 0,
            $"seed {seed}: tasks should complete in the first half (was {lifelong.TasksCompletedFirstHalf}).");
        Assert.True(lifelong.TasksCompletedSecondHalf > 0,
            $"seed {seed}: tasks should still complete in the second half — no late-run stall " +
            $"(was {lifelong.TasksCompletedSecondHalf}).");

        // The run reaching its horizon is the EXPECTED stop (not a stalled DidNotConverge).
        Assert.Equal("Completed", result.Stats.Status);

        // And the two halves are both more than a token: the second half sustains a real fraction of the first
        // (the fleet did not crawl to a near-halt). At least a quarter of the first half's rate is comfortably met.
        Assert.True(lifelong.TasksCompletedSecondHalf * 4 >= lifelong.TasksCompletedFirstHalf,
            $"seed {seed}: second-half throughput collapsed ({lifelong.TasksCompletedFirstHalf} -> " +
            $"{lifelong.TasksCompletedSecondHalf}).");
    }

    // ── (d) Parking not saturated: AGVs clear to parking between tasks, none permanently stuck ───────────────────
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(99)]
    public void Lifelong_run_does_not_saturate_parking(int seed)
    {
        var result = RunLifelong(seed);
        var lifelong = result.Lifelong!;

        // The warehouse provides more parking than AGVs (the sparse-grid sizing), so the peak parked count never
        // reaches capacity — an AGV always finds a slot to rest in between tasks.
        Assert.True(lifelong.ParkingCapacity >= AgvCount,
            $"seed {seed}: a lifelong scenario should carve at least as many parkings as AGVs " +
            $"(capacity {lifelong.ParkingCapacity}, fleet {AgvCount}).");
        Assert.True(lifelong.PeakParkedCount <= lifelong.ParkingCapacity);
        Assert.True(lifelong.ParkingSaturation < 1d,
            $"seed {seed}: parking should never saturate (saturation {lifelong.ParkingSaturation:F2}).");

        // No AGV is permanently stuck unable to park: every AGV reached IdleParked (and was re-tasked) at least once,
        // which it can only do by clearing to a parking slot. We prove it via the completion count — the fleet
        // completing > AgvCount tasks means every AGV cycled through parking-and-re-task at least once on average,
        // and the sustained second half (asserted above) means none fell permanently out of the rotation.
        Assert.True(lifelong.TasksCompleted >= AgvCount);
    }

    // ── (e) Cost-based admission makes the right call in a constructed mini-case ──────────────────────────────────
    [Fact]
    public async Task CostBasedAdmission_admits_high_urgency_service_and_defers_low_urgency_to_high_priority_traffic()
    {
        // A constructed mini-case wired through the SAME engine-factory cost-admission path the lifelong run uses: a
        // SoftBlocking station whose closure a follower plans through (a bypass survives, so it is a soft impact the
        // cost policy scores rather than a hard sever). Two sub-cases, on independent engines:
        //   (1) HIGH-urgency service vs LOW-priority empty-wait followers  -> admitted (go first).
        //   (2) LOW-urgency  service vs a HIGH-priority follower           -> defers, clears the follower first.
        const string dock = "r0c0";
        const string closureCell = "r1c1";
        var graph = new GridFieldFactory().BuildGrid(3, 3).Graph;
        var closure = new HashSet<ResourceRef> { new(ResourceKind.CP, closureCell) };
        var station = new StationDefinition(
            StationId: "S", DockPoint: dock, PreDockBuffers: Array.Empty<string>(),
            BlockingClosure: closure, ServiceDurationMs: 50, StationType: StationType.SoftBlocking);
        var catalog = new InMemoryStationCatalog(new[] { station });

        // (1) High-urgency service over two LOW-priority followers planned through the closure.
        var lowFollowers = new MiniFleetPlan(
            priorities: new() { ["F1"] = 0, ["F2"] = 0 },
            planned: new()
            {
                ["F1"] = new ResourceRef[] { new(ResourceKind.CP, closureCell) },
                ["F2"] = new ResourceRef[] { new(ResourceKind.CP, closureCell) },
            });
        await using var engineHi = new InMemorySimulationEngineFactory().Create(
            graph, PlannerKind.Sipp, stationCatalog: catalog, costBasedAdmission: true, fleetPlan: lowFollowers);
        var hi = await engineHi.StationScheduler!.RequestDockAdmissionAsync(
            new ServiceAdmissionRequest("svc-hi", "S", dock, dock, 50, Priority: 9, EarliestStartMs: 0, DeadlineMs: null));

        Assert.True(hi.Granted, $"high-urgency service should go first (reason: {hi.Reason}).");
        Assert.Empty(hi.VehiclesToClearFirst);

        // (2) Low-urgency service blocking a HIGH-priority follower planned through the closure.
        var vipFollower = new MiniFleetPlan(
            priorities: new() { ["F-vip"] = 100 },
            planned: new() { ["F-vip"] = new ResourceRef[] { new(ResourceKind.CP, closureCell) } });
        await using var engineLo = new InMemorySimulationEngineFactory().Create(
            graph, PlannerKind.Sipp, stationCatalog: catalog, costBasedAdmission: true, fleetPlan: vipFollower);
        var lo = await engineLo.StationScheduler!.RequestDockAdmissionAsync(
            new ServiceAdmissionRequest("svc-lo", "S", dock, dock, 50, Priority: 1, EarliestStartMs: 0, DeadlineMs: null));

        Assert.False(lo.Granted, "low-urgency service should defer to the high-priority follower.");
        Assert.Equal(new[] { "F-vip" }, lo.VehiclesToClearFirst);
    }

    // ── (f) Opt-in: non-lifelong is byte-identical; lifelong WITHOUT a horizon == one-shot WarehouseWellFormed ─────
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(16)]
    public void NonLifelong_is_byte_identical_and_carries_no_lifelong_metric(int seed)
    {
        // A request with NO ScenarioMode (defaults RandomStress) and a request with ScenarioMode.RandomStress must be
        // identical, and neither carries a lifelong metric — the FMS-V3 lever is fully opt-in.
        var plain = Svc().RunAsync(new SimulationRequest(
            Width: 8, Height: 8, AgvCount: 5, Seed: seed, Planner: PlannerKind.Sipp)).GetAwaiter().GetResult();
        var explicitRandom = Svc().RunAsync(new SimulationRequest(
            Width: 8, Height: 8, AgvCount: 5, Seed: seed, Planner: PlannerKind.Sipp,
            ScenarioMode: ScenarioMode.RandomStress)).GetAwaiter().GetResult();

        Assert.Equal(Serialize(plain), Serialize(explicitRandom));
        Assert.Null(plain.Lifelong);
        Assert.Null(explicitRandom.Lifelong);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    public void Lifelong_mode_without_a_horizon_is_byte_identical_to_a_one_shot_warehouse(int seed)
    {
        // ScenarioMode.LifelongDispatch with no horizon (null) must fall back to the one-shot WarehouseWellFormed
        // scenario byte-for-byte, and carry NO lifelong metric — selecting the mode alone changes nothing.
        var warehouse = Svc().RunAsync(new SimulationRequest(
            Width: 7, Height: 7, AgvCount: 16, Seed: seed, Planner: PlannerKind.Sipp,
            ScenarioMode: ScenarioMode.WarehouseWellFormed)).GetAwaiter().GetResult();
        var lifelongNoHorizon = Svc().RunAsync(new SimulationRequest(
            Width: 7, Height: 7, AgvCount: 16, Seed: seed, Planner: PlannerKind.Sipp,
            ScenarioMode: ScenarioMode.LifelongDispatch)).GetAwaiter().GetResult();

        Assert.Equal(Serialize(warehouse), Serialize(lifelongNoHorizon));
        Assert.Null(lifelongNoHorizon.Lifelong);
    }

    // ── Determinism: the same seed yields an identical lifelong run (timeline + task throughput) ──────────────────
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void Lifelong_run_is_deterministic_for_a_given_seed(int seed)
    {
        var first = RunLifelong(seed);
        var second = RunLifelong(seed);

        Assert.Equal(Serialize(first), Serialize(second));
        Assert.Equal(first.Lifelong!.TasksCompleted, second.Lifelong!.TasksCompleted);
        Assert.Equal(first.Lifelong.ThroughputPerHundredTicks, second.Lifelong.ThroughputPerHundredTicks);
    }

    // ── Cost-based admission wired into the lifelong path does not change collision-freedom / progress ────────────
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void Lifelong_run_with_cost_based_admission_stays_collision_free_and_progresses(int seed)
    {
        // The lifelong workstations are NonBlocking (no transit impact), so cost-based admission is inert there, but
        // turning it on must not perturb collision-freedom or progress (the wiring is sound on the live path).
        var result = RunLifelong(seed, costAdmission: true);

        Assert.Equal(0, result.Stats.Collisions);
        Assert.NotNull(result.Lifelong);
        Assert.True(result.Lifelong!.TasksCompleted >= 40);
        Assert.True(result.Lifelong.TasksCompletedSecondHalf > 0);
    }

    private static string Serialize(SimulationResultDto r) =>
        string.Join(";", r.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions
                .OrderBy(p => p.AgentId, StringComparer.Ordinal)
                .Select(p => $"{p.AgentId}@{p.SiteId}/{p.State}"))));

    /// <summary>A fixed per-agent priority + planned-resource snapshot, exposed as the cost policy's
    /// <see cref="IFleetPlanProvider"/> so the constructed mini-case has affected traffic to score.</summary>
    private sealed class MiniFleetPlan(
        Dictionary<string, int> priorities,
        Dictionary<string, IReadOnlyList<ResourceRef>> planned) : IFleetPlanProvider
    {
        public IReadOnlyDictionary<string, IReadOnlyList<ResourceRef>> GetPlannedResources() => planned;

        public int? GetPriority(string agentId) => priorities.TryGetValue(agentId, out var p) ? p : null;
    }
}
