using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Data.Repositories
{
    public interface IConnectedSystemRepository
    {
        public Task<IList<ConnectedSystem>> GetConnectedSystemsAsync();
        public Task<ConnectedSystem?> GetConnectedSystemAsync(int id);
        public Task<IList<SyncRun>?> GetSynchronisationRunsAsync(int id);
        public Task<IList<ConnectedSystemAttribute>?> GetAttributesAsync(int id);
        public Task<IList<ConnectedSystemObjectType>?> GetObjectTypesAsync(int id);
        public Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, int id);
        public Task<int> GetConnectedSystemObjectCountAsync();
        public Task<int> GetConnectedSystemObjectOfTypeCountAsync(int connectedSystemObjectTypeId);
        public Task<IList<SyncRule>?> GetSyncRulesAsync();
        public Task<SyncRule?> GetSyncRuleAsync(int id);
    }
}
