using System.Collections.Generic;
using System.Linq;
using Hangfire;
using Microsoft.Extensions.Logging;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.TrafficControl.Domain.Aggregates;

namespace SwarmRoute.TrafficControl.Infra.BackgroundJobs;

/// <summary>
/// Recurring Hangfire job that ages contended requests on the singleton <see cref="ReservationTable"/>:
/// it increments each request's <c>HadWaitedTime</c> so long-waiting agents eventually win the right-of-way
/// tie-break (liveness / no-starvation, invariant I7), and raises <c>AllocationContendedEvent</c> so the
/// Deadlock context re-scans.
/// </summary>
public sealed class StaleRequestEscalationJob
{
    /// <summary>Seconds of wait time credited to each contended request per escalation pass.</summary>
    public const int DefaultAgingSeconds = 1;

    private readonly ReservationTable _table;
    private readonly ILogger<StaleRequestEscalationJob> _logger;
    private readonly IIntegrationEventPublisher? _publisher;

    public StaleRequestEscalationJob(
        ReservationTable table,
        ILogger<StaleRequestEscalationJob> logger,
        IIntegrationEventPublisher? publisher = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _publisher = publisher;
    }

    /// <summary>
    /// Ages all contended requests by <paramref name="agingSeconds"/> (raising <c>AllocationContendedEvent</c>
    /// on the table when any remain outstanding). Returns the number of requests aged. Pure domain mutation —
    /// does NOT publish (so unit tests can inspect the buffered events); the scheduled entry point is
    /// <see cref="RunAsync"/>.
    /// </summary>
    public int Escalate(int agingSeconds = DefaultAgingSeconds)
    {
        var count = _table.EscalateStaleRequests(agingSeconds);
        if (count > 0)
            _logger.LogInformation("Escalated {Count} stale contended request(s) by {Seconds}s.", count, agingSeconds);
        return count;
    }

    /// <summary>
    /// The recurring-job entry point: escalates, then DRAINS and PUBLISHES the buffered integration events so
    /// the Deadlock context's <c>Allocation.Contended</c> subscriber actually re-scans. (Without this drain the
    /// escalation events would accumulate on the table and never reach the bus — the v0 publish gap.)
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Escalate();

        if (_publisher is null)
            return;

        var events = _table.DomainEvents?.ToList() ?? new List<Event>();
        _table.ClearDomainEvents();
        if (events.Count > 0)
            await _publisher.PublishAsync(events, cancellationToken).ConfigureAwait(false);
    }
}
