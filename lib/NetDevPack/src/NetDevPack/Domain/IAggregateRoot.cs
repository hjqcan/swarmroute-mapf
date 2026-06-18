namespace NetDevPack.Domain
{
    /// <summary>
    /// Represents an aggregate root in a domain-driven design context. Aggregate roots serve as the entry point for
    /// accessing and modifying related entities within an aggregate.
    /// </summary>
    /// <remarks>Implement this interface to identify domain entities that act as aggregate roots. Aggregate
    /// roots enforce consistency boundaries and are responsible for coordinating changes to the entities within their
    /// aggregate. Typically, only aggregate roots are referenced directly from outside the aggregate.</remarks>
    public interface IAggregateRoot { }
}