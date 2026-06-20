using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.Liveness.Application.Contract.Policy;
using SwarmRoute.Liveness.Application.Policy;
using SwarmRoute.Map.Domain.Entities;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;
using Xunit;

namespace SwarmRoute.Liveness.Tests;

/// <summary>
/// FMS-V2 "persistent" (clearance-driven) parked step-aside. With <see cref="LivenessOptions.PersistentRelocation"/>
/// OFF (the default), a relocated parked gatekeeper re-parks at its own goal the instant its fixed
/// <see cref="LivenessOptions.GatekeeperYieldWindow"/> countdown elapses — modelled by the policy emitting
/// <see cref="RestoreGoal"/> when <c>YieldTicksRemaining == 1</c>. With it ON, the window becomes a MINIMUM hold: the
/// gatekeeper keeps holding aside past the window until the corridor it freed (its own goal cell) is no longer on any
/// live agent's approach (the walled agent has advanced past it / reached goal), and only then returns.
/// <para>
/// These tests pin the new <see cref="RestoreGoal"/> firing decision in isolation (pure policy, no engine): the
/// gatekeeper does NOT return while the walled agent still needs the corridor, and returns / stays-parked once it is
/// clear. Each persistent assertion is paired with the SAME snapshot under the default mode to prove the
/// <see cref="LivenessOptions.PersistentRelocation"/> toggle (not an inert fixture) is what changes the decision, and
/// that the default path is byte-identical to the pre-V2 countdown.
/// </para>
/// </summary>
public sealed class PersistentRelocationTests
{
    // ── Small directed-graph builder (mirrors LivenessPolicyTests / InServiceGateTests) ─────────────────────────
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

    // A view with sensible defaults; tests override only the fields they exercise. `effectiveGoal` defaults to `goal`
    // (the common case) but is overridable so a relocated gatekeeper can hold aside (EffectiveGoal == its aside site)
    // while its Goal stays the freed corridor cell — exactly the executor's RedirectTarget shape.
    private static AgentLivenessView View(
        string id, string position, string goal, int priority,
        string? effectiveGoal = null,
        string? enRouteNextCell = null, bool enRoute = false, bool done = false,
        bool inJointResolver = false, bool hasActiveRedirect = false, bool holdingAtAvoidSite = false,
        int blockedTicks = 0, int stuckTicks = 0, int yieldTicksRemaining = 0,
        int pibtHeldTicks = 0, int pibtEpisodeTicksLeft = 0,
        bool atRouteEnd = false, bool nextCellIsParked = false,
        bool scheduledToAdvance = false, bool scheduledToMoveThisTick = false,
        MobilityClass mobility = MobilityClass.Movable) =>
        new(id, position, goal, effectiveGoal ?? goal, priority, enRouteNextCell, enRoute, done,
            inJointResolver, hasActiveRedirect, holdingAtAvoidSite, blockedTicks, stuckTicks, yieldTicksRemaining,
            pibtHeldTicks, pibtEpisodeTicksLeft, atRouteEnd, nextCellIsParked, scheduledToAdvance,
            scheduledToMoveThisTick, mobility);

    // The canonical corridor: S -> P -> G. P is the single-width corridor the gatekeeper was parked on (its goal is
    // P) and has stepped aside FROM to the off-path siding F (P<->F). A walled agent at S makes for G via P.
    private static RoadmapGraph CorridorGraph() => Graph(
        ["S", "P", "G", "F"], ("S", "P"), ("P", "G"), ("P", "F"), ("F", "P"), ("G", "P"), ("P", "S"));

    // A relocated gatekeeper mid-hold: it was parked on the corridor cell P (its Goal), now redirected to the siding
    // F (EffectiveGoal). `yieldLeft` is the ticks left in its minimum window; `position` is where it physically sits
    // (still on P until it physically steps, or already on F once it has).
    private static AgentLivenessView Gatekeeper(string position, int yieldLeft, bool holdingAtAvoidSite = false) =>
        View("gatekeeper", position, goal: "P", priority: 1, effectiveGoal: "F",
            hasActiveRedirect: true, holdingAtAvoidSite: holdingAtAvoidSite, yieldTicksRemaining: yieldLeft);

    private static bool RestoresGatekeeper(LivenessPolicy policy, LivenessSnapshot snapshot) =>
        policy.Evaluate(snapshot).OfType<RestoreGoal>().Any(r => r.AgentId == "gatekeeper");

    // ── (1) Default mode is byte-identical: RestoreGoal fires exactly on the countdown edge (YieldTicksRemaining==1)
    [Fact]
    public void DefaultMode_RestoresGatekeeper_OnTheCountdownEdge()
    {
        var graph = CorridorGraph();
        var policy = new LivenessPolicy(graph, new LivenessOptions { StepAside = true }); // PersistentRelocation off

        // Even with the walled agent STILL needing the corridor (path S->P->G through P), the default countdown
        // returns the gatekeeper the instant its window hits the edge — the pre-V2 behaviour, unchanged.
        var snapshot = new LivenessSnapshot(
            Tick: 30, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-walled", "S", "G", priority: 0, stuckTicks: 5),
                Gatekeeper(position: "F", yieldLeft: 1, holdingAtAvoidSite: true),
            },
            new HashSet<string>());

        Assert.True(RestoresGatekeeper(policy, snapshot));
    }

    [Fact]
    public void DefaultMode_DoesNotRestore_BeforeTheCountdownEdge()
    {
        var graph = CorridorGraph();
        var policy = new LivenessPolicy(graph, new LivenessOptions { StepAside = true });

        // Window not yet at the edge (2 left) ⇒ no restore, in either mode (the minimum hold).
        var snapshot = new LivenessSnapshot(
            Tick: 29, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-walled", "S", "G", priority: 0, stuckTicks: 5),
                Gatekeeper(position: "F", yieldLeft: 2, holdingAtAvoidSite: true),
            },
            new HashSet<string>());

        Assert.False(RestoresGatekeeper(policy, snapshot));
    }

    // ── (2) Persistent mode respects the MINIMUM window: it never returns early even once the corridor is clear ────
    [Fact]
    public void PersistentMode_DoesNotRestore_WhileMinimumWindowRemains_EvenIfCorridorClear()
    {
        var graph = CorridorGraph();
        var policy = new LivenessPolicy(graph,
            new LivenessOptions { StepAside = true, PersistentRelocation = true });

        // The walled agent has already reached G (Done) ⇒ corridor is clear — but the minimum window still has 5
        // ticks ⇒ the gatekeeper holds (the window is a floor, not skipped).
        var snapshot = new LivenessSnapshot(
            Tick: 25, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-walled", "G", "G", priority: 0, done: true),
                Gatekeeper(position: "F", yieldLeft: 5, holdingAtAvoidSite: true),
            },
            new HashSet<string> { "G" });

        Assert.False(RestoresGatekeeper(policy, snapshot));
    }

    // ── (3) THE KEY CASE: window elapsed but corridor STILL NEEDED ⇒ persistent HOLDS where the default would RETURN
    [Fact]
    public void PersistentMode_DoesNotRestore_WhenWindowElapsedButWalledAgentStillNeedsCorridor()
    {
        var graph = CorridorGraph();

        // Window has elapsed (0 left, clamped by the executor's `>0` decrement) and the walled agent is still at S,
        // its only approach to G being S->P->G through the freed cell P. The gatekeeper must keep holding aside.
        var snapshot = new LivenessSnapshot(
            Tick: 40, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-walled", "S", "G", priority: 0, stuckTicks: 3),
                Gatekeeper(position: "F", yieldLeft: 0, holdingAtAvoidSite: true),
            },
            new HashSet<string>());

        var persistent = new LivenessPolicy(graph,
            new LivenessOptions { StepAside = true, PersistentRelocation = true });
        Assert.False(RestoresGatekeeper(persistent, snapshot)); // persistent: corridor still needed ⇒ holds

        // CONTRAST: the SAME elapsed-window snapshot under the default countdown would already have returned it at
        // the edge — proving the toggle (not the fixture) is what now keeps it aside. (At yieldLeft==1 the default
        // fires; we show the default fires while persistent does not.)
        var edgeSnapshot = snapshot with
        {
            Agents = new[] { snapshot.Agents[0], Gatekeeper(position: "F", yieldLeft: 1, holdingAtAvoidSite: true) },
        };
        var defaultMode = new LivenessPolicy(graph, new LivenessOptions { StepAside = true });
        Assert.True(RestoresGatekeeper(defaultMode, edgeSnapshot));   // default: returns at the edge (re-seal risk)
        Assert.False(RestoresGatekeeper(persistent, edgeSnapshot));   // persistent: still needed ⇒ still holds
    }

    // ── (4) Persistent mode RETURNS once the corridor is clear AND the minimum window is satisfied ────────────────
    [Fact]
    public void PersistentMode_Restores_OnceWalledAgentHasClearedTheCorridor()
    {
        var graph = CorridorGraph();
        var policy = new LivenessPolicy(graph,
            new LivenessOptions { StepAside = true, PersistentRelocation = true });

        // The walled agent has advanced past P onto G (reached goal, Done) and no other agent's approach uses P, so
        // the corridor is no longer needed; the window has elapsed ⇒ the gatekeeper is freed to re-park at P.
        var snapshot = new LivenessSnapshot(
            Tick: 50, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-walled", "G", "G", priority: 0, done: true),
                Gatekeeper(position: "F", yieldLeft: 0, holdingAtAvoidSite: true),
            },
            new HashSet<string> { "G" });

        Assert.True(RestoresGatekeeper(policy, snapshot));
    }

    // ── (5) An agent physically STANDING ON the freed cell counts as needing it (it is using the corridor now) ─────
    [Fact]
    public void PersistentMode_DoesNotRestore_WhenAnotherAgentIsStandingOnTheFreedCell()
    {
        var graph = CorridorGraph();
        var policy = new LivenessPolicy(graph,
            new LivenessOptions { StepAside = true, PersistentRelocation = true });

        // The walled agent has stepped ONTO P (the freed corridor cell) and is heading to G. It is occupying the
        // corridor right now, so the gatekeeper must not return on top of it — even with the window elapsed.
        var snapshot = new LivenessSnapshot(
            Tick: 45, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-walled", "P", "G", priority: 0, enRouteNextCell: "G", enRoute: true),
                Gatekeeper(position: "F", yieldLeft: 0, holdingAtAvoidSite: true),
            },
            new HashSet<string>());

        Assert.False(RestoresGatekeeper(policy, snapshot));
    }

    // ── (6) Clearance is fleet-wide: a DIFFERENT agent whose approach later routes through the freed cell holds it ─
    [Fact]
    public void PersistentMode_HoldsForAnyAgentWhoseApproachRoutesThroughTheFreedCell()
    {
        var graph = CorridorGraph();
        var policy = new LivenessPolicy(graph,
            new LivenessOptions { StepAside = true, PersistentRelocation = true });

        // The original walled agent reached G (Done), BUT a second agent now sits at S also bound for G via P. The
        // corridor is still on a live approach ⇒ the gatekeeper keeps holding (clearance is over the whole fleet,
        // not just the one agent the relocation was first triggered for).
        var snapshot = new LivenessSnapshot(
            Tick: 55, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-first", "G", "G", priority: 0, done: true),
                View("agv-second", "S", "G", priority: 2, stuckTicks: 1),
                Gatekeeper(position: "F", yieldLeft: 0, holdingAtAvoidSite: true),
            },
            new HashSet<string> { "G" });

        Assert.False(RestoresGatekeeper(policy, snapshot));
    }

    // ── (7) Other parked / stepped-aside vehicles are NOT counted as corridor needers (they are stationary) ───────
    [Fact]
    public void PersistentMode_Restores_WhenOnlyParkedOrAsideVehiclesRemain()
    {
        var graph = CorridorGraph();
        var policy = new LivenessPolicy(graph,
            new LivenessOptions { StepAside = true, PersistentRelocation = true });

        // The only other vehicles are (a) a finished vehicle parked on G and (b) a second gatekeeper holding at its
        // own aside site. Neither is transiting the corridor, so it is clear and the (window-elapsed) gatekeeper may
        // return — a stationary vehicle's mere presence must not pin a corridor open forever (livelock guard).
        var snapshot = new LivenessSnapshot(
            Tick: 60, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                View("agv-parked", "G", "G", priority: 0, done: true),
                // A second relocated gatekeeper (its own redirect), parked aside at F': should not be read as a needer.
                View("gatekeeper-2", "F", "P", priority: 3, effectiveGoal: "F",
                    hasActiveRedirect: true, holdingAtAvoidSite: true, yieldTicksRemaining: 0),
                Gatekeeper(position: "F", yieldLeft: 0, holdingAtAvoidSite: true),
            },
            new HashSet<string> { "G" });

        Assert.True(RestoresGatekeeper(policy, snapshot));
    }

    // ── (8) A non-redirecting agent (no active redirect) is never touched by either mode's RestoreGoal ────────────
    [Fact]
    public void NeitherMode_RestoresAnAgentWithoutAnActiveRedirect()
    {
        var graph = CorridorGraph();
        var snapshot = new LivenessSnapshot(
            Tick: 12, LivenessPhase.BeforePlanning, ScheduleFaithful: true,
            new[]
            {
                // A plain waiting agent that happens to carry a (meaningless) yield count but NO redirect: must be
                // ignored by RestoreGoal under both modes — only relocated gatekeepers (RedirectTarget set) recover.
                View("agv-plain", "S", "G", priority: 0, yieldTicksRemaining: 1),
            },
            new HashSet<string>());

        Assert.False(RestoresGatekeeper(
            new LivenessPolicy(graph, new LivenessOptions { StepAside = true }), snapshot));
        Assert.False(RestoresGatekeeper(
            new LivenessPolicy(graph, new LivenessOptions { StepAside = true, PersistentRelocation = true }),
            snapshot));
    }
}
