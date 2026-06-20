namespace SwarmRoute.Dispatch.Domain;

/// <summary>
/// The disjoint sets of roadmap site ids the Dispatch layer draws endpoints from, partitioned by FMS role
/// (端點集合). A well-formed scenario assigns each vehicle a goal from one of these sets and keeps the transit
/// core itself endpoint-free.
/// </summary>
/// <param name="Workstations">Site ids that are task workstations.</param>
/// <param name="Parkings">Site ids that are parking slots.</param>
/// <param name="Buffers">Site ids that are general staging buffers.</param>
/// <param name="Chargers">Site ids that are charging / battery-swap sites.</param>
public sealed record EndpointSet(
    IReadOnlySet<string> Workstations,
    IReadOnlySet<string> Parkings,
    IReadOnlySet<string> Buffers,
    IReadOnlySet<string> Chargers)
{
    /// <summary>Site ids that are task workstations.</summary>
    public IReadOnlySet<string> Workstations { get; } = Validation.NotNull(Workstations, nameof(Workstations));

    /// <summary>Site ids that are parking slots.</summary>
    public IReadOnlySet<string> Parkings { get; } = Validation.NotNull(Parkings, nameof(Parkings));

    /// <summary>Site ids that are general staging buffers.</summary>
    public IReadOnlySet<string> Buffers { get; } = Validation.NotNull(Buffers, nameof(Buffers));

    /// <summary>Site ids that are charging / battery-swap sites.</summary>
    public IReadOnlySet<string> Chargers { get; } = Validation.NotNull(Chargers, nameof(Chargers));
}
