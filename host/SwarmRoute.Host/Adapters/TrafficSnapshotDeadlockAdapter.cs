using SwarmRoute.Deadlock.Application.Abstractions;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;

namespace SwarmRoute.Host.Adapters;

/// <summary>
/// Bridges the Deadlock context's <see cref="IDeadlockSnapshotProvider"/> (async, consumer-side seam) to
/// TrafficControl's authoritative <see cref="ITrafficControlSnapshotProvider.GetSnapshot()"/> (sync, owns the
/// live reservation state). This is the in-process realisation of the architecture's
/// "TrafficControl → Deadlock: read a consistent snapshot" seam (design §6): Deadlock stays a pure analyser
/// and reads the RAG edges from the single writer.
/// </summary>
public sealed class TrafficSnapshotDeadlockAdapter : IDeadlockSnapshotProvider
{
    private readonly ITrafficControlSnapshotProvider _traffic;

    public TrafficSnapshotDeadlockAdapter(ITrafficControlSnapshotProvider traffic)
        => _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));

    /// <inheritdoc />
    public Task<ResourceAllocationGraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_traffic.GetSnapshot());
}
