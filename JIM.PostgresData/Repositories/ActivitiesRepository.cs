using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
namespace JIM.PostgresData.Repositories;

public class ActivityRepository : IActivityRepository
{
    private PostgresDataRepository Repository { get; }

    internal ActivityRepository(PostgresDataRepository dataRepository)
    {
        Repository = dataRepository;
    }

    public async Task CreateActivityAsync(Activity activity)
    {
        Repository.Database.Activities.Add(activity);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task UpdateActivityAsync(Activity activity)
    {
        Repository.Database.Activities.Update(activity);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task DeleteActivityAsync(Activity activity)
    {
        Repository.Database.Activities.Remove(activity);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Retrieves a page's worth of top-level activities, i.e. those that do not have a parent activity.
    /// </summary>
    public async Task<PagedResultSet<Activity>> GetActivitiesAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true,
        Guid? initiatedById = null)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var query = Repository.Database.Activities
            .AsSplitQuery() // Use split query to avoid cartesian explosion from multiple collection includes
            .Include(a => a.InitiatedByMetaverseObject)
            .ThenInclude(ib => ib!.AttributeValues.Where(av => av.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
            .ThenInclude(av => av.Attribute)
            .Include(st => st.InitiatedByMetaverseObject)
            .ThenInclude(ib => ib!.Type)
            .Include(a => a.InitiatedByApiKey)
            .Where(a => a.ParentActivityId == null)
            .AsQueryable();

        // Apply initiated by filter (searches by InitiatedById which covers both MVO and ApiKey)
        if (initiatedById.HasValue)
        {
            query = query.Where(a => a.InitiatedById == initiatedById.Value);
        }

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchLower = searchQuery.ToLower();
            query = query.Where(a =>
                (a.TargetName != null && a.TargetName.ToLower().Contains(searchLower)) ||
                EF.Functions.ILike(a.TargetType.ToString(), $"%{searchQuery}%"));
        }

        // Apply sorting
        query = sortBy?.ToLower() switch
        {
            "targettype" or "type" => sortDescending
                ? query.OrderByDescending(a => a.TargetType)
                : query.OrderBy(a => a.TargetType),
            "targetname" or "target" => sortDescending
                ? query.OrderByDescending(a => a.TargetName)
                : query.OrderBy(a => a.TargetName),
            "targetoperationtype" or "operation" => sortDescending
                ? query.OrderByDescending(a => a.TargetOperationType)
                : query.OrderBy(a => a.TargetOperationType),
            "initiatedbyname" or "initiatedby" => sortDescending
                ? query.OrderByDescending(a => a.InitiatedByName)
                : query.OrderBy(a => a.InitiatedByName),
            "status" => sortDescending
                ? query.OrderByDescending(a => a.Status)
                : query.OrderBy(a => a.Status),
            "executiontime" => sortDescending
                ? query.OrderByDescending(a => a.ExecutionTime)
                : query.OrderBy(a => a.ExecutionTime),
            _ => sortDescending
                ? query.OrderByDescending(a => a.Created)
                : query.OrderBy(a => a.Created) // Default: sort by Created
        };

        // Get total count for pagination
        var grossCount = await query.CountAsync();
        var offset = (page - 1) * pageSize;
        var results = await query.Skip(offset).Take(pageSize).ToListAsync();

        var pagedResultSet = new PagedResultSet<Activity>
        {
            PageSize = pageSize,
            TotalResults = grossCount,
            CurrentPage = page,
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

    public async Task<Activity?> GetActivityAsync(Guid id)
    {
        return await Repository.Database.Activities
            .AsSplitQuery() // Use split query to avoid cartesian explosion from multiple collection includes
            .Include(a => a.InitiatedByMetaverseObject)
            .ThenInclude(ib => ib!.AttributeValues.Where(av => av.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
            .ThenInclude(av => av.Attribute)
            .Include(st => st.InitiatedByMetaverseObject)
            .ThenInclude(ib => ib!.Type)
            .Include(a => a.InitiatedByApiKey)
            .SingleOrDefaultAsync(a => a.Id == id);
    }

    /// <summary>
    /// Retrieves a page's worth of worker task activities - operations executed by the worker service
    /// such as run profile executions, data generation, and connected system operations.
    /// </summary>
    public async Task<PagedResultSet<Activity>> GetWorkerTaskActivitiesAsync(
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = true)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        // Worker task activity types - operations executed by the worker service
        var workerTaskTargetTypes = new[]
        {
            ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetType.DataGenerationTemplate,
            ActivityTargetType.ConnectedSystem,
            ActivityTargetType.HistoryRetentionCleanup
        };

        // Worker task operations
        var workerTaskOperations = new[]
        {
            ActivityTargetOperationType.Execute,
            ActivityTargetOperationType.Clear,
            ActivityTargetOperationType.Delete
        };

        var query = Repository.Database.Activities
            .AsSplitQuery()
            .Include(a => a.InitiatedByMetaverseObject)
            .ThenInclude(ib => ib!.AttributeValues.Where(av => av.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
            .ThenInclude(av => av.Attribute)
            .Include(st => st.InitiatedByMetaverseObject)
            .ThenInclude(ib => ib!.Type)
            .Include(a => a.InitiatedByApiKey)
            .Where(a => a.ParentActivityId == null)
            .Where(a => workerTaskTargetTypes.Contains(a.TargetType) && workerTaskOperations.Contains(a.TargetOperationType))
            .AsQueryable();

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchLower = searchQuery.ToLower();
            query = query.Where(a =>
                (a.TargetName != null && a.TargetName.ToLower().Contains(searchLower)) ||
                EF.Functions.ILike(a.TargetType.ToString(), $"%{searchQuery}%"));
        }

        // Apply sorting
        query = sortBy?.ToLower() switch
        {
            "targettype" or "type" => sortDescending
                ? query.OrderByDescending(a => a.TargetType)
                : query.OrderBy(a => a.TargetType),
            "targetname" or "target" => sortDescending
                ? query.OrderByDescending(a => a.TargetName)
                : query.OrderBy(a => a.TargetName),
            "targetoperationtype" or "operation" => sortDescending
                ? query.OrderByDescending(a => a.TargetOperationType)
                : query.OrderBy(a => a.TargetOperationType),
            "initiatedbyname" or "initiatedby" => sortDescending
                ? query.OrderByDescending(a => a.InitiatedByName)
                : query.OrderBy(a => a.InitiatedByName),
            "status" => sortDescending
                ? query.OrderByDescending(a => a.Status)
                : query.OrderBy(a => a.Status),
            "executiontime" => sortDescending
                ? query.OrderByDescending(a => a.ExecutionTime)
                : query.OrderBy(a => a.ExecutionTime),
            _ => sortDescending
                ? query.OrderByDescending(a => a.Created)
                : query.OrderBy(a => a.Created)
        };

        // Get total count for pagination
        var grossCount = await query.CountAsync();
        var offset = (page - 1) * pageSize;
        var results = await query.Skip(offset).Take(pageSize).ToListAsync();

        var pagedResultSet = new PagedResultSet<Activity>
        {
            PageSize = pageSize,
            TotalResults = grossCount,
            CurrentPage = page,
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

    #region synchronisation related
    public async Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
        Guid activityId,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<ObjectChangeType>? changeTypeFilter = null,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var query = Repository.Database.ActivityRunProfileExecutionItems
            .AsSplitQuery() // Use split query to avoid cartesian explosion from multiple collection includes
            .Include(a => a.ConnectedSystemObject)
                .ThenInclude(cso => cso!.Type)
            .Include(a => a.ConnectedSystemObject)
                .ThenInclude(cso => cso!.AttributeValues)
                    .ThenInclude(av => av.Attribute)
            .Where(a => a.Activity.Id == activityId);

        // Apply change type filter if specified
        if (changeTypeFilter != null)
        {
            var changeTypes = changeTypeFilter.ToList();
            if (changeTypes.Count > 0)
            {
                query = query.Where(a => changeTypes.Contains(a.ObjectChangeType));
            }
        }

        // Apply object type filter if specified
        if (objectTypeFilter != null)
        {
            var objectTypes = objectTypeFilter.ToList();
            if (objectTypes.Count > 0)
            {
                query = query.Where(a =>
                    a.ConnectedSystemObject != null &&
                    a.ConnectedSystemObject.Type != null &&
                    objectTypes.Contains(a.ConnectedSystemObject.Type.Name));
            }
        }

        // Apply error type filter if specified
        if (errorTypeFilter != null)
        {
            var errorTypes = errorTypeFilter.ToList();
            if (errorTypes.Count > 0)
            {
                query = query.Where(a =>
                    a.ErrorType != null &&
                    errorTypes.Contains(a.ErrorType.Value));
            }
        }

        // Apply search filter - search on display name, external ID, or object type
        // Search is case-insensitive for user convenience
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchPattern = $"%{searchQuery}%";
            query = query.Where(item =>
                // Search display name
                (item.ConnectedSystemObject != null &&
                 item.ConnectedSystemObject.AttributeValues.Any(av =>
                    EF.Functions.ILike(av.Attribute.Name, "displayname") &&
                    av.StringValue != null &&
                    EF.Functions.ILike(av.StringValue, searchPattern))) ||
                // Search external ID
                (item.ConnectedSystemObject != null &&
                 item.ConnectedSystemObject.AttributeValues.Any(av =>
                    av.AttributeId == item.ConnectedSystemObject.ExternalIdAttributeId &&
                    av.StringValue != null &&
                    EF.Functions.ILike(av.StringValue, searchPattern))) ||
                // Search object type name
                (item.ConnectedSystemObject != null &&
                 item.ConnectedSystemObject.Type != null &&
                 EF.Functions.ILike(item.ConnectedSystemObject.Type.Name, searchPattern)));
        }

        // Apply sorting
        query = sortBy?.ToLower() switch
        {
            "externalid" => sortDescending
                ? query.OrderByDescending(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => av.AttributeId == item.ConnectedSystemObject.ExternalIdAttributeId)
                        .Select(av => av.StringValue)
                        .FirstOrDefault()
                    : null)
                : query.OrderBy(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => av.AttributeId == item.ConnectedSystemObject.ExternalIdAttributeId)
                        .Select(av => av.StringValue)
                        .FirstOrDefault()
                    : null),
            "displayname" or "name" => sortDescending
                ? query.OrderByDescending(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => EF.Functions.ILike(av.Attribute.Name, "displayname"))
                        .Select(av => av.StringValue)
                        .FirstOrDefault()
                    : null)
                : query.OrderBy(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => EF.Functions.ILike(av.Attribute.Name, "displayname"))
                        .Select(av => av.StringValue)
                        .FirstOrDefault()
                    : null),
            "type" or "objecttype" => sortDescending
                ? query.OrderByDescending(item => item.ConnectedSystemObject != null && item.ConnectedSystemObject.Type != null
                    ? item.ConnectedSystemObject.Type.Name
                    : null)
                : query.OrderBy(item => item.ConnectedSystemObject != null && item.ConnectedSystemObject.Type != null
                    ? item.ConnectedSystemObject.Type.Name
                    : null),
            "changetype" => sortDescending
                ? query.OrderByDescending(item => item.ObjectChangeType)
                : query.OrderBy(item => item.ObjectChangeType),
            "errortype" => sortDescending
                ? query.OrderByDescending(item => item.ErrorType)
                : query.OrderBy(item => item.ErrorType),
            _ => sortDescending
                ? query.OrderByDescending(item => item.Id)
                : query.OrderBy(item => item.Id)
        };

        // Get total count before pagination
        var totalCount = await query.CountAsync();

        // Apply pagination and materialise
        var offset = (page - 1) * pageSize;
        var entities = await query.Skip(offset).Take(pageSize).ToListAsync();

        // Project to DTO in memory
        // Use ExternalIdSnapshot as fallback if CSO was deleted (preserves historical external ID)
        var results = entities.Select(i => new ActivityRunProfileExecutionItemHeader
        {
            Id = i.Id,
            ExternalIdValue = i.ConnectedSystemObject?.ExternalIdAttributeValue?.ToStringNoName() ?? i.ExternalIdSnapshot,
            DisplayName = i.ConnectedSystemObject?.AttributeValues.FirstOrDefault(av => av.Attribute.Name.Equals("displayname", StringComparison.OrdinalIgnoreCase))?.StringValue,
            ConnectedSystemObjectType = i.ConnectedSystemObject?.Type?.Name,
            ErrorType = i.ErrorType,
            ObjectChangeType = i.ObjectChangeType
        }).ToList();

        // Build paged result set
        var pagedResultSet = new PagedResultSet<ActivityRunProfileExecutionItemHeader>
        {
            PageSize = pageSize,
            TotalResults = totalCount,
            CurrentPage = page,
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
        
    public async Task<ActivityRunProfileExecutionStats> GetActivityRunProfileExecutionStatsAsync(Guid activityId)
    {
        // Get total objects processed from the activity itself (tracks all objects in scope)
        var activity = await Repository.Database.Activities.FirstOrDefaultAsync(a => a.Id == activityId);
        var totalObjectsProcessed = activity?.ObjectsProcessed ?? 0;

        // Single query to get all counts grouped by change type, error status, and no-change reason
        // This replaces 15+ individual COUNT queries with one efficient GROUP BY query
        var rpeiQuery = Repository.Database.ActivityRunProfileExecutionItems
            .Where(q => q.Activity.Id == activityId);

        var aggregateData = await rpeiQuery
            .GroupBy(q => new
            {
                q.ObjectChangeType,
                HasError = q.ErrorType != null && q.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet,
                q.NoChangeReason
            })
            .Select(g => new
            {
                g.Key.ObjectChangeType,
                g.Key.HasError,
                g.Key.NoChangeReason,
                Count = g.Count()
            })
            .ToListAsync();

        // Get object type counts with names (separate query as it needs GROUP BY on type name)
        var objectTypeCounts = await rpeiQuery
            .Where(q => q.ConnectedSystemObject != null && q.ConnectedSystemObject.Type != null)
            .GroupBy(q => q.ConnectedSystemObject!.Type!.Name)
            .Select(g => new { TypeName = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TypeName, x => x.Count);

        var totalObjectTypes = objectTypeCounts.Count;

        // Get error type counts (separate query as it needs GROUP BY on error type)
        var errorTypeCounts = await rpeiQuery
            .Where(q => q.ErrorType != null && q.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .GroupBy(q => q.ErrorType!.Value)
            .Select(g => new { ErrorType = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ErrorType, x => x.Count);

        // Calculate totals from grouped data
        var totalObjectChangeCount = aggregateData.Sum(x => x.Count);
        var totalObjectErrors = aggregateData.Where(x => x.HasError).Sum(x => x.Count);

        // Import stats
        var totalCsoAdds = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Added).Sum(x => x.Count);
        var totalCsoUpdates = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Updated).Sum(x => x.Count);
        var totalCsoDeletes = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Deleted).Sum(x => x.Count);

        // Sync stats
        var totalProjections = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Projected).Sum(x => x.Count);
        var totalJoins = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Joined).Sum(x => x.Count);
        var totalAttributeFlows = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.AttributeFlow).Sum(x => x.Count);
        var totalDisconnections = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Disconnected).Sum(x => x.Count);
        var totalDisconnectedOutOfScope = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope).Sum(x => x.Count);
        var totalOutOfScopeRetainJoin = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.OutOfScopeRetainJoin).Sum(x => x.Count);
        var totalDriftCorrections = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.DriftCorrection).Sum(x => x.Count);

        // Export stats
        var totalProvisioned = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Provisioned).Sum(x => x.Count);
        var totalExported = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Exported).Sum(x => x.Count);
        var totalDeprovisioned = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Deprovisioned).Sum(x => x.Count);

        // Pending export stats (surfaced during sync for operator visibility)
        var totalPendingExports = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.PendingExport).Sum(x => x.Count);

        // Pending export reconciliation stats (populated during confirming import)
        // TotalPendingExportsConfirmed is stored directly on the Activity (not derived from RPEIs)
        var totalPendingExportsConfirmed = activity?.PendingExportsConfirmed ?? 0;
        // Retrying and Failed are derived from error type counts (already calculated above)
        errorTypeCounts.TryGetValue(ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed, out var totalPendingExportsRetrying);
        errorTypeCounts.TryGetValue(ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed, out var totalPendingExportsFailed);

        // NoChange stats
        var noChangeItems = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.NoChange).ToList();
        var totalNoChanges = noChangeItems.Sum(x => x.Count);
        var totalMvoNoAttributeChanges = noChangeItems.Where(x => x.NoChangeReason == NoChangeReason.MvoNoAttributeChanges).Sum(x => x.Count);
        var totalCsoAlreadyCurrent = noChangeItems.Where(x => x.NoChangeReason == NoChangeReason.CsoAlreadyCurrent).Sum(x => x.Count);

        return new ActivityRunProfileExecutionStats
        {
            ActivityId = activityId,
            TotalObjectsProcessed = totalObjectsProcessed,
            TotalObjectChangeCount = totalObjectChangeCount,
            TotalObjectErrors = totalObjectErrors,
            TotalObjectTypes = totalObjectTypes,
            ObjectTypeCounts = objectTypeCounts,
            ErrorTypeCounts = errorTypeCounts,

            // Import stats
            TotalCsoAdds = totalCsoAdds,
            TotalCsoUpdates = totalCsoUpdates,
            TotalCsoDeletes = totalCsoDeletes,

            // Sync stats
            TotalProjections = totalProjections,
            TotalJoins = totalJoins,
            TotalAttributeFlows = totalAttributeFlows,
            TotalDisconnections = totalDisconnections,
            TotalDisconnectedOutOfScope = totalDisconnectedOutOfScope,
            TotalOutOfScopeRetainJoin = totalOutOfScopeRetainJoin,
            TotalDriftCorrections = totalDriftCorrections,

            // Export stats
            TotalProvisioned = totalProvisioned,
            TotalExported = totalExported,
            TotalDeprovisioned = totalDeprovisioned,

            // Pending export stats
            TotalPendingExports = totalPendingExports,

            // Pending export reconciliation stats
            TotalPendingExportsConfirmed = totalPendingExportsConfirmed,
            TotalPendingExportsRetrying = totalPendingExportsRetrying,
            TotalPendingExportsFailed = totalPendingExportsFailed,

            // NoChange stats
#pragma warning disable CS0618 // Type or member is obsolete
            TotalNoChanges = totalNoChanges,
#pragma warning restore CS0618
            TotalMvoNoAttributeChanges = totalMvoNoAttributeChanges,
            TotalCsoAlreadyCurrent = totalCsoAlreadyCurrent,
        };
    }

    public async Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id)
    {
        return await Repository.Database.ActivityRunProfileExecutionItems
            .AsSplitQuery() // Use split query to avoid cartesian explosion from multiple collection includes
            // CSO includes
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.Type)
            // CSO -> MVO includes (for projected/joined CSOs to access the linked MVO)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.MetaverseObject)
            .ThenInclude(mvo => mvo!.Type)
            // CSO change includes (for import updates/deletes)
            .Include(q => q.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.Attribute)
            .Include(q => q.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .ThenInclude(vc => vc.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .ThenInclude(vc => vc.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            // For deletions, include the preserved object type to support deletion rule UI display
            .Include(q => q.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.DeletedObjectType)
            // MVO change includes (for future use when MetaverseObjectChange is populated during sync)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.Type)
            .SingleOrDefaultAsync(q => q.Id == id);
    }
    #endregion
}
