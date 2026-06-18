using System.Collections.Generic;

namespace SwarmRoute.Deadlock.Application.Contract.Dtos;

/// <summary>
/// One detected circular wait: the agents involved, the chosen victim (if a resolution was requested),
/// and the avoid target suggested to the fleet.
/// </summary>
public sealed record DeadlockCycleDto
{
    /// <summary>Id of the <c>DeadlockCase</c> opened for this cycle.</summary>
    public Guid CaseId { get; init; }

    /// <summary>The agents participating in the circular wait (sorted ordinal).</summary>
    public IReadOnlyList<string> AgentIds { get; init; } = [];

    /// <summary>The victim agent chosen to yield, or <see langword="null"/> if none was requested.</summary>
    public string? VictimAgentId { get; init; }

    /// <summary>The suggested avoidance-site id for the victim, if any.</summary>
    public string? SuggestedAvoidTarget { get; init; }
}
