namespace SwarmRoute.Domain.Abstractions.EventBus;

/// <summary>
/// Marker interface for integration events. A domain event that also implements this is published to
/// the message bus (CAP) for cross-bounded-context communication.
/// </summary>
/// <remarks>
/// Implementations must derive from <see cref="NetDevPack.Messaging.Event"/> (typically
/// <see cref="NetDevPack.Messaging.DomainEvent"/>) so the publisher can read metadata such as
/// <c>AggregateId</c> and <c>Timestamp</c>.
/// <para>Recommended shape:</para>
/// <code>
/// public class RoadmapPublishedEvent : DomainEvent, IIntegrationEvent
/// {
///     public RoadmapPublishedEvent(Guid roadmapId) : base(roadmapId) { }
///     public string EventName => "Map.Roadmap.Published";
///     public string Version => "v1";
/// }
/// </code>
/// Naming convention for <see cref="EventName"/>: <c>BoundedContext.Aggregate.Action</c>.
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>Event name used for routing. Format: <c>BoundedContext.Aggregate.Action</c>.</summary>
    string EventName { get; }

    /// <summary>Event schema version, e.g. <c>"v1"</c>, used for compatibility/evolution.</summary>
    string Version { get; }
}
