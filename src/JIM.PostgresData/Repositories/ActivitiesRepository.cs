using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
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

    public async Task UpdateActivityProgressOutOfBandAsync(Activity activity)
    {
        // Open an independent connection to bypass any in-flight transaction on the main DbContext.
        // This ensures progress updates are immediately visible to other sessions (e.g., UI polling).
        var connectionString = Repository.Database.Database.GetConnectionString();
        if (string.IsNullOrEmpty(connectionString))
        {
            // Fallback for in-memory test databases that have no connection string
            await UpdateActivityAsync(activity);
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"UPDATE ""Activities"" SET ""ObjectsProcessed"" = @processed, ""ObjectsToProcess"" = @toProcess, ""Message"" = @message WHERE ""Id"" = @id";
        command.Parameters.AddWithValue("processed", activity.ObjectsProcessed);
        command.Parameters.AddWithValue("toProcess", activity.ObjectsToProcess);
        command.Parameters.AddWithValue("message", (object?)activity.Message ?? DBNull.Value);
        command.Parameters.AddWithValue("id", activity.Id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<(int TotalWithErrors, int TotalRpeis)> GetActivityRpeiErrorCountsAsync(Guid activityId)
    {
        var counts = await Repository.Database.ActivityRunProfileExecutionItems
            .Where(r => r.Activity.Id == activityId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalRpeis = g.Count(),
                TotalWithErrors = g.Count(r => r.ErrorType != null && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet)
            })
            .FirstOrDefaultAsync();

        return counts != null
            ? (counts.TotalWithErrors, counts.TotalRpeis)
            : (0, 0);
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
            .AsNoTracking()
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
            .SingleOrDefaultAsync(a => a.Id == id);
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
        bool sortDescending = true)
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
        // Note: DataGenerationTemplate and HistoryRetentionCleanup are intentionally excluded
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
            // MVO change includes (for sync operations that produce MVO attribute changes)
            .Include(q => q.MetaverseObjectChange)
            .ThenInclude(c => c!.MetaverseObject)
            .ThenInclude(mvo => mvo!.AttributeValues)
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

    // -----------------------------------------------------------------------------------------------------------------
    // Bulk RPEI persistence (Phase 5 - Worker Database Performance Optimisation)
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Maximum number of parameters per SQL statement to stay under PostgreSQL's 65,535 limit.
    /// </summary>
    private const int MaxParametersPerStatement = 60000;

    public async Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        if (rpeis.Count == 0)
            return true;

        // Pre-generate IDs for all RPEIs (bypasses EF ValueGeneratedOnAdd)
        foreach (var rpei in rpeis)
        {
            if (rpei.Id == Guid.Empty)
                rpei.Id = Guid.NewGuid();
        }

        // Try raw SQL path first (production with real database).
        // BeginTransactionAsync throws InvalidOperationException in test environments (in-memory/mocked
        // providers) and NullReferenceException with mocked DbContext. We catch those narrowly to
        // detect the test environment, then fall back to EF tracking.
        //
        // IMPORTANT: Once raw SQL succeeds, any subsequent failure MUST propagate — never fall back
        // to EF's AddRange in production, as it triggers graph traversal causing identity conflicts
        // with shared MetaverseAttribute/MetaverseObjectType entities.
        IDbContextTransaction? transaction = null;
        try
        {
            // If a transaction is already active (e.g., caller opened one), reuse it.
            // Otherwise start a new one. This prevents InvalidOperationException when
            // FlushRpeisAsync is called during cross-page resolution where preceding
            // operations may have left a transaction open.
            var existingTransaction = Repository.Database.Database.CurrentTransaction;
            if (existingTransaction == null)
                transaction = await Repository.Database.Database.BeginTransactionAsync();

            await BulkInsertRpeisRawAsync(rpeis);

            // Bulk insert sync outcomes for all RPEIs (within the same transaction)
            var allOutcomes = rpeis.SelectMany(r => FlattenSyncOutcomes(r)).ToList();

            // Persist CSO change records that are linked to sync outcomes (PendingExportCreated snapshots)
            // before inserting outcomes, so the FK references resolve correctly.
            var outcomeCsoChanges = allOutcomes
                .Where(o => o.ConnectedSystemObjectChange != null)
                .Select(o => o.ConnectedSystemObjectChange!)
                .ToList();
            if (outcomeCsoChanges.Count > 0)
            {
                Repository.Database.AddRange(outcomeCsoChanges);
                await Repository.Database.SaveChangesAsync();
                // Set the FK on outcomes from the now-persisted change records
                foreach (var outcome in allOutcomes.Where(o => o.ConnectedSystemObjectChange != null))
                    outcome.ConnectedSystemObjectChangeId = outcome.ConnectedSystemObjectChange!.Id;
            }

            if (allOutcomes.Count > 0)
                await BulkInsertSyncOutcomesRawAsync(allOutcomes);

            if (transaction != null)
                await transaction.CommitAsync();

            return true; // Raw SQL used — RPEIs persisted outside EF change tracker
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // This catch ONLY handles test environment detection where transactions/raw SQL
            // are not available. If we already executed any SQL, this is a real error — rethrow.
            if (transaction != null)
                throw; // Transaction was created but SQL failed — real production error

            // Test environment fallback: RPEIs need explicit AddRange to be tracked by EF.
            // List<T> navigation properties don't trigger automatic change detection in
            // the in-memory provider — items must be explicitly added to the change tracker.
            var untracked = new List<ActivityRunProfileExecutionItem>();
            foreach (var rpei in rpeis)
            {
                try
                {
                    var entry = Repository.Database.Entry(rpei);
                    if (entry.State == EntityState.Detached)
                        untracked.Add(rpei);
                }
                catch (NullReferenceException)
                {
                    // Mocked DbContext: Entry() not available — treat as untracked
                    untracked.Add(rpei);
                }
            }

            // Pre-generate IDs and set FK references on sync outcomes before EF tracking.
            // FlattenSyncOutcomes assigns IDs and links ParentSyncOutcomeId/ActivityRunProfileExecutionItemId.
            // The outcomes themselves are NOT explicitly added via AddRange — EF's AddRange
            // traverses RPEI → SyncOutcomes navigation and discovers them automatically.
            // Explicitly adding them would cause duplicate key errors.
            foreach (var rpei in rpeis)
                FlattenSyncOutcomes(rpei);

            if (untracked.Count > 0)
                Repository.Database.AddRange(untracked);

            return false; // EF fallback used — RPEIs tracked by EF via AddRange
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }
    }

    public void DetachRpeisFromChangeTracker(List<ActivityRunProfileExecutionItem> rpeis)
    {
        foreach (var rpei in rpeis)
        {
            try
            {
                var entry = Repository.Database.Entry(rpei);
                if (entry.State != EntityState.Detached)
                    entry.State = EntityState.Detached;
            }
            catch (NullReferenceException)
            {
                // Mocked DbContext in tests — Entry() not available, nothing to detach
            }
        }
    }

    /// <inheritdoc />
    public async Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        var changes = rpeis
            .Where(r => r.ConnectedSystemObjectChange != null)
            .Select(r => r.ConnectedSystemObjectChange!)
            .ToList();

        if (changes.Count == 0)
            return;

        // Null out RPEI navigation properties to prevent EF graph traversal from
        // re-discovering already-persisted RPEIs (which would cause duplicate key violations).
        // The FK (ActivityRunProfileExecutionItemId) is already set.
        foreach (var change in changes)
            change.ActivityRunProfileExecutionItem = null;

        Repository.Database.AddRange(changes);
        await Repository.Database.SaveChangesAsync();
    }

    /// <summary>
    /// Bulk updates OutcomeSummary and error fields on already-persisted RPEIs,
    /// and inserts any new SyncOutcomes that were added after initial persistence.
    /// Used by confirming imports to merge reconciliation outcomes onto existing import RPEIs.
    /// </summary>
    public async Task BulkUpdateRpeiOutcomesAsync(
        List<ActivityRunProfileExecutionItem> rpeis,
        List<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes)
    {
        if (rpeis.Count == 0)
            return;

        IDbContextTransaction? transaction = null;
        try
        {
            var existingTransaction = Repository.Database.Database.CurrentTransaction;
            if (existingTransaction == null)
                transaction = await Repository.Database.Database.BeginTransactionAsync();

            // Bulk UPDATE OutcomeSummary and error fields on existing RPEIs
            await BulkUpdateRpeiFieldsRawAsync(rpeis);

            // Bulk INSERT new sync outcomes
            if (newOutcomes.Count > 0)
                await BulkInsertSyncOutcomesRawAsync(newOutcomes);

            if (transaction != null)
                await transaction.CommitAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // Test environment detection — same pattern as BulkInsertRpeisAsync
            if (transaction != null)
                throw;

            // EF fallback: mark modified fields as changed
            foreach (var rpei in rpeis)
            {
                try
                {
                    var entry = Repository.Database.Entry(rpei);
                    if (entry.State == EntityState.Detached)
                        Repository.Database.Attach(rpei);
                    entry.Property(r => r.OutcomeSummary).IsModified = true;
                    entry.Property(r => r.ErrorType).IsModified = true;
                    entry.Property(r => r.ErrorMessage).IsModified = true;
                }
                catch (NullReferenceException)
                {
                    // Mocked DbContext — property marking not available
                }
            }

            // EF will discover new outcomes via navigation property traversal
            // when the RPEI is already tracked — no explicit AddRange needed.
        }
        finally
        {
            if (transaction != null)
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// Bulk updates OutcomeSummary and error fields on already-persisted RPEIs using parameterised UPDATE statements.
    /// </summary>
    private async Task BulkUpdateRpeiFieldsRawAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        const int columnsPerRow = 4; // Id (WHERE) + OutcomeSummary, ErrorType, ErrorMessage
        var chunkSize = MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in ChunkList(rpeis, chunkSize))
        {
            // Use individual UPDATE statements per RPEI within a single round-trip
            var sql = new System.Text.StringBuilder();
            var parameters = new List<NpgsqlParameter>();

            for (var i = 0; i < chunk.Count; i++)
            {
                var offset = i * columnsPerRow;
                sql.Append(@"UPDATE ""ActivityRunProfileExecutionItems"" SET ""OutcomeSummary"" = @p");
                sql.Append(offset);
                sql.Append(@", ""ErrorType"" = @p");
                sql.Append(offset + 1);
                sql.Append(@", ""ErrorMessage"" = @p");
                sql.Append(offset + 2);
                sql.Append(@" WHERE ""Id"" = @p");
                sql.Append(offset + 3);
                sql.Append("; ");

                var rpei = chunk[i];
                parameters.Add(new NpgsqlParameter($"p{offset}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.OutcomeSummary ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 1}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = rpei.ErrorType.HasValue ? (object)(int)rpei.ErrorType.Value : DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 2}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.ErrorMessage ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 3}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = rpei.Id });
            }

            await Repository.Database.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Bulk inserts ActivityRunProfileExecutionItem rows using parameterised multi-row INSERT.
    /// Chunks automatically to stay within the PostgreSQL parameter limit.
    /// </summary>
    private async Task BulkInsertRpeisRawAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        const int columnsPerRow = 13;
        var chunkSize = MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in ChunkList(rpeis, chunkSize))
        {
            var sql = new System.Text.StringBuilder();
            sql.Append(@"INSERT INTO ""ActivityRunProfileExecutionItems"" (""Id"", ""ActivityId"", ""ObjectChangeType"", ""NoChangeReason"", ""ConnectedSystemObjectId"", ""ExternalIdSnapshot"", ""DisplayNameSnapshot"", ""ObjectTypeSnapshot"", ""ErrorType"", ""ErrorMessage"", ""ErrorStackTrace"", ""AttributeFlowCount"", ""OutcomeSummary"") VALUES ");

            // Use NpgsqlParameter objects with explicit NpgsqlDbType for nullable columns.
            // EF Core's RawSqlCommandBuilder passes DbParameter objects through directly to the
            // provider, bypassing the CLR type → store type mapping that fails for DBNull.Value.
            var parameters = new List<NpgsqlParameter>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"(@p{offset}, @p{offset + 1}, @p{offset + 2}, @p{offset + 3}, @p{offset + 4}, @p{offset + 5}, @p{offset + 6}, @p{offset + 7}, @p{offset + 8}, @p{offset + 9}, @p{offset + 10}, @p{offset + 11}, @p{offset + 12})");

                var rpei = chunk[i];
                parameters.Add(new NpgsqlParameter($"p{offset}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = rpei.Id });
                parameters.Add(new NpgsqlParameter($"p{offset + 1}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = rpei.ActivityId });
                parameters.Add(new NpgsqlParameter($"p{offset + 2}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)rpei.ObjectChangeType });
                parameters.Add(new NpgsqlParameter($"p{offset + 3}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = rpei.NoChangeReason.HasValue ? (object)(int)rpei.NoChangeReason.Value : DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 4}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)rpei.ConnectedSystemObjectId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 5}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.ExternalIdSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 6}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.DisplayNameSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 7}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.ObjectTypeSnapshot ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 8}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = rpei.ErrorType.HasValue ? (object)(int)rpei.ErrorType.Value : DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 9}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.ErrorMessage ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 10}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.ErrorStackTrace ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 11}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)rpei.AttributeFlowCount ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 12}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)rpei.OutcomeSummary ?? DBNull.Value });
            }

            await Repository.Database.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Bulk inserts ActivityRunProfileExecutionItemSyncOutcome rows using parameterised multi-row INSERT.
    /// Chunks automatically to stay within the PostgreSQL parameter limit.
    /// </summary>
    private async Task BulkInsertSyncOutcomesRawAsync(List<ActivityRunProfileExecutionItemSyncOutcome> outcomes)
    {
        const int columnsPerRow = 10;
        var chunkSize = MaxParametersPerStatement / columnsPerRow;

        foreach (var chunk in ChunkList(outcomes, chunkSize))
        {
            var sql = new System.Text.StringBuilder();
            sql.Append(@"INSERT INTO ""ActivityRunProfileExecutionItemSyncOutcomes"" (""Id"", ""ActivityRunProfileExecutionItemId"", ""ParentSyncOutcomeId"", ""OutcomeType"", ""TargetEntityId"", ""TargetEntityDescription"", ""DetailCount"", ""DetailMessage"", ""Ordinal"", ""ConnectedSystemObjectChangeId"") VALUES ");

            var parameters = new List<NpgsqlParameter>();
            for (var i = 0; i < chunk.Count; i++)
            {
                if (i > 0) sql.Append(", ");
                var offset = i * columnsPerRow;
                sql.Append($"(@p{offset}, @p{offset + 1}, @p{offset + 2}, @p{offset + 3}, @p{offset + 4}, @p{offset + 5}, @p{offset + 6}, @p{offset + 7}, @p{offset + 8}, @p{offset + 9})");

                var outcome = chunk[i];
                parameters.Add(new NpgsqlParameter($"p{offset}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = outcome.Id });
                parameters.Add(new NpgsqlParameter($"p{offset + 1}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = outcome.ActivityRunProfileExecutionItemId });
                parameters.Add(new NpgsqlParameter($"p{offset + 2}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)outcome.ParentSyncOutcomeId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 3}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (int)outcome.OutcomeType });
                parameters.Add(new NpgsqlParameter($"p{offset + 4}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)outcome.TargetEntityId ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 5}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)outcome.TargetEntityDescription ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 6}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = (object?)outcome.DetailCount ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 7}", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)outcome.DetailMessage ?? DBNull.Value });
                parameters.Add(new NpgsqlParameter($"p{offset + 8}", NpgsqlTypes.NpgsqlDbType.Integer) { Value = outcome.Ordinal });
                parameters.Add(new NpgsqlParameter($"p{offset + 9}", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)outcome.ConnectedSystemObjectChangeId ?? DBNull.Value });
            }

            await Repository.Database.Database.ExecuteSqlRawAsync(sql.ToString(), parameters.ToArray());
        }
    }

    /// <summary>
    /// Flattens the sync outcome tree for an RPEI into a list, pre-generating IDs
    /// and setting FK references for bulk insertion.
    /// </summary>
    private static List<ActivityRunProfileExecutionItemSyncOutcome> FlattenSyncOutcomes(ActivityRunProfileExecutionItem rpei)
    {
        var result = new List<ActivityRunProfileExecutionItemSyncOutcome>();

        // SyncOutcomeBuilder adds all nodes (root + children) to rpei.SyncOutcomes as a flat list,
        // AND also builds a parent→Children tree. If we pass the full flat list to
        // FlattenOutcomesRecursive, children get visited twice (once from the flat list, once from
        // parent.Children recursion), causing duplicate inserts.
        // Start from root outcomes only and let recursion reach children via parent.Children.
        var roots = rpei.SyncOutcomes.Where(o => o.ParentSyncOutcome == null && o.ParentSyncOutcomeId == null).ToList();
        FlattenOutcomesRecursive(roots, rpei.Id, null, result);
        return result;
    }

    private static void FlattenOutcomesRecursive(
        List<ActivityRunProfileExecutionItemSyncOutcome> outcomes,
        Guid rpeiId,
        Guid? parentId,
        List<ActivityRunProfileExecutionItemSyncOutcome> result)
    {
        foreach (var outcome in outcomes)
        {
            if (outcome.Id == Guid.Empty)
                outcome.Id = Guid.NewGuid();

            outcome.ActivityRunProfileExecutionItemId = rpeiId;
            outcome.ParentSyncOutcomeId = parentId;
            result.Add(outcome);

            if (outcome.Children.Count > 0)
                FlattenOutcomesRecursive(outcome.Children, rpeiId, outcome.Id, result);
        }
    }

    private static List<List<T>> ChunkList<T>(List<T> source, int chunkSize)
    {
        var chunks = new List<List<T>>();
        for (var i = 0; i < source.Count; i += chunkSize)
        {
            chunks.Add(source.GetRange(i, Math.Min(chunkSize, source.Count - i)));
        }
        return chunks;
    }
}
