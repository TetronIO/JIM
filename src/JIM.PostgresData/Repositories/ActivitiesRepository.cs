using JIM.Data.Repositories;
using JIM.Models.Activities;
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
                .Include(a => a.RunProfile)
                .SingleOrDefaultAsync(a => a.Id == id);
        }
    }
}
