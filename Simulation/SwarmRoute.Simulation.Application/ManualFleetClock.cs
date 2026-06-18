using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// A discrete, externally-advanced <see cref="IFleetClock"/> for closed-loop simulation. The
/// <see cref="FleetLoopDriver"/> sets it to the current integer tick before each planning cycle, so every
/// reserved <see cref="TimeInterval"/> is expressed on the SAME axis the executor advances on (one tick = one
/// control-point hop).
/// <para>
/// This is what makes the reservation table's interval-based collision-freedom a real guarantee at execution
/// time. The production <see cref="SwarmRoute.TrafficControl.Application.Services.SystemFleetClock"/> reads
/// wall-clock milliseconds, which decouples the reservation time axis from the (tick-based) execution axis:
/// because cycles run sub-millisecond, two reservations the table considers time-separated can land on the
/// same control point on the same tick. A monotone tick clock removes that mismatch.
/// </para>
/// </summary>
public sealed class ManualFleetClock : IFleetClock
{
    /// <summary>The current fleet-clock instant — here, the simulation tick the driver last set.</summary>
    public long NowMs { get; private set; }

    /// <summary>Sets the clock to <paramref name="tick"/>; called by the driver at the start of each tick.</summary>
    public void SetTick(long tick) => NowMs = tick;
}
