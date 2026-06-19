using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.Deadlock.Domain.ValueObjects;

namespace SwarmRoute.Deadlock.Tests;

public class VictimSelectionTests
{
    private readonly IVictimSelector _selector = new DeterministicVictimSelector();

    [Fact]
    public void SelectsSmallestAgentId_Deterministically()
    {
        var cycle = DeadlockCycle.FromAgentIds(["C", "A", "B"]);

        Assert.Equal("A", _selector.SelectVictim(cycle));
    }

    [Fact]
    public void IsStable_RegardlessOfInputOrder()
    {
        var c1 = DeadlockCycle.FromAgentIds(["agent-3", "agent-1", "agent-2"]);
        var c2 = DeadlockCycle.FromAgentIds(["agent-2", "agent-3", "agent-1"]);

        Assert.Equal(_selector.SelectVictim(c1), _selector.SelectVictim(c2));
        Assert.Equal("agent-1", _selector.SelectVictim(c1));
    }

    [Fact]
    public void OrdinalComparison_IsUsed_NotNumericOrLength()
    {
        // Ordinal: "10" < "2" (since '1' < '2'). Confirms we are not doing numeric/length-aware compare.
        var cycle = DeadlockCycle.FromAgentIds(["2", "10"]);

        Assert.Equal("10", _selector.SelectVictim(cycle));
    }

    [Fact]
    public void RepeatedCalls_ReturnSameVictim()
    {
        var cycle = DeadlockCycle.FromAgentIds(["X", "Y", "Z", "W"]);

        var first = _selector.SelectVictim(cycle);
        for (var i = 0; i < 10; i++)
            Assert.Equal(first, _selector.SelectVictim(cycle));

        Assert.Equal("W", first);
    }
}
