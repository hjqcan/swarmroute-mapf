using System.Threading.Tasks;

namespace NetDevPack.Data
{
    /// <summary>
    /// Defines a contract for a unit of work, which is responsible for committing changes to the data store.
    /// </summary>
    public interface IUnitOfWork
    {
        Task<bool> Commit();
    }
}