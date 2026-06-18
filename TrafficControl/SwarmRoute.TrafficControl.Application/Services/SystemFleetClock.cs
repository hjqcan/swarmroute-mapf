using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.TrafficControl.Application.Services;

/// <summary>
/// The default <see cref="IFleetClock"/>: fleet time = Unix epoch milliseconds (UTC). A single shared instance
/// gives the whole fleet one monotonic clock. Replaced by a simulated clock in scenario tests.
/// </summary>
public sealed class SystemFleetClock : IFleetClock
{
    /// <inheritdoc />
    public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
