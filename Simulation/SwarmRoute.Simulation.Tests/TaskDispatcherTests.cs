using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.Simulation.Application;
using SwarmRoute.Simulation.Tests.TestSupport;
using Xunit;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for the <see cref="TaskDispatcher"/> assignment policies: <see cref="AssignmentPolicy.Random"/> keeps
/// the input pairing; <see cref="AssignmentPolicy.Optimal"/> (Hungarian) returns the minimum-total-travel matching and
/// beats the crossed random pairing; every policy returns a valid bijection of the goal pool.
/// </summary>
public sealed class TaskDispatcherTests
{
    private static long Total(RoadmapGraph g, IReadOnlyList<string> starts, IReadOnlyList<string> goals) =>
        Enumerable.Range(0, starts.Count).Sum(i => g.DistanceTo(starts[i], goals[i]) ?? 0L);

    [Fact]
    public void Random_keeps_the_input_pairing()
    {
        var g = new RoadmapGraphBuilder().Bidi("A", "B").Bidi("B", "C").Bidi("C", "D").Build();
        Assert.Equal(new[] { "C", "B" }, TaskDispatcher.Assign(["A", "D"], ["C", "B"], g, AssignmentPolicy.Random));
    }

    [Fact]
    public void Optimal_minimises_total_travel_and_beats_the_crossed_random_pairing()
    {
        // Line A-B-C-D. The random pairing A→C, D→B crosses (total 2 long hops); the optimal matching is A→B, D→C.
        var g = new RoadmapGraphBuilder().Bidi("A", "B").Bidi("B", "C").Bidi("C", "D").Build();
        var starts = new[] { "A", "D" };
        var pool = new[] { "C", "B" };

        var optimal = TaskDispatcher.Assign(starts, pool, g, AssignmentPolicy.Optimal);
        var random = TaskDispatcher.Assign(starts, pool, g, AssignmentPolicy.Random);

        Assert.Equal(new[] { "B", "C" }, optimal);                          // A→B, D→C
        Assert.True(Total(g, starts, optimal) < Total(g, starts, random),   // strictly cheaper than the cross pairing
            $"optimal {Total(g, starts, optimal)} should beat random {Total(g, starts, random)}");
    }

    [Theory]
    [InlineData(AssignmentPolicy.Nearest)]
    [InlineData(AssignmentPolicy.Optimal)]
    public void Every_policy_returns_a_bijection_of_the_goal_pool(AssignmentPolicy policy)
    {
        var g = new RoadmapGraphBuilder().Bidi("A", "B").Bidi("B", "C").Bidi("C", "D").Bidi("D", "E").Bidi("E", "F").Build();
        var starts = new[] { "A", "B", "C" };
        var pool = new[] { "F", "E", "D" };

        var goals = TaskDispatcher.Assign(starts, pool, g, policy);

        Assert.Equal(
            pool.OrderBy(x => x, StringComparer.Ordinal),
            goals.OrderBy(x => x, StringComparer.Ordinal)); // a permutation of the pool — every goal assigned once
    }
}
