using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
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
        // Use detach-safe update to avoid graph traversal on detached Activity entities.
        // After ClearChangeTracker(), Update() would traverse RPEIs → CSO → MVO → Type → Attributes
        // causing identity conflicts with shared MetaverseAttribute/MetaverseObjectType instances.
        Repository.UpdateDetachedSafe(activity);
        await Repository.Database.SaveChangesAsync();
    }

    public async Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(Guid activityId)
    {
        var counts = await Repository.Database.ActivityRunProfileExecutionItems
            .Where(r => r.Activity.Id == activityId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalRpeis = g.Count(),
                TotalWithErrors = g.Count(r => r.ErrorType != null && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet),
                TotalUnhandledErrors = g.Count(r => r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError)
            })
            .FirstOrDefaultAsync();

        return counts != null
            ? (counts.TotalWithErrors, counts.TotalRpeis, counts.TotalUnhandledErrors)
            : (0, 0, 0);
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
        Guid? initiatedById = null,
        IEnumerable<ActivityTargetOperationType>? operationFilter = null,
        IEnumerable<ActivityOutcomeType>? outcomeFilter = null,
        IEnumerable<ActivityTargetType>? typeFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        bool? hasChildActivities = null)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var query = Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ParentActivityId == null)
            .AsQueryable();

        // Apply initiated by filter (searches by InitiatedById which covers both MVO and ApiKey)
        if (initiatedById.HasValue)
        {
            query = query.Where(a => a.InitiatedById == initiatedById.Value);
        }

        // Apply operation filter
        if (operationFilter != null)
        {
            var operations = operationFilter.ToList();
            if (operations.Count > 0)
                query = query.Where(a => operations.Contains(a.TargetOperationType));
        }

        // Apply type filter
        if (typeFilter != null)
        {
            var types = typeFilter.ToList();
            if (types.Count > 0)
                query = query.Where(a => types.Contains(a.TargetType));
        }

        // Apply status filter
        if (statusFilter != null)
        {
            var statuses = statusFilter.ToList();
            if (statuses.Count > 0)
                query = query.Where(a => statuses.Contains(a.Status));
        }

        // Apply outcome filter (activities that have > 0 for any of the selected outcome stat types)
        if (outcomeFilter != null)
        {
            var outcomes = outcomeFilter.ToList();
            if (outcomes.Count > 0)
            {
                query = query.Where(a =>
                    (outcomes.Contains(ActivityOutcomeType.Added) && a.TotalAdded > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Updated) && a.TotalUpdated > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Deleted) && a.TotalDeleted > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Projected) && a.TotalProjected > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Joined) && a.TotalJoined > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.AttributeFlows) && a.TotalAttributeFlows > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Disconnected) && a.TotalDisconnected > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.DriftCorrections) && a.TotalDriftCorrections > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Provisioned) && a.TotalProvisioned > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Exported) && a.TotalExported > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Deprovisioned) && a.TotalDeprovisioned > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Created) && a.TotalCreated > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.PendingExports) && a.TotalPendingExports > 0) ||
                    (outcomes.Contains(ActivityOutcomeType.Errors) && a.TotalErrors > 0));
            }
        }

        // Apply search filter (target name, context, and initiated by name)
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchLower = searchQuery.ToLower();
            query = query.Where(a =>
                (a.TargetName != null && a.TargetName.ToLower().Contains(searchLower)) ||
                (a.TargetContext != null && a.TargetContext.ToLower().Contains(searchLower)) ||
                (a.InitiatedByName != null && a.InitiatedByName.ToLower().Contains(searchLower)));
        }

        // Apply child activities filter
        if (hasChildActivities == true)
        {
            query = query.Where(a => Repository.Database.Activities.Any(c => c.ParentActivityId == a.Id));
        }
        else if (hasChildActivities == false)
        {
            query = query.Where(a => !Repository.Database.Activities.Any(c => c.ParentActivityId == a.Id));
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
            .SingleOrDefaultAsync(a => a.Id == id);
    }

    public async Task<List<Activity>> GetChildActivitiesAsync(Guid parentActivityId)
    {
        return await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ParentActivityId == parentActivityId)
            .OrderBy(a => a.Created)
            .ToListAsync();
    }

    public async Task<Dictionary<Guid, int>> GetChildActivityCountsAsync(IEnumerable<Guid> activityIds)
    {
        var ids = activityIds.ToList();
        return await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ParentActivityId != null && ids.Contains(a.ParentActivityId.Value))
            .GroupBy(a => a.ParentActivityId!.Value)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ParentId, x => x.Count);
    }

    /// <summary>
    /// Retrieves a page's worth of worker task activities - operations executed by the worker service
    /// such as run profile executions, data generation, and connected system operations.
    /// </summary>
    public async Task<PagedResultSet<Activity>> GetWorkerTaskActivitiesAsync(
        int page,
        int pageSize,
        IEnumerable<string>? connectedSystemFilter = null,
        IEnumerable<string>? runProfileFilter = null,
        IEnumerable<ActivityStatus>? statusFilter = null,
        string? initiatedByFilter = null,
        string? sortBy = null,
        bool sortDescending = true,
        bool? hasChildActivities = null)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var query = BuildWorkerTaskQuery();

        // Apply filters
        var connectedSystems = connectedSystemFilter?.ToList();
        if (connectedSystems is { Count: > 0 })
            query = query.Where(a => a.TargetContext != null && connectedSystems.Contains(a.TargetContext));

        var runProfiles = runProfileFilter?.ToList();
        if (runProfiles is { Count: > 0 })
            query = query.Where(a => a.TargetName != null && runProfiles.Contains(a.TargetName));

        var statuses = statusFilter?.ToList();
        if (statuses is { Count: > 0 })
            query = query.Where(a => statuses.Contains(a.Status));

        if (!string.IsNullOrWhiteSpace(initiatedByFilter))
        {
            var filterLower = initiatedByFilter.ToLower();
            query = query.Where(a => a.InitiatedByName != null && a.InitiatedByName.ToLower().Contains(filterLower));
        }

        // Apply child activities filter
        if (hasChildActivities == true)
        {
            query = query.Where(a => Repository.Database.Activities.Any(c => c.ParentActivityId == a.Id));
        }
        else if (hasChildActivities == false)
        {
            query = query.Where(a => !Repository.Database.Activities.Any(c => c.ParentActivityId == a.Id));
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

    public async Task<ActivityFilterOptions> GetWorkerTaskActivityFilterOptionsAsync()
    {
        var query = BuildWorkerTaskQuery();

        var connectedSystems = await query
            .Where(a => a.TargetContext != null)
            .Select(a => a.TargetContext!)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync();

        var runProfiles = await query
            .Where(a => a.TargetName != null)
            .Select(a => a.TargetName!)
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync();

        return new ActivityFilterOptions
        {
            ConnectedSystems = connectedSystems,
            RunProfiles = runProfiles
        };
    }

    /// <summary>
    /// Builds the base query for worker task activities, filtering to parent activities
    /// with worker task target types and operation types.
    /// </summary>
    private IQueryable<Activity> BuildWorkerTaskQuery()
    {
        // Worker task activity types - run profile executions and connected system operations
        // Note: ExampleDataTemplate and HistoryRetentionCleanup are intentionally excluded
        var workerTaskTargetTypes = new[]
        {
            ActivityTargetType.ConnectedSystemRunProfile,
            ActivityTargetType.ConnectedSystem
        };

        // Worker task operations
        var workerTaskOperations = new[]
        {
            ActivityTargetOperationType.Execute,
            ActivityTargetOperationType.Clear,
            ActivityTargetOperationType.Delete
        };

        return Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ParentActivityId == null)
            .Where(a => workerTaskTargetTypes.Contains(a.TargetType) && workerTaskOperations.Contains(a.TargetOperationType))
            .AsQueryable();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Schedule execution queries
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<List<Activity>> GetActivitiesByScheduleExecutionAsync(Guid scheduleExecutionId)
    {
        return await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ScheduleExecutionId == scheduleExecutionId)
            .OrderBy(a => a.ScheduleStepIndex)
            .ThenBy(a => a.Created)
            .ToListAsync();
    }

    public async Task<List<Activity>> GetActivitiesByScheduleExecutionStepAsync(Guid scheduleExecutionId, int stepIndex)
    {
        return await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.ScheduleExecutionId == scheduleExecutionId && a.ScheduleStepIndex == stepIndex)
            .OrderBy(a => a.Created)
            .ToListAsync();
    }

    // -----------------------------------------------------------------------------------------------------------------
    // History retention cleanup queries
    // -----------------------------------------------------------------------------------------------------------------

    public async Task<DateTime?> GetLastHistoryCleanupTimeAsync()
    {
        return await Repository.Database.Activities
            .AsNoTracking()
            .Where(a => a.TargetType == ActivityTargetType.HistoryRetentionCleanup)
            .OrderByDescending(a => a.Created)
            .Select(a => (DateTime?)a.Created)
            .FirstOrDefaultAsync();
    }

    #region synchronisation related
    public async Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
        Guid activityId,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false,
        IEnumerable<string>? objectTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemErrorType>? errorTypeFilter = null,
        IEnumerable<ActivityRunProfileExecutionItemSyncOutcomeType>? outcomeTypeFilter = null)
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

        // Apply object type filter if specified (falls back to ObjectTypeSnapshot when CSO/Type is null)
        if (objectTypeFilter != null)
        {
            var objectTypes = objectTypeFilter.ToList();
            if (objectTypes.Count > 0)
            {
                query = query.Where(a =>
                    (a.ConnectedSystemObject != null &&
                     a.ConnectedSystemObject.Type != null &&
                     objectTypes.Contains(a.ConnectedSystemObject.Type.Name)) ||
                    (a.ObjectTypeSnapshot != null &&
                     objectTypes.Contains(a.ObjectTypeSnapshot)));
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

        // Apply outcome type filter if specified — matches against the denormalised OutcomeSummary string
        if (outcomeTypeFilter != null)
        {
            var outcomeTypes = outcomeTypeFilter.ToList();
            if (outcomeTypes.Count > 0)
            {
                // Build a predicate that requires at least one of the selected outcome types
                // to appear in the OutcomeSummary string (e.g., "Projected:" prefix match)
                query = query.Where(a =>
                    a.OutcomeSummary != null &&
                    outcomeTypes.Any(ot => a.OutcomeSummary.Contains(ot.ToString() + ":")));
            }
        }

        // Apply search filter - search on display name and external ID (case-insensitive).
        // Object type is excluded from search as it has a dedicated filter control.
        // Falls back to snapshot fields when CSO navigation is null.
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var searchPattern = $"%{searchQuery}%";
            query = query.Where(item =>
                // Search display name (live CSO attribute)
                (item.ConnectedSystemObject != null &&
                 item.ConnectedSystemObject.AttributeValues.Any(av =>
                    EF.Functions.ILike(av.Attribute.Name, "displayname") &&
                    av.StringValue != null &&
                    EF.Functions.ILike(av.StringValue, searchPattern))) ||
                // Search display name (snapshot fallback)
                (item.DisplayNameSnapshot != null &&
                 EF.Functions.ILike(item.DisplayNameSnapshot, searchPattern)) ||
                // Search external ID (live CSO attribute)
                (item.ConnectedSystemObject != null &&
                 item.ConnectedSystemObject.AttributeValues.Any(av =>
                    av.AttributeId == item.ConnectedSystemObject.ExternalIdAttributeId &&
                    av.StringValue != null &&
                    EF.Functions.ILike(av.StringValue, searchPattern))) ||
                // Search external ID (snapshot fallback)
                (item.ExternalIdSnapshot != null &&
                 EF.Functions.ILike(item.ExternalIdSnapshot, searchPattern)));
        }

        // Apply sorting
        query = sortBy?.ToLower() switch
        {
            "externalid" => sortDescending
                ? query.OrderByDescending(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => av.AttributeId == item.ConnectedSystemObject.ExternalIdAttributeId)
                        .Select(av => av.StringValue)
                        .FirstOrDefault() ?? item.ExternalIdSnapshot
                    : item.ExternalIdSnapshot)
                : query.OrderBy(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => av.AttributeId == item.ConnectedSystemObject.ExternalIdAttributeId)
                        .Select(av => av.StringValue)
                        .FirstOrDefault() ?? item.ExternalIdSnapshot
                    : item.ExternalIdSnapshot),
            "displayname" or "name" => sortDescending
                ? query.OrderByDescending(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => EF.Functions.ILike(av.Attribute.Name, "displayname"))
                        .Select(av => av.StringValue)
                        .FirstOrDefault() ?? item.DisplayNameSnapshot
                    : item.DisplayNameSnapshot)
                : query.OrderBy(item => item.ConnectedSystemObject != null
                    ? item.ConnectedSystemObject.AttributeValues
                        .Where(av => EF.Functions.ILike(av.Attribute.Name, "displayname"))
                        .Select(av => av.StringValue)
                        .FirstOrDefault() ?? item.DisplayNameSnapshot
                    : item.DisplayNameSnapshot),
            "type" or "objecttype" => sortDescending
                ? query.OrderByDescending(item => item.ConnectedSystemObject != null && item.ConnectedSystemObject.Type != null
                    ? item.ConnectedSystemObject.Type.Name
                    : item.ObjectTypeSnapshot)
                : query.OrderBy(item => item.ConnectedSystemObject != null && item.ConnectedSystemObject.Type != null
                    ? item.ConnectedSystemObject.Type.Name
                    : item.ObjectTypeSnapshot),
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
        // Use snapshot fields as fallback if CSO was deleted (preserves historical display data)
        var results = entities.Select(i => new ActivityRunProfileExecutionItemHeader
        {
            Id = i.Id,
            ExternalIdValue = i.ConnectedSystemObject?.ExternalIdAttributeValue?.ToStringNoName() ?? i.ExternalIdSnapshot,
            DisplayName = i.ConnectedSystemObject?.AttributeValues.FirstOrDefault(av => av.Attribute.Name.Equals("displayname", StringComparison.OrdinalIgnoreCase))?.StringValue
                ?? i.DisplayNameSnapshot,
            ConnectedSystemObjectType = i.ConnectedSystemObject?.Type?.Name
                ?? i.ObjectTypeSnapshot,
            ErrorType = i.ErrorType,
            ObjectChangeType = i.ObjectChangeType,
            OutcomeSummary = i.OutcomeSummary
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

        var rpeiQuery = Repository.Database.ActivityRunProfileExecutionItems
            .Where(q => q.Activity.Id == activityId);

        // Check if this activity has sync outcome data (phases 1-3 of outcome graph).
        // If outcomes exist, derive stats from outcome nodes for richer counting
        // (e.g., multi-system exports count each target system separately).
        // If no outcomes exist (legacy data or tracking level = None), fall back to RPEI ObjectChangeType counting.
        var hasOutcomes = await Repository.Database.ActivityRunProfileExecutionItemSyncOutcomes
            .AnyAsync(o => o.ActivityRunProfileExecutionItem.Activity.Id == activityId);

        // --- RPEI-based aggregate data (always needed for shared stats, NoChange, errors, and RPEI-only types) ---
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

        // Get object type counts with names (separate query as it needs GROUP BY on type name).
        // Falls back to ObjectTypeSnapshot when the CSO/Type navigation is null (e.g. export RPEIs
        // where the CSO was deleted, or the snapshot was populated but FK not retained).
        var objectTypeCounts = await rpeiQuery
            .Select(q => q.ConnectedSystemObject != null && q.ConnectedSystemObject.Type != null
                ? q.ConnectedSystemObject.Type.Name
                : q.ObjectTypeSnapshot)
            .Where(name => name != null)
            .GroupBy(name => name!)
            .Select(g => new { TypeName = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TypeName, x => x.Count);

        var totalObjectTypes = objectTypeCounts.Count;

        // Get error type counts (separate query as it needs GROUP BY on error type)
        var errorTypeCounts = await rpeiQuery
            .Where(q => q.ErrorType != null && q.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            .GroupBy(q => q.ErrorType!.Value)
            .Select(g => new { ErrorType = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ErrorType, x => x.Count);

        // Shared stats always come from RPEIs
        var totalObjectChangeCount = aggregateData.Sum(x => x.Count);
        var totalObjectErrors = aggregateData.Where(x => x.HasError).Sum(x => x.Count);

        // --- Outcome-based or RPEI-based stats depending on whether outcomes exist ---
        int totalCsoAdds, totalCsoUpdates, totalCsoDeletes;
        int totalProjections, totalJoins, totalAttributeFlows;
        int totalDisconnections, totalDisconnectedOutOfScope;
        int totalExported, totalDeprovisioned;
        int totalPendingExportsFromOutcomes;
        int totalDriftCorrections;
        int totalProvisioned;

        if (hasOutcomes)
        {
            // Derive stats from outcome nodes — counts outcome actions across all RPEIs.
            // This gives richer semantics: e.g., one object exported to 2 systems = 2 Exported outcomes.
            var outcomeCounts = await Repository.Database.ActivityRunProfileExecutionItemSyncOutcomes
                .Where(o => o.ActivityRunProfileExecutionItem.Activity.Id == activityId)
                .GroupBy(o => o.OutcomeType)
                .Select(g => new { OutcomeType = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.OutcomeType, x => x.Count);

            // Import stats from outcomes
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded, out totalCsoAdds);
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, out totalCsoUpdates);
            // Deletions: CsoDeleted (sync-phase actual deletions) + DeletionDetected (import-phase detection)
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted, out totalCsoDeletes);
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected, out var totalDeletionDetected);
            totalCsoDeletes += totalDeletionDetected;

            // Sync stats from outcomes
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.Projected, out totalProjections);
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.Joined, out totalJoins);
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, out totalAttributeFlows);
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected, out totalDisconnections);
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope, out totalDisconnectedOutOfScope);

            // Export stats from outcomes
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.Exported, out totalExported);
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned, out totalDeprovisioned);

            // Pending export stats from outcomes
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated, out totalPendingExportsFromOutcomes);

            // Drift correction from outcomes
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection, out totalDriftCorrections);

            // Provisioned from outcomes (outcome-only concept, no legacy fallback)
            outcomeCounts.TryGetValue(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned, out totalProvisioned);
        }
        else
        {
            // Legacy fallback: derive stats from RPEI ObjectChangeType (pre-outcome graph behaviour)
            totalCsoAdds = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Added).Sum(x => x.Count);
            totalCsoUpdates = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Updated).Sum(x => x.Count);
            totalCsoDeletes = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Deleted).Sum(x => x.Count);

            totalProjections = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Projected).Sum(x => x.Count);
            totalJoins = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Joined).Sum(x => x.Count);
            totalAttributeFlows = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.AttributeFlow).Sum(x => x.Count);

            totalDisconnections = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Disconnected).Sum(x => x.Count);
            totalDisconnectedOutOfScope = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope).Sum(x => x.Count);

            totalExported = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Exported).Sum(x => x.Count);
            totalDeprovisioned = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Deprovisioned).Sum(x => x.Count);

            totalPendingExportsFromOutcomes = 0;

            totalDriftCorrections = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.DriftCorrection).Sum(x => x.Count);

            totalProvisioned = 0; // Provisioned is an outcome-only concept; no ObjectChangeType equivalent
        }

        // --- Stats that always come from RPEIs (no outcome type equivalent) ---
        var totalOutOfScopeRetainJoin = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.OutOfScopeRetainJoin).Sum(x => x.Count);
        var totalCreated = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.Created).Sum(x => x.Count);

        // Pending export stats: use outcome-based count when available, otherwise fall back to RPEI count
        var totalPendingExportsFromRpeis = aggregateData.Where(x => x.ObjectChangeType == ObjectChangeType.PendingExport).Sum(x => x.Count);
        var totalPendingExports = hasOutcomes ? totalPendingExportsFromOutcomes : totalPendingExportsFromRpeis;

        // Pending export reconciliation stats (populated during confirming import)
        // TotalPendingExportsConfirmed is stored directly on the Activity (not derived from RPEIs)
        var totalPendingExportsConfirmed = activity?.PendingExportsConfirmed ?? 0;
        // Retrying and Failed are derived from error type counts (already calculated above)
        errorTypeCounts.TryGetValue(ActivityRunProfileExecutionItemErrorType.ExportNotConfirmed, out var totalPendingExportsRetrying);
        errorTypeCounts.TryGetValue(ActivityRunProfileExecutionItemErrorType.ExportConfirmationFailed, out var totalPendingExportsFailed);

        // NoChange stats (always from RPEIs — no outcome equivalent)
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
            TotalProvisioned = totalProvisioned,

            // Direct creation stats
            TotalCreated = totalCreated,

            // Export stats
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
            .ThenInclude(cso => cso!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
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
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
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
            // MVO change includes (for sync operations that produce MVO attribute changes)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
            .ThenInclude(av => av.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.Type)
            // MVO change attribute detail (for displaying MVO attribute changes in outcome tree)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.Attribute)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .ThenInclude(vc => vc.ReferenceValue)
            .ThenInclude(rv => rv!.Type)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .ThenInclude(vc => vc.ReferenceValue)
            .ThenInclude(rv => rv!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            // Sync outcome tree (causality chain)
            .Include(q => q.SyncOutcomes)
            // PendingExportCreated outcome CSO change snapshots (for rendering attribute detail)
            .Include(q => q.SyncOutcomes)
            .ThenInclude(o => o.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.Attribute)
            .Include(q => q.SyncOutcomes)
            .ThenInclude(o => o.ConnectedSystemObjectChange)
            .ThenInclude(c => c!.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .SingleOrDefaultAsync(q => q.Id == id);
    }
    #endregion

    // Bulk RPEI operations (BulkInsertRpeisAsync, BulkUpdateRpeiOutcomesAsync,
    // PersistRpeiCsoChangesAsync, DetachRpeisFromChangeTracker, UpdateActivityProgressOutOfBandAsync)
    // have been moved to PostgresData.SyncRepository.RpeiOperations.cs — they are Worker-only
    // and no longer part of the shared IActivityRepository interface.
}
