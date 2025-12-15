using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Logic.DTOs;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Serilog;
namespace JIM.PostgresData.Repositories;

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
        return await Repository.Database.ConnectorDefinitions
            .Include(x => x.Files)
            .Include(x => x.Settings)
            .SingleOrDefaultAsync(cd => cd.Name.Equals(name));
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

    public int GetConnectedSystemCount()
    {
        return Repository.Database.ConnectedSystems.Count();
    }
        
    public async Task<List<ConnectedSystemHeader>> GetConnectedSystemHeadersAsync()
    {
        var headers = await Repository.Database.ConnectedSystems.Include(q => q.ConnectorDefinition).OrderBy(a => a.Name).Select(cs => new ConnectedSystemHeader
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
        return headers;
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
        // doing it in one giant include tree query will make it timeout.

        var connectedSystem = await Repository.Database.ConnectedSystems.
            Include(cs => cs.ConnectorDefinition).
            Include(cs => cs.SettingValues).
            ThenInclude(sv => sv.Setting).
            SingleOrDefaultAsync(x => x.Id == id);

        if (connectedSystem == null)
            return null;

        var runProfiles = await Repository.Database.ConnectedSystemRunProfiles.Include(q => q.Partition).Where(q => q.ConnectedSystemId == id).ToListAsync();

        var types = await Repository.Database.ConnectedSystemObjectTypes
            .Include(ot => ot.Attributes.OrderBy(a => a.Name))
            .Include(ot => ot.ObjectMatchingRules)
                .ThenInclude(omr => omr.Sources)
                    .ThenInclude(s => s.ConnectedSystemAttribute)
            .Include(ot => ot.ObjectMatchingRules)
                .ThenInclude(omr => omr.Sources)
                    .ThenInclude(s => s.MetaverseAttribute)
            .Include(ot => ot.ObjectMatchingRules)
                .ThenInclude(omr => omr.TargetMetaverseAttribute)
            .Where(q => q.ConnectedSystemId == id).ToListAsync();

        // supporting 11 levels deep. arbitrary, unless performance profiling identifies issues, or admins need to go deeper
        var partitions = await Repository.Database.ConnectedSystemPartitions
            .Include(p => p.Containers)!
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

    #region Object Types and Attributes
    /// <summary>
    /// Gets a Connected System Object Type by ID.
    /// </summary>
    public async Task<ConnectedSystemObjectType?> GetObjectTypeAsync(int id)
    {
        return await Repository.Database.ConnectedSystemObjectTypes
            .Include(ot => ot.Attributes)
            .Include(ot => ot.ConnectedSystem)
            .SingleOrDefaultAsync(ot => ot.Id == id);
    }

    /// <summary>
    /// Updates a Connected System Object Type.
    /// </summary>
    public async Task UpdateObjectTypeAsync(ConnectedSystemObjectType objectType)
    {
        Repository.Database.Update(objectType);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Gets a Connected System Attribute by ID.
    /// </summary>
    public async Task<ConnectedSystemObjectTypeAttribute?> GetAttributeAsync(int id)
    {
        return await Repository.Database.ConnectedSystemAttributes
            .Include(a => a.ConnectedSystemObjectType)
                .ThenInclude(ot => ot.ConnectedSystem)
            .SingleOrDefaultAsync(a => a.Id == id);
    }

    /// <summary>
    /// Updates a Connected System Attribute.
    /// </summary>
    public async Task UpdateAttributeAsync(ConnectedSystemObjectTypeAttribute attribute)
    {
        Repository.Database.Update(attribute);
        await Repository.Database.SaveChangesAsync();
    }
    #endregion

    #region Object Matching Rules
    /// <summary>
    /// Creates a new object matching rule for a Connected System Object Type.
    /// </summary>
    public async Task CreateObjectMatchingRuleAsync(ObjectMatchingRule rule)
    {
        Repository.Database.ObjectMatchingRules.Add(rule);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Updates an existing object matching rule.
    /// </summary>
    public async Task UpdateObjectMatchingRuleAsync(ObjectMatchingRule rule)
    {
        Repository.Database.Update(rule);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes an object matching rule and its sources.
    /// </summary>
    public async Task DeleteObjectMatchingRuleAsync(ObjectMatchingRule rule)
    {
        // Remove all sources first
        Repository.Database.ObjectMatchingRuleSources.RemoveRange(rule.Sources);
        Repository.Database.ObjectMatchingRules.Remove(rule);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Gets an object matching rule by ID with all related entities loaded.
    /// </summary>
    public async Task<ObjectMatchingRule?> GetObjectMatchingRuleAsync(int id)
    {
        return await Repository.Database.ObjectMatchingRules
            .Include(omr => omr.Sources)
                .ThenInclude(s => s.ConnectedSystemAttribute)
            .Include(omr => omr.Sources)
                .ThenInclude(s => s.MetaverseAttribute)
            .Include(omr => omr.TargetMetaverseAttribute)
            .Include(omr => omr.ConnectedSystemObjectType)
            .SingleOrDefaultAsync(omr => omr.Id == id);
    }
    #endregion

    #region Connected System Objects
    public async Task DeleteConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
    {
        Repository.Database.ConnectedSystemObjects.Remove(connectedSystemObject);
        await Repository.Database.SaveChangesAsync();
    }
    
    /// <summary>
    /// Retrieves a page's worth of Connected System Object Headers for a specific system, with sort and range properties.
    /// This has a max page size of 100 objects.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many Connected System Objects to return in this page of result.</param>
    /// <param name="querySortBy">What attribute to sort the results by.</param>
    /// <param name="queryRange">What time-range of results to restrict the query to. Tightly scoping the amount improves response times.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task<PagedResultSet<ConnectedSystemObjectHeader>> GetConnectedSystemObjectHeadersAsync(
        int connectedSystemId,
        int page,
        int pageSize,
        QuerySortBy querySortBy = QuerySortBy.DateCreated,
        QueryRange queryRange = QueryRange.Forever)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        // todo: just get the display name and unique identifier attribute values
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
        if (page <= pagedResultSet.TotalPages) 
            return pagedResultSet;
            
        pagedResultSet.TotalResults = 0;
        pagedResultSet.Results.Clear();
        return pagedResultSet;
    }

    /// <summary>
    /// Retrieves a page's worth of Connected System Objects for a specific system.
    /// This has a max page size of 500 objects.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="page">Which page to return results for, i.e. 1-n.</param>
    /// <param name="pageSize">How many Connected System Objects to return in this page of result.</param>
    /// <param name="returnAttributes">Controls whether ConnectedSystemObject.AttributeValues[n].Attribute is populated. By default, it isn't for performance reasons.</param>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public async Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(
        int connectedSystemId,
        int page,
        int pageSize,
        bool returnAttributes = false)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 500)
            pageSize = 500;

        // start building the query for all the CSOs for a particular system.
        var query = Repository.Database.ConnectedSystemObjects
            .Include(cso => cso.AttributeValues);
        
        // for optimum performance, do not include attributes
        // if you need details from the attribute, get the schema upfront and then lookup the Attribute in the schema whilst in memory
        // using the cso.AttributeValues[n].AttributeId accessor to look up against the schema.
        if (returnAttributes)
            query.ThenInclude(av => av.Attribute);

        // add the Connected System filter
        var objects = from cso in query.Where(q => q.ConnectedSystemId == connectedSystemId)
            select cso;
        
        // now just add a page's worth of results filter to the query and project to a list we can return.
        var grossCount = objects.Count();
        var offset = (page - 1) * pageSize;
        var itemsToGet = grossCount >= pageSize ? pageSize : grossCount;
        var pagedObjects = objects.Skip(offset).Take(itemsToGet);
        var results = await pagedObjects.ToListAsync();

        // now with all the ids we know how many total results there are and so can populate paging info
        var pagedResultSet = new PagedResultSet<ConnectedSystemObject>
        {
            PageSize = pageSize,
            TotalResults = grossCount,
            CurrentPage = page,
            Results = results
        };

        if (page == 1 && pagedResultSet.TotalPages == 0)
            return pagedResultSet;

        // don't let callers try and request a page that doesn't exist
        if (page <= pagedResultSet.TotalPages) 
            return pagedResultSet;
            
        pagedResultSet.TotalResults = 0;
        pagedResultSet.Results.Clear();
        return pagedResultSet;

    }

    /// <summary>
    /// Returns all the CSOs for a Connected System that are marked as Obsolete.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="returnAttributes">Controls whether ConnectedSystemObject.AttributeValues[n].Attribute is populated. By default, it isn't for performance reasons.</param>
    public async Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsObsoleteAsync(int connectedSystemId, bool returnAttributes)
    {
        // start building the query for all the obsolete CSOs for a particular system.
        var query = Repository.Database.ConnectedSystemObjects.Include(cso => cso.AttributeValues);
        
        // for optimum performance, do not include attributes
        // if you need details from the attribute, get the schema upfront and then lookup the Attribute in the schema whilst in memory
        // using the cso.AttributeValues[n].AttributeId accessor to look up against the schema.
        if (returnAttributes)
            query.ThenInclude(av => av.Attribute);

        // add the Connected System filter
        var objects = from cso in query.Where(q => 
                q.ConnectedSystem.Id == connectedSystemId &&
                q.Status == ConnectedSystemObjectStatus.Obsolete)
            select cso;

        return await objects.ToListAsync();
    }
    
    /// <summary>
    /// Returns all the CSOs for a Connected System that are not joined to Metaverse Objects.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the system to return CSOs for.</param>
    /// <param name="returnAttributes">Controls whether ConnectedSystemObject.AttributeValues[n].Attribute is populated. By default, it isn't for performance reasons.</param>
    public async Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsUnJoinedAsync(int connectedSystemId, bool returnAttributes)
    {
        // start building the query for all the obsolete CSOs for a particular system.
        var query = Repository.Database.ConnectedSystemObjects.Include(cso => cso.AttributeValues);
        
        // for optimum performance, do not include attributes
        // if you need details from the attribute, get the schema upfront and then lookup the Attribute in the schema whilst in memory
        // using the cso.AttributeValues[n].AttributeId accessor to look up against the schema.
        if (returnAttributes)
            query.ThenInclude(av => av.Attribute);

        // add the Connected System filter
        var objects = from cso in query.Where(q => 
                q.ConnectedSystem.Id == connectedSystemId && 
                q.MetaverseObject == null)
            select cso;

        return await objects.ToListAsync();
    }

    public async Task<Guid?> GetConnectedSystemObjectIdByAttributeValueAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
    {
        return await Repository.Database.ConnectedSystemObjects.Where(cso =>
            cso.ConnectedSystem.Id == connectedSystemId &&
            cso.AttributeValues.Any(av => av.Attribute.Id == connectedSystemAttributeId && av.StringValue != null && av.StringValue.ToLower() == attributeValue.ToLower())).Select(cso => cso.Id).SingleOrDefaultAsync();
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid id)
    {
        return await Repository.Database.ConnectedSystemObjects
            .Include(cso => cso.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(cso => cso.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(cso => cso!.Type)
            .Include(cso => cso.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .SingleOrDefaultAsync(x => x.ConnectedSystem.Id == connectedSystemId && x.Id == id);
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, string attributeValue)
    {
        return await Repository.Database.ConnectedSystemObjects.SingleOrDefaultAsync(x =>
            x.ConnectedSystem.Id == connectedSystemId &&
            x.AttributeValues.Any(av => av.Attribute.Id == connectedSystemAttributeId && av.StringValue != null && av.StringValue.ToLower() == attributeValue.ToLower()));
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, int attributeValue)
    {
        return await Repository.Database.ConnectedSystemObjects.SingleOrDefaultAsync(cso =>
            cso.ConnectedSystem.Id == connectedSystemId &&
            cso.AttributeValues.Any(av => av.Attribute.Id == connectedSystemAttributeId && av.IntValue == attributeValue));
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(int connectedSystemId, int connectedSystemAttributeId, Guid attributeValue)
    {
        return await Repository.Database.ConnectedSystemObjects.SingleOrDefaultAsync(x =>
            x.ConnectedSystem.Id == connectedSystemId &&
            x.AttributeValues.Any(av => av.Attribute.Id == connectedSystemAttributeId && av.GuidValue == attributeValue));
    }

    /// <summary>
    /// Gets a Connected System Object by its secondary external ID attribute value.
    /// Used to find PendingProvisioning CSOs during import reconciliation when the
    /// primary external ID (e.g., objectGUID) is system-assigned and not yet known.
    /// </summary>
    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(int connectedSystemId, int objectTypeId, string secondaryExternalIdValue)
    {
        return await Repository.Database.ConnectedSystemObjects
            .Include(cso => cso.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(cso => cso.MetaverseObject)
            .SingleOrDefaultAsync(cso =>
                cso.ConnectedSystemId == connectedSystemId &&
                cso.TypeId == objectTypeId &&
                cso.SecondaryExternalIdAttributeId != null &&
                cso.AttributeValues.Any(av =>
                    av.AttributeId == cso.SecondaryExternalIdAttributeId &&
                    av.StringValue != null &&
                    av.StringValue.Equals(secondaryExternalIdValue, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<int> GetConnectedSystemObjectCountAsync()
    {
        return await Repository.Database.ConnectedSystemObjects.CountAsync();
    }

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the object count for.</param>s
    public async Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId)
    {
        return await Repository.Database.ConnectedSystemObjects.CountAsync(cso => cso.ConnectedSystemId == connectedSystemId);
    }

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System, where the status is Obosolete.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the Obosolete object count for.</param>
    public async Task<int> GetConnectedSystemObjectObsoleteCountAsync(int connectedSystemId)
    {
        return await Repository.Database.ConnectedSystemObjects.CountAsync(cso => 
            cso.ConnectedSystemId == connectedSystemId &&
            cso.Status == ConnectedSystemObjectStatus.Obsolete);
    }

    /// <summary>
    /// Returns the count of Connected System Objects for a particular Connected System, that are not joined to a Metaverse Object.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to find the unjoined object count for.</param>
    public async Task<int> GetConnectedSystemObjectUnJoinedCountAsync(int connectedSystemId)
    {
        return await Repository.Database.ConnectedSystemObjects.CountAsync(cso => 
            cso.ConnectedSystemId == connectedSystemId &&
            cso.MetaverseObject == null);
    }

    public async Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
    {
        Repository.Database.ConnectedSystemObjects.Add(connectedSystemObject);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
    {
        Repository.Database.ConnectedSystemObjects.AddRange(connectedSystemObjects);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
    {
        Repository.Database.ConnectedSystemObjects.Update(connectedSystemObject);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
    {
        Repository.Database.ConnectedSystemObjects.UpdateRange(connectedSystemObjects);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        // this is quite a weird way of querying. it's like this so that we can unit-test import synchronisation.
        // if we could mock JimDbContext.ConnectedSystemObjectAttributeValues from the mocked ConnectedSystemObject DbSet, then we could
        // use the more traditional (efficient?) query commented out below.
        return (await Repository.Database.ConnectedSystemObjects.Where(cso =>
                cso.ConnectedSystemId == connectedSystemId &&
                cso.Type.Id == connectedSystemObjectTypeId)
            .SelectMany(q => 
                q.AttributeValues.Where(av => 
                        av.Attribute.Type == AttributeDataType.Text &&
                        av.Attribute.IsExternalId &&
                        av.StringValue != null)
                    .Select(av => av.StringValue)).ToListAsync())!;
    }
    
    public async Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {

        return await Repository.Database.ConnectedSystemObjects.Where(cso =>
                cso.ConnectedSystemId == connectedSystemId &&
                cso.Type.Id == connectedSystemObjectTypeId)
            .SelectMany(q => 
                q.AttributeValues.Where(av => 
                        av.Attribute.Type == AttributeDataType.Number &&
                        av.Attribute.IsExternalId &&
                        av.IntValue.HasValue)
                    .Select(av => av.IntValue!.Value)).ToListAsync();
    }
    
    public async Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int connectedSystemObjectTypeId)
    {
        return await Repository.Database.ConnectedSystemObjects.Where(cso =>
                cso.ConnectedSystemId == connectedSystemId &&
                cso.Type.Id == connectedSystemObjectTypeId)
            .SelectMany(q => 
                q.AttributeValues.Where(av => 
                        av.Attribute.Type == AttributeDataType.Guid &&
                        av.Attribute.IsExternalId &&
                        av.GuidValue.HasValue)
                    .Select(av => av.GuidValue!.Value)).ToListAsync();
    }
    #endregion

    #region Connected System Object Types
    /// <summary>
    /// Retrieves all the Connected System Object Types for a given Connected System.
    /// Includes Attributes.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System to return the types for.</param>
    public async Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId)
    {
        return await Repository.Database.ConnectedSystemObjectTypes
            .Include(q => q.Attributes)
            .Where(x => x.ConnectedSystemId == connectedSystemId).OrderBy(x => x.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Determines if a connected system object type attribute is being referenced by any sync rule attribute flow, or any attribute values.
    /// This is to enable checks to see if things like attribute types can be edited.
    /// </summary>
    public async Task<bool> IsObjectTypeAttributeBeingReferencedAsync(ConnectedSystemObjectTypeAttribute connectedSystemObjectTypeAttribute)
    {
        if (connectedSystemObjectTypeAttribute.Id == 0)
            return false;

        // check for sync rule references (attribute flow or object matching)
        if (await Repository.Database.SyncRuleMappingSources.AnyAsync(q =>
                q.ConnectedSystemAttribute != null &&
                q.ConnectedSystemAttribute.Id == connectedSystemObjectTypeAttribute.Id))
            return true;
        
        // check for attribute values
        if (await Repository.Database.ConnectedSystemObjectAttributeValues.AnyAsync(q =>
                q.Attribute.Id == connectedSystemObjectTypeAttribute.Id))
            return true;

        return false;
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

        // Null out the FK reference in Activities to preserve audit history
        // Only execute raw SQL if we have a real database connection (not mocked)
        try
        {
            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Activities"" SET ""ConnectedSystemRunProfileId"" = NULL WHERE ""ConnectedSystemRunProfileId"" = {0}",
                runProfile.Id);
        }
        catch (Exception)
        {
            // Expected when running with mocked DbContext in tests
        }

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
        return await Repository.Database.ConnectedSystemRunProfiles.Select(rph => new ConnectedSystemRunProfileHeader
        {
            Id = rph.Id,
            ConnectedSystemName = Repository.Database.ConnectedSystems.Single(cs => cs.Id == rph.ConnectedSystemId).Name,
            ConnectedSystemRunProfileName = rph.Name
        }).SingleOrDefaultAsync(q => q.Id == connectedSystemRunProfileId);
    }
    #endregion
    
    #region Pending Exports
    /// <summary>
    /// Retrieves all the Pending Exports for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System the Pending Exports relate to.</param>
    public async Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId)
    {
        // does not include PendingExport.AttributeValueChanges.Attributes.
        // it's expected that the schema is retrieved separately by the caller.
        // this is to keep the latency as low as possible for this method.
        
        return await Repository.Database.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .Where(pe => pe.ConnectedSystemId == connectedSystemId).ToListAsync();
    }

    /// <summary>
    /// Retrieves the count of how many Pending Export objects there are for a particular Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System the Pending Exports relate to.</param>
    public async Task<int> GetPendingExportsCountAsync(int connectedSystemId)
    {
        return await Repository.Database.PendingExports.CountAsync(pe => pe.ConnectedSystemId == connectedSystemId);
    }

    public async Task DeletePendingExportAsync(PendingExport pendingExport)
    {
        Repository.Database.PendingExports.Remove(pendingExport);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdatePendingExportAsync(PendingExport pendingExport)
    {
        Repository.Database.PendingExports.Update(pendingExport);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task CreatePendingExportAsync(PendingExport pendingExport)
    {
        await Repository.Database.PendingExports.AddAsync(pendingExport);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Retrieves a page of Pending Export headers for a Connected System.
    /// </summary>
    public async Task<PagedResultSet<PendingExportHeader>> GetPendingExportHeadersAsync(
        int connectedSystemId,
        int page,
        int pageSize,
        IEnumerable<PendingExportStatus>? statusFilters = null,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // Limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var query = Repository.Database.PendingExports
            .Include(pe => pe.AttributeValueChanges)
            .Include(pe => pe.ConnectedSystemObject)
                .ThenInclude(cso => cso!.AttributeValues)
            .Include(pe => pe.SourceMetaverseObject)
                .ThenInclude(mvo => mvo!.AttributeValues)
                    .ThenInclude(av => av.Attribute)
            .Where(pe => pe.ConnectedSystemId == connectedSystemId);

        // Apply status filter (supports multiple statuses)
        var statusList = statusFilters?.ToList();
        if (statusList != null && statusList.Count > 0)
            query = query.Where(pe => statusList.Contains(pe.Status));

        // Apply search filter - search on target identifier, source MVO display name, or error message
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchLower = searchQuery.ToLower();
            query = query.Where(pe =>
                (pe.LastErrorMessage != null && pe.LastErrorMessage.ToLower().Contains(searchLower)) ||
                (pe.ConnectedSystemObject != null && pe.ConnectedSystemObject.AttributeValues
                    .Any(av => av.AttributeId == pe.ConnectedSystemObject.ExternalIdAttributeId &&
                         av.StringValue != null && av.StringValue.ToLower().Contains(searchLower))) ||
                (pe.SourceMetaverseObject != null && pe.SourceMetaverseObject.AttributeValues
                    .Any(av => av.Attribute != null && av.Attribute.Name.ToLower() == "displayname" &&
                         av.StringValue != null && av.StringValue.ToLower().Contains(searchLower))));
        }

        // Apply sorting
        query = sortBy?.ToLower() switch
        {
            "changetype" => sortDescending
                ? query.OrderByDescending(pe => pe.ChangeType)
                : query.OrderBy(pe => pe.ChangeType),
            "status" => sortDescending
                ? query.OrderByDescending(pe => pe.Status)
                : query.OrderBy(pe => pe.Status),
            "targetobject" => sortDescending
                ? query.OrderByDescending(pe => pe.ConnectedSystemObject != null
                    ? pe.ConnectedSystemObject.AttributeValues
                        .Where(av => av.AttributeId == pe.ConnectedSystemObject.ExternalIdAttributeId)
                        .Select(av => av.StringValue)
                        .FirstOrDefault()
                    : null)
                : query.OrderBy(pe => pe.ConnectedSystemObject != null
                    ? pe.ConnectedSystemObject.AttributeValues
                        .Where(av => av.AttributeId == pe.ConnectedSystemObject.ExternalIdAttributeId)
                        .Select(av => av.StringValue)
                        .FirstOrDefault()
                    : null),
            "sourcemvo" => sortDescending
                ? query.OrderByDescending(pe => pe.SourceMetaverseObject != null
                    ? pe.SourceMetaverseObject.AttributeValues
                        .Where(av => av.Attribute != null && av.Attribute.Name.ToLower() == "displayname")
                        .Select(av => av.StringValue)
                        .FirstOrDefault()
                    : null)
                : query.OrderBy(pe => pe.SourceMetaverseObject != null
                    ? pe.SourceMetaverseObject.AttributeValues
                        .Where(av => av.Attribute != null && av.Attribute.Name.ToLower() == "displayname")
                        .Select(av => av.StringValue)
                        .FirstOrDefault()
                    : null),
            "changes" => sortDescending
                ? query.OrderByDescending(pe => pe.AttributeValueChanges.Count)
                : query.OrderBy(pe => pe.AttributeValueChanges.Count),
            "errors" => sortDescending
                ? query.OrderByDescending(pe => pe.ErrorCount)
                : query.OrderBy(pe => pe.ErrorCount),
            "nextretry" => sortDescending
                ? query.OrderByDescending(pe => pe.NextRetryAt ?? DateTime.MaxValue)
                : query.OrderBy(pe => pe.NextRetryAt ?? DateTime.MaxValue),
            _ => sortDescending
                ? query.OrderByDescending(pe => pe.CreatedAt)
                : query.OrderBy(pe => pe.CreatedAt) // Default: sort by Created
        };

        var totalCount = await query.CountAsync();
        var offset = (page - 1) * pageSize;
        var pagedItems = await query.Skip(offset).Take(pageSize).ToListAsync();

        // Convert to headers
        var headers = pagedItems.Select(pe =>
        {
            // Get target object identifier from CSO if available
            string? targetIdentifier = null;
            if (pe.ConnectedSystemObject != null)
            {
                // Try to get external ID attribute value
                var externalIdAttr = pe.ConnectedSystemObject.AttributeValues?
                    .FirstOrDefault(av => av.AttributeId == pe.ConnectedSystemObject.ExternalIdAttributeId);
                targetIdentifier = externalIdAttr?.StringValue ?? pe.ConnectedSystemObject.Id.ToString();
            }

            // Get source MVO display name if available
            string? sourceMvoDisplayName = null;
            if (pe.SourceMetaverseObject != null)
            {
                sourceMvoDisplayName = pe.SourceMetaverseObject.AttributeValues?
                    .FirstOrDefault(av => av.Attribute?.Name?.ToLower() == "displayname")?.StringValue;
            }

            return PendingExportHeader.FromEntity(pe, targetIdentifier, sourceMvoDisplayName);
        }).ToList();

        return new PagedResultSet<PendingExportHeader>
        {
            PageSize = pageSize,
            TotalResults = totalCount,
            CurrentPage = page,
            Results = headers
        };
    }

    /// <summary>
    /// Retrieves a single Pending Export by ID with all related data.
    /// </summary>
    public async Task<PendingExport?> GetPendingExportAsync(Guid id)
    {
        return await Repository.Database.PendingExports
            .Include(pe => pe.ConnectedSystem)
            .Include(pe => pe.ConnectedSystemObject)
                .ThenInclude(cso => cso!.AttributeValues)
                    .ThenInclude(av => av.Attribute)
            .Include(pe => pe.AttributeValueChanges)
                .ThenInclude(avc => avc.Attribute)
            .Include(pe => pe.SourceMetaverseObject)
                .ThenInclude(mvo => mvo!.AttributeValues)
                    .ThenInclude(av => av.Attribute)
            .FirstOrDefaultAsync(pe => pe.Id == id);
    }

    public async Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId)
    {
        return await Repository.Database.ConnectedSystemObjects
            .Where(cso => cso.MetaverseObjectId == metaverseObjectId)
            .ToListAsync();
    }

    public async Task<ConnectedSystemObject?> GetConnectedSystemObjectByMetaverseObjectIdAsync(Guid metaverseObjectId, int connectedSystemId)
    {
        return await Repository.Database.ConnectedSystemObjects
            .FirstOrDefaultAsync(cso => cso.MetaverseObjectId == metaverseObjectId && cso.ConnectedSystemId == connectedSystemId);
    }

    /// <summary>
    /// Finds a Connected System Object that matches the given Metaverse Object using the specified matching rule.
    /// This is the reverse of FindMetaverseObjectUsingMatchingRuleAsync - it looks up CSOs by MVO attribute values.
    /// Used during export evaluation to find existing CSOs for provisioning decisions.
    /// </summary>
    public async Task<ConnectedSystemObject?> FindConnectedSystemObjectUsingMatchingRuleAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        ObjectMatchingRule objectMatchingRule)
    {
        if (objectMatchingRule.Sources.Count == 0)
            throw new InvalidDataException("ObjectMatchingRule has no sources.");

        if (objectMatchingRule.Sources.Count > 1)
            throw new NotImplementedException("Object Matching Rules with more than one Source are not yet supported (i.e. functions).");

        var source = objectMatchingRule.Sources[0];

        // For export matching, the source should reference an MVO attribute
        if (source.MetaverseAttribute == null)
        {
            Log.Warning("FindConnectedSystemObjectUsingMatchingRuleAsync: Source does not have a MetaverseAttribute, skipping rule {RuleId}", objectMatchingRule.Id);
            return null;
        }

        // Get the source attribute value from the MVO
        var mvoAttributeValue = metaverseObject.AttributeValues
            .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id || av.Attribute?.Id == source.MetaverseAttribute.Id);

        if (mvoAttributeValue == null)
        {
            Log.Debug("FindConnectedSystemObjectUsingMatchingRuleAsync: MVO {MvoId} does not have a value for attribute {AttrId}",
                metaverseObject.Id, source.MetaverseAttribute.Id);
            return null;
        }

        // The target is a CS attribute (what we're looking for on the CSO)
        var targetCsAttribute = objectMatchingRule.TargetMetaverseAttribute;
        if (targetCsAttribute == null)
        {
            // For export matching, we need to find the corresponding CS attribute
            // Since the matching rule's target is the MV attribute during import,
            // for export we look at what CS attribute maps to that value
            // This is typically the external ID attribute of the object type
            Log.Warning("FindConnectedSystemObjectUsingMatchingRuleAsync: No target attribute configured on rule {RuleId}", objectMatchingRule.Id);
            return null;
        }

        // Query CSOs that match the value
        var query = Repository.Database.ConnectedSystemObjects
            .Include(cso => cso.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Where(cso => cso.ConnectedSystemId == connectedSystem.Id && cso.TypeId == connectedSystemObjectType.Id);

        // Match based on attribute type
        switch (source.MetaverseAttribute.Type)
        {
            case AttributeDataType.Text:
                query = query.Where(cso => cso.AttributeValues.Any(av =>
                    av.Attribute != null &&
                    av.Attribute.Name == source.ConnectedSystemAttribute!.Name &&
                    av.StringValue == mvoAttributeValue.StringValue));
                break;
            case AttributeDataType.Number:
                query = query.Where(cso => cso.AttributeValues.Any(av =>
                    av.Attribute != null &&
                    av.Attribute.Name == source.ConnectedSystemAttribute!.Name &&
                    av.IntValue == mvoAttributeValue.IntValue));
                break;
            case AttributeDataType.Guid:
                query = query.Where(cso => cso.AttributeValues.Any(av =>
                    av.Attribute != null &&
                    av.Attribute.Name == source.ConnectedSystemAttribute!.Name &&
                    av.GuidValue == mvoAttributeValue.GuidValue));
                break;
            default:
                throw new NotSupportedException($"Attribute type {source.MetaverseAttribute.Type} is not supported for export matching.");
        }

        return await query.FirstOrDefaultAsync();
    }
    #endregion

    #region Sync Rules
    public async Task<List<SyncRule>> GetSyncRulesAsync()
    {
        return await Repository.Database.SyncRules
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.TargetConnectedSystemAttribute)
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.TargetMetaverseAttribute)
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.Sources)
            .ThenInclude(s => s.ConnectedSystemAttribute)
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.Sources)
            .ThenInclude(s => s.MetaverseAttribute)
            .Include(sr => sr.ConnectedSystem)
            .Include(sr => sr.ConnectedSystemObjectType)
            .ThenInclude(csot => csot.Attributes.OrderBy(a => a.Name))
            .Include(sr => sr.MetaverseObjectType)
            .ThenInclude(mvot => mvot.Attributes.OrderBy(a => a.Name))
            .OrderBy(x => x.Name)
            .ToListAsync();
    }
    
    /// <summary>
    /// Retrieves all the sync rules for a given Connected System.
    /// </summary>
    /// <param name="connectedSystemId">The unique identifier for the Connected System.</param>
    /// <param name="includeDisabledSyncRules">Controls whether to return sync rules that are disabled</param>
    public async Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabledSyncRules)
    {
        var query = Repository.Database.SyncRules
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.TargetConnectedSystemAttribute)
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.TargetMetaverseAttribute)
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.Sources)
            .ThenInclude(s => s.ConnectedSystemAttribute)
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.Sources)
            .ThenInclude(s => s.MetaverseAttribute)
            .Include(sr => sr.ConnectedSystem)
            .Include(sr => sr.ConnectedSystemObjectType)
            .ThenInclude(csot => csot.Attributes.OrderBy(a => a.Name))
            .Include(sr => sr.MetaverseObjectType)
            .ThenInclude(mvot => mvot.Attributes.OrderBy(a => a.Name))
            .Include(sr => sr.ObjectMatchingRules.OrderBy(q => q.Order))
            .ThenInclude(omr => omr.Sources)
            .ThenInclude(s => s.ConnectedSystemAttribute)
            .Include(sr => sr.ObjectMatchingRules)
            .ThenInclude(omr => omr.TargetMetaverseAttribute)
            .Where(sr => sr.ConnectedSystemId == connectedSystemId);

        if (!includeDisabledSyncRules)
            query = query.Where(sr => sr.Enabled);

        return await query.ToListAsync();
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
            ProvisionToConnectedSystem = sr.ProvisionToConnectedSystem,
            Enabled = sr.Enabled
        }).ToListAsync();
    }

    public async Task<SyncRule?> GetSyncRuleAsync(int id)
    {
        return await Repository.Database.SyncRules
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.TargetConnectedSystemAttribute)
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.TargetMetaverseAttribute)
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.Sources)
            .ThenInclude(s =>s.ConnectedSystemAttribute)
            .Include(sr => sr.AttributeFlowRules)
            .ThenInclude(afr => afr.Sources)
            .ThenInclude(s => s.MetaverseAttribute)
            .Include(sr => sr.ConnectedSystem)
            .Include(sr => sr.ConnectedSystemObjectType)
            .ThenInclude(csot => csot.Attributes.OrderBy(a => a.Name))
            .Include(sr => sr.ObjectScopingCriteriaGroups)
            .Include(sr => sr.CreatedBy) // needs basic attributes included to use as a link to the user in the ui
            .ThenInclude(cb => cb!.AttributeValues.Where(av => av.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
            .Include(sr => sr.MetaverseObjectType)
            .ThenInclude(mvot => mvot.Attributes.OrderBy(a => a.Name))
            .Include(sr => sr.ObjectMatchingRules.OrderBy(q => q.Order))
            .ThenInclude(omr => omr.Sources)
            .ThenInclude(s => s.ConnectedSystemAttribute)
            .Include(sr => sr.ObjectMatchingRules)
            .ThenInclude(omr => omr.Sources)
            .ThenInclude(s => s.MetaverseAttribute)
            .Include(sr => sr.ObjectMatchingRules)
            .ThenInclude(omr => omr.Sources)
            .ThenInclude(s => s.Function)
            .Include(sr => sr.ObjectMatchingRules)
            .ThenInclude(omr => omr.TargetMetaverseAttribute)
            .SingleOrDefaultAsync(x => x.Id == id);
    }

    public async Task CreateSyncRuleAsync(SyncRule syncRule)
    {
        Repository.Database.SyncRules.Add(syncRule);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateSyncRuleAsync(SyncRule syncRule)
    {
        Repository.Database.Update(syncRule);
        await Repository.Database.SaveChangesAsync();
    }
        
    public async Task DeleteSyncRuleAsync(SyncRule syncRule)
    {
        // Null out the FK reference in Activities to preserve audit history
        // Only execute raw SQL if we have a real database connection (not mocked)
        try
        {
            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"UPDATE ""Activities"" SET ""SyncRuleId"" = NULL WHERE ""SyncRuleId"" = {0}",
                syncRule.Id);
        }
        catch (Exception)
        {
            // Expected when running with mocked DbContext in tests
        }

        Repository.Database.Remove(syncRule);
        await Repository.Database.SaveChangesAsync();
    }
    #endregion

    #region Sync Rule Mappings
    /// <summary>
    /// Gets all mappings for a sync rule.
    /// </summary>
    public async Task<List<SyncRuleMapping>> GetSyncRuleMappingsAsync(int syncRuleId)
    {
        return await Repository.Database.SyncRuleMappings
            .Include(m => m.Sources)
                .ThenInclude(s => s.ConnectedSystemAttribute)
            .Include(m => m.Sources)
                .ThenInclude(s => s.MetaverseAttribute)
            .Include(m => m.TargetMetaverseAttribute)
            .Include(m => m.TargetConnectedSystemAttribute)
            .Where(m => m.SyncRule!.Id == syncRuleId)
            .OrderBy(m => m.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Gets a specific sync rule mapping by ID.
    /// </summary>
    public async Task<SyncRuleMapping?> GetSyncRuleMappingAsync(int id)
    {
        return await Repository.Database.SyncRuleMappings
            .Include(m => m.SyncRule)
            .Include(m => m.Sources)
                .ThenInclude(s => s.ConnectedSystemAttribute)
            .Include(m => m.Sources)
                .ThenInclude(s => s.MetaverseAttribute)
            .Include(m => m.TargetMetaverseAttribute)
            .Include(m => m.TargetConnectedSystemAttribute)
            .SingleOrDefaultAsync(m => m.Id == id);
    }

    /// <summary>
    /// Creates a new sync rule mapping.
    /// </summary>
    public async Task CreateSyncRuleMappingAsync(SyncRuleMapping mapping)
    {
        Repository.Database.SyncRuleMappings.Add(mapping);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Updates an existing sync rule mapping.
    /// </summary>
    public async Task UpdateSyncRuleMappingAsync(SyncRuleMapping mapping)
    {
        Repository.Database.Update(mapping);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes a sync rule mapping.
    /// </summary>
    public async Task DeleteSyncRuleMappingAsync(SyncRuleMapping mapping)
    {
        // Remove all sources first
        Repository.Database.RemoveRange(mapping.Sources);
        Repository.Database.SyncRuleMappings.Remove(mapping);
        await Repository.Database.SaveChangesAsync();
    }
    #endregion

    #region Connected System Deletion
    /// <summary>
    /// Shared method for deleting CSOs and their immediate dependencies.
    /// Used by both ClearConnectedSystemObjects and DeleteConnectedSystem.
    /// </summary>
    public async Task DeleteAllConnectedSystemObjectsAndDependenciesAsync(int connectedSystemId, bool deleteChangeHistory)
    {
        Log.Information("DeleteAllConnectedSystemObjectsAndDependenciesAsync: Starting for Connected System {Id}, deleteChangeHistory={DeleteHistory}",
            connectedSystemId, deleteChangeHistory);

        // 1. Delete PendingExportAttributeValueChanges (child of PendingExport)
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""PendingExportAttributeValueChanges""
              WHERE ""PendingExportId"" IN (SELECT ""Id"" FROM ""PendingExports"" WHERE ""ConnectedSystemId"" = {0})",
            connectedSystemId);

        // 2. Delete PendingExports
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""PendingExports"" WHERE ""ConnectedSystemId"" = {0}",
            connectedSystemId);

        // 3. Delete DeferredReferences for this system
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""DeferredReferences"" WHERE ""TargetSystemId"" = {0}",
            connectedSystemId);

        // 4. Disconnect CSOs from MVOs (clear MetaverseObjectId FK)
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" SET ""MetaverseObjectId"" = NULL WHERE ""ConnectedSystemId"" = {0}",
            connectedSystemId);

        // 5. Delete CSO attribute values
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ConnectedSystemObjectAttributeValues""
              WHERE ""ConnectedSystemObjectId"" IN (SELECT ""Id"" FROM ""ConnectedSystemObjects"" WHERE ""ConnectedSystemId"" = {0})",
            connectedSystemId);

        // 6. Handle ConnectedSystemObjectChanges based on deleteChangeHistory flag
        if (deleteChangeHistory)
        {
            // Delete change attribute values first (child records)
            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""ConnectedSystemObjectChangeAttributeValues""
                  WHERE ""ConnectedSystemObjectChangeAttributeId"" IN (
                    SELECT ca.""Id"" FROM ""ConnectedSystemObjectChangeAttributes"" ca
                    INNER JOIN ""ConnectedSystemObjectChanges"" c ON ca.""ConnectedSystemChangeId"" = c.""Id""
                    WHERE c.""ConnectedSystemId"" = {0}
                  )",
                connectedSystemId);

            // Delete change attributes
            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""ConnectedSystemObjectChangeAttributes""
                  WHERE ""ConnectedSystemChangeId"" IN (
                    SELECT ""Id"" FROM ""ConnectedSystemObjectChanges"" WHERE ""ConnectedSystemId"" = {0}
                  )",
                connectedSystemId);

            // Delete the changes themselves
            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""ConnectedSystemObjectChanges"" WHERE ""ConnectedSystemId"" = {0}",
                connectedSystemId);
        }
        else
        {
            // Null CSO FK on ConnectedSystemObjectChanges (preserve audit trail)
            await Repository.Database.Database.ExecuteSqlRawAsync(
                @"UPDATE ""ConnectedSystemObjectChanges"" SET ""ConnectedSystemObjectId"" = NULL
                  WHERE ""ConnectedSystemObjectId"" IN (SELECT ""Id"" FROM ""ConnectedSystemObjects"" WHERE ""ConnectedSystemId"" = {0})",
                connectedSystemId);
        }

        // 7. Null CSO FK on ActivityRunProfileExecutionItems
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ActivityRunProfileExecutionItems"" SET ""ConnectedSystemObjectId"" = NULL
              WHERE ""ConnectedSystemObjectId"" IN (SELECT ""Id"" FROM ""ConnectedSystemObjects"" WHERE ""ConnectedSystemId"" = {0})",
            connectedSystemId);

        // 8. Delete CSOs
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ConnectedSystemObjects"" WHERE ""ConnectedSystemId"" = {0}",
            connectedSystemId);

        Log.Information("DeleteAllConnectedSystemObjectsAndDependenciesAsync: Completed for Connected System {Id}", connectedSystemId);
    }

    public async Task DeleteConnectedSystemAsync(int connectedSystemId)
    {
        // Use raw SQL for bulk deletion - much faster than EF Core tracking
        // Delete order follows dependency graph from design doc
        Log.Information("DeleteConnectedSystemAsync: Starting bulk deletion for Connected System {Id}", connectedSystemId);

        // 1. Delete all CSOs and their dependencies (preserving change history)
        await DeleteAllConnectedSystemObjectsAndDependenciesAsync(connectedSystemId, deleteChangeHistory: false);

        // 2. Delete Containers (child of Partition)
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ConnectedSystemContainers""
              WHERE ""PartitionId"" IN (SELECT ""Id"" FROM ""ConnectedSystemPartitions"" WHERE ""ConnectedSystemId"" = {0})",
            connectedSystemId);

        // 3. Delete Partitions
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ConnectedSystemPartitions"" WHERE ""ConnectedSystemId"" = {0}",
            connectedSystemId);

        // 4. Delete Run Profiles
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ConnectedSystemRunProfiles"" WHERE ""ConnectedSystemId"" = {0}",
            connectedSystemId);

        // 5. Delete SyncRuleMappingSourceParamValues (child of SyncRuleMappingSource)
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""SyncRuleMappingSourceParamValues""
              WHERE ""SyncRuleMappingSourceId"" IN (
                SELECT sms.""Id"" FROM ""SyncRuleMappingSources"" sms
                INNER JOIN ""SyncRuleMappings"" srm ON sms.""SyncRuleMappingId"" = srm.""Id""
                INNER JOIN ""SyncRules"" sr ON srm.""AttributeFlowSynchronisationRuleId"" = sr.""Id""
                WHERE sr.""ConnectedSystemId"" = {0}
              )",
            connectedSystemId);

        // 6. Delete SyncRuleMappingSources (child of SyncRuleMapping)
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""SyncRuleMappingSources""
              WHERE ""SyncRuleMappingId"" IN (
                SELECT srm.""Id"" FROM ""SyncRuleMappings"" srm
                INNER JOIN ""SyncRules"" sr ON srm.""AttributeFlowSynchronisationRuleId"" = sr.""Id""
                WHERE sr.""ConnectedSystemId"" = {0}
              )",
            connectedSystemId);

        // 7. Delete SyncRuleMappings (attribute flow rules)
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""SyncRuleMappings""
              WHERE ""AttributeFlowSynchronisationRuleId"" IN (SELECT ""Id"" FROM ""SyncRules"" WHERE ""ConnectedSystemId"" = {0})
                 OR ""ObjectMatchingSynchronisationRuleId"" IN (SELECT ""Id"" FROM ""SyncRules"" WHERE ""ConnectedSystemId"" = {0})",
            connectedSystemId);

        // 8. Delete SyncRuleScopingCriteria (child of SyncRuleScopingCriteriaGroup)
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""SyncRuleScopingCriteria""
              WHERE ""SyncRuleScopingCriteriaGroupId"" IN (
                SELECT scg.""Id"" FROM ""SyncRuleScopingCriteriaGroups"" scg
                INNER JOIN ""SyncRules"" sr ON scg.""SyncRuleId"" = sr.""Id""
                WHERE sr.""ConnectedSystemId"" = {0}
              )",
            connectedSystemId);

        // 9. Delete SyncRuleScopingCriteriaGroups
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""SyncRuleScopingCriteriaGroups""
              WHERE ""SyncRuleId"" IN (SELECT ""Id"" FROM ""SyncRules"" WHERE ""ConnectedSystemId"" = {0})",
            connectedSystemId);

        // 10. Delete Sync Rules
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""SyncRules"" WHERE ""ConnectedSystemId"" = {0}",
            connectedSystemId);

        // 11. Delete Object Type Attributes
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ConnectedSystemAttributes""
              WHERE ""ConnectedSystemObjectTypeId"" IN (SELECT ""Id"" FROM ""ConnectedSystemObjectTypes"" WHERE ""ConnectedSystemId"" = {0})",
            connectedSystemId);

        // 12. Delete Object Types
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ConnectedSystemObjectTypes"" WHERE ""ConnectedSystemId"" = {0}",
            connectedSystemId);

        // 13. Delete Setting Values
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ConnectedSystemSettingValues"" WHERE ""ConnectedSystemId"" = {0}",
            connectedSystemId);

        // 14. Null Activity FKs (preserve audit trail)
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"UPDATE ""Activities"" SET ""ConnectedSystemId"" = NULL WHERE ""ConnectedSystemId"" = {0}",
            connectedSystemId);

        // 15. Finally, delete the Connected System itself
        await Repository.Database.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""ConnectedSystems"" WHERE ""Id"" = {0}",
            connectedSystemId);

        Log.Information("DeleteConnectedSystemAsync: Completed bulk deletion for Connected System {Id}", connectedSystemId);
    }

    public async Task<int> GetSyncRuleCountAsync(int connectedSystemId)
    {
        return await Repository.Database.SyncRules.CountAsync(sr => sr.ConnectedSystemId == connectedSystemId);
    }

    public async Task<int> GetRunProfileCountAsync(int connectedSystemId)
    {
        return await Repository.Database.ConnectedSystemRunProfiles.CountAsync(rp => rp.ConnectedSystemId == connectedSystemId);
    }

    public async Task<int> GetPartitionCountAsync(int connectedSystemId)
    {
        return await Repository.Database.ConnectedSystemPartitions.CountAsync(p => p.ConnectedSystem.Id == connectedSystemId);
    }

    public async Task<int> GetContainerCountAsync(int connectedSystemId)
    {
        // Count containers that belong to this connected system either:
        // 1. Directly via ConnectedSystem reference (top-level containers without partitions)
        // 2. Via Partition reference (containers within a partition)
        return await Repository.Database.ConnectedSystemContainers
            .CountAsync(c =>
                (c.ConnectedSystem != null && c.ConnectedSystem.Id == connectedSystemId) ||
                (c.Partition != null && c.Partition.ConnectedSystem.Id == connectedSystemId));
    }

    public async Task<int> GetActivityCountAsync(int connectedSystemId)
    {
        return await Repository.Database.Activities.CountAsync(a => a.ConnectedSystemId == connectedSystemId);
    }

    public async Task<int> GetJoinedMvoCountAsync(int connectedSystemId)
    {
        return await Repository.Database.ConnectedSystemObjects
            .Where(cso => cso.ConnectedSystemId == connectedSystemId && cso.MetaverseObjectId != null)
            .Select(cso => cso.MetaverseObjectId)
            .Distinct()
            .CountAsync();
    }

    public async Task<SynchronisationWorkerTask?> GetRunningSyncTaskAsync(int connectedSystemId)
    {
        // Get run profile IDs for this system
        var runProfileIds = await Repository.Database.ConnectedSystemRunProfiles
            .Where(rp => rp.ConnectedSystemId == connectedSystemId)
            .Select(rp => rp.Id)
            .ToListAsync();

        if (runProfileIds.Count == 0)
            return null;

        // Check for running sync tasks
        return await Repository.Database.SynchronisationWorkerTasks
            .Where(t => runProfileIds.Contains(t.ConnectedSystemRunProfileId) &&
                       (t.Status == WorkerTaskStatus.Queued || t.Status == WorkerTaskStatus.Processing))
            .FirstOrDefaultAsync();
    }
    #endregion
}
