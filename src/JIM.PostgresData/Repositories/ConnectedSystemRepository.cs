using JIM.Data.Repositories;
using JIM.Models.Logic;
using JIM.Models.Logic.Dtos;
using JIM.Models.Staging;
using JIM.Models.Staging.Dtos;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories
{
    public class ConnectedSystemRepository : IConnectedSystemRepository
    {
        private PostgresDataRepository Repository { get; }

        internal ConnectedSystemRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public async Task<IList<ConnectedSystem>> GetConnectedSystemsAsync()
        {
            return await Repository.Database.ConnectedSystems.OrderBy(x => x.Name).ToListAsync();
        }

        public async Task<IList<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync()
        {
            return await Repository.Database.ConnectedSystems.OrderBy(a => a.Name).Select(cs => new ConnectedSystemHeader
            {
                Id = cs.Id,
                Name = cs.Name,
                Description = cs.Description,
                ObjectCount = cs.Objects.Count,
                ConnectorsCount = cs.Objects.Count(q => q.MetaverseObject != null),
                PendingExportObjectsCount = cs.PendingExports.Count,
                LastUpdated = cs.LastUpdated
            }).ToListAsync();
        }

        public async Task<ConnectedSystem?> GetConnectedSystemAsync(int id)
        {
            return await Repository.Database.ConnectedSystems.SingleOrDefaultAsync(x => x.Id == id);
        }

        public async Task<IList<SyncRun>?> GetSynchronisationRunsAsync(int id)
        {
            return await Repository.Database.SyncRuns.Where(x => x.ConnectedSystem.Id == id).OrderByDescending(x => x.Created).ToListAsync();
        }

        public async Task<IList<ConnectedSystemAttribute>?> GetAttributesAsync(int id)
        {
            return await Repository.Database.ConnectedSystemAttributes.Where(x => x.ConnectedSystem.Id == id).OrderBy(x => x.Name).ToListAsync();
        }

        public async Task<IList<ConnectedSystemObjectType>?> GetObjectTypesAsync(int id)
        {
            return await Repository.Database.ConnectedSystemObjectTypes.Where(x => x.ConnectedSystem.Id == id).OrderBy(x => x.Name).ToListAsync();
        }

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, int id)
        {
            return await Repository.Database.ConnectedSystemObjects.SingleOrDefaultAsync(x => x.ConnectedSystem.Id == connectedSystemId && x.Id == id);
        }

        public async Task<IList<SyncRule>> GetSyncRulesAsync()
        {
            return await Repository.Database.SyncRules.OrderBy(x => x.Name).ToListAsync();
        }

        public async Task<IList<SyncRuleHeader>> GetSyncRuleHeadersAsync()
        {
            return await Repository.Database.SyncRules.OrderBy(a => a.Name).Select(sr => new SyncRuleHeader
            {
                Id = sr.Id,
                Name = sr.Name,
                ConnectedSystemName = sr.ConnectedSystem.Name,
                ConnectedSystemObjectTypeName = sr.ConnectedSystemObjectType.Name,
                Created = sr.Created,
                Direction = sr.Direction,
                MetaverseObjectTypeName = sr.MetaverseObjectType.Name,
                ProjectToMetaverse = sr.ProjectToMetaverse,
                ProvisionToConnectedSystem = sr.ProvisionToConnectedSystem
            }).ToListAsync();
        }

        public async Task<SyncRule?> GetSyncRuleAsync(int id)
        {
            return await Repository.Database.SyncRules.SingleOrDefaultAsync(x => x.Id == id);
        }

        public async Task<int> GetConnectedSystemObjectCountAsync()
        {
            return await Repository.Database.ConnectedSystemObjects.CountAsync();
        }

        public async Task<int> GetConnectedSystemObjectOfTypeCountAsync(int connectedSystemObjectTypeId)
        {
            return await Repository.Database.ConnectedSystemObjects.Where(x => x.ConnectedSystem.Id == connectedSystemObjectTypeId).CountAsync();
        }

        public async Task<List<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync()
        {
            return await Repository.Database.ConnectorDefinitions.OrderBy(x => x.Name).Select(cd => new ConnectorDefinitionHeader
            {
                Id = cd.Id,
                Name = cd.Name,
                Created = cd.Created,
                LastUpdated = cd.LastUpdated,
                Description = cd.Description,
                BuiltIn = cd.BuiltIn,
                Files = cd.Files.Count,
                InUse = cd.ConnectedSystems != null && cd.ConnectedSystems.Count > 0,
                Versions = string.Join(", ", cd.Files.Select(q => q.Version).Distinct())
            }).ToListAsync();
        }

        public async Task<ConnectorDefinition?> GetConnectorDefinitionAsync(int id)
        {
            return await Repository.Database.ConnectorDefinitions.Include(x => x.Files).SingleOrDefaultAsync(cd => cd.Id == id);
        }
    }
}
