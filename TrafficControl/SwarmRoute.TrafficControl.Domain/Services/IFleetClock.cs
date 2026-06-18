namespace SwarmRoute.TrafficControl.Domain.Services;

/// <summary>
/// The single, monotonic fleet clock (milliseconds) that all <c>TimeInterval</c>s are expressed against
/// (see the Kernel <c>TimeInterval</c> contract). Abstracted so background jobs and tests can drive a
/// simulated clock rather than wall time.
/// </summary>
public interface IFleetClock
{
    /// <summary>The current fleet-clock instant, in milliseconds.</summary>
    long NowMs { get; }
}
