using System.Threading.Tasks;

namespace NetDevPack.Messaging
{
    public interface IDomainEventHandler<in TEvent> where TEvent : DomainEvent
    {
        Task Handle(TEvent @event);
    }
}
