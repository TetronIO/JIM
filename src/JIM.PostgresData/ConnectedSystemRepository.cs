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
            return Repository.Database.ConnectedSystems.OrderBy(x => x.Name).ToList();
        }

        public ConnectedSystem? GetConnectedSystem(int id)
        {
            return Repository.Database.ConnectedSystems.SingleOrDefault(x => x.Id == id);
        }

        public IList<SyncRun>? GetSynchronisationRuns(int id)
        {
            return Repository.Database.SynchronisationRuns.Where(x => x.ConnectedSystem.Id == id).OrderByDescending(x => x.Created).ToList();
        }

        public IList<ConnectedSystemAttribute>? GetAttributes(int id)
        {
            return Repository.Database.ConnectedSystemAttributes.Where(x => x.ConnectedSystem.Id == id).OrderBy(x => x.Name).ToList();
        }

        public IList<ConnectedSystemObjectType>? GetObjectTypes(int id)
        {
            return Repository.Database.ConnectedSystemObjectTypes.Where(x => x.ConnectedSystem.Id == id).OrderBy(x => x.Name).ToList();
        }

        public ConnectedSystemObject? GetConnectedSystemObject(int connectedSystemId, int id)
        {
            return Repository.Database.ConnectedSystemObjects.SingleOrDefault(x => x.ConnectedSystem.Id == connectedSystemId && x.Id == id);
        }

        public IList<SyncRule>? GetSyncRules()
        {
            return Repository.Database.SyncRules.OrderBy(x => x.Name).ToList();
        }

        public SyncRule? GetSyncRule(int id)
        {
            return Repository.Database.SyncRules.SingleOrDefault(x => x.Id == id);
        }

        public int GetConnectedSystemObjectCount()
        {
            return Repository.Database.ConnectedSystemObjects.Count();
        }

        public int GetConnectedSystemObjectOfTypeCount(int connectedSystemObjectTypeId)
        {
            return Repository.Database.ConnectedSystemObjects.Where(x => x.ConnectedSystem.Id == connectedSystemObjectTypeId).Count();
        }
    }
}
