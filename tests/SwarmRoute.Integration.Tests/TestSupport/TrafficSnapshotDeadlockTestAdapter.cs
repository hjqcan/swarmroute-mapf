using SwarmRoute.Deadlock.Application.Abstractions;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;

namespace SwarmRoute.Integration.Tests.TestSupport;

/// <summary>
/// Test-scope equivalent of the host adapter that lets Deadlock scan TrafficControl's authoritative RAG
/// snapshot without depending on the web host project.
/// </summary>
public sealed class TrafficSnapshotDeadlockTestAdapter : IDeadlockSnapshotProvider
{
    private readonly ITrafficControlSnapshotProvider _traffic;

    public TrafficSnapshotDeadlockTestAdapter(ITrafficControlSnapshotProvider traffic)
        => _traffic = traffic ?? throw new ArgumentNullException(nameof(traffic));

    public Task<ResourceAllocationGraphSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_traffic.GetSnapshot());
}
