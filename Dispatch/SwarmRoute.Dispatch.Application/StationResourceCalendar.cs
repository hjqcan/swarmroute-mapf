using SwarmRoute.Dispatch.Application.Contract;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Application.Contract.Services;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The Foundations-phase <see cref="IStationResourceCalendar"/>: realises a station service window as a long
/// interval-lease over the station's dock-point control point plus its
/// <see cref="StationDefinition.BlockingClosure"/>, on top of the frozen TrafficControl reservation seam
/// (<see cref="ITrafficCoordinatorAppService"/>) — no <c>ResourceKind.Station</c>, per ADR-F2.
/// <para>
/// A window <c>[StartMs, EndMs)</c> is expressed as a <see cref="SpaceTimePath"/> whose cells are the dock-point
/// CP (<c>ResourceRef(<see cref="ResourceKind.CP"/>, station.DockPoint)</c>) and one cell per
/// <see cref="ResourceRef"/> in the blocking closure, every cell carrying the same window; the path is reserved
/// all-or-nothing through <see cref="ITrafficCoordinatorAppService.TryReserveAsync"/>. Release is the
/// closure-symmetric <see cref="ITrafficCoordinatorAppService.ReleaseAsync"/> over the same resources.
/// </para>
/// <para>
/// Deterministic and thread-safe: all reads and writes of the in-memory grant ledger are guarded by a single
/// private lock. The ledger is a best-effort overlap index used only by the cheap
/// <see cref="CanReserveServiceWindow"/> pre-check — the <b>authoritative</b> free/contended verdict is always the
/// reservation table's via <see cref="TryReserveServiceWindowAsync"/>.
/// </para>
/// </summary>
public sealed class StationResourceCalendar : IStationResourceCalendar
{
    private readonly ITrafficCoordinatorAppService _coordinator;
    private readonly object _sync = new();

    /// <summary>
    /// Per-station ledger of currently-held windows. The value records both the agent and the exact resources
    /// that were reserved, so <see cref="ReleaseServiceWindowAsync"/> can free precisely the closure that was held
    /// (closure-symmetric release) without re-deriving it.
    /// </summary>
    private readonly Dictionary<string, List<GrantedWindow>> _grantsByStation =
        new(StringComparer.Ordinal);

    /// <summary>Creates the calendar over the frozen TrafficControl write seam.</summary>
    /// <param name="coordinator">The reservation write seam the windows are planned through.</param>
    public StationResourceCalendar(ITrafficCoordinatorAppService coordinator)
        => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <inheritdoc />
    /// <remarks>
    /// A cheap, purely in-memory pre-check: reports <see langword="false"/> when <paramref name="window"/> overlaps
    /// (half-open <see cref="TimeInterval.Overlaps"/>) a window this calendar has already granted on
    /// <paramref name="stationId"/>. It does not consult the reservation table, so traffic booked by other paths is
    /// invisible here — the authoritative check is <see cref="TryReserveServiceWindowAsync"/>.
    /// </remarks>
    public bool CanReserveServiceWindow(string stationId, TimeInterval window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);

        lock (_sync)
        {
            return !OverlapsExistingGrant(stationId, window);
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryReserveServiceWindowAsync(
        StationDefinition station,
        string agentId,
        TimeInterval window,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var resources = BuildResourceSet(station);
        var path = BuildServiceWindowPath(resources, window);

        var outcome = await _coordinator.TryReserveAsync(path, agentId, ct).ConfigureAwait(false);
        if (outcome != AllocationOutcome.Granted)
            return false;

        lock (_sync)
        {
            if (!_grantsByStation.TryGetValue(station.StationId, out var grants))
            {
                grants = [];
                _grantsByStation[station.StationId] = grants;
            }

            grants.Add(new GrantedWindow(agentId, window, resources));
        }

        return true;
    }

    /// <inheritdoc />
    public async Task ReleaseServiceWindowAsync(
        string stationId,
        string agentId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // Snapshot + drop the agent's ledger entries for this station under the lock; release on the seam outside it.
        List<IReadOnlyList<ResourceRef>>? toRelease = null;
        lock (_sync)
        {
            if (_grantsByStation.TryGetValue(stationId, out var grants))
            {
                for (var i = grants.Count - 1; i >= 0; i--)
                {
                    if (!string.Equals(grants[i].AgentId, agentId, StringComparison.Ordinal))
                        continue;

                    (toRelease ??= []).Add(grants[i].Resources);
                    grants.RemoveAt(i);
                }

                if (grants.Count == 0)
                    _grantsByStation.Remove(stationId);
            }
        }

        // Idempotent: releasing a station the agent does not hold is a no-op.
        if (toRelease is null)
            return;

        foreach (var resources in toRelease)
            await _coordinator.ReleaseAsync(agentId, resources, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The ordered resource set of a station service window: the dock-point CP first, then every
    /// <see cref="StationDefinition.BlockingClosure"/> member. The same set is reserved (as a
    /// <see cref="SpaceTimePath"/>) and later released (closure-symmetric).
    /// </summary>
    private static IReadOnlyList<ResourceRef> BuildResourceSet(StationDefinition station)
    {
        var dockPoint = new ResourceRef(ResourceKind.CP, station.DockPoint);

        var resources = new List<ResourceRef>(station.BlockingClosure.Count + 1) { dockPoint };
        foreach (var member in station.BlockingClosure)
        {
            // The dock point may also appear in the closure; reserve it once (a path with a self-overlapping
            // duplicate cell would conflict with itself).
            if (member != dockPoint)
                resources.Add(member);
        }

        return resources;
    }

    /// <summary>
    /// Builds the <see cref="SpaceTimePath"/> for a service window: one <see cref="SpaceTimeCell"/> per resource,
    /// every cell carrying the whole <paramref name="window"/>.
    /// </summary>
    private static SpaceTimePath BuildServiceWindowPath(IReadOnlyList<ResourceRef> resources, TimeInterval window)
    {
        var cells = new List<SpaceTimeCell>(resources.Count);
        foreach (var resource in resources)
            cells.Add(new SpaceTimeCell(resource, window));

        return new SpaceTimePath(cells);
    }

    /// <summary>Must be called under <see cref="_sync"/>.</summary>
    private bool OverlapsExistingGrant(string stationId, TimeInterval window)
    {
        if (!_grantsByStation.TryGetValue(stationId, out var grants))
            return false;

        foreach (var grant in grants)
        {
            if (grant.Window.Overlaps(window))
                return true;
        }

        return false;
    }

    /// <summary>A live grant: the holding agent, its window, and the exact resources held (for symmetric release).</summary>
    private readonly record struct GrantedWindow(
        string AgentId,
        TimeInterval Window,
        IReadOnlyList<ResourceRef> Resources);
}
