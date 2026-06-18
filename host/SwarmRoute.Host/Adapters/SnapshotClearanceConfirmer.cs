using System.Linq;
using SwarmRoute.Deadlock.Domain.Services;
using SwarmRoute.TrafficControl.Application.Contract.Services;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Real <see cref="IClearanceConfirmer"/>: confirms a deadlock cleared by re-running detection over a FRESH
/// TrafficControl snapshot and checking the victim is no longer in ANY circular wait. Reuses the same
/// <see cref="IDeadlockDetector"/> that found the cycle, so "cleared" means exactly "the detector no longer
/// sees a cycle containing the victim" — no duplicate logic. Drives the recovery half of the loop
/// (<c>ConfirmCleared → Recover → Resolved</c>) instead of the optimistic <c>NullClearanceConfirmer</c>.
/// </summary>
public sealed class SnapshotClearanceConfirmer : IClearanceConfirmer
{
    private readonly ITrafficControlSnapshotProvider _snapshots;
    private readonly IDeadlockDetector _detector;

    public SnapshotClearanceConfirmer(
        ITrafficControlSnapshotProvider snapshots,
        IDeadlockDetector detector)
    {
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
    }

    /// <inheritdoc />
    public bool IsCleared(string victimAgentId)
    {
        if (string.IsNullOrWhiteSpace(victimAgentId))
            return true;

        var snapshot = _snapshots.GetSnapshot();
        var cycles = _detector.Detect(snapshot);
        return cycles.All(cycle => !cycle.Contains(victimAgentId));
    }
}
