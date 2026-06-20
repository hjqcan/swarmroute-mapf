using SwarmRoute.Coordination.Application;
using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Liveness.Application.Contract.Policy;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// Production form of the validated closed-loop driver (the body previously inlined in
/// <c>ClosedLoopIntegrationTests.RunToCompletionAsync</c>). Given an <see cref="IFleetCoordinationCycle"/>, a
/// roadmap id, its <see cref="RoadmapGraph"/> and a fleet of <see cref="FleetAgentSpec"/>s, it drives the REAL
/// engine and records a tick-by-tick timeline:
/// <list type="number">
///   <item><description><b>Tick clock</b>: advance the fleet clock to the current tick so every interval reserved
///     this cycle is on the same axis the executor moves on (one tick = one CP hop).</description></item>
///   <item><description><b>Plan + reserve</b> every idle agent via <see cref="IFleetCoordinationCycle.RunCycleAsync"/>
///     (deterministic priority order). Newly-reserved agents become en route at their start CP.</description></item>
///   <item><description><b>Advance</b> each en-route agent at most one CP, through a right-of-way gate: it enters
///     the next CP only if no vehicle occupies it this tick (else it waits), awaiting
///     <see cref="IFleetCoordinationCycle.ReleaseAsync"/> to hand back the CP+lane it left behind; on arrival it
///     releases all its path resources (no leak).</description></item>
///   <item><description><b>Record</b> a frame for the tick (every agent's CP + motion state).</description></item>
///   <item><description><b>Check safety</b> (defensive): assert no two agents holding right-of-way share a CP.
///     With the gate this can no longer happen; if it ever did it would be reported via
///     <see cref="FleetLoopStatus.CollisionDetected"/> (the frame is recorded) as a regression signal.</description></item>
/// </list>
/// Deterministic given deterministic inputs (the tick clock removes the wall-clock dependence). This driver is a
/// <b>verifier</b>: it does NOT throw on a standoff — non-convergence is reported via
/// <see cref="FleetLoopResult.Stats"/> (<see cref="FleetLoopStatus.DidNotConverge"/>) so callers (sim API,
/// frontend) can surface it. It throws only on an internal invariant breach (a reserved path that doesn't run
/// start→goal).
/// <para><b>Collision-freedom.</b> Two layers guarantee it: the reservation table coordinates <em>who plans
/// through which CP and when</em> (interval-exclusive leases), and the executor's right-of-way gate is the final
/// stop-and-wait so a vehicle never enters an occupied CP. A pathological standoff therefore degrades to
/// <see cref="FleetLoopStatus.DidNotConverge"/>, never a crash. The gate is conservative (a trailing vehicle
/// waits one tick for the cell ahead to clear); v1's SIPP planner will tighten throughput by routing in time.</para>
/// <para><b>Statelessness.</b> This DI-injected service holds NO mutable state. Every call constructs a fresh
/// <see cref="FleetLoopRun"/> that owns ALL run-scoped state (the fleet, parked cells, recorded frames, counters
/// and pooled scratch buffers), so concurrent calls share nothing mutable. The driver here is a thin wrapper that
/// only validates arguments, picks the default policy, and delegates; the loop body lives in
/// <see cref="FleetLoopRun"/>. Behaviour is byte-identical to the previous single-method implementation.</para>
/// </summary>
public sealed class FleetLoopDriver
{
    /// <summary>
    /// Runs the closed loop, recording a frame per tick, until every agent arrives or the tick budget is
    /// exhausted. The fleet is processed in a stable order so a given input always produces the same timeline.
    /// </summary>
    /// <param name="advanceClock">Sets the fleet clock to the current tick at the start of each tick (the sim
    /// passes its <see cref="ManualFleetClock"/>'s setter); when null the engine's own clock is left untouched.</param>
    /// <param name="executionMode">How en-route agents advance: <see cref="FleetExecutionMode.Greedy"/> (v0
    /// right-of-way gate, the default so existing callers/tests keep v0 behaviour) or
    /// <see cref="FleetExecutionMode.ScheduleFaithful"/> (follow the SIPP-planned per-CP arrival ticks).</param>
    /// <param name="policy">The liveness decision policy for PHYSICAL standoffs: parked step-aside, PIBT/CBS cluster
    /// resolution, schedule-faithful stall-reroute / head-on yield / parked-ahead reroute. The driver routes every
    /// such decision through it and applies the returned directives with its own mechanism. When null, a no-op
    /// policy is used (no joint resolver, no step-aside) — byte-identical to the pre-policy baseline with those
    /// levers off. The policy carries the joint-resolver kind, the step-aside flag, and all standoff thresholds.</param>
    /// <param name="fms">(FMS-V1 R2) The optional FMS station overlay: site roles, station definitions and the
    /// arrival policy. When <see langword="null"/> (the default) every FMS branch in the executor is skipped, so the
    /// run is byte-identical to a non-FMS run. When supplied, the executor honours stations end-to-end — pre-dock
    /// buffer admission hold, in-service dock occupancy, and post-service relocation to parking.</param>
    /// <param name="stationScheduler">(FMS-V1 R2) The dock-admission scheduler the executor consults per tick when
    /// <paramref name="fms"/> is set, to decide whether an AGV queued at a station's pre-dock buffer may advance onto
    /// the dock (its service window reserved over the SAME reservation system the fleet plans on). Required when
    /// <paramref name="fms"/> defines stations; ignored when <paramref name="fms"/> is null.</param>
    /// <param name="parkingManager">(FMS-V2) Optional parking manager the clear-to-parking step uses to pick a
    /// serviced vehicle's resting slot (nearest free parking, fall back to buffer, avoiding occupied cells). When
    /// <see langword="null"/> (the default) the executor uses its FMS-V1 inline nearest-parking pick, so behaviour is
    /// byte-identical. Ignored when <paramref name="fms"/> is null (no service ever completes).</param>
    /// <exception cref="ArgumentNullException">If <paramref name="cycle"/>, <paramref name="graph"/> or <paramref name="agents"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="maxTicks"/> &lt; 1.</exception>
    /// <exception cref="FleetLoopException">Only on an internal invariant breach (reserved path does not start at the agent's current CP).</exception>
    public async Task<FleetLoopResult> RunToCompletionAsync(
        IFleetCoordinationCycle cycle,
        Guid roadmapId,
        RoadmapGraph graph,
        IReadOnlyCollection<FleetAgentSpec> agents,
        int maxTicks,
        Action<long>? advanceClock = null,
        FleetExecutionMode executionMode = FleetExecutionMode.Greedy,
        ILivenessPolicy? policy = null,
        Action<string>? log = null,
        FmsScenario? fms = null,
        IStationScheduler? stationScheduler = null,
        IParkingManager? parkingManager = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTicks, 1);

        // No policy supplied ⇒ the baseline behaviour with every standoff lever off (the no-op policy returns no
        // directives in any phase), so a plain closed-loop / sim run is byte-identical to the pre-policy driver.
        policy ??= NoOpLivenessPolicy.Instance;

        // All run-scoped state lives on this per-call instance, so the driver itself stays stateless (zero instance
        // fields) and concurrent calls are isolated.
        return await new FleetLoopRun(
                cycle, roadmapId, graph, agents, maxTicks, advanceClock, executionMode, policy, log,
                fms, stationScheduler, parkingManager)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
