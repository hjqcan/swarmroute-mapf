using SwarmRoute.Dispatch.Domain.Shared;
using SwarmRoute.SpatioTemporal.Kernel;

namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// Immutable definition of an FMS station: the dock point a vehicle occupies while in service, the pre-dock
/// buffers it may queue in, and the <see cref="BlockingClosure"/> of roadmap resources that must be free for the
/// duration of a service window (站點定義).
/// <para>
/// Per ADR-F2 a station is modelled with the existing frozen Kernel vocabulary — a long lease over the
/// <see cref="DockPoint"/> control point plus a lease over the <see cref="BlockingClosure"/> zone — rather than a
/// new <c>ResourceKind.Station</c>. The closure is the set of <see cref="ResourceRef"/>s whose occupation by the
/// servicing vehicle would conflict with transit traffic; for a <see cref="StationType.HardBlocking"/> station it
/// spans the severed transit core.
/// </para>
/// </summary>
/// <param name="StationId">Opaque, fleet-stable identifier of the station.</param>
/// <param name="DockPoint">The control-point id the vehicle occupies while docked and in service.</param>
/// <param name="PreDockBuffers">Ordered buffer site ids upstream of the dock point where vehicles may queue for admission.</param>
/// <param name="BlockingClosure">The set of roadmap resources that must be free for the whole service window.</param>
/// <param name="ServiceDurationMs">Nominal service duration in fleet-clock milliseconds; must be &gt; 0.</param>
/// <param name="StationType">How much of the transit topology the station occupies while in service.</param>
public sealed record StationDefinition(
    string StationId,
    string DockPoint,
    IReadOnlyList<string> PreDockBuffers,
    IReadOnlySet<ResourceRef> BlockingClosure,
    long ServiceDurationMs,
    StationType StationType)
{
    /// <summary>Opaque, fleet-stable identifier of the station.</summary>
    public string StationId { get; } = Validation.NotNullOrWhiteSpace(StationId, nameof(StationId));

    /// <summary>The control-point id the vehicle occupies while docked and in service.</summary>
    public string DockPoint { get; } = Validation.NotNullOrWhiteSpace(DockPoint, nameof(DockPoint));

    /// <summary>Ordered buffer site ids upstream of the dock point where vehicles may queue for admission.</summary>
    public IReadOnlyList<string> PreDockBuffers { get; } =
        Validation.NotNull(PreDockBuffers, nameof(PreDockBuffers));

    /// <summary>The set of roadmap resources that must be free for the whole service window.</summary>
    public IReadOnlySet<ResourceRef> BlockingClosure { get; } =
        Validation.NotNull(BlockingClosure, nameof(BlockingClosure));

    /// <summary>Nominal service duration in fleet-clock milliseconds; always &gt; 0.</summary>
    public long ServiceDurationMs { get; } =
        Validation.Positive(ServiceDurationMs, nameof(ServiceDurationMs));
}
