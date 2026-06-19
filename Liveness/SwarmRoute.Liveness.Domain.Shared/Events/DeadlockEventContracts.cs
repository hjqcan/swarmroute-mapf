namespace SwarmRoute.Deadlock.Domain.Shared.Events;

/// <summary>
/// Transport-agnostic carrier contracts for the Deadlock integration events, hosted in the leaf
/// <c>Domain.Shared</c> assembly so consumers in other contexts (e.g. Coordination) can read the event
/// payload type-safely WITHOUT taking a dependency on <c>Deadlock.Domain</c>. The concrete domain events
/// in <c>Deadlock.Domain.Events</c> implement these; an <c>IIntegrationEventHandler</c> can pattern-match
/// the carrier interface instead of the concrete type.
/// </summary>
public interface IDeadlockResolutionRequested
{
    /// <summary>The deadlock case id.</summary>
    Guid CaseId { get; }

    /// <summary>The agent that should yield (be routed to an avoidance site and re-planned).</summary>
    string VictimAgentId { get; }

    /// <summary>Suggested avoidance site id, or <see langword="null"/> if none was available.</summary>
    string? SuggestedAvoidTarget { get; }
}

/// <summary>Carrier for <c>Deadlock.Case.Resolved</c>: the cycle was broken and the victim recovered.</summary>
public interface IDeadlockResolved
{
    /// <summary>The deadlock case id.</summary>
    Guid CaseId { get; }

    /// <summary>The agent that yielded to break the cycle.</summary>
    string VictimAgentId { get; }
}

/// <summary>Carrier for <c>Deadlock.Case.Escalated</c>: automatic resolution was abandoned (e.g. livelock).</summary>
public interface IDeadlockEscalated
{
    /// <summary>The deadlock case id.</summary>
    Guid CaseId { get; }

    /// <summary>The agent whose resolution was abandoned.</summary>
    string VictimAgentId { get; }
}
