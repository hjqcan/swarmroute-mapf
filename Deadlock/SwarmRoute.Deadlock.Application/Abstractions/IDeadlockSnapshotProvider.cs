using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Deadlock.Application.Abstractions;

/// <summary>
/// Consumer-side seam for obtaining a consistent <see cref="ResourceAllocationGraphSnapshot"/> to scan.
/// <para>
/// Deadlock declares this so it stays buildable standalone (no compile-time dependency on
/// TrafficControl). At integration this is adapted to TrafficControl's
/// <c>ITrafficControlSnapshotProvider</c>. A <c>NullDeadlockSnapshotProvider</c> (empty snapshot) is
/// provided for standalone builds/tests.
/// </para>
/// </summary>
public interface IDeadlockSnapshotProvider
{
    /// <summary>Returns the current resource-allocation snapshot to run detection over.</summary>
    Task<ResourceAllocationGraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Standalone <see cref="IDeadlockSnapshotProvider"/> returning an empty (healthy) snapshot.
/// <para>TODO(integration): replace with an adapter over TrafficControl's
/// <c>ITrafficControlSnapshotProvider</c>.</para>
/// </summary>
public sealed class NullDeadlockSnapshotProvider : IDeadlockSnapshotProvider
{
    /// <inheritdoc />
    public Task<ResourceAllocationGraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ResourceAllocationGraphSnapshot([], []));
}
