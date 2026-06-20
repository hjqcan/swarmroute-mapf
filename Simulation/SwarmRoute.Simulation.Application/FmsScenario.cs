using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Map.Domain.Shared.Enums;

namespace SwarmRoute.Simulation.Application;

/// <summary>
/// What an AGV does the moment it reaches its goal control point in a closed-loop run (抵達後處置策略).
/// <para>
/// The default <see cref="Disappear"/> is the pre-FMS behaviour exactly — an arrived agent is marked done and its
/// goal CP becomes a permanent parked obstacle the rest of the fleet routes around. The FMS modes layer a station
/// service lifecycle on top: an AGV that arrives at a station <em>dock point</em> goes into service instead of
/// disappearing, and on service completion relocates to a parking slot before truly finishing.
/// </para>
/// </summary>
public enum ArrivalPolicy
{
    /// <summary>Pre-FMS behaviour: an arrived agent is done immediately and parks on its goal CP (原地消失/停泊).</summary>
    Disappear = 0,

    /// <summary>Like <see cref="Disappear"/> but stations are honoured: arriving at a station dock point starts a
    /// service (the vehicle holds the dock, in service, immovable) but on completion the vehicle still parks on the
    /// dock point rather than relocating (永久停泊於停靠點).</summary>
    PermanentPark = 1,

    /// <summary>The full M-F1 lifecycle: arriving at a station dock point starts a service; on completion the vehicle
    /// is redirected to the nearest <see cref="SiteRole.Parking"/> slot, clearing the dock for transit traffic before
    /// it finally parks (作業完成後前往停車位).</summary>
    ClearToParking = 2
}

/// <summary>
/// The FMS overlay for one closed-loop run (FMS 情境): the per-site <see cref="SiteRole"/> map, the station
/// definitions, and the <see cref="ArrivalPolicy"/> the executor applies on arrival.
/// <para>
/// This is the single opt-in switch that turns the executor's station behaviour on. When the driver is handed a
/// <see langword="null"/> <see cref="FmsScenario"/> every FMS branch in the executor is skipped, so the run is
/// byte-identical to a non-FMS run (the existing suite proves this). With a scenario, the executor:
/// </para>
/// <list type="bullet">
///   <item>holds an AGV at a station's pre-dock buffer until the dock-admission scheduler grants its service window
///     (clearance-before-service — transit AGVs pass the blocking closure first);</item>
///   <item>on dock arrival, puts the AGV <em>in service</em> as a hard immovable obstacle for the station's service
///     duration (the Phase-1 InService gate keeps it from ever being relocated);</item>
///   <item>on service completion, releases the dock and (under <see cref="ArrivalPolicy.ClearToParking"/>) routes the
///     AGV to the nearest parking slot before finishing.</item>
/// </list>
/// </summary>
/// <param name="SiteRoles">Maps a control-point id to its FMS <see cref="SiteRole"/>. Sites absent from the map are
/// treated as <see cref="SiteRole.Transit"/>. The executor uses it to recognise dock points and parking slots.</param>
/// <param name="Stations">The station definitions in play this run (dock point, pre-dock buffers, blocking closure,
/// service duration). A station's dock point should also carry <see cref="SiteRole.DockPoint"/> in
/// <see cref="SiteRoles"/>.</param>
/// <param name="Arrival">What an AGV does on reaching its goal — see <see cref="ArrivalPolicy"/>.</param>
public sealed record FmsScenario(
    IReadOnlyDictionary<string, SiteRole> SiteRoles,
    IReadOnlyList<StationDefinition> Stations,
    ArrivalPolicy Arrival)
{
    /// <summary>Maps a control-point id to its FMS role; never <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, SiteRole> SiteRoles { get; } =
        SiteRoles ?? throw new ArgumentNullException(nameof(SiteRoles));

    /// <summary>The station definitions in play this run; never <see langword="null"/>.</summary>
    public IReadOnlyList<StationDefinition> Stations { get; } =
        Stations ?? throw new ArgumentNullException(nameof(Stations));

    /// <summary>The FMS role of <paramref name="siteId"/>, or <see cref="SiteRole.Transit"/> when unmapped.</summary>
    public SiteRole RoleOf(string siteId) =>
        SiteRoles.TryGetValue(siteId, out var role) ? role : SiteRole.Transit;
}
