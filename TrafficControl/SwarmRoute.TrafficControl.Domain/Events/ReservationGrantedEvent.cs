using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.TrafficControl.Domain.Events;

/// <summary>
/// Integration event raised when a whole-path reservation is granted to an agent.
/// </summary>
public sealed class ReservationGrantedEvent : DomainEvent, IIntegrationEvent
{
    public ReservationGrantedEvent(Guid reservationTableId, string agentId, int leaseCount)
        : base(reservationTableId)
    {
        ReservationTableId = reservationTableId;
        AgentId = agentId;
        LeaseCount = leaseCount;
    }

    /// <summary>The reservation table aggregate id.</summary>
    public Guid ReservationTableId { get; }

    /// <summary>The agent the reservation was granted to.</summary>
    public string AgentId { get; }

    /// <summary>How many resource leases were created (whole-path closure count).</summary>
    public int LeaseCount { get; }

    /// <inheritdoc />
    public string EventName => "TrafficControl.Reservation.Granted";

    /// <inheritdoc />
    public string Version => "v1";
}
