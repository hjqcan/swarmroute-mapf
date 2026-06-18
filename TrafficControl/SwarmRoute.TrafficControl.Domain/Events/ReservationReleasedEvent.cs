using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.TrafficControl.Domain.Events;

/// <summary>
/// Integration event raised when an agent's leases are released (incrementally behind it, or all at once).
/// </summary>
public sealed class ReservationReleasedEvent : DomainEvent, IIntegrationEvent
{
    public ReservationReleasedEvent(Guid reservationTableId, string agentId, int releasedCount, bool partial)
        : base(reservationTableId)
    {
        ReservationTableId = reservationTableId;
        AgentId = agentId;
        ReleasedCount = releasedCount;
        Partial = partial;
    }

    /// <summary>The reservation table aggregate id.</summary>
    public Guid ReservationTableId { get; }

    /// <summary>The agent whose leases were released.</summary>
    public string AgentId { get; }

    /// <summary>How many leases were freed (including parent-block + interference closure).</summary>
    public int ReleasedCount { get; }

    /// <summary>True for an incremental "release behind" (drove past); false for a full release-all.</summary>
    public bool Partial { get; }

    /// <inheritdoc />
    public string EventName => "TrafficControl.Reservation.Released";

    /// <inheritdoc />
    public string Version => "v1";
}
