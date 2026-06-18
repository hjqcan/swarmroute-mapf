namespace SwarmRoute.Domain.Abstractions.EventBus;

/// <summary>
/// Flags a domain-event class as an integration event without requiring it to implement
/// <see cref="IIntegrationEvent"/>. May coexist with the interface.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntegrationEventAttribute : Attribute
{
    /// <summary>Event name used for routing. Format: <c>BoundedContext.Aggregate.Action</c>.</summary>
    public string EventName { get; }

    /// <summary>Event schema version.</summary>
    public string Version { get; }

    public IntegrationEventAttribute(string eventName, string version = "v1")
    {
        if (string.IsNullOrWhiteSpace(eventName))
            throw new ArgumentException("Event name must not be empty.", nameof(eventName));

        EventName = eventName;
        Version = version;
    }
}
