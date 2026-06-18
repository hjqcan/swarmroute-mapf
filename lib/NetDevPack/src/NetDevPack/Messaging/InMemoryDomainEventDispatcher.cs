using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace NetDevPack.Messaging
{
    public class InMemoryDomainEventDispatcher : IDomainEventDispatcher
    {
        private readonly IServiceProvider _serviceProvider;

        public InMemoryDomainEventDispatcher(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void Dispatch(IEnumerable<DomainEvent> domainEvents)
        {
            var events = domainEvents?.ToList();
            if (events == null || events.Count == 0) return;

            foreach (var @event in events)
            {
                var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(@event.GetType());
                var handlersObj = _serviceProvider.GetService(typeof(IEnumerable<>).MakeGenericType(handlerType));
                if (handlersObj == null) continue;

                var method = handlerType.GetMethod("Handle");
                foreach (var handler in (IEnumerable<object>)handlersObj)
                {
                    var task = (Task)method.Invoke(handler, new object[] { @event });
                    task.GetAwaiter().GetResult();
                }
            }
        }

        public async Task DispatchAsync(IEnumerable<DomainEvent> domainEvents)
        {
            var events = domainEvents?.ToList();
            if (events == null || events.Count == 0) return;

            foreach (var @event in events)
            {
                var handlerType = typeof(IDomainEventHandler<>).MakeGenericType(@event.GetType());
                var handlersObj = _serviceProvider.GetService(typeof(IEnumerable<>).MakeGenericType(handlerType));
                if (handlersObj == null) continue;

                var method = handlerType.GetMethod("Handle");
                foreach (var handler in (IEnumerable<object>)handlersObj)
                {
                    var task = (Task)method.Invoke(handler, new object[] { @event });
                    await task.ConfigureAwait(false);
                }
            }
        }
    }
}
