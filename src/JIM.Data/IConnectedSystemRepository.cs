using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Data
{
    public interface IConnectedSystemRepository
    {
        public IList<ConnectedSystem> GetConnectedSystems();
        public ConnectedSystem? GetConnectedSystem(Guid id);
        public IList<SyncRun>? GetSynchronisationRuns(Guid id);
        public IList<ConnectedSystemAttribute>? GetAttributes(Guid id);
        public IList<ConnectedSystemObjectType>? GetObjectTypes(Guid id);
        public ConnectedSystemObject? GetConnectedSystemObject(Guid connectedSystemId, Guid id);
        public int GetConnectedSystemObjectCount();
        public int GetConnectedSystemObjectOfTypeCount(Guid connectedSystemObjectTypeId);
        public IList<SyncRule>? GetSyncRules();
        public SyncRule? GetSyncRule(Guid id);
    }
}
