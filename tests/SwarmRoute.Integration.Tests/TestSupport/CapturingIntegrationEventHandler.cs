using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Integration.Tests.TestSupport;

/// <summary>
/// Passive in-process event-bus handler used by integration tests to observe integration events without
/// replacing the publisher or short-circuiting real subscribers.
/// </summary>
public sealed class CapturingIntegrationEventHandler : IIntegrationEventHandler
{
    private readonly List<Event> _handled = [];
    private readonly object _gate = new();

    public IReadOnlyList<Event> Handled
    {
        get { lock (_gate) { return _handled.ToList(); } }
    }

    public bool CanHandle(Event domainEvent) => domainEvent is IIntegrationEvent;

    public Task HandleAsync(Event domainEvent, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _handled.Add(domainEvent);
        return Task.CompletedTask;
    }
}
