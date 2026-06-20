namespace SwarmRoute.Dispatch.Domain.Shared;

/// <summary>
/// How much of the surrounding transit topology a station occupies while a vehicle is in service (站點阻塞型別).
/// <para>
/// This drives admission policy: a <see cref="NonBlocking"/> station can service freely, a
/// <see cref="SoftBlocking"/> station degrades nearby flow (admit with a cost / bypass check), and a
/// <see cref="HardBlocking"/> station severs the transit core for the duration of the service window, so its
/// blocking closure must be entirely free before admission is granted.
/// </para>
/// </summary>
public enum StationType
{
    /// <summary>Service occupies only the dock point; transit traffic is unaffected (非阻塞).</summary>
    NonBlocking = 0,

    /// <summary>Service degrades but does not sever nearby traffic; a bypass exists (軟阻塞).</summary>
    SoftBlocking = 1,

    /// <summary>Service severs the transit core; the whole blocking closure must be free to admit (硬阻塞).</summary>
    HardBlocking = 2
}
