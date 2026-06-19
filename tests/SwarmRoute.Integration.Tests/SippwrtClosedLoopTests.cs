using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// End-to-end validation of the v3 third pillar — SIPPwRT (continuous-time SIPP) driven through the REAL engine
/// and executed by the event-driven continuous executor (<see cref="FleetExecutionMode.Continuous"/>). It proves
/// the machinery and the opt-in contract:
/// <list type="bullet">
///   <item><b>Collision-free</b> — honouring the interval-exclusive SIPPwRT schedule never co-locates two agents,
///     exactly as the schedule-faithful executor guarantees for v1.</item>
///   <item><b>Real-ms transport</b> — a populated, well-formed <see cref="ContinuousTimelineDto"/> on the
///     fleet-clock millisecond axis, every trajectory starting at t=0 and ending at the agent's goal.</item>
///   <item><b>Byte-identical when off</b> — every DISCRETE planner leaves <c>Continuous</c> null (omitted from JSON).</item>
///   <item><b>Deterministic</b> — same seed ⇒ identical continuous timeline.</item>
/// </list>
/// The sim builds a UNIFORM grid, where continuous-time is inert by design (equal edges ⇒ equal durations); the
/// time-OPTIMAL win lives in the planner unit tests on non-uniform graphs. Here we prove the executor + transport.
/// </summary>
public sealed class SippwrtClosedLoopTests
{
    private static SimulationResultDto Run(int width, int height, int agv, int seed, PlannerKind planner)
    {
        var service = new SimulationService(
            new GridFieldFactory(),
            new FleetLoopDriver(),
            new InMemorySimulationEngineFactory(),
            NullLogger<SimulationService>.Instance);
        return service.RunAsync(new SimulationRequest(width, height, agv, seed, planner)).GetAwaiter().GetResult();
    }

    // ── Hard invariant: SIPPwRT + the continuous executor is collision-free and converges on solvable densities ──
    [Theory]
    [InlineData(4, 4, 3)]
    [InlineData(5, 5, 4)]
    [InlineData(6, 6, 6)]
    public void Sippwrt_is_collision_free_and_converges(int width, int height, int agv)
    {
        var r = Run(width, height, agv, seed: 7, PlannerKind.Sippwrt);

        Assert.Equal(0, r.Stats.Collisions);
        Assert.Equal("Completed", r.Stats.Status);
        Assert.Equal(agv, r.Stats.Arrived);
    }

    // ── Collision-freedom is a HARD invariant even at a density that may not converge (honest liveness) ─────────
    [Fact]
    public void Sippwrt_is_collision_free_even_when_dense()
    {
        var dense = Enumerable.Range(1, 6).Select(s => Run(7, 7, 16, s, PlannerKind.Sippwrt)).ToList();

        Assert.All(dense, r => Assert.Equal(0, r.Stats.Collisions));
        Assert.All(dense, r => Assert.NotEqual("CollisionDetected", r.Stats.Status));
    }

    // ── The continuous timeline is populated, well-formed, and on the real-ms forward-time axis ────────────────
    [Fact]
    public void Sippwrt_emits_a_well_formed_continuous_timeline()
    {
        var r = Run(5, 5, 4, seed: 7, PlannerKind.Sippwrt);

        Assert.NotNull(r.Continuous);
        var c = r.Continuous!;
        Assert.True(c.DurationMs > 0, "a converged continuous run spans a positive duration");
        Assert.Equal(4, c.Agents.Count);

        foreach (var t in c.Agents)
        {
            Assert.NotEmpty(t.Waypoints);
            Assert.Equal(0, t.Waypoints[0].ArriveMs);                       // every trajectory starts at t=0 ...
            for (var i = 1; i < t.Waypoints.Count; i++)                     // ... and arrivals strictly increase.
                Assert.True(t.Waypoints[i].ArriveMs > t.Waypoints[i - 1].ArriveMs,
                    $"agent {t.AgentId} waypoint {i} must arrive after its predecessor");
        }

        Assert.Equal(c.Agents.Max(t => t.Waypoints[^1].ArriveMs), c.DurationMs); // duration = last fleet arrival
    }

    // ── Each agent's continuous trajectory actually reaches its goal ───────────────────────────────────────────
    [Fact]
    public void Sippwrt_continuous_trajectory_reaches_each_goal()
    {
        var r = Run(5, 5, 4, seed: 7, PlannerKind.Sippwrt);
        var startById = r.Agents.ToDictionary(a => a.Id, a => a.StartSiteId, StringComparer.Ordinal);
        var goalById = r.Agents.ToDictionary(a => a.Id, a => a.GoalSiteId, StringComparer.Ordinal);

        Assert.NotNull(r.Continuous);
        foreach (var t in r.Continuous!.Agents)
        {
            Assert.Equal(startById[t.AgentId], t.Waypoints[0].SiteId);      // starts at the start
            Assert.Equal(goalById[t.AgentId], t.Waypoints[^1].SiteId);      // ends at the goal
        }
    }

    // ── Opt-in contract: every DISCRETE planner leaves Continuous null (byte-identical JSON, no extra key) ──────
    [Theory]
    [InlineData(PlannerKind.Dijkstra)]
    [InlineData(PlannerKind.Sipp)]
    public void Discrete_planners_emit_no_continuous_timeline(PlannerKind planner)
    {
        var r = Run(5, 5, 4, seed: 7, planner);
        Assert.Null(r.Continuous);
    }

    // ── Wire-level proof: the continuous channel is OMITTED from discrete JSON (WhenWritingNull → byte-identical
    //    to the pre-v3 response) and present only for the SIPPwRT continuous executor. ───────────────────────────
    [Fact]
    public void Continuous_channel_is_omitted_from_discrete_json_and_present_for_sippwrt()
    {
        string Json(PlannerKind p) => System.Text.Json.JsonSerializer.Serialize(Run(5, 5, 4, seed: 7, p));

        Assert.DoesNotContain("Continuous", Json(PlannerKind.Dijkstra)); // no extra key → byte-identical to v0/v1
        Assert.DoesNotContain("Continuous", Json(PlannerKind.Sipp));
        Assert.Contains("Continuous", Json(PlannerKind.Sippwrt));        // present only under the continuous executor
    }

    // ── Determinism: same seed ⇒ identical continuous timeline ────────────────────────────────────────────────
    [Fact]
    public void Sippwrt_continuous_timeline_is_deterministic()
    {
        var first = Run(6, 6, 6, seed: 99, PlannerKind.Sippwrt);
        var second = Run(6, 6, 6, seed: 99, PlannerKind.Sippwrt);

        Assert.Equal(first.Stats.Status, second.Stats.Status);
        Assert.Equal(SerializeContinuous(first), SerializeContinuous(second));
    }

    private static string SerializeContinuous(SimulationResultDto r) =>
        r.Continuous is null
            ? "null"
            : r.Continuous.DurationMs + "|" + string.Join(";", r.Continuous.Agents
                .OrderBy(t => t.AgentId, StringComparer.Ordinal)
                .Select(t => t.AgentId + "=" + string.Join(",", t.Waypoints.Select(w => $"{w.SiteId}@{w.ArriveMs}"))));
}
