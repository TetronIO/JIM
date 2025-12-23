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
            .Include(a => a.InitiatedBy)
            .ThenInclude(ib => ib!.AttributeValues.Where(av => av.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
            .ThenInclude(av => av.Attribute)
            .Include(st => st.InitiatedBy)
            .ThenInclude(ib => ib!.Type)
            .Where(a => a.ParentActivityId == null)
            .AsQueryable();

        // Apply initiated by filter
        if (initiatedById.HasValue)
        {
            query = query.Where(a => a.InitiatedBy != null && a.InitiatedBy.Id == initiatedById.Value);
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
            .Include(a => a.InitiatedBy)
            .ThenInclude(ib => ib!.AttributeValues.Where(av => av.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
            .ThenInclude(av => av.Attribute)
            .Include(st => st.InitiatedBy)
            .ThenInclude(ib => ib!.Type)
            .SingleOrDefaultAsync(a => a.Id == id);
    }

    #region synchronisation related
    public async Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(Guid activityId, int page, int pageSize)
    {
        if (pageSize < 1)
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be a positive number");

        if (page < 1)
            page = 1;

        // limit page size to avoid increasing latency unnecessarily
        if (pageSize > 100)
            pageSize = 100;

        var objects = from o in Repository.Database.ActivityRunProfileExecutionItems
                .AsSplitQuery() // Use split query to avoid cartesian explosion from multiple collection includes
                .Include(a => a.ConnectedSystemObject)
                    .ThenInclude(cso => cso!.Type)
                .Include(a => a.ConnectedSystemObject)
                    .ThenInclude(cso => cso!.AttributeValues)
                        .ThenInclude(av => av.Attribute)
                .Where(a => a.Activity.Id == activityId)
            select o;

        // now just retrieve a page's worth of images from the results
        var grossCount = objects.Count();
        var offset = (page - 1) * pageSize;
        var itemsToGet = grossCount >= pageSize ? pageSize : grossCount;
        // Materialize the entities first, then project to DTO in memory
        var entities = await objects.Skip(offset).Take(itemsToGet).ToListAsync();
        var results = entities.Select(i => new ActivityRunProfileExecutionItemHeader
        {
            Id = i.Id,
            ExternalIdValue = i.ConnectedSystemObject?.ExternalIdAttributeValue?.ToStringNoName(),
            DisplayName = i.ConnectedSystemObject?.AttributeValues.FirstOrDefault(av => av.Attribute.Name.Equals("displayname", StringComparison.OrdinalIgnoreCase))?.StringValue,
            ConnectedSystemObjectType = i.ConnectedSystemObject?.Type.Name,
            ErrorType = i.ErrorType,
            ObjectChangeType = i.ObjectChangeType
        }).ToList();

        // now with all the ids we know how many total results there are and so can populate paging info
        var pagedResultSet = new PagedResultSet<ActivityRunProfileExecutionItemHeader>
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
