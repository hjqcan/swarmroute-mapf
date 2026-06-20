using SwarmRoute.Dispatch.Domain;

namespace SwarmRoute.Dispatch.Application;

/// <summary>
/// The Foundations-phase <see cref="IStationCatalog"/>: an immutable, in-memory snapshot of the fleet's
/// <see cref="StationDefinition"/>s keyed by <see cref="StationDefinition.StationId"/> (記憶體站點目錄). Deterministic
/// and inherently thread-safe (read-only after construction).
/// </summary>
public sealed class InMemoryStationCatalog : IStationCatalog
{
    private readonly IReadOnlyDictionary<string, StationDefinition> _byId;

    /// <summary>
    /// Builds the catalog from the given stations.
    /// </summary>
    /// <param name="stations">The stations to index. Must not contain two definitions sharing a station id.</param>
    /// <exception cref="ArgumentException">Thrown when two stations share a <see cref="StationDefinition.StationId"/>.</exception>
    public InMemoryStationCatalog(IEnumerable<StationDefinition> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);

        var byId = new Dictionary<string, StationDefinition>(StringComparer.Ordinal);
        foreach (var station in stations)
        {
            ArgumentNullException.ThrowIfNull(station);
            if (!byId.TryAdd(station.StationId, station))
                throw new ArgumentException(
                    $"Duplicate station id '{station.StationId}'.", nameof(stations));
        }

        _byId = byId;
    }

    /// <inheritdoc />
    public bool TryGet(string stationId, out StationDefinition? station)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);

        if (_byId.TryGetValue(stationId, out var found))
        {
            station = found;
            return true;
        }

        station = null;
        return false;
    }
}
