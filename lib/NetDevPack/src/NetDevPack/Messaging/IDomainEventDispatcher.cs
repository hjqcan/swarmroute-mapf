using System.Collections.Generic;
using System.Threading.Tasks;

namespace NetDevPack.Messaging
{
    public interface IDomainEventDispatcher
    {
        Task DispatchAsync(IEnumerable<DomainEvent> domainEvents);
        void Dispatch(IEnumerable<DomainEvent> domainEvents);
    }
}
