using NetDevPack.Messaging;

namespace SwarmRoute.Domain.Abstractions.EventBus;

/// <summary>
/// Converts a domain <see cref="Event"/> into a plain POCO DTO for cross-boundary transport.
/// </summary>
public interface IIntegrationDtoConverter
{
    /// <summary>True when this converter can handle <paramref name="domainEvent"/>.</summary>
    bool CanConvert(Event domainEvent);

    /// <summary>Converts the domain event into its integration DTO.</summary>
    object Convert(Event domainEvent);
}
