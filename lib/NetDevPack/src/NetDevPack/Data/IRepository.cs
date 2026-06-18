using System;
using NetDevPack.Domain;

namespace NetDevPack.Data
{
    /// <summary>
    /// Base repository interface for managing aggregate roots in a data store.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public interface IRepository<T> : IDisposable where T : IAggregateRoot
    {
        IUnitOfWork UnitOfWork { get; }
    }
}