using NetDevPack.Data;
using NetDevPack.Domain;

namespace SwarmRoute.Domain.Abstractions.Repositories;

/// <summary>
/// Base repository contract for aggregate roots, layered on top of NetDevPack's
/// <see cref="IRepository{T}"/> (which surfaces the <see cref="IUnitOfWork"/>).
/// Adapted from grukirbs' <c>IBaseRepository&lt;T&gt;</c>.
/// </summary>
/// <typeparam name="T">The aggregate-root entity type.</typeparam>
public interface IBaseRepository<T> : IRepository<T> where T : Entity, IAggregateRoot
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<T>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a queryable for complex queries and paging.</summary>
    IQueryable<T> GetQueryable();

    void Add(T model);

    void Update(T model);

    void Remove(T model);

    Task RemoveByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
