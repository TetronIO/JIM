using TIM.Models.Core;
using TIM.Models.Logic;
using TIM.Models.Staging;
using TIM.Models.Transactional;

namespace TIM.Data
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
