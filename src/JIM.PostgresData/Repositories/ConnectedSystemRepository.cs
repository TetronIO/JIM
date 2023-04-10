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

        #region Connector Definitions
        public async Task<IList<ConnectorDefinitionHeader>> GetConnectorDefinitionHeadersAsync()
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
            return await Repository.Database.ConnectorDefinitions
                .Include(cd => cd.Files)
                .Include(cd => cd.Settings)
                .SingleOrDefaultAsync(cd => cd.Id == id);
        }

        public async Task<ConnectorDefinition?> GetConnectorDefinitionAsync(string name)
        {
            return await Repository.Database.ConnectorDefinitions.Include(x => x.Files).SingleOrDefaultAsync(cd => cd.Name.Equals(name));
        }

        public async Task CreateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition)
        {
            Repository.Database.ConnectorDefinitions.Add(connectorDefinition);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateConnectorDefinitionAsync(ConnectorDefinition connectorDefinition)
        {
            await Repository.Database.SaveChangesAsync();
        }

        public async Task DeleteConnectorDefinitionAsync(ConnectorDefinition connectorDefinition)
        {
            Repository.Database.ConnectorDefinitions.Remove(connectorDefinition);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task CreateConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile)
        {
            Repository.Database.ConnectorDefinitionFiles.Add(connectorDefinitionFile);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task DeleteConnectorDefinitionFileAsync(ConnectorDefinitionFile connectorDefinitionFile)
        {
            Repository.Database.ConnectorDefinitionFiles.Remove(connectorDefinitionFile);
            await Repository.Database.SaveChangesAsync();
        }
        #endregion

        #region Connected Systems
        public async Task<List<ConnectedSystem>> GetConnectedSystemsAsync()
        {
            return await Repository.Database.ConnectedSystems.OrderBy(x => x.Name).ToListAsync();
        }

        public async Task<List<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync()
        {
            return await Repository.Database.ConnectedSystems.Include(q => q.ConnectorDefinition).OrderBy(a => a.Name).Select(cs => new ConnectedSystemHeader
            {
                Id = cs.Id,
                Name = cs.Name,
                Description = cs.Description,
                ObjectCount = cs.Objects.Count,
                ConnectorsCount = cs.Objects.Count(q => q.MetaverseObject != null),
                PendingExportObjectsCount = cs.PendingExports.Count,
                ConnectorName = cs.ConnectorDefinition.Name,
                ConnectorId = cs.ConnectorDefinition.Id
            }).ToListAsync();
        }

        public async Task<ConnectedSystem?> GetConnectedSystemAsync(int id)
        {
            // retrieve a complex connected system object. break the query down into three parts for optimal performance.
            // (doing it in one giant include tree query will make it timeout.
            ConnectedSystem? connectedSystem = null;
            List<ConnectedSystemObjectType>? types = null;
            List<ConnectedSystemRunProfile>? runProfiles = null;
            List<ConnectedSystemPartition>? partitions = null;
            var tasks = new List<Task>
            {
                Task.Run(async () =>
                {
                    using var dbc1 = new JimDbContext();
                    connectedSystem = await dbc1.ConnectedSystems.
                    Include(cs => cs.ConnectorDefinition).
                    Include(cs => cs.SettingValues).
                    ThenInclude(sv => sv.Setting).
                    SingleOrDefaultAsync(x => x.Id == id);
                }),
                Task.Run(async () =>
                {
                    using var dbc2 = new JimDbContext();
                    runProfiles = await dbc2.ConnectedSystemRunProfiles.Include(q => q.Partition).Where(q => q.ConnectedSystem.Id == id).ToListAsync();
                }),
                Task.Run(async () =>
                {
                    using var dbc3 = new JimDbContext();
                    types = await dbc3.ConnectedSystemObjectTypes.Include(ot => ot.Attributes).Where(q => q.ConnectedSystem.Id == id).ToListAsync();
                }),
                Task.Run(async () =>
                {
                    using var dbc4 = new JimDbContext();
                    partitions = await dbc4.ConnectedSystemPartitions
                    .Include(p => p.Containers)
                    .ThenInclude(c => c.ChildContainers)
                    .ThenInclude(c => c.ChildContainers)
                    .ThenInclude(c => c.ChildContainers)
                    .ThenInclude(c => c.ChildContainers)
                    .ThenInclude(c => c.ChildContainers)
                    .ThenInclude(c => c.ChildContainers)
                    .ThenInclude(c => c.ChildContainers)
                    .ThenInclude(c => c.ChildContainers)
                    .ThenInclude(c => c.ChildContainers)
                    .ThenInclude(c => c.ChildContainers)
                    .Where(p => p.ConnectedSystem.Id == id).ToListAsync();
                })
            };

            // collect and merge data
            await Task.WhenAll(tasks);
            if (connectedSystem == null)
                return null;

            connectedSystem.RunProfiles = runProfiles;
            connectedSystem.ObjectTypes = types;
            connectedSystem.Partitions = partitions;
            return connectedSystem;
        }

        public async Task CreateConnectedSystemAsync(ConnectedSystem connectedSystem)
        {
            Repository.Database.ConnectedSystems.Add(connectedSystem);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem)
        {
            Repository.Database.Update(connectedSystem);
            await Repository.Database.SaveChangesAsync();
        }
        #endregion

        #region Connected System Objects
        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, int id)
        {
            return await Repository.Database.ConnectedSystemObjects.SingleOrDefaultAsync(x => x.ConnectedSystem.Id == connectedSystemId && x.Id == id);
        }

        public async Task<int> GetConnectedSystemObjectCountAsync()
        {
            return await Repository.Database.ConnectedSystemObjects.CountAsync();
        }

        public async Task<int> GetConnectedSystemObjectOfTypeCountAsync(int connectedSystemObjectTypeId)
        {
            return await Repository.Database.ConnectedSystemObjects.Where(x => x.ConnectedSystem.Id == connectedSystemObjectTypeId).CountAsync();
        }
        #endregion

        #region Connected System Object Types
        public async Task<IList<ConnectedSystemObjectType>?> GetObjectTypesAsync(int id)
        {
            return await Repository.Database.ConnectedSystemObjectTypes.Where(x => x.ConnectedSystem.Id == id).OrderBy(x => x.Name).ToListAsync();
        }
        #endregion

        #region Connected System Partitions
        public async Task CreateConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition)
        {
            Repository.Database.ConnectedSystemPartitions.Add(connectedSystemPartition);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task<IList<ConnectedSystemPartition>> GetConnectedSystemPartitionsAsync(ConnectedSystem connectedSystem)
        {
            return await Repository.Database.ConnectedSystemPartitions.Include(csp => csp.Containers).Where(q => q.ConnectedSystem.Id == connectedSystem.Id).ToListAsync();
        }

        public async Task DeleteConnectedSystemPartitionAsync(ConnectedSystemPartition connectedSystemPartition)
        {
            Repository.Database.ConnectedSystemPartitions.Remove(connectedSystemPartition);
            await Repository.Database.SaveChangesAsync();
        }
        #endregion

        #region Connected System Containers
        /// <summary>
        /// Used to create a top-level container (optionally with children), when the connector does not implement Partitions.
        /// If the connector implements Partitions, then use CreateConnectedSystemPartitionAsync and add the container to that.
        /// </summary>
        public async Task CreateConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer)
        {
            Repository.Database.ConnectedSystemContainers.Add(connectedSystemContainer);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task<IList<ConnectedSystemContainer>> GetConnectedSystemContainersAsync(ConnectedSystem connectedSystem)
        {
            return await Repository.Database.ConnectedSystemContainers.Where(q => q.ConnectedSystem.Id == connectedSystem.Id).ToListAsync();
        }

        public async Task DeleteConnectedSystemContainerAsync(ConnectedSystemContainer connectedSystemContainer)
        {
            Repository.Database.ConnectedSystemContainers.Remove(connectedSystemContainer);
            await Repository.Database.SaveChangesAsync();
        }
        #endregion

        #region Connected System Run Profiles
        public async Task CreateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile)
        {
            Repository.Database.ConnectedSystemRunProfiles.Add(runProfile);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task DeleteConnectedSystemRunProfileAsync(ConnectedSystemRunProfile runProfile)
        {
            Repository.Database.ConnectedSystemRunProfiles.Remove(runProfile);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateConnectedSystemRunProfileAsync(ConnectedSystemRunProfile connectedSystemRunProfile)
        {
            await Repository.Database.SaveChangesAsync();
        }

        public async Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(ConnectedSystem connectedSystem)
        {
            return await GetConnectedSystemRunProfilesAsync(connectedSystem.Id);
        }

        public async Task<List<ConnectedSystemRunProfile>> GetConnectedSystemRunProfilesAsync(int connectedSystemId)
        {
            return await Repository.Database.ConnectedSystemRunProfiles.Include(q => q.Partition).Where(q => q.ConnectedSystem.Id == connectedSystemId).ToListAsync();
        }
        #endregion

        #region Sync Rules
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
        #endregion

        #region Sync Runs
        public async Task<IList<SyncRun>?> GetSynchronisationRunsAsync(int id)
        {
            return await Repository.Database.SyncRuns.Where(x => x.ConnectedSystem.Id == id).OrderByDescending(x => x.Created).ToListAsync();
        }
        #endregion
    }
}
