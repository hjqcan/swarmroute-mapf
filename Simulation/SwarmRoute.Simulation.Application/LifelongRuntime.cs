using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// (FMS-V3) The opt-in lifelong-dispatch runtime threaded into a closed-loop run: the runtime
/// <see cref="ITaskDispatcher"/>, the released-over-time backlog of <see cref="TransportTask"/>s, the run horizon, and
/// the mutable ledger that records each task's release / assignment / completion so the run can report continuous-operation
/// metrics (throughput, backlog wait, queue depth). When the driver is handed a <see langword="null"/>
/// <see cref="LifelongRuntime"/> every lifelong branch in the executor is skipped, so a non-lifelong run is byte-identical.
/// <para>
/// <b>Re-tasking model.</b> Each transport task is "drive to a workstation dock CP, service briefly, clear to parking".
/// The instant an AGV reaches <see cref="Dispatch.Domain.Shared.AgvMissionState.IdleParked"/> (task done + parked) the
/// executor's re-task arm calls <see cref="ITaskDispatcher.AssignNext"/> for the next eligible task and the AGV heads out
/// again — continuous operation. The dispatcher reads the backlog as a snapshot; this runtime owns mutating it.
/// </para>
/// </summary>
public sealed class LifelongRuntime
{
    private readonly ITaskDispatcher _dispatcher;
    private readonly RoadmapGraph _graph;

    /// <summary>The full set of tasks generated for the run, indexed by id (each is released at its own
    /// <see cref="TransportTask.ReleaseMs"/>). Immutable membership; the live backlog is derived from it per tick.</summary>
    private readonly IReadOnlyList<TransportTask> _allTasks;

    /// <summary>The live backlog: released-but-unassigned tasks, in ascending task-id order (the dispatcher re-ranks
    /// it). A task moves out of here the tick it is assigned.</summary>
    private readonly List<TransportTask> _backlog = new();

    /// <summary>Tasks not yet released (release tick &gt; now), ascending by release then id; drained into the backlog
    /// as the clock reaches each release.</summary>
    private readonly Queue<TransportTask> _unreleased;

    /// <summary>Per-task ledger entry: when it was released, assigned, and completed (each null until it happens).</summary>
    private sealed class TaskRecord
    {
        public long ReleaseTick;
        public long? AssignTick;
        public long? CompleteTick;
    }

    private readonly Dictionary<string, TaskRecord> _ledger = new(StringComparer.Ordinal);

    /// <summary>The task each AGV is currently executing (agent id → task id), so a completion is attributed correctly.</summary>
    private readonly Dictionary<string, string> _activeTaskByAgent = new(StringComparer.Ordinal);

    /// <summary>Maps a workstation dock CP to the station bound to it, so a re-tasked AGV can recover the station for the
    /// goal it was just handed.</summary>
    private readonly IReadOnlyDictionary<string, StationDefinition> _stationByDock;

    private int _maxQueueDepth;

    /// <summary>Creates the lifelong runtime over the chosen dispatcher, the generated task set, the dock→station index,
    /// and the run graph (the dispatcher's distance origin for goal ranking).</summary>
    public LifelongRuntime(
        ITaskDispatcher dispatcher,
        IReadOnlyList<TransportTask> tasks,
        IReadOnlyDictionary<string, StationDefinition> stationByDock,
        RoadmapGraph graph,
        long horizonTicks)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _allTasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _stationByDock = stationByDock ?? throw new ArgumentNullException(nameof(stationByDock));
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
        HorizonTicks = horizonTicks;

        _unreleased = new Queue<TransportTask>(
            _allTasks.OrderBy(t => t.ReleaseMs).ThenBy(t => t.TaskId, StringComparer.Ordinal));
        foreach (var t in _allTasks)
            _ledger[t.TaskId] = new TaskRecord { ReleaseTick = t.ReleaseMs };
    }

    /// <summary>The horizon (in ticks) the lifelong run executes to before stopping and reporting throughput.</summary>
    public long HorizonTicks { get; }

    /// <summary>Peak number of AGVs simultaneously parked (resting between tasks), sampled each tick.</summary>
    public int PeakParkedCount { get; private set; }

    /// <summary>Total tasks the run will release (the whole generated set; all are released by their release tick).</summary>
    public int TasksReleased => _allTasks.Count;

    /// <summary>
    /// Releases any task whose release tick has been reached into the live backlog and records the deepest backlog
    /// seen. Called once at the top of every tick BEFORE re-tasking, so a task released this tick is assignable now.
    /// </summary>
    public void AdvanceReleases(long nowTick)
    {
        while (_unreleased.Count > 0 && _unreleased.Peek().ReleaseMs <= nowTick)
            _backlog.Add(_unreleased.Dequeue());
        _maxQueueDepth = Math.Max(_maxQueueDepth, _backlog.Count);
    }

    /// <summary>Samples how many AGVs are parked this tick (the peak feeds parking-saturation).</summary>
    public void SampleParked(int parkedCount) => PeakParkedCount = Math.Max(PeakParkedCount, parkedCount);

    /// <summary>
    /// Asks the dispatcher for <paramref name="agentId"/>'s next task from the live backlog (origin
    /// <paramref name="currentSiteId"/>, clock <paramref name="nowTick"/>). On a hit the task leaves the backlog, the
    /// assignment is recorded, the agent is bound to it, and the assignment plus its station (recovered from the goal
    /// dock) is returned; on a miss returns <see langword="null"/> (the agent stays parked and retries next tick).
    /// </summary>
    public (TaskAssignment Assignment, StationDefinition Station)? TryAssignNext(
        string agentId, string currentSiteId, long nowTick)
    {
        if (_backlog.Count == 0)
            return null;

        var assignment = _dispatcher.AssignNext(agentId, currentSiteId, _backlog, _graph, nowTick);
        if (assignment is null)
            return null;

        // The dispatcher only ever returns a task it was given, and every lifelong task's goal is a workstation dock,
        // so the station lookup always resolves; guard defensively and skip an unmappable task rather than throw.
        if (!_stationByDock.TryGetValue(assignment.Task.GoalSiteId, out var station))
            return null;

        _backlog.RemoveAll(t => string.Equals(t.TaskId, assignment.Task.TaskId, StringComparison.Ordinal));
        _ledger[assignment.Task.TaskId].AssignTick = nowTick;
        _activeTaskByAgent[agentId] = assignment.Task.TaskId;
        return (assignment, station);
    }

    /// <summary>Records that <paramref name="agentId"/> completed its active task at <paramref name="nowTick"/> (parked
    /// after service). Idempotent and a no-op if the agent has no active task.</summary>
    public void CompleteActiveTask(string agentId, long nowTick)
    {
        if (!_activeTaskByAgent.TryGetValue(agentId, out var taskId))
            return;
        _activeTaskByAgent.Remove(agentId);
        var record = _ledger[taskId];
        record.CompleteTick ??= nowTick;
    }

    /// <summary>Assembles the run's <see cref="LifelongMetricsDto"/> from the ledger (deterministic).</summary>
    /// <param name="parkingCapacity">The number of parking slots the warehouse provides (saturation denominator).</param>
    public LifelongMetricsDto BuildMetrics(int parkingCapacity)
    {
        var completed = _ledger.Values.Where(r => r.CompleteTick.HasValue).ToList();
        var completedCount = completed.Count;

        // Backlog wait = assignment tick − release tick, over completed tasks (a task that completed was assigned).
        var waits = completed
            .Where(r => r.AssignTick.HasValue)
            .Select(r => (int)Math.Max(0, r.AssignTick!.Value - r.ReleaseTick))
            .OrderBy(w => w)
            .ToList();

        var half = HorizonTicks / 2;
        var firstHalf = completed.Count(r => r.CompleteTick!.Value <= half);
        var secondHalf = completedCount - firstHalf;

        var saturation = parkingCapacity <= 0 ? 0d : (double)PeakParkedCount / parkingCapacity;

        return new LifelongMetricsDto(
            HorizonTicks: HorizonTicks,
            TasksReleased: TasksReleased,
            TasksCompleted: completedCount,
            ThroughputPerHundredTicks: HorizonTicks <= 0 ? 0d : completedCount * 100d / HorizonTicks,
            MeanWaitTicks: waits.Count == 0 ? 0d : waits.Average(),
            P95WaitTicks: waits.Count == 0 ? 0 : Percentile(waits, 0.95),
            MaxQueueDepth: _maxQueueDepth,
            ParkingCapacity: parkingCapacity,
            PeakParkedCount: PeakParkedCount,
            ParkingSaturation: saturation,
            TasksCompletedFirstHalf: firstHalf,
            TasksCompletedSecondHalf: secondHalf);
    }

    /// <summary>Nearest-rank percentile on an ascending-sorted list (deterministic; no interpolation).</summary>
    private static int Percentile(IReadOnlyList<int> sortedAscending, double q)
    {
        var rank = (int)Math.Ceiling(q * sortedAscending.Count);
        return sortedAscending[Math.Clamp(rank - 1, 0, sortedAscending.Count - 1)];
    }
}
