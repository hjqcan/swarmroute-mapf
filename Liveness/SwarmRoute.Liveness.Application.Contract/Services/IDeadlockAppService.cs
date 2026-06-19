using SwarmRoute.Deadlock.Application.Contract.Dtos;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Deadlock.Application.Contract.Services;

/// <summary>
/// Application service for the Deadlock bounded context. The entry point used by Coordination /
/// TrafficControl to scan the current resource-allocation state for deadlocks and (in v0) open cases +
/// request resolution.
/// </summary>
public interface IDeadlockAppService
{
    /// <summary>
    /// Scans <paramref name="snapshot"/> for circular waits. For each cycle found it opens a
    /// <c>DeadlockCase</c>, selects a victim and requests resolution (raising the corresponding
    /// integration events), and returns a <see cref="DeadlockReportDto"/> describing what was found.
    /// Returns <see cref="DeadlockReportDto.Empty"/> when the snapshot is healthy.
    /// </summary>
    Task<DeadlockReportDto> ScanAsync(
        ResourceAllocationGraphSnapshot snapshot,
        CancellationToken cancellationToken = default);
}
