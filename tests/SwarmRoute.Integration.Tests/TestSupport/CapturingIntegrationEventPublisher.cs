using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;

namespace SwarmRoute.Integration.Tests.TestSupport;

/// <summary>
/// Test <see cref="IIntegrationEventPublisher"/> that records every published event for assertions, instead of
/// going to a broker. Mirrors the Deadlock context's own test double so M3-lite can assert that a
/// <c>Deadlock.Case.ResolutionRequested</c> event is produced.
/// </summary>
public sealed class CapturingIntegrationEventPublisher : IIntegrationEventPublisher
{
    private readonly List<Event> _published = [];
    private readonly object _gate = new();

    public IReadOnlyList<Event> Published
    {
        get { lock (_gate) { return _published.ToList(); } }
    }

    public Task PublishAsync(IEnumerable<Event> domainEvents, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            _published.AddRange(domainEvents);
        return Task.CompletedTask;
    }
}
