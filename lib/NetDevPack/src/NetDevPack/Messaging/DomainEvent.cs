using System;

namespace NetDevPack.Messaging
{
    /// <summary>
    /// Base class for implementing Domain Events in Domain-Driven Design (DDD).
    /// </summary>
    public abstract class DomainEvent : Event
    {
        protected DomainEvent(Guid aggregateId)
        {
            AggregateId = aggregateId;
        }
    }
}
