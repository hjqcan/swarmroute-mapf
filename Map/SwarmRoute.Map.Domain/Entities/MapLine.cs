using NetDevPack.Domain;
using SwarmRoute.Map.Domain.Shared;
using SwarmRoute.Map.Domain.Shared.Enums;
using SwarmRoute.Map.Domain.ValueObjects;

namespace SwarmRoute.Map.Domain.Entities;

/// <summary>
/// A directed roadmap line / segment (路段) from <see cref="StartStationId"/> to <see cref="EndStationId"/>.
/// An entity WITHIN the <see cref="Aggregates.Roadmap"/> aggregate (not an aggregate root).
/// <para>
/// Ported from <c>AJR.MAPF.Map.MapLine</c> — fixes the original property typo <c>Distince</c> → <see cref="Distance"/>
/// and replaces the navigation references (<c>StartStation</c>/<c>EndStation</c> objects) with stable string ids.
/// </para>
/// </summary>
public class MapLine : Entity
{
    private readonly List<string> _interferenceSiteIds = new();
    private readonly List<string> _interferenceLineIds = new();

    // EF Core
    private MapLine()
    {
        LineId = string.Empty;
        StartStationId = string.Empty;
        EndStationId = string.Empty;
    }

    /// <summary>
    /// Creates a directed line. <paramref name="lineId"/> is the topology-stable key. Endpoint ids must resolve
    /// to sites in the owning roadmap (validated by <see cref="Aggregates.Roadmap"/>).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when any required id is null/whitespace or <paramref name="distance"/> is negative.</exception>
    public MapLine(
        string lineId,
        string startStationId,
        string endStationId,
        double distance,
        MapLineType lineType = MapLineType.Straight,
        MapPosition? controlPos1 = null,
        MapPosition? controlPos2 = null,
        IEnumerable<string>? interferenceSiteIds = null,
        IEnumerable<string>? interferenceLineIds = null)
    {
        if (string.IsNullOrWhiteSpace(lineId))
            throw new ArgumentException($"[{MapErrorCodes.MissingIdentifier}] Line id must not be empty.", nameof(lineId));
        if (string.IsNullOrWhiteSpace(startStationId))
            throw new ArgumentException($"[{MapErrorCodes.MissingIdentifier}] Line start station id must not be empty.", nameof(startStationId));
        if (string.IsNullOrWhiteSpace(endStationId))
            throw new ArgumentException($"[{MapErrorCodes.MissingIdentifier}] Line end station id must not be empty.", nameof(endStationId));
        if (distance < 0)
            throw new ArgumentException($"[{MapErrorCodes.NegativeDistance}] Line distance must be >= 0 (was {distance}).", nameof(distance));

        LineId = lineId.Trim();
        StartStationId = startStationId.Trim();
        EndStationId = endStationId.Trim();
        Distance = distance;
        LineType = lineType;
        ControlPos1 = controlPos1;
        ControlPos2 = controlPos2;

        if (interferenceSiteIds is not null)
            _interferenceSiteIds.AddRange(interferenceSiteIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
        if (interferenceLineIds is not null)
            _interferenceLineIds.AddRange(interferenceLineIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()));
    }

    /// <summary>Topology-stable line identifier.</summary>
    public string LineId { get; private set; }

    /// <summary>Id of the start site (graph edge source).</summary>
    public string StartStationId { get; private set; }

    /// <summary>Id of the end site (graph edge destination).</summary>
    public string EndStationId { get; private set; }

    /// <summary>Length of the line in metres; the graph edge weight is <c>Distance * 1000</c>.</summary>
    public double Distance { get; private set; }

    /// <summary>Geometry type (straight / Bézier).</summary>
    public MapLineType LineType { get; private set; }

    /// <summary>First Bézier control point (optional, typically only for <see cref="MapLineType.Bezier"/>).</summary>
    public MapPosition? ControlPos1 { get; private set; }

    /// <summary>Second Bézier control point (optional, typically only for <see cref="MapLineType.Bezier"/>).</summary>
    public MapPosition? ControlPos2 { get; private set; }

    /// <summary>Ids of sites whose footprint interferes with this line.</summary>
    public IReadOnlyCollection<string> InterferenceSiteIds => _interferenceSiteIds.AsReadOnly();

    /// <summary>Ids of lines whose footprint interferes with this line.</summary>
    public IReadOnlyCollection<string> InterferenceLineIds => _interferenceLineIds.AsReadOnly();
}
