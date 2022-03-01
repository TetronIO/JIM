using JIM.Data;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.PostgresData
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
            using var db = new JimDbContext();
            return db.ConnectedSystems.OrderBy(x => x.Name).ToList();
        }

        public ConnectedSystem? GetConnectedSystem(int id)
        {
            using var db = new JimDbContext();
            return db.ConnectedSystems.SingleOrDefault(x => x.Id == id);
        }

        public IList<SyncRun>? GetSynchronisationRuns(int id)
        {
            using var db = new JimDbContext();
            return db.SynchronisationRuns.Where(x => x.ConnectedSystem.Id == id).OrderByDescending(x => x.Created).ToList();
        }

        public IList<ConnectedSystemAttribute>? GetAttributes(int id)
        {
            using var db = new JimDbContext();
            return db.ConnectedSystemAttributes.Where(x => x.ConnectedSystem.Id == id).OrderBy(x => x.Name).ToList();
        }

        public IList<ConnectedSystemObjectType>? GetObjectTypes(int id)
        {
            using var db = new JimDbContext();
            return db.ConnectedSystemObjectTypes.Where(x => x.ConnectedSystem.Id == id).OrderBy(x => x.Name).ToList();
        }

        public ConnectedSystemObject? GetConnectedSystemObject(int connectedSystemId, int id)
        {
            using var db = new JimDbContext();
            return db.ConnectedSystemObjects.SingleOrDefault(x => x.ConnectedSystem.Id == connectedSystemId && x.Id == id);
        }

        public IList<SyncRule>? GetSyncRules()
        {
            using var db = new JimDbContext();
            return db.SyncRules.OrderBy(x => x.Name).ToList();
        }

        public SyncRule? GetSyncRule(int id)
        {
            using var db = new JimDbContext();
            return db.SyncRules.SingleOrDefault(x => x.Id == id);
        }

        public int GetConnectedSystemObjectCount()
        {
            using var db = new JimDbContext();
            return db.ConnectedSystemObjects.Count();
        }

        public int GetConnectedSystemObjectOfTypeCount(int connectedSystemObjectTypeId)
        {
            using var db = new JimDbContext();
            return db.ConnectedSystemObjects.Where(x => x.ConnectedSystem.Id == connectedSystemObjectTypeId).Count();
        }
    }
}
