using SwarmRoute.Dispatch.Domain;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// Read-only lookup from a station id to its <see cref="StationDefinition"/> (站點目錄). The
/// <see cref="StationScheduler"/> consults it to recover the station's <see cref="StationDefinition.BlockingClosure"/>
/// — which a goal-agnostic <see cref="Contract.ServiceAdmissionRequest"/> deliberately does not carry — before
/// reserving a service window through the <see cref="Contract.IStationResourceCalendar"/>.
/// <para>
/// This is an additive Dispatch-application seam, not a frozen cross-context contract; the Host (or a scenario
/// loader) supplies the concrete catalog. The Foundations phase ships the trivial in-memory
/// <see cref="InMemoryStationCatalog"/>.
/// </para>
/// </summary>
public interface IStationCatalog
{
    /// <summary>
    /// Resolves the definition of the station identified by <paramref name="stationId"/>.
    /// </summary>
    /// <param name="stationId">The fleet-stable station id to resolve.</param>
    /// <param name="station">On success, the matching <see cref="StationDefinition"/>; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if a station with that id is known; otherwise <see langword="false"/>.</returns>
    bool TryGet(string stationId, out StationDefinition? station);
}
