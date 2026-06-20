using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Dispatch.Tests;

/// <summary>
/// FMS-V3 <see cref="PriorityTaskDispatcher"/>: the runtime transport-task dispatcher. Asserts the total order
/// (priority → earliest deadline → nearest goal → ordinal task id), null-when-empty, release gating, and
/// order-independent determinism.
/// </summary>
public sealed class PriorityTaskDispatcherTests
{
    private static readonly ITaskDispatcherShim Dispatcher = new ITaskDispatcherShim();

    /// <summary>A line graph A—B—C—D—E (unit edges, both directions) so distances grow with hops from A.</summary>
    private static RoadmapGraph Line()
        => RoadmapGraphFixtures.Directed(
            ["A", "B", "C", "D", "E"],
            ("A", "B"), ("B", "A"),
            ("B", "C"), ("C", "B"),
            ("C", "D"), ("D", "C"),
            ("D", "E"), ("E", "D"));

    private static TransportTask Task(
        string id, string goal, int priority = 0, long releaseMs = 0, long? deadlineMs = null)
        => new(id, goal, priority, releaseMs, deadlineMs);

    // ---- null / empty ------------------------------------------------------------------------------------

    [Fact]
    public void EmptyPending_ReturnsNull()
    {
        var assignment = Dispatcher.AssignNext("AGV-1", "A", [], Line());

        Assert.Null(assignment);
    }

    [Fact]
    public void SingleEligibleTask_IsAssignedToTheAgent()
    {
        var assignment = Dispatcher.AssignNext("AGV-1", "A", [Task("T1", "C")], Line());

        Assert.NotNull(assignment);
        Assert.Equal("AGV-1", assignment!.AgentId);
        Assert.Equal("T1", assignment.Task.TaskId);
        Assert.Equal("C", assignment.GoalSiteId); // convenience pass-through
    }

    // ---- ordering: priority dominates --------------------------------------------------------------------

    [Fact]
    public void HigherPriority_WinsOverEarlierDeadlineAndNearerGoal()
    {
        // T-low is nearer (B vs E) AND has an earlier deadline, but T-high out-ranks it on priority.
        var tasks = new[]
        {
            Task("T-low", goal: "B", priority: 1, deadlineMs: 10),
            Task("T-high", goal: "E", priority: 9, deadlineMs: 9_999),
        };

        var assignment = Dispatcher.AssignNext("AGV-1", "A", tasks, Line());

        Assert.Equal("T-high", assignment!.Task.TaskId);
    }

    // ---- ordering: deadline breaks equal priority --------------------------------------------------------

    [Fact]
    public void EqualPriority_EarliestDeadlineWins_OverNearerGoal()
    {
        // Same priority. T-soon has a far goal (E) but the earliest deadline; it still wins over the nearer T-late.
        var tasks = new[]
        {
            Task("T-late", goal: "B", priority: 5, deadlineMs: 5_000),
            Task("T-soon", goal: "E", priority: 5, deadlineMs: 100),
        };

        var assignment = Dispatcher.AssignNext("AGV-1", "A", tasks, Line());

        Assert.Equal("T-soon", assignment!.Task.TaskId);
    }

    [Fact]
    public void EqualPriority_DatedTask_BeatsUndatedTask_EvenIfUndatedIsNearer()
    {
        // A task with no deadline is least urgent: the dated (far) task wins over the undated (near) one.
        var tasks = new[]
        {
            Task("T-undated", goal: "B", priority: 5, deadlineMs: null),
            Task("T-dated", goal: "E", priority: 5, deadlineMs: 1_000),
        };

        var assignment = Dispatcher.AssignNext("AGV-1", "A", tasks, Line());

        Assert.Equal("T-dated", assignment!.Task.TaskId);
    }

    // ---- ordering: distance breaks equal priority+deadline -----------------------------------------------

    [Fact]
    public void EqualPriorityAndDeadline_NearestGoalWins()
    {
        // From A: B is 1 hop, D is 3 hops. Same priority + deadline => the nearer goal (B) wins.
        var tasks = new[]
        {
            Task("T-far", goal: "D", priority: 5, deadlineMs: 1_000),
            Task("T-near", goal: "B", priority: 5, deadlineMs: 1_000),
        };

        var assignment = Dispatcher.AssignNext("AGV-1", "A", tasks, Line());

        Assert.Equal("T-near", assignment!.Task.TaskId);
    }

    [Fact]
    public void GoalAtCurrentSite_HasZeroDistance_AndWins()
    {
        // The agent is already at C; the task whose goal is C is distance 0, beating the 1-hop task to B.
        var tasks = new[]
        {
            Task("T-hop", goal: "B", priority: 5, deadlineMs: 1_000),
            Task("T-here", goal: "C", priority: 5, deadlineMs: 1_000),
        };

        var assignment = Dispatcher.AssignNext("AGV-1", "C", tasks, Line());

        Assert.Equal("T-here", assignment!.Task.TaskId);
    }

    // ---- ordering: ordinal task-id is the final tie-break ------------------------------------------------

    [Fact]
    public void AllElseEqual_SmallestOrdinalTaskId_Wins()
    {
        // Identical priority, deadline, and goal (same distance) => the smaller ordinal task id wins.
        var tasks = new[]
        {
            Task("T-b", goal: "B", priority: 5, deadlineMs: 1_000),
            Task("T-a", goal: "B", priority: 5, deadlineMs: 1_000),
        };

        var assignment = Dispatcher.AssignNext("AGV-1", "A", tasks, Line());

        Assert.Equal("T-a", assignment!.Task.TaskId);
    }

    // ---- release gating ----------------------------------------------------------------------------------

    [Fact]
    public void TaskGatedByFutureRelease_IsNotEligible()
    {
        // The only task releases at 500 but the clock is 100 => nothing eligible => null.
        var assignment = Dispatcher.AssignNext(
            "AGV-1", "A", [Task("T1", "C", releaseMs: 500)], Line(), nowMs: 100);

        Assert.Null(assignment);
    }

    [Fact]
    public void OnlyReleasedTasksAreEligible_AtTheCurrentClock()
    {
        // T-early (priority 9) is still gated at now=100; T-ready (priority 1) is released and is the only candidate.
        var tasks = new[]
        {
            Task("T-early", goal: "B", priority: 9, releaseMs: 1_000),
            Task("T-ready", goal: "E", priority: 1, releaseMs: 0),
        };

        var assignment = Dispatcher.AssignNext("AGV-1", "A", tasks, Line(), nowMs: 100);

        Assert.Equal("T-ready", assignment!.Task.TaskId);
    }

    [Fact]
    public void ReleaseExactlyAtNow_IsEligible_HalfOpenIsNotApplied()
    {
        // Eligibility is nowMs >= ReleaseMs: a task releasing exactly at the clock is in.
        var assignment = Dispatcher.AssignNext(
            "AGV-1", "A", [Task("T1", "C", releaseMs: 100)], Line(), nowMs: 100);

        Assert.Equal("T1", assignment!.Task.TaskId);
    }

    // ---- unreachable goal --------------------------------------------------------------------------------

    [Fact]
    public void UnreachableGoal_SortsLast_ButReachableTaskIsStillChosen()
    {
        // "Island" is a vertex with no edges; its task is eligible but ranks behind the reachable one.
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "Island"], ("A", "B"), ("B", "A"));
        var tasks = new[]
        {
            Task("T-island", goal: "Island", priority: 5, deadlineMs: 1_000),
            Task("T-reach", goal: "B", priority: 5, deadlineMs: 1_000),
        };

        var assignment = Dispatcher.AssignNext("AGV-1", "A", tasks, graph);

        Assert.Equal("T-reach", assignment!.Task.TaskId);
    }

    [Fact]
    public void OnlyUnreachableTaskRemains_IsStillAssigned_NotNull()
    {
        // A single eligible-but-unreachable task is still returned (null is only for "no eligible task").
        var graph = RoadmapGraphFixtures.Directed(
            ["A", "B", "Island"], ("A", "B"), ("B", "A"));

        var assignment = Dispatcher.AssignNext("AGV-1", "A", [Task("T-island", "Island")], graph);

        Assert.NotNull(assignment);
        Assert.Equal("T-island", assignment!.Task.TaskId);
    }

    // ---- determinism -------------------------------------------------------------------------------------

    [Fact]
    public void Choice_IsIndependentOfInputOrder()
    {
        var graph = Line();
        var forward = new[]
        {
            Task("T1", goal: "B", priority: 5, deadlineMs: 1_000),
            Task("T2", goal: "B", priority: 5, deadlineMs: 1_000),
            Task("T3", goal: "C", priority: 5, deadlineMs: 1_000),
        };
        var reversed = new[] { forward[2], forward[1], forward[0] };

        var a = Dispatcher.AssignNext("AGV-1", "A", forward, graph);
        var b = Dispatcher.AssignNext("AGV-1", "A", reversed, graph);

        // Both orders pick T1 (nearest goal B, smallest ordinal id) — order cannot change the verdict.
        Assert.Equal("T1", a!.Task.TaskId);
        Assert.Equal(a.Task.TaskId, b!.Task.TaskId);
    }

    [Fact]
    public void RepeatedCalls_AreStable()
    {
        var graph = Line();
        var tasks = new[]
        {
            Task("T-a", goal: "D", priority: 3, deadlineMs: 2_000),
            Task("T-b", goal: "B", priority: 7, deadlineMs: 5_000),
            Task("T-c", goal: "C", priority: 7, deadlineMs: 5_000),
        };

        var first = Dispatcher.AssignNext("AGV-1", "A", tasks, graph)!.Task.TaskId;
        for (var i = 0; i < 5; i++)
            Assert.Equal(first, Dispatcher.AssignNext("AGV-1", "A", tasks, graph)!.Task.TaskId);
    }

    /// <summary>
    /// A thin shim so the tests exercise the dispatcher through the <see cref="ITaskDispatcher"/> seam (matching
    /// how the simulation consumes it) while keeping the call sites terse.
    /// </summary>
    private sealed class ITaskDispatcherShim
    {
        private readonly ITaskDispatcher _inner = new PriorityTaskDispatcher();

        public TaskAssignment? AssignNext(
            string agentId,
            string currentSiteId,
            IReadOnlyList<TransportTask> pending,
            RoadmapGraph roadmap,
            long nowMs = 0)
            => _inner.AssignNext(agentId, currentSiteId, pending, roadmap, nowMs);
    }
}
