using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.TrafficControl.Domain.Events;

/// <summary>
/// Integration event raised when allocation is contended — a request is queued waiting on a resource held by
/// another agent. This is the trigger the Deadlock context subscribes to (per the frozen contract) to run a
/// detection cycle against the <c>ResourceAllocationGraphSnapshot</c>.
/// </summary>
public sealed class AllocationContendedEvent : DomainEvent, IIntegrationEvent
{
    public AllocationContendedEvent(Guid reservationTableId, string agentId, int contendedRequestCount)
        : base(reservationTableId)
    {
        ReservationTableId = reservationTableId;
        AgentId = agentId;
        ContendedRequestCount = contendedRequestCount;
    }

    /// <summary>The reservation table aggregate id.</summary>
    public Guid ReservationTableId { get; }

    /// <summary>The agent whose request became contended.</summary>
    public string AgentId { get; }

    /// <summary>Total number of contended ("Waits") requests now outstanding.</summary>
    public int ContendedRequestCount { get; }

    /// <inheritdoc />
    public string EventName => "TrafficControl.Allocation.Contended";

    /// <inheritdoc />
    public string Version => "v1";
}
