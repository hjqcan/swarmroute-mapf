namespace SwarmRoute.Coordination.Application.Dispatch;

/// <summary>One vehicle the dispatcher controls: its current control point and its current order (if any).</summary>
public sealed class Vehicle(string id, string siteId)
{
    public string Id { get; } = id;

    /// <summary>The control point the vehicle currently occupies (its pose).</summary>
    public string SiteId { get; set; } = siteId;

    /// <summary>The order this vehicle is fulfilling, or null when idle.</summary>
    public string? OrderId { get; set; }

    /// <summary>The destination the vehicle is heading to, or null when idle.</summary>
    public string? GoalSiteId { get; set; }

    /// <summary>The assigned order's priority (drives the coordination cycle's deterministic order).</summary>
    public int Priority { get; set; }

    /// <summary>True when the vehicle has no assigned order and can take a new one.</summary>
    public bool IsIdle => OrderId is null;
}

/// <summary>
/// The fleet the autonomous dispatcher commands: a fixed set of vehicles, each tracked by pose. Thread-safe; the
/// dispatcher mutates poses/assignments under the registry's lock while status readers take snapshots.
/// </summary>
public sealed class VehicleRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Vehicle> _vehicles = new(StringComparer.Ordinal);

    /// <summary>Registers (or repositions) a vehicle at <paramref name="siteId"/>.</summary>
    public void Register(string vehicleId, string siteId)
    {
        if (string.IsNullOrWhiteSpace(vehicleId))
            throw new ArgumentException("Vehicle id must be provided.", nameof(vehicleId));
        if (string.IsNullOrWhiteSpace(siteId))
            throw new ArgumentException("Vehicle site must be provided.", nameof(siteId));
        lock (_gate)
            _vehicles[vehicleId] = new Vehicle(vehicleId, siteId.Trim());
    }

    /// <summary>Runs <paramref name="action"/> under the registry lock with the live vehicle set (for the dispatcher).</summary>
    public T Mutate<T>(Func<IReadOnlyCollection<Vehicle>, T> action)
    {
        lock (_gate)
            return action(_vehicles.Values.OrderBy(v => v.Id, StringComparer.Ordinal).ToList());
    }

    /// <summary>An immutable snapshot of (id, pose, orderId, goal) per vehicle, for status readers.</summary>
    public IReadOnlyList<(string Id, string SiteId, string? OrderId, string? GoalSiteId)> Snapshot()
    {
        lock (_gate)
            return _vehicles.Values
                .OrderBy(v => v.Id, StringComparer.Ordinal)
                .Select(v => (v.Id, v.SiteId, v.OrderId, v.GoalSiteId))
                .ToList();
    }

    /// <summary>Number of registered vehicles.</summary>
    public int Count
    {
        get { lock (_gate) return _vehicles.Count; }
    }
}
