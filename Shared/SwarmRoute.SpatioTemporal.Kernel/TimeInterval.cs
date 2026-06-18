namespace SwarmRoute.SpatioTemporal.Kernel;

/// <summary>
/// A half-open time interval <c>[StartMs, EndMs)</c> on the single, monotonic fleet clock (milliseconds).
/// This is the time dimension the v0 engine lacks and the whole reservation/SIPP model is built on.
/// Half-open semantics mean two intervals that merely touch at an endpoint (e.g. <c>[0,10)</c> and
/// <c>[10,20)</c>) do NOT overlap — a vehicle may exit a resource exactly as the next enters.
/// </summary>
/// <param name="StartMs">Inclusive start of the interval, in fleet-clock milliseconds.</param>
/// <param name="EndMs">Exclusive end of the interval, in fleet-clock milliseconds.</param>
public readonly record struct TimeInterval
{
    /// <summary>Inclusive start of the interval, in fleet-clock milliseconds.</summary>
    public long StartMs { get; }

    /// <summary>Exclusive end of the interval, in fleet-clock milliseconds.</summary>
    public long EndMs { get; }

    /// <summary>
    /// Creates a half-open interval, guarding the invariant <c>StartMs &lt;= EndMs</c>.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <paramref name="endMs"/> precedes <paramref name="startMs"/>.</exception>
    public TimeInterval(long startMs, long endMs)
    {
        if (endMs < startMs)
            throw new ArgumentException($"TimeInterval end ({endMs}) must be >= start ({startMs}).", nameof(endMs));

        StartMs = startMs;
        EndMs = endMs;
    }

    /// <summary>Duration of the interval in milliseconds (<c>EndMs - StartMs</c>); always &gt;= 0.</summary>
    public long Duration => EndMs - StartMs;

    /// <summary>
    /// True when this interval and <paramref name="other"/> share at least one instant under
    /// half-open <c>[Start, End)</c> semantics. Touching endpoints do not count as overlapping.
    /// </summary>
    public bool Overlaps(TimeInterval other) => StartMs < other.EndMs && other.StartMs < EndMs;

    /// <summary>
    /// True when instant <paramref name="t"/> falls within the half-open range, i.e. <c>StartMs &lt;= t &lt; EndMs</c>.
    /// </summary>
    public bool Contains(long t) => t >= StartMs && t < EndMs;
}
