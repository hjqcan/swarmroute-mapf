using System.Diagnostics.Metrics;

namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// The fleet's observability instruments (<see cref="System.Diagnostics.Metrics"/>, BCL-only — no package
/// dependency). One shared <see cref="Meter"/> named <c>SwarmRoute.Mapf</c> exposes the v0 DoD §8 metrics:
/// planning latency, reservation grant/deny/release counts, active leases, and deadlock detections. The host
/// bridges this meter to Prometheus/OTLP via OpenTelemetry; tests and non-host processes can ignore it (the
/// instruments are no-ops when nothing is listening).
/// <para>Application services call these from the Application layer (not the Domain), keeping the domain pure.</para>
/// </summary>
public static class SwarmRouteMetrics
{
    /// <summary>The shared meter name; the host subscribes OpenTelemetry to this.</summary>
    public const string MeterName = "SwarmRoute.Mapf";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Planning latency per agent plan (ms). Histogram <c>swarmroute_planning_latency_ms</c>.</summary>
    public static readonly Histogram<double> PlanningLatencyMs =
        Meter.CreateHistogram<double>("swarmroute_planning_latency_ms", unit: "ms",
            description: "Latency of a single agent plan computation.");

    /// <summary>Reservations granted. Counter <c>swarmroute_reservation_grants_total</c>.</summary>
    public static readonly Counter<long> ReservationGrants =
        Meter.CreateCounter<long>("swarmroute_reservation_grants_total",
            description: "Reservations granted (TryReserve → Granted).");

    /// <summary>Reservations denied/queued. Counter <c>swarmroute_reservation_denials_total</c>.</summary>
    public static readonly Counter<long> ReservationDenials =
        Meter.CreateCounter<long>("swarmroute_reservation_denials_total",
            description: "Reservations denied or queued (TryReserve → Blocked/Queued).");

    /// <summary>Release calls. Counter <c>swarmroute_reservation_releases_total</c>.</summary>
    public static readonly Counter<long> ReservationReleases =
        Meter.CreateCounter<long>("swarmroute_reservation_releases_total",
            description: "Resource release calls (incremental hand-back).");

    /// <summary>Deadlocks detected. Counter <c>swarmroute_deadlock_detected_total</c>.</summary>
    public static readonly Counter<long> DeadlocksDetected =
        Meter.CreateCounter<long>("swarmroute_deadlock_detected_total",
            description: "Circular-wait deadlock cycles detected.");

    /// <summary>Deadlocks resolved (victim recovered). Counter <c>swarmroute_deadlock_resolved_total</c>.</summary>
    public static readonly Counter<long> DeadlocksResolved =
        Meter.CreateCounter<long>("swarmroute_deadlock_resolved_total",
            description: "Deadlock cases resolved (victim recovered to its goal).");
}
