using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.Liveness.Application.Policy;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// (FMS-V1 R2 — M-F1) End-to-end validation of the station-honouring executor through the REAL engine
/// (Coordination + PathPlanning + TrafficControl + the Dispatch dock-admission scheduler over the same reservation
/// table). The M-F1 demo: a transit corridor crossed by 2+ AGVs, plus one HardBlocking station whose dock sits off
/// the corridor behind a pre-dock buffer with a blocking closure over the corridor cell. The contract proven here:
/// <list type="bullet">
///   <item>the dock AGV <b>WAITS</b> at its pre-dock buffer (MissionState WaitingDockAdmission ⇒ it does not advance
///     toward the dock) while the transit AGVs cross the corridor;</item>
///   <item>once the corridor (the station's blocking closure) is clear it is admitted, <b>docks</b>, and goes
///     <b>InService</b>;</item>
///   <item>while InService it <b>never moves</b> and is never relocated;</item>
///   <item><b>0 collisions</b> and the run is not falsely reported as a collision;</item>
///   <item>the transit AGVs <b>complete</b>, and (under ClearToParking) the serviced AGV clears to parking;</item>
///   <item>with <c>fms = null</c> the executor is <b>byte-identical</b> to a normal run — every station branch is
///     inert, so the dock AGV simply parks on its goal like any other AGV.</item>
/// </list>
/// </summary>
public sealed class DockAdmissionClosedLoopTests
{
    private const string DockAgent = MF1ScenarioBuilder.DockAgentIdConst;
    private const string Buffer = MF1ScenarioBuilder.PreDockBuffer;     // r1c2
    private const string Dock = MF1ScenarioBuilder.DockPoint;           // r2c2
    private const string CorridorCell = MF1ScenarioBuilder.CorridorBlockedCell; // r0c2

    private static SimulationService Svc() =>
        new(new GridFieldFactory(), new FleetLoopDriver(), new InMemorySimulationEngineFactory(),
            NullLogger<SimulationService>.Instance);

    // Run the M-F1 station scenario end-to-end via the service (the opt-in StationScenario flag).
    private static SimulationResultDto RunMf1(int transitAgvCount = 2) =>
        Svc().RunAsync(new SimulationRequest(
                Width: MF1ScenarioBuilder.Width, Height: MF1ScenarioBuilder.Height,
                AgvCount: transitAgvCount, StationScenario: StationScenarioKind.MF1))
            .GetAwaiter().GetResult();

    // Position of an agent on a given frame.
    private static string PosOf(FrameDto frame, string agentId) =>
        frame.Positions.First(p => p.AgentId == agentId).SiteId;

    private static string StateOf(FrameDto frame, string agentId) =>
        frame.Positions.First(p => p.AgentId == agentId).State;

    // First frame index whose predicate holds, or -1 (IReadOnlyList has no List.FindIndex).
    private static int FirstIndex(IReadOnlyList<FrameDto> frames, Func<FrameDto, bool> predicate)
    {
        for (var i = 0; i < frames.Count; i++)
            if (predicate(frames[i]))
                return i;
        return -1;
    }

    // ── (1) The dock AGV waits at its buffer while a transit AGV is on / crossing the corridor cell ──────────────
    [Fact]
    public void DockAgv_waits_at_buffer_until_transit_clears_then_docks_and_services()
    {
        var result = RunMf1(transitAgvCount: 2);
        var frames = result.Timeline.Frames;

        // It reaches the buffer at some point...
        var firstAtBuffer = FirstIndex(frames, f => PosOf(f, DockAgent) == Buffer);
        Assert.True(firstAtBuffer >= 0, "the dock AGV should reach its pre-dock buffer.");

        // ...and HOLDS there for more than one frame (a genuine admission wait, not a pass-through).
        var bufferFrames = frames.Count(f => PosOf(f, DockAgent) == Buffer);
        Assert.True(bufferFrames > 1,
            $"the dock AGV should wait multiple ticks at the buffer (held {bufferFrames}).");

        // While it waits at the buffer, a transit AGV is still on (or has not yet cleared) the corridor cell r0c2 —
        // i.e. the wait genuinely overlaps the corridor being occupied (clearance-before-service).
        var transitOnCorridorWhileWaiting = frames
            .Where(f => PosOf(f, DockAgent) == Buffer)
            .Any(f => f.Positions.Any(p => p.AgentId != DockAgent && p.SiteId == CorridorCell));
        Assert.True(transitOnCorridorWhileWaiting,
            "a transit AGV should be on the corridor cell while the dock AGV waits at the buffer.");

        // While waiting at the buffer the dock AGV is stationary (motion state Waiting, not Moving) — i.e. it is held,
        // not in transit. (The schedule-faithful executor reports a held, non-advancing agent as Waiting.)
        Assert.Contains(frames, f => PosOf(f, DockAgent) == Buffer && StateOf(f, DockAgent) == "Waiting");

        // The dock AGV NEVER occupies the corridor cell r0c2: it approaches the buffer via the row-1 stub, so its
        // wait is a genuine dock-admission hold, not a race with the transit AGVs for the corridor.
        Assert.DoesNotContain(frames, f => PosOf(f, DockAgent) == CorridorCell);

        // It eventually docks (reaches the dock point).
        var firstAtDock = FirstIndex(frames, f => PosOf(f, DockAgent) == Dock);
        Assert.True(firstAtDock > firstAtBuffer,
            "the dock AGV should advance from the buffer onto the dock AFTER waiting.");
    }

    // ── (2) While in service the dock AGV never moves, and is never relocated off the dock ───────────────────────
    [Fact]
    public void DockAgv_is_immovable_while_in_service()
    {
        var result = RunMf1(transitAgvCount: 2);
        var frames = result.Timeline.Frames;

        var firstAtDock = FirstIndex(frames, f => PosOf(f, DockAgent) == Dock);
        Assert.True(firstAtDock >= 0, "the dock AGV should dock.");

        // The service window is long (40 ticks); for that span after docking the AGV must sit on the dock unmoved.
        // (We check the contiguous run of dock-occupancy from the first docking frame.)
        var contiguousAtDock = 0;
        for (var i = firstAtDock; i < frames.Count && PosOf(frames[i], DockAgent) == Dock; i++)
            contiguousAtDock++;

        Assert.True(contiguousAtDock >= (int)MF1ScenarioBuilder.ServiceDurationMs,
            $"the dock AGV should hold the dock for the whole service window " +
            $"({contiguousAtDock} < {MF1ScenarioBuilder.ServiceDurationMs}).");

        // It is never relocated to a DIFFERENT cell and back during service (the contiguous run above already implies
        // it stayed; assert explicitly that no frame in the service span shows it elsewhere then returning).
        for (var i = firstAtDock; i < firstAtDock + contiguousAtDock; i++)
            Assert.Equal(Dock, PosOf(frames[i], DockAgent));
    }

    // ── (3) No collisions, and the run is not falsely reported as a collision ────────────────────────────────────
    [Fact]
    public void Mf1_run_is_collision_free()
    {
        var result = RunMf1(transitAgvCount: 2);

        Assert.Equal(0, result.Stats.Collisions);
        Assert.NotEqual("CollisionDetected", result.Stats.Status);
        Assert.Null(result.Stats.CollisionTick);
    }

    // ── (4) Transit AGVs complete, and the serviced AGV clears the dock (ClearToParking) and finishes ───────────
    [Fact]
    public void Transit_agvs_complete_and_serviced_agv_clears_to_parking()
    {
        var result = RunMf1(transitAgvCount: 2);
        var last = result.Timeline.Frames[^1];

        // Every transit AGV reached its corridor goal (Arrived).
        foreach (var transit in result.Agents.Where(a => a.Id.StartsWith("transit-", StringComparison.Ordinal)))
        {
            Assert.Equal("Arrived", StateOf(last, transit.Id));
            Assert.Equal(transit.GoalSiteId, PosOf(last, transit.Id));
        }

        // The whole run converged (everyone arrived), and the run did not stall.
        Assert.Equal("Completed", result.Stats.Status);
        Assert.Equal(result.Agents.Count, result.Stats.Arrived);

        // Under ClearToParking the serviced AGV ends OFF the dock (it relocated to a parking slot), Arrived.
        Assert.Equal("Arrived", StateOf(last, DockAgent));
        Assert.NotEqual(Dock, PosOf(last, DockAgent));
    }

    // ── (5) fms = null ⇒ byte-identical: every station branch is inert and the dock AGV just parks on its goal ────
    [Fact]
    public async Task WithoutFms_the_dock_agv_just_parks_on_its_goal_like_a_normal_run()
    {
        // Build the SAME M-F1 grid + fleet, but drive the loop with NO FMS overlay and NO scheduler. The dock AGV's
        // goal is the dock point; with stations off it must behave exactly like a normal AGV — route straight to the
        // dock and park there (Arrived at r2c2), never staging at the buffer or going in service.
        var scenario = MF1ScenarioBuilder.Build(new GridFieldFactory(), transitAgvCount: 2);

        var factory = new InMemorySimulationEngineFactory();
        // No catalog ⇒ no scheduler ⇒ pure baseline engine.
        await using var engineNoFms = factory.Create(scenario.Field.Graph, PlannerKind.Dijkstra);
        var policy = new LivenessPolicy(scenario.Field.Graph, new LivenessOptions());

        var off = await new FleetLoopDriver().RunToCompletionAsync(
            engineNoFms.Cycle, engineNoFms.RoadmapId, scenario.Field.Graph, scenario.Agents, maxTicks: 500,
            advanceClock: engineNoFms.Clock.SetTick,
            executionMode: FleetExecutionMode.Greedy,
            policy: policy,
            fms: null,            // ← the byte-identical switch
            stationScheduler: null);

        // The dock AGV parked on its goal (the dock point) — the FMS lifecycle never fired.
        Assert.Equal(0, off.Stats.Collisions);
        Assert.Equal(scenario.Agents.Count, off.Stats.Arrived);
        var lastDockPos = off.Frames[^1].Positions.First(p => p.AgentId == scenario.DockAgentId).SiteId;
        Assert.Equal(Dock, lastDockPos);
        // It is "done" (parked on its goal) — Arrived — not held in service.
        Assert.Equal(AgentMotionState.Arrived,
            off.Frames[^1].Positions.First(p => p.AgentId == scenario.DockAgentId).State);

        // And the dock AGV NEVER staged at the buffer as a waiting hold: it routed straight through to the dock. (It
        // may transit r1c2 if that lies on the shortest path, but it never WAITS there — i.e. never occupies r1c2 for
        // multiple consecutive frames the way the FMS admission hold does.)
        var bufferRun = 0;
        var maxBufferRun = 0;
        foreach (var f in off.Frames)
        {
            bufferRun = f.Positions.First(p => p.AgentId == scenario.DockAgentId).SiteId == Buffer ? bufferRun + 1 : 0;
            maxBufferRun = Math.Max(maxBufferRun, bufferRun);
        }
        Assert.True(maxBufferRun <= 1, $"with fms=null the dock AGV must not hold at the buffer (held {maxBufferRun}).");
    }

    // ── (6) The station run is deterministic: same scenario ⇒ identical timeline (incl. the admission lifecycle) ───
    [Fact]
    public void Mf1_run_is_deterministic()
    {
        var first = RunMf1(transitAgvCount: 2);
        var second = RunMf1(transitAgvCount: 2);

        Assert.Equal(Serialize(first), Serialize(second));
    }

    private static string Serialize(SimulationResultDto r) =>
        string.Join(";", r.Timeline.Frames.Select(f =>
            $"{f.Tick}:" + string.Join(",", f.Positions
                .OrderBy(p => p.AgentId, StringComparer.Ordinal)
                .Select(p => $"{p.AgentId}@{p.SiteId}/{p.State}"))));
}
