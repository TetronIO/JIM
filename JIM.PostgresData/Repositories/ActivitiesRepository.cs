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

    #region synchronisation related
    public async Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(
        Guid activityId,
        int page,
        int pageSize,
        string? searchQuery = null,
        string? sortBy = null,
        bool sortDescending = false)
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
        var results = entities.Select(i => new ActivityRunProfileExecutionItemHeader
        {
            Id = i.Id,
            ExternalIdValue = i.ConnectedSystemObject?.ExternalIdAttributeValue?.ToStringNoName(),
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
        return new ActivityRunProfileExecutionStats
        {
            ActivityId = activityId,
            TotalObjectChangeCount = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId),
            TotalObjectErrors = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId && q.ErrorType != null && q.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet),
            TotalObjectCreates = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId && q.ObjectChangeType == ObjectChangeType.Create),
            TotalObjectDeletes = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId && q.ObjectChangeType == ObjectChangeType.Delete),
            TotalObjectUpdates = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId && q.ObjectChangeType == ObjectChangeType.Update),
            TotalObjectTypes = await Repository.Database.ActivityRunProfileExecutionItems.Where(q => q.Activity.Id == activityId && q.ConnectedSystemObject != null).Select(q => q.ConnectedSystemObject!.Type).Distinct().CountAsync(),
            TotalMvoNoAttributeChanges = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q =>
                q.Activity.Id == activityId &&
                q.ObjectChangeType == ObjectChangeType.NoChange &&
                q.NoChangeReason == NoChangeReason.MvoNoAttributeChanges),
            TotalCsoAlreadyCurrent = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q =>
                q.Activity.Id == activityId &&
                q.ObjectChangeType == ObjectChangeType.NoChange &&
                q.NoChangeReason == NoChangeReason.CsoAlreadyCurrent),
        };
    }

    public async Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id)
    {
        return await Repository.Database.ActivityRunProfileExecutionItems
            .AsSplitQuery() // Use split query to avoid cartesian explosion from multiple collection includes
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.AttributeValues)
            .ThenInclude(av => av.Attribute)
            .Include(q => q.ConnectedSystemObject)
            .ThenInclude(cso => cso!.Type)
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
            .SingleOrDefaultAsync(q => q.Id == id);
    }
    #endregion
}
