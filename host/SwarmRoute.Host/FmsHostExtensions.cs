using Microsoft.Extensions.DependencyInjection.Extensions;
using SwarmRoute.Coordination.Application;
using SwarmRoute.Dispatch.Application;
using SwarmRoute.Dispatch.Domain;
using SwarmRoute.Dispatch.Infra.CrossCutting.IoC;
using SwarmRoute.Host.Adapters;
using SwarmRoute.Map.Domain.ValueObjects;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Services;

namespace SwarmRoute.Host;

/// <summary>
/// (FMS-V1 R2 — Host wiring) Wires the Dispatch (FMS) layer into the composition root, entirely opt-in via
/// <c>Fms:Enabled</c> (default <see langword="false"/>). When OFF the host boot is <b>byte-identical</b> to its
/// pre-FMS state: the Dispatch context is not registered, the coordination loop's
/// <see cref="IDockAdmissionController"/> falls back to the inert
/// <see cref="PassThroughDockAdmissionController"/>, and the <see cref="MapResourceTopologyAdapter"/> keeps its pure
/// roadmap-derived closure.
/// <para>
/// When ON it registers, AFTER <c>AddCoordination</c>:
/// <list type="bullet">
///   <item>the Dispatch context's services (<see cref="DispatchNativeInjectorBootStrapper"/> — the dock-admission
///     scheduler/calendar and the goal-filtering <see cref="DockAdmissionController"/> overriding the pass-through
///     default the loop binds);</item>
///   <item>a default empty <see cref="IStationCatalog"/> (<see cref="InMemoryStationCatalog"/>) unless a scenario
///     loader already supplied one — so the scheduler/controller resolve, and with no stations the admission pass is
///     a no-op;</item>
///   <item>the <see cref="MapResourceTopologyAdapter"/> re-registered with a station-closure overlay built from the
///     catalog, so reserving a station's dock control point also locks its <see cref="StationDefinition.BlockingClosure"/>
///     zone and releasing it frees the zone (停靠閉包). With an empty catalog the overlay is empty, so the topology is
///     still byte-identical.</item>
/// </list>
/// </para>
/// </summary>
public static class FmsHostExtensions
{
    /// <summary>
    /// Wires the FMS (Dispatch) layer into the host when <c>Fms:Enabled</c> is <see langword="true"/>; otherwise a
    /// no-op so the boot is byte-identical. Call this AFTER <c>AddCoordination</c> (the controller it registers
    /// overrides the loop's pass-through default) and after the Map-backed <see cref="IResourceTopology"/> is
    /// registered (it re-registers that singleton with the station-closure overlay).
    /// </summary>
    public static WebApplicationBuilder AddSwarmRouteFms(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.Configuration.GetValue("Fms:Enabled", false))
            return builder; // OFF (default) ⇒ byte-identical boot: no Dispatch, pass-through admission, pure topology.

        var services = builder.Services;

        // The Dispatch composition root: the service-window calendar, the dock-admission scheduler, and the
        // goal-filtering IDockAdmissionController (DockAdmissionController). The last overrides the inert
        // PassThroughDockAdmissionController the coordination loop binds when no FMS controller is registered, so the
        // admission gate now runs each cycle. Registered AFTER AddCoordination per the wiring contract.
        DispatchNativeInjectorBootStrapper.RegisterServices(builder);

        // A default, empty station catalog so the scheduler/controller resolve out of the box. TryAdd so a scenario
        // loader registered earlier (its own populated catalog) wins. An empty catalog ⇒ the admission pass admits
        // every goal unchanged and the topology overlay is empty, i.e. still byte-identical until stations are loaded.
        services.TryAddSingleton<IStationCatalog>(_ => new InMemoryStationCatalog([]));

        // Re-register the Map-backed topology with the station-closure overlay derived from the catalog: a map from
        // each station's dock-point control-point ref to its blocking-closure zone. Reserving the dock CP then locks
        // the whole zone, and releasing it frees the zone (grant/release stay symmetric — IResourceTopology.ClosureOf
        // drives both). Last AddSingleton wins, replacing the plain Map-backed registration; the ReservationTable
        // captures whichever IResourceTopology is resolved at construction.
        services.AddSingleton<IResourceTopology>(sp => new MapResourceTopologyAdapter(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ICoordinationGoalSource>(),
            BuildStationClosures(sp.GetRequiredService<IStationCatalog>())));

        return builder;
    }

    /// <summary>
    /// Builds the dock-point → blocking-closure overlay from the station catalog. The key is the station's dock-point
    /// control-point ref (<see cref="RoadmapGraph.SiteRef"/>, matching the topology's CP key space); the value unions
    /// the station's <see cref="StationDefinition.BlockingClosure"/> with the dock ref itself so the closure always
    /// contains its own key (the <see cref="IResourceTopology.ClosureOf"/> contract). If two stations ever share a
    /// dock point their closures are merged. Empty catalog ⇒ empty overlay ⇒ byte-identical topology.
    /// </summary>
    private static IReadOnlyDictionary<ResourceRef, IReadOnlyCollection<ResourceRef>> BuildStationClosures(
        IStationCatalog catalog)
    {
        var overlay = new Dictionary<ResourceRef, IReadOnlyCollection<ResourceRef>>();
        foreach (var station in catalog.Stations)
        {
            var dockRef = RoadmapGraph.SiteRef(station.DockPoint);
            var closure = overlay.TryGetValue(dockRef, out var existing)
                ? new HashSet<ResourceRef>(existing)
                : new HashSet<ResourceRef> { dockRef };
            closure.UnionWith(station.BlockingClosure);
            overlay[dockRef] = closure.ToArray();
        }

        return overlay;
    }
}
