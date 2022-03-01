using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Data
{
    public interface IConnectedSystemRepository
    {
        public IList<ConnectedSystem> GetConnectedSystems();
        public ConnectedSystem? GetConnectedSystem(int id);
        public IList<SyncRun>? GetSynchronisationRuns(int id);
        public IList<ConnectedSystemAttribute>? GetAttributes(int id);
        public IList<ConnectedSystemObjectType>? GetObjectTypes(int id);
        public ConnectedSystemObject? GetConnectedSystemObject(int connectedSystemId, int id);
        public int GetConnectedSystemObjectCount();
        public int GetConnectedSystemObjectOfTypeCount(int connectedSystemObjectTypeId);
        public IList<SyncRule>? GetSyncRules();
        public SyncRule? GetSyncRule(int id);
    }
}
