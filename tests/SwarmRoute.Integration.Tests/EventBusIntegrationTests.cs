using Microsoft.Extensions.DependencyInjection;
using NetDevPack.Messaging;
using SwarmRoute.Domain.Abstractions.EventBus;
using SwarmRoute.EventBus.Extensions;

namespace SwarmRoute.Integration.Tests;

public sealed class EventBusIntegrationTests
{
    private sealed class TestIntegrationEvent(string eventName) : DomainEvent(Guid.NewGuid()), IIntegrationEvent
    {
        public string EventName { get; } = eventName;

        public string Version => "v1";
    }

    private sealed class ReentrantHandler(IIntegrationEventPublisher publisher) : IIntegrationEventHandler
    {
        private int _publishedNested;

        public List<string> HandledEventNames { get; } = [];

        public bool CanHandle(Event domainEvent)
            => domainEvent is TestIntegrationEvent;

        public async Task HandleAsync(Event domainEvent, CancellationToken cancellationToken = default)
        {
            var integrationEvent = (IIntegrationEvent)domainEvent;
            HandledEventNames.Add(integrationEvent.EventName);

            if (integrationEvent.EventName != "Test.Same" ||
                Interlocked.Exchange(ref _publishedNested, 1) != 0)
                return;

            await publisher.PublishAsync(
                [
                    new TestIntegrationEvent("Test.Same"),
                    new TestIntegrationEvent("Test.Other"),
                ],
                cancellationToken);
        }
    }

    [Fact]
    public async Task InProcessPublisher_DoesNotDispatchSameEventNameReentrantly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEventBus();
        services.AddScoped<ReentrantHandler>();
        services.AddScoped<IIntegrationEventHandler>(sp =>
            sp.GetRequiredService<ReentrantHandler>());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IIntegrationEventPublisher>();
        var handler = scope.ServiceProvider.GetRequiredService<ReentrantHandler>();

        await publisher.PublishAsync([new TestIntegrationEvent("Test.Same")]);

        Assert.Equal(new[] { "Test.Same", "Test.Other" }, handler.HandledEventNames);
    }
}
