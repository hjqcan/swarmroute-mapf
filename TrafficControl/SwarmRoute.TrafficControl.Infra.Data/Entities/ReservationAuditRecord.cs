namespace SwarmRoute.TrafficControl.Infra.Data.Entities;

/// <summary>
/// A persistence-only snapshot / audit row for the reservation table. The authoritative live state is the
/// in-memory <c>ReservationTable</c> aggregate (ADR-002 / R2): EF is used <b>only</b> to persist periodic
/// snapshots (for crash recovery) and an audit trail of allocation outcomes — never on the hot path. This is
/// deliberately a flat record, not a mapped domain aggregate, so the reservation hot path takes no EF
/// dependency.
/// </summary>
public sealed class ReservationAuditRecord
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>The reservation table aggregate id this row belongs to.</summary>
    public Guid ReservationTableId { get; set; }

    /// <summary>The reservation table's optimistic-concurrency version at snapshot time.</summary>
    public long StateVersion { get; set; }

    /// <summary>The agent the audited action concerned.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>The audited action / outcome (e.g. <c>Granted</c>, <c>Queued</c>, <c>Released</c>).</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Number of leases active (snapshot) or affected (audit) at the time.</summary>
    public int LeaseCount { get; set; }

    /// <summary>A JSON blob of the serialized leases for a full snapshot (nullable for slim audit rows).</summary>
    public string? LeasesJson { get; set; }

    /// <summary>UTC timestamp of the row.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
}
