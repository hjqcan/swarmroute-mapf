namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// The single, monotonic fleet clock (milliseconds) that all <see cref="TimeInterval"/> values are expressed
/// against. Production maps this to Unix epoch milliseconds; tests may supply a simulated clock.
/// </summary>
public interface IFleetClock
{
    /// <summary>The current fleet-clock instant, in milliseconds.</summary>
    long NowMs { get; }
}
