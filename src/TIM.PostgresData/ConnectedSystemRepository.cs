using TIM.Data;
using TIM.Models.Core;
using TIM.Models.Logic;
using TIM.Models.Staging;
using TIM.Models.Transactional;

namespace TIM.PostgresData
{
    public class ConnectedSystemRepository : IConnectedSystemRepository
    {
        private PostgresDataRepository Repository { get; }

        internal ConnectedSystemRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public IList<ConnectedSystem> GetConnectedSystems()
        {
            using var db = new TimDbContext();
            return db.ConnectedSystems.OrderBy(x => x.Name).ToList();
        }

        public ConnectedSystem? GetConnectedSystem(Guid id)
        {
            using var db = new TimDbContext();
            return db.ConnectedSystems.SingleOrDefault(x => x.Id == id);
        }

        public IList<SyncRun>? GetSynchronisationRuns(Guid id)
        {
            using var db = new TimDbContext();
            return db.SynchronisationRuns.Where(x => x.ConnectedSystem.Id == id).OrderByDescending(x => x.Created).ToList();
        }

        public IList<ConnectedSystemAttribute>? GetAttributes(Guid id)
        {
            using var db = new TimDbContext();
            return db.ConnectedSystemAttributes.Where(x => x.ConnectedSystem.Id == id).OrderBy(x => x.Name).ToList();
        }

        public IList<ConnectedSystemObjectType>? GetObjectTypes(Guid id)
        {
            using var db = new TimDbContext();
            return db.ConnectedSystemObjectTypes.Where(x => x.ConnectedSystem.Id == id).OrderBy(x => x.Name).ToList();
        }

        public ConnectedSystemObject? GetConnectedSystemObject(Guid connectedSystemId, Guid id)
        {
            using var db = new TimDbContext();
            return db.ConnectedSystemObjects.SingleOrDefault(x => x.ConnectedSystem.Id == connectedSystemId && x.Id == id);
        }

        public IList<SyncRule>? GetSyncRules()
        {
            using var db = new TimDbContext();
            return db.SyncRules.OrderBy(x => x.Name).ToList();
        }

        public SyncRule? GetSyncRule(Guid id)
        {
            using var db = new TimDbContext();
            return db.SyncRules.SingleOrDefault(x => x.Id == id);
        }

        public int GetConnectedSystemObjectCount()
        {
            using var db = new TimDbContext();
            return db.ConnectedSystemObjects.Count();
        }

        public int GetConnectedSystemObjectOfTypeCount(Guid connectedSystemObjectTypeId)
        {
            using var db = new TimDbContext();
            return db.ConnectedSystemObjects.Where(x => x.ConnectedSystem.Id == connectedSystemObjectTypeId).Count();
        }
    }
}
