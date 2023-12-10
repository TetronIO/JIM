using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Utility;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories
{
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
            int maxResults,
            QuerySortBy querySortBy = QuerySortBy.DateCreated)
        {
            // todo: include referenced properties

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

            var objects = from o in Repository.Database.Activities
                .Include(a => a.InitiatedBy)
                .ThenInclude(ib => ib.AttributeValues.Where(av => av.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
                .ThenInclude(av => av.Attribute)
                .Include(st => st.InitiatedBy)
                .ThenInclude(ib => ib.Type)
                .Where(a => a.ParentActivityId == null)
                select o;

            switch (querySortBy)
            {
                case QuerySortBy.DateCreated:
                    objects = objects.OrderByDescending(q => q.Created);
                    break;

                // todo: support more ways of sorting, i.e. by attribute value
            }

            // now just retrieve a page's worth of images from the results
            var grossCount = objects.Count();
            var offset = (page - 1) * pageSize;
            var itemsToGet = grossCount >= pageSize ? pageSize : grossCount;
            var results = await objects.Skip(offset).Take(itemsToGet).ToListAsync();

            // now with all the ids we know how many total results there are and so can populate paging info
            var pagedResultSet = new PagedResultSet<Activity>
            {
                PageSize = pageSize,
                TotalResults = grossCount,
                CurrentPage = page,
                QuerySortBy = querySortBy,
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

        public async Task<Activity?> GetActivityAsync(Guid id)
        {
            return await Repository.Database.Activities
                .Include(a => a.InitiatedBy)
                .ThenInclude(ib => ib.AttributeValues.Where(av => av.Attribute.Name == Constants.BuiltInAttributes.DisplayName))
                .ThenInclude(av => av.Attribute)
                .Include(st => st.InitiatedBy)
                .ThenInclude(ib => ib.Type)
                .SingleOrDefaultAsync(a => a.Id == id);
        }

        #region synchronisation related
        public async Task<PagedResultSet<ActivityRunProfileExecutionItemHeader>> GetActivityRunProfileExecutionItemHeadersAsync(Guid activityId, int page, int pageSize, int maxResults)
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

            var objects = from o in Repository.Database.ActivityRunProfileExecutionItems
                .Include(a => a.ConnectedSystemObject)
                .Where(a => a.Activity.Id == activityId)
                select o;

            // now just retrieve a page's worth of images from the results
            var grossCount = objects.Count();
            var offset = (page - 1) * pageSize;
            var itemsToGet = grossCount >= pageSize ? pageSize : grossCount;
            var results = await objects.Skip(offset).Take(itemsToGet).Select(i => new ActivityRunProfileExecutionItemHeader
            {
                Id = i.Id,
                ExternalIdValue = i.ConnectedSystemObject != null && i.ConnectedSystemObject.AttributeValues.Any(av => av.Attribute.IsExternalId) ? i.ConnectedSystemObject.AttributeValues.Single(av => av.Attribute.IsExternalId).ToString() : null,
                DisplayName = i.ConnectedSystemObject != null && i.ConnectedSystemObject.AttributeValues.Any(av => av.Attribute.Name.ToLower() == "displayname") ? i.ConnectedSystemObject.AttributeValues.Single(av => av.Attribute.Name.ToLower() == "displayname").StringValue : null,
                ConnectedSystemObjectType = i.ConnectedSystemObject != null ? i.ConnectedSystemObject.Type.Name : null,
                ErrorType = i.ErrorType,
                ObjectChangeType = i.ObjectChangeType                
            }).ToListAsync();

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
            if (page > pagedResultSet.TotalPages)
            {
                pagedResultSet.TotalResults = 0;
                pagedResultSet.Results.Clear();
                return pagedResultSet;
            }

            return pagedResultSet;
        }
        
        public async Task<ActivityRunProfileExecutionStats> GetActivityRunProfileExecutionStatsAsync(Guid activityId)
        {
            return new ActivityRunProfileExecutionStats
            {
                ActivityId = activityId,
                TotalObjectChangeCount = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId),
                TotalObjectErrors = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId && q.ErrorType != null),                
                TotalObjectCreates = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId && q.ObjectChangeType == ObjectChangeType.Create),
                TotalObjectDeletes = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId && q.ObjectChangeType == ObjectChangeType.Delete),
                TotalObjectUpdates = await Repository.Database.ActivityRunProfileExecutionItems.CountAsync(q => q.Activity.Id == activityId && q.ObjectChangeType == ObjectChangeType.Update),
                TotalObjectTypes = await Repository.Database.ActivityRunProfileExecutionItems.Where(q => q.Activity.Id == activityId && q.ConnectedSystemObject != null).Select(q => q.ConnectedSystemObject.Type).Distinct().CountAsync(),
            };
        }

        public async Task<ActivityRunProfileExecutionItem?> GetActivityRunProfileExecutionItemAsync(Guid id)
        {
            return await Repository.Database.ActivityRunProfileExecutionItems
                .Include(q => q.ConnectedSystemObject)
                .ThenInclude(cso => cso.AttributeValues)
                .ThenInclude(av => av.Attribute)
                .Include(q => q.ConnectedSystemObject)
                .ThenInclude(cso => cso.Type)
                .Include(q => q.ConnectedSystemObjectChange)
                .SingleOrDefaultAsync(q => q.Id == id);
        }
        #endregion
    }
}
