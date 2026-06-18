namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// The unified discrete time axis shared by the v1 reservation-aware planner (SIPP) and the
/// schedule-faithful executor. One graph hop spans exactly <see cref="HopMs"/> tick on the single monotonic
/// fleet clock, so a hop's space-time cell is the half-open interval <c>[t, t + HopMs)</c> and the executor's
/// per-tick advance lines up one-for-one with a planned hop.
/// <para>
/// This is the axis-unification fact at the heart of v1. v0's <c>DijkstraPathPlanner</c> instead sizes a hop's
/// interval by the edge's scaled distance (<c>round(Distance * 1000)</c>), which makes reservations behave as
/// pure spatial locks because the executor still advances one control point per tick. SIPP builds its timeline
/// at <see cref="HopMs"/> so reserved intervals and execution ticks live on the SAME axis — which is what lets
/// the schedule-faithful executor honour planned timing (back-to-back following on touching half-open
/// intervals) rather than fall back to the conservative one-tick-per-cell gate.
/// </para>
/// </summary>
public static class TimeAxis
{
    /// <summary>
    /// Duration of a single graph hop, in fleet-clock milliseconds, on the unified v1 axis. One tick.
    /// </summary>
    public const long HopMs = 1;
}
