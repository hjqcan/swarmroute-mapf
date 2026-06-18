using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.TrafficControl.Domain.Events;

/// <summary>
/// Integration event raised when a reservation could not be granted as-is (queued or blocked).
/// </summary>
public sealed class ReservationDeniedEvent : DomainEvent, IIntegrationEvent
{
    public ReservationDeniedEvent(Guid reservationTableId, string agentId, int requestedCellCount, string outcome)
        : base(reservationTableId)
    {
        ReservationTableId = reservationTableId;
        AgentId = agentId;
        RequestedCellCount = requestedCellCount;
        Outcome = outcome;
    }

    /// <summary>The reservation table aggregate id.</summary>
    public Guid ReservationTableId { get; }

    /// <summary>The agent whose reservation was denied.</summary>
    public string AgentId { get; }

    /// <summary>How many space-time cells (closure-expanded) were requested.</summary>
    public int RequestedCellCount { get; }

    /// <summary>The denial outcome (<c>Queued</c> or <c>Blocked</c>).</summary>
    public string Outcome { get; }

    /// <inheritdoc />
    public string EventName => "TrafficControl.Reservation.Denied";

    /// <inheritdoc />
    public string Version => "v1";
}
