using NetDevPack.Domain;
using SwarmRoute.SpatioTemporal.Kernel;
using SwarmRoute.TrafficControl.Domain.Shared;

namespace SwarmRoute.TrafficControl.Domain.ValueObjects;

/// <summary>
/// A detected spatio-temporal conflict between two agents over (up to) two resources. Produced by
/// <c>IConflictDetector</c> when a candidate <c>SpaceTimePath</c> clashes with the live reservations.
/// </summary>
/// <remarks>
/// For a <see cref="ConflictType.VertexSame"/> / <see cref="ConflictType.Following"/> conflict the two
/// resources are equal (the contended cell). For <see cref="ConflictType.EdgeSwap"/> the two resources are
/// the opposing lanes; for <see cref="ConflictType.Interference"/> they are the mutually-interfering pair.
/// </remarks>
public sealed class Conflict : ValueObject
{
    /// <summary>Creates a conflict of <paramref name="type"/> between the two agents over the two resources.</summary>
    /// <exception cref="ArgumentException">Thrown when an agent id is null/whitespace.</exception>
    public Conflict(
        ConflictType type,
        string agentA,
        string agentB,
        ResourceRef resourceA,
        ResourceRef resourceB)
    {
        if (string.IsNullOrWhiteSpace(agentA))
            throw new ArgumentException("Conflict agentA must be provided.", nameof(agentA));
        if (string.IsNullOrWhiteSpace(agentB))
            throw new ArgumentException("Conflict agentB must be provided.", nameof(agentB));

        Type = type;
        AgentA = agentA;
        AgentB = agentB;
        ResourceA = resourceA;
        ResourceB = resourceB;
    }

    /// <summary>The classification of the conflict.</summary>
    public ConflictType Type { get; }

    /// <summary>The candidate (incoming) agent.</summary>
    public string AgentA { get; }

    /// <summary>The incumbent (already-holding) agent.</summary>
    public string AgentB { get; }

    /// <summary>The resource <see cref="AgentA"/> wanted.</summary>
    public ResourceRef ResourceA { get; }

    /// <summary>The resource <see cref="AgentB"/> holds (equal to <see cref="ResourceA"/> for vertex/following).</summary>
    public ResourceRef ResourceB { get; }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Type;
        yield return AgentA;
        yield return AgentB;
        yield return ResourceA;
        yield return ResourceB;
    }
}
