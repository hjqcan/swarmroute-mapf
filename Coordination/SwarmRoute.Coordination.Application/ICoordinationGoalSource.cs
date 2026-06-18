namespace SwarmRoute.Coordination.Application;

/// <summary>
/// The current set of agent goals the lifelong loop should drive, keyed by roadmap. The Host supplies the
/// real source (dispatcher / order book); the default is empty so the loop is a safe no-op until goals exist.
/// Separated from the loop so the loop body stays testable and the goal book can be swapped without touching
/// the control logic.
/// </summary>
public interface ICoordinationGoalSource
{
    /// <summary>The roadmap whose fleet is currently being coordinated, or <see langword="null"/> when idle.</summary>
    Guid? CurrentRoadmapId { get; }

    /// <summary>A snapshot of the goals to plan/reserve this tick (empty when idle).</summary>
    IReadOnlyCollection<AgentGoal> CurrentGoals { get; }
}

/// <summary>
/// A mutable, thread-safe default <see cref="ICoordinationGoalSource"/>. The Host (or a test) calls
/// <see cref="Set"/> / <see cref="Clear"/> to feed the loop on demand; with nothing set the loop idles.
/// </summary>
public sealed class InMemoryCoordinationGoalSource : ICoordinationGoalSource
{
    private readonly object _gate = new();
    private Guid? _roadmapId;
    private IReadOnlyCollection<AgentGoal> _goals = Array.Empty<AgentGoal>();

    /// <inheritdoc />
    public Guid? CurrentRoadmapId
    {
        get { lock (_gate) { return _roadmapId; } }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AgentGoal> CurrentGoals
    {
        get { lock (_gate) { return _goals; } }
    }

    /// <summary>Sets the roadmap + goals the loop should drive (replaces any previous set).</summary>
    public void Set(Guid roadmapId, IReadOnlyCollection<AgentGoal> goals)
    {
        ArgumentNullException.ThrowIfNull(goals);
        lock (_gate)
        {
            _roadmapId = roadmapId;
            _goals = goals.ToList();
        }
    }

    /// <summary>Clears the goal book so the loop idles.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _roadmapId = null;
            _goals = Array.Empty<AgentGoal>();
        }
    }
}
