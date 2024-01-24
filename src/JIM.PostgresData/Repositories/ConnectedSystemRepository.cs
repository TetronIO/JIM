using JIM.Data.Repositories;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Utility;
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

        public async Task<ConnectedSystemHeader?> GetConnectedSystemHeaderAsync(int id)
        {
            return await Repository.Database.ConnectedSystems.Include(q => q.ConnectorDefinition).Select(cs => new ConnectedSystemHeader
            {
                Id = cs.Id,
                Name = cs.Name,
                Description = cs.Description,
                ObjectCount = cs.Objects.Count,
                ConnectorsCount = cs.Objects.Count(q => q.MetaverseObject != null),
                PendingExportObjectsCount = cs.PendingExports.Count,
                ConnectorName = cs.ConnectorDefinition.Name,
                ConnectorId = cs.ConnectorDefinition.Id
            }).SingleOrDefaultAsync(cs => cs.Id == id);
        }

        public async Task<ConnectedSystem?> GetConnectedSystemAsync(int id)
        {
            // retrieve a complex connected system object. break the query down into three parts for optimal performance.
            // (doing it in one giant include tree query will make it timeout.
            List<ConnectedSystemObjectType>? types = null;
            List<ConnectedSystemRunProfile>? runProfiles = null;
            List<ConnectedSystemPartition>? partitions = null;

            var connectedSystem = await Repository.Database.ConnectedSystems.
                Include(cs => cs.ConnectorDefinition).
                Include(cs => cs.SettingValues).
                ThenInclude(sv => sv.Setting).
                SingleOrDefaultAsync(x => x.Id == id);

            if (connectedSystem == null)
                return null;

            connectedSystem = await Repository.Database.ConnectedSystems.
                Include(cs => cs.ConnectorDefinition).
                Include(cs => cs.SettingValues).
                ThenInclude(sv => sv.Setting).
                SingleOrDefaultAsync(x => x.Id == id);

            runProfiles = await Repository.Database.ConnectedSystemRunProfiles.Include(q => q.Partition).Where(q => q.ConnectedSystemId == id).ToListAsync();

            types = await Repository.Database.ConnectedSystemObjectTypes
                .Include(ot => ot.Attributes)
                .Where(q => q.ConnectedSystem.Id == id).ToListAsync();

            partitions = await Repository.Database.ConnectedSystemPartitions
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

            // collect and merge data
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
        public async Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(
            int connectedSystemId,
            int page,
            int pageSize,
            int maxResults,
            QuerySortBy querySortBy = QuerySortBy.DateCreated,
            QueryRange queryRange = QueryRange.Forever)
        {
            if (pageSize < 1)
                throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

            if (page < 1)
                page = 1;

            // limit page size to avoid increasing latency unecessarily
            if (pageSize > 100)
                pageSize = 100;

            // limit how big the id query is to avoid unnecessary charges and to keep latency within an acceptable range
            if (maxResults > 500)
                maxResults = 500;

            // todo: just get the displayname and unique identifier attribute values
            var objects = from o in Repository.Database.ConnectedSystemObjects.
                          Where(cso => cso.ConnectedSystem.Id == connectedSystemId)
                          select o;

            if (queryRange != QueryRange.Forever)
            {
                switch (queryRange)
                {
                    case QueryRange.LastYear:
                        objects = objects.Where(q => q.Created >= DateTime.UtcNow - TimeSpan.FromDays(365));
                        break;
                    case QueryRange.LastMonth:
                        objects = objects.Where(q => q.Created >= DateTime.UtcNow - TimeSpan.FromDays(30));
                        break;
                    case QueryRange.LastWeek:
                        objects = objects.Where(q => q.Created >= DateTime.UtcNow - TimeSpan.FromDays(7));
                        break;
                }
            }

            switch (querySortBy)
            {
                case QuerySortBy.DateCreated:
                    objects = objects.OrderByDescending(q => q.Created);
                    break;

                    // todo: support additional ways of sorting, i.e. by attribute value
            }

            // now just retrieve a page's worth of objects from the results
            var grossCount = objects.Count();
            var offset = (page - 1) * pageSize;
            var itemsToGet = grossCount >= pageSize ? pageSize : grossCount;
            var pagedObjects = objects.Skip(offset).Take(itemsToGet);
            var selectedObjects = pagedObjects.Select(cso => new ConnectedSystemObjectHeader
            {
                Id = cso.Id,
                ConnectedSystemId = cso.ConnectedSystemId,
                Created = cso.Created,
                DateJoined = cso.DateJoined,
                JoinType = cso.JoinType,
                LastUpdated = cso.LastUpdated,
                Status = cso.Status,
                TypeId = cso.Type.Id,
                TypeName = cso.Type.Name,
                DisplayName = cso.AttributeValues.Any(av => av.Attribute.Name.ToLower() == "displayname") ? cso.AttributeValues.Single(av => av.Attribute.Name.ToLower() == "displayname").StringValue : null,
                ExternalIdAttributeValue = cso.AttributeValues.SingleOrDefault(av => av.Attribute.Id == cso.ExternalIdAttributeId),
                SecondaryExternalIdAttributeValue = cso.AttributeValues.SingleOrDefault(av => av.Attribute.Id == cso.SecondaryExternalIdAttributeId)
            });
            var results = await selectedObjects.ToListAsync();

            // now with all the ids we know how many total results there are and so can populate paging info
            var pagedResultSet = new PagedResultSet<ConnectedSystemObjectHeader>
            {
                PageSize = pageSize,
                TotalResults = grossCount,
                CurrentPage = page,
                QuerySortBy = querySortBy,
                QueryRange = queryRange,
                Results = results
            };

            if (page == 1 && pagedResultSet.TotalPages == 0)
                return pagedResultSet;

            // don't let users try and request a page that doesn't exist
            if (page > pagedResultSet.TotalPages)
            {
                pagedResultSet.TotalResults = 0;
                pagedResultSet.Results.Clear();
                return pagedResultSet;
            }

            return pagedResultSet;
        }

        /// <summary>
        /// Returns an array of ids for Connected System Objects that have attributes where the value is an unresolved reference.
        /// </summary>
        /// <param name="connectedSystemId">The unique identifier for the Connected System to find objects within.</param>
        /// <returns>An array of Connected System Object unique identifiers</returns>
        public async Task<Guid[]> GetConnectedSystemObjectsWithUnresolvedReferencesAsync(int connectedSystemId)
        {
            return await Repository.Database.ConnectedSystemObjects.Where(cso => cso.ConnectedSystem.Id == connectedSystemId && cso.AttributeValues.Any(av => !string.IsNullOrEmpty(av.UnresolvedReferenceValue))).Select(cso => cso.Id).ToArrayAsync();
        }

        public async Task<Guid?> GetConnectedSystemObjectIdByAttributeValueAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
        {
            return await Repository.Database.ConnectedSystemObjects.Where(cso =>
                cso.ConnectedSystem.Id == connectedSystemId &&
                cso.AttributeValues.Any(av => av.Attribute.Id == connectedSystemAttributeId && av.StringValue != null && av.StringValue.ToLower() == attributeValue.ToLower())).Select(cso => cso.Id).SingleOrDefaultAsync();
        }

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
        {
            return await Repository.Database.ConnectedSystemObjects.Include(cso => cso.AttributeValues).ThenInclude(av => av.Attribute).SingleOrDefaultAsync(x => x.ConnectedSystem.Id == connectedSystemId && x.Id == id);
        }

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByExternalIdAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
        {
            return await Repository.Database.ConnectedSystemObjects.SingleOrDefaultAsync(x =>
                x.ConnectedSystem.Id == connectedSystemId &&
                x.AttributeValues.Any(av => av.Attribute.Id == connectedSystemAttributeId && av.StringValue != null && av.StringValue.ToLower() == attributeValue.ToLower()));
        }

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByExternalIdAsync(int connectedSystemId, int connectedSystemAttributeId, int attributeValue)
        {
            return await Repository.Database.ConnectedSystemObjects.SingleOrDefaultAsync(x =>
                x.ConnectedSystem.Id == connectedSystemId &&
                x.AttributeValues.Any(av => av.Attribute.Id == connectedSystemAttributeId && av.IntValue == attributeValue));
        }

        public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByExternalIdAsync(int connectedSystemId, int connectedSystemAttributeId, Guid attributeValue)
        {
            return await Repository.Database.ConnectedSystemObjects.SingleOrDefaultAsync(x =>
                x.ConnectedSystem.Id == connectedSystemId &&
                x.AttributeValues.Any(av => av.Attribute.Id == connectedSystemAttributeId && av.GuidValue == attributeValue));
        }

        public async Task<int> GetConnectedSystemObjectCountAsync()
        {
            return await Repository.Database.ConnectedSystemObjects.CountAsync();
        }

        public async Task<int> GetConnectedSystemObjectOfTypeCountAsync(int connectedSystemObjectTypeId)
        {
            return await Repository.Database.ConnectedSystemObjects.Where(x => x.ConnectedSystem.Id == connectedSystemObjectTypeId).CountAsync();
        }

        public async Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        {
            Repository.Database.ConnectedSystemObjects.Add(connectedSystemObject);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        {
            Repository.Database.ConnectedSystemObjects.Update(connectedSystemObject);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task DeleteAllConnectedSystemObjectsAsync(int connectedSystemId, bool deleteAllConnectedSystemObjectChangeObjects)
        {
            if (deleteAllConnectedSystemObjectChangeObjects)
            {
                // it sounds like postgresql cascade delete might auto-delete dependent objects
                await Repository.Database.Database.ExecuteSqlRawAsync($"DELETE FROM \"ConnectedSystemObjectChanges\" WHERE \"ConnectedSystemId\" = {connectedSystemId}");
            }

            // it sounds like postgresql cascade delete might auto-delete dependent objects
            await Repository.Database.Database.ExecuteSqlRawAsync($"DELETE FROM \"ConnectedSystemObjects\" WHERE \"ConnectedSystemId\" = {connectedSystemId}");
        }

        public void DeleteAllPendingExportObjects(int connectedSystemId)
        {
            // it sounds like postgresql cascade delete might auto-delete dependent objects
            var command = $"DELETE FROM \"PendingExports\" WHERE \"ConnectedSystemId\" = {connectedSystemId}";
            Repository.Database.Database.ExecuteSqlRaw(command);
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
            return await Repository.Database.ConnectedSystemContainers.Where(q => q.ConnectedSystem != null && q.ConnectedSystem.Id == connectedSystem.Id).ToListAsync();
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
            // is there any work to do?
            if (!Repository.Database.ConnectedSystemRunProfiles.Any(q => q.Id == runProfile.Id))
                return;

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
            return await Repository.Database.ConnectedSystemRunProfiles.
                Include(q => q.Partition).
                Where(q => q.ConnectedSystemId == connectedSystemId).ToListAsync();
        }

        public async Task<ConnectedSystemRunProfileHeader?> GetConnectedSystemRunProfileHeaderAsync(int connectedSystemRunProfileId)
        {
            using var db = new JimDbContext();
            return await db.ConnectedSystemRunProfiles.Select(rph => new ConnectedSystemRunProfileHeader
            {
                Id = rph.Id,
                ConnectedSystemName = db.ConnectedSystems.Single(cs => cs.Id == rph.ConnectedSystemId).Name,
                ConnectedSystemRunProfileName = rph.Name
            }).SingleOrDefaultAsync(q => q.Id == connectedSystemRunProfileId);
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
    }
}
