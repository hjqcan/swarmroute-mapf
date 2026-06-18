using SwarmRoute.TrafficControl.Domain.ValueObjects;
using static SwarmRoute.TrafficControl.Tests.TestHelpers;

namespace SwarmRoute.TrafficControl.Tests;

public class RightOfWayTests
{
    private readonly RightOfWay _row = RightOfWay.Default;

    [Fact]
    public void Higher_priority_wins_first()
    {
        // A has higher priority despite shorter wait and later id.
        Assert.True(_row.AHasRightOfWay(
            priorityA: 5, hadWaitedA: 0, agentA: "Z",
            priorityB: 1, hadWaitedB: 999, agentB: "A"));
    }

    [Fact]
    public void Equal_priority_longer_wait_wins()
    {
        Assert.True(_row.AHasRightOfWay(
            priorityA: 1, hadWaitedA: 50, agentA: "Z",
            priorityB: 1, hadWaitedB: 10, agentB: "A"));
    }

    [Fact]
    public void Equal_priority_and_wait_lower_id_wins_deterministically()
    {
        // "AGV-1" is ordinal-earlier than "AGV-2" -> "AGV-1" wins.
        Assert.True(_row.AHasRightOfWay(
            priorityA: 1, hadWaitedA: 10, agentA: "AGV-1",
            priorityB: 1, hadWaitedB: 10, agentB: "AGV-2"));

        Assert.False(_row.AHasRightOfWay(
            priorityA: 1, hadWaitedA: 10, agentA: "AGV-2",
            priorityB: 1, hadWaitedB: 10, agentB: "AGV-1"));
    }

    [Fact]
    public void Comparison_is_total_and_antisymmetric()
    {
        var ab = _row.Compare(1, 10, "AGV-1", 1, 10, "AGV-2");
        var ba = _row.Compare(1, 10, "AGV-2", 1, 10, "AGV-1");

        Assert.True(ab > 0);
        Assert.True(ba < 0);
        Assert.Equal(ab, -ba);
    }

    [Fact]
    public void Winner_picks_the_deterministic_request()
    {
        var r1 = new ReservationRequest("AGV-1", "S1", DateTime.UtcNow, 5, 10, T(0, 100), priority: 1);
        var r2 = new ReservationRequest("AGV-2", "S1", DateTime.UtcNow, 5, 10, T(0, 100), priority: 1);

        // Same priority/wait -> lower id wins. Stable regardless of argument order.
        Assert.Equal("AGV-1", _row.Winner(r1, r2).AgentId);
        Assert.Equal("AGV-1", _row.Winner(r2, r1).AgentId);
    }
}
