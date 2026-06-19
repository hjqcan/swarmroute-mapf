using SwarmRoute.Liveness.Domain.Detection;

namespace SwarmRoute.Simulation.Tests;

/// <summary>
/// Unit tests for <see cref="StuckClusterDetector"/>: it isolates the mutually-blocking standoff components,
/// excludes a free agent and a singleton queued behind a vehicle that is itself free to move, and only fires
/// once a member crosses the stuck threshold.
/// </summary>
public sealed class StuckClusterDetectorTests
{
    private const int Threshold = 8;

    private static StuckAgentSnapshot Agent(string id, string? next, int blocked)
        => new(id, next, StuckTicks: blocked, IsCandidate: true);

    [Fact]
    public void Isolates_two_standoff_components_excluding_free_and_draining_agents()
    {
        // Two independent head-on standoffs, one free agent, and one agent queued behind a free-to-move vehicle.
        var occupant = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["C1"] = "a1", ["C2"] = "a2",   // head-on #1: a1<->a2
            ["C3"] = "a3", ["C4"] = "a4",   // head-on #2: a3<->a4
            ["C5"] = "a5",                  // a5 wants a free cell → about to advance
            ["X"] = "a6", ["Y"] = "a7",     // a6 queued behind a7; a7 wants a free cell
        };
        var fleet = new List<StuckAgentSnapshot>
        {
            Agent("a1", "C2", blocked: 10),
            Agent("a2", "C1", blocked: 10),
            Agent("a3", "C4", blocked: 10),
            Agent("a4", "C3", blocked: 10),
            Agent("a5", "C6", blocked: 0),   // C6 not occupied → free → not a candidate
            Agent("a6", "Y", blocked: 10),   // blocked by a7, but a7 is free to move → singleton, dropped
            Agent("a7", "Z", blocked: 0),    // Z not occupied → free → not a candidate
        };

        var clusters = StuckClusterDetector.Assemble(fleet, occupant, Threshold);

        Assert.Equal(2, clusters.Count);
        Assert.Equal(new HashSet<string> { "a1", "a2" }, clusters[0]); // ordered by smallest id
        Assert.Equal(new HashSet<string> { "a3", "a4" }, clusters[1]);
        Assert.DoesNotContain(clusters, c => c.Contains("a5") || c.Contains("a6") || c.Contains("a7"));
    }

    [Fact]
    public void No_cluster_until_a_member_crosses_the_threshold()
    {
        var occupant = new Dictionary<string, string>(StringComparer.Ordinal) { ["C1"] = "a1", ["C2"] = "a2" };
        var fleet = new List<StuckAgentSnapshot>
        {
            Agent("a1", "C2", blocked: 3), // mutually blocked but neither has been stuck long enough yet
            Agent("a2", "C1", blocked: 3),
        };

        Assert.Empty(StuckClusterDetector.Assemble(fleet, occupant, Threshold));
    }

    [Fact]
    public void Circular_chain_of_three_is_one_cluster()
    {
        // a1→a2→a3→a1 ring: each blocked by the next's cell.
        var occupant = new Dictionary<string, string>(StringComparer.Ordinal) { ["C1"] = "a1", ["C2"] = "a2", ["C3"] = "a3" };
        var fleet = new List<StuckAgentSnapshot>
        {
            Agent("a1", "C2", blocked: 9),
            Agent("a2", "C3", blocked: 2),
            Agent("a3", "C1", blocked: 2),
        };

        var clusters = StuckClusterDetector.Assemble(fleet, occupant, Threshold);

        Assert.Single(clusters);
        Assert.Equal(new HashSet<string> { "a1", "a2", "a3" }, clusters[0]);
    }
}
