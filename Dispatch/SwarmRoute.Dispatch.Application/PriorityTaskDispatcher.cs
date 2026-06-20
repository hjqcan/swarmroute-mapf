using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The FMS-V3 default <see cref="ITaskDispatcher"/>: a pure, deterministic priority dispatcher (優先序任務派發器).
/// <para>
/// From the tasks that are <em>eligible</em> (released by <c>nowMs</c>), it picks the single best by this total
/// order, most-significant key first:
/// <list type="number">
///   <item><b>Priority</b> — higher <see cref="TransportTask.Priority"/> wins.</item>
///   <item><b>Deadline</b> — earliest <see cref="TransportTask.DeadlineMs"/> wins; a task with no deadline is
///   considered least urgent (sorts after any dated task).</item>
///   <item><b>Distance</b> — nearest goal by <see cref="RoadmapGraph.DistanceTo(string, string)"/> from the
///   agent's current site wins; an unreachable / unknown goal is considered farthest (sorts after any reachable
///   task) but is still eligible — a single best is always returned when the list is non-empty.</item>
///   <item><b>Task id</b> — the final deterministic tie-break: smaller ordinal <see cref="TransportTask.TaskId"/>
///   wins, so the choice never depends on the input order.</item>
/// </list>
/// Stateless and thread-safe; <see cref="AssignNext"/> reads <paramref name="pending"/> as a snapshot and mutates
/// nothing.
/// </para>
/// </summary>
public sealed class PriorityTaskDispatcher : ITaskDispatcher
{
    /// <inheritdoc />
    public TaskAssignment? AssignNext(
        string agentId,
        string currentSiteId,
        IReadOnlyList<TransportTask> pending,
        RoadmapGraph roadmap,
        long nowMs = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSiteId);
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(roadmap);

        TransportTask? best = null;
        var bestKey = default(RankKey);

        foreach (var task in pending)
        {
            // Eligibility: a task gated by a future release is not yet a candidate.
            if (task.ReleaseMs > nowMs)
                continue;

            var key = RankKey.For(task, currentSiteId, roadmap);
            if (best is null || key.IsBetterThan(bestKey))
            {
                best = task;
                bestKey = key;
            }
        }

        return best is null ? null : new TaskAssignment(agentId, best);
    }

    /// <summary>
    /// The comparable ranking key for one task from a fixed origin. Lower is better on every component, so the
    /// natural orientation of each field is chosen accordingly (priority is negated; "missing" deadline / distance
    /// become <see cref="long.MaxValue"/> so they lose to any concrete value).
    /// </summary>
    private readonly record struct RankKey(long NegPriority, long Deadline, long Distance, string TaskId)
    {
        public static RankKey For(TransportTask task, string fromSiteId, RoadmapGraph roadmap)
        {
            // No deadline => least urgent: sorts after any dated task.
            var deadline = task.DeadlineMs ?? long.MaxValue;

            // Same site => distance 0; unreachable / unknown goal => farthest (still eligible, just last).
            var distance =
                string.Equals(fromSiteId, task.GoalSiteId, StringComparison.Ordinal)
                    ? 0L
                    : roadmap.DistanceTo(fromSiteId, task.GoalSiteId) ?? long.MaxValue;

            return new RankKey(-(long)task.Priority, deadline, distance, task.TaskId);
        }

        /// <summary>True when this key ranks strictly ahead of <paramref name="other"/> under the total order.</summary>
        public bool IsBetterThan(RankKey other)
        {
            if (NegPriority != other.NegPriority) return NegPriority < other.NegPriority;
            if (Deadline != other.Deadline) return Deadline < other.Deadline;
            if (Distance != other.Distance) return Distance < other.Distance;
            return string.CompareOrdinal(TaskId, other.TaskId) < 0;
        }
    }
}
