using Hangfire;
using Microsoft.Extensions.Logging;
using SwarmRoute.TrafficControl.Domain.Aggregates;

namespace SwarmRoute.TrafficControl.Infra.BackgroundJobs;

/// <summary>
/// Recurring Hangfire job that ages contended requests on the singleton <see cref="ReservationTable"/>:
/// it increments each request's <c>HadWaitedTime</c> so long-waiting agents eventually win the right-of-way
/// tie-break (liveness / no-starvation, invariant I7), and raises <c>AllocationContendedEvent</c> so the
/// Deadlock context re-scans. Scheduling is wired in the Host.
/// </summary>
public sealed class StaleRequestEscalationJob
{
    /// <summary>Seconds of wait time credited to each contended request per escalation pass.</summary>
    public const int DefaultAgingSeconds = 1;

    private readonly ReservationTable _table;
    private readonly ILogger<StaleRequestEscalationJob> _logger;

    public StaleRequestEscalationJob(ReservationTable table, ILogger<StaleRequestEscalationJob> logger)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Runs one escalation pass, aging all contended requests by <paramref name="agingSeconds"/> and raising
    /// <c>AllocationContendedEvent</c> when any remain outstanding. Returns the number of requests aged.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public int Escalate(int agingSeconds = DefaultAgingSeconds)
    {
        var count = _table.EscalateStaleRequests(agingSeconds);
        if (count > 0)
            _logger.LogInformation("Escalated {Count} stale contended request(s) by {Seconds}s.", count, agingSeconds);
        return count;
    }
}
