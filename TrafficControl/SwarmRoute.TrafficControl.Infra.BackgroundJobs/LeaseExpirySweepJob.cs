using Hangfire;
using Microsoft.Extensions.Logging;
using SwarmRoute.TrafficControl.Domain.Aggregates;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.TrafficControl.Infra.BackgroundJobs;

/// <summary>
/// Recurring Hangfire job that evicts expired leases from the singleton <see cref="ReservationTable"/>: any
/// lease whose window has fully elapsed at the current fleet-clock instant is removed (a safety net behind the
/// normal incremental <c>Release</c>, guaranteeing no stale hold survives a missed release). Scheduling
/// (e.g. <c>RecurringJob.AddOrUpdate</c> every few seconds) is wired in the Host.
/// </summary>
public sealed class LeaseExpirySweepJob
{
    private readonly ReservationTable _table;
    private readonly IFleetClock _clock;
    private readonly ILogger<LeaseExpirySweepJob> _logger;

    public LeaseExpirySweepJob(ReservationTable table, IFleetClock clock, ILogger<LeaseExpirySweepJob> logger)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Sweeps once at the current fleet-clock instant, evicting expired leases. Returns the count evicted.</summary>
    [AutomaticRetry(Attempts = 0)]
    public int Sweep()
    {
        var nowMs = _clock.NowMs;
        var evicted = _table.Refresh(nowMs);
        if (evicted.Count > 0)
            _logger.LogInformation("Lease sweep at {NowMs}ms evicted {Count} expired lease(s).", nowMs, evicted.Count);
        return evicted.Count;
    }
}
