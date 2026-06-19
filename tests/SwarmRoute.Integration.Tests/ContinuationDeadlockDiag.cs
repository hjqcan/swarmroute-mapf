using Microsoft.Extensions.Logging.Abstractions;
using SwarmRoute.Host.Adapters;
using SwarmRoute.PathPlanning.Domain.Shared.Enums;
using SwarmRoute.Simulation.Application;
using Xunit;

namespace SwarmRoute.Integration.Tests;

/// <summary>
/// Lifelong-continuation deadlock recovery: when an AGV is walled out of its goal by parked vehicles sitting on
/// the only approach (the RAG detector can't see it — parked vehicles hold no lease), the gatekeepers step aside
/// for a window so it passes, then re-park. Reproduced from the auto-loop (continuation makes a seed alone
/// insufficient, so the exact per-agent starts are pinned here — the same JSON the engine now logs on any
/// non-convergence).
/// </summary>
public sealed class ContinuationDeadlockTests
{
    private static readonly string[] WalledOutStarts =
        ["r6c0","r4c8","r6c6","r4c9","r7c7","r0c9","r7c3","r4c1","r4c5","r5c9","r2c8","r4c2","r2c1","r6c9","r2c6","r3c5"];

    private static SimulationResultDto Run(SimulationRequest req)
        => new SimulationService(new GridFieldFactory(), new FleetLoopDriver(),
                new InMemorySimulationEngineFactory(), NullLogger<SimulationService>.Instance)
            .RunAsync(req).GetAwaiter().GetResult();

    private static int CountSwaps(SimulationResultDto r)
    {
        var f = r.Timeline.Frames; var n = 0;
        for (var t = 0; t + 1 < f.Count; t++)
        {
            var a = f[t].Positions.ToDictionary(p => p.AgentId, p => p.SiteId);
            var b = f[t + 1].Positions.ToDictionary(p => p.AgentId, p => p.SiteId);
            var ids = a.Keys.ToList();
            for (var i = 0; i < ids.Count; i++)
                for (var j = i + 1; j < ids.Count; j++)
                    if (a[ids[i]] != a[ids[j]] && a[ids[i]] == b[ids[j]] && a[ids[j]] == b[ids[i]]) n++;
        }
        return n;
    }

    [Fact]
    public void Walled_out_agent_is_freed_by_a_gatekeeper_step_aside()
    {
        // agv-14 starts walled out of goal r1c4 (only entrance r1c5 is parked-on by agv-11).
        var r = Run(new SimulationRequest(10, 8, 16, 82730, PlannerKind.Sipp, Starts: WalledOutStarts, StepAside: true));

        Assert.Equal("Completed", r.Stats.Status);
        Assert.Equal(16, r.Stats.Arrived);
        Assert.Equal(0, r.Stats.Collisions);
        Assert.Equal(0, CountSwaps(r));
    }

    [Fact]
    public void Continuation_chains_remain_collision_free_when_some_runs_do_not_converge()
    {
        // Chain runs like the auto-loop (each run's starts = the previous run's final poses) and count how many
        // of the chained runs fully converge. Convergence is telemetry here: the hard invariant is that a
        // non-converged continuation still reports honestly without CP collisions or edge swaps.
        const int w = 10, h = 8, agv = 16;
        var rng = new Random(12345);
        IReadOnlyList<string>? starts = null;
        int converged = 0, total = 0;

        for (var i = 0; i < 30; i++)
        {
            var r = Run(new SimulationRequest(w, h, agv, rng.Next(100_000), PlannerKind.Sipp, Starts: starts, StepAside: true));
            total++;
            if (r.Stats.Status == "Completed") converged++;
            Assert.Equal(0, CountSwaps(r));       // never pass through another AGV, converged or not
            Assert.Equal(0, r.Stats.Collisions);
            starts = r.Timeline.Frames[^1].Positions
                .OrderBy(p => int.Parse(p.AgentId.Split('-')[1]))
                .Select(p => p.SiteId).ToList();
        }

        Assert.True(converged > 0, $"continuation chains should still demonstrate some real completions: {converged}/{total}.");
    }
}
