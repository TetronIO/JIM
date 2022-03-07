using JIM.Data.Repositories;
using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories
{
    public class MetaverseRepository : IMetaverseRepository
    {
        private PostgresDataRepository Repository { get; }

        internal MetaverseRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public async Task<IList<MetaverseObjectType>> GetMetaverseObjectTypesAsync()
        {
            return await Repository.Database.MetaverseObjectTypes.Include(q => q.Attributes).OrderBy(x => x.Name).ToListAsync();
        }

        public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(int id)
        {
            return await Repository.Database.MetaverseObjectTypes.Include(q => q.Attributes).SingleOrDefaultAsync(x => x.Id == id);
        }

        public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(string name)
        {
            return await Repository.Database.MetaverseObjectTypes.Include(q => q.Attributes).SingleOrDefaultAsync(q => q.Name == name);
        }

        public async Task<IList<MetaverseAttribute>?> GetMetaverseAttributesAsync()
        {
            return await Repository.Database.MetaverseAttributes.OrderBy(x => x.Name).ToListAsync();
        }

        public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id)
        {
            return await Repository.Database.MetaverseAttributes.SingleOrDefaultAsync(x => x.Id == id);
        }

        public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name)
        {
            return await Repository.Database.MetaverseAttributes.SingleOrDefaultAsync(x => x.Name == name);
        }

        public async Task<MetaverseObject?> GetMetaverseObjectAsync(int id)
        {
            return await Repository.Database.MetaverseObjects.Include(q => q.AttributeValues).Include(q => q.Type).SingleOrDefaultAsync(x => x.Id == id);
        }

        public async Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            await Repository.Database.SaveChangesAsync();
        }

        public async Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            Repository.Database.MetaverseObjects.Add(metaverseObject);
            await Repository.Database.SaveChangesAsync();
        }

        public async Task<MetaverseObject?> GetMetaverseObjectByTypeAndAttributeAsync(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue)
        {
            var av = await Repository.Database.MetaverseObjectAttributeValues.Include(q => q.MetaverseObject).SingleOrDefaultAsync(av =>
                 av.Attribute.Id == metaverseAttribute.Id &&
                 av.StringValue != null && av.StringValue == attributeValue &&
                 av.MetaverseObject.Type.Id == metaverseObjectType.Id);

            if (av != null)
                return av.MetaverseObject;

            return null;
        }

        public async Task<int> GetMetaverseObjectCountAsync()
        {
            return await Repository.Database.MetaverseObjects.CountAsync();
        }

        public async Task<int> GetMetaverseObjectOfTypeCountAsync(int metaverseObjectTypeId)
        {
            return await Repository.Database.MetaverseObjects.Where(x => x.Type.Id == metaverseObjectTypeId).CountAsync();
        }

        public async Task<PagedResultSet<MetaverseObject>> GetMetaverseObjectsOfTypeAsync(
            int metaverseObjectTypeId,
            int page = 1,
            int pageSize = 20,
            int maxResults = 500,
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

            var objects = from o in Repository.Database.MetaverseObjects.Where(q => q.Type.Id == metaverseObjectTypeId)
                          select o;

            if (queryRange != QueryRange.Forever)
            {
                switch (queryRange)
                {
                    case QueryRange.LastYear:
                        objects = objects.Where(q => q.Created >= DateTime.Now - TimeSpan.FromDays(365));
                        break;
                    case QueryRange.LastMonth:
                        objects = objects.Where(q => q.Created >= DateTime.Now - TimeSpan.FromDays(30));
                        break;
                    case QueryRange.LastWeek:
                        objects = objects.Where(q => q.Created >= DateTime.Now - TimeSpan.FromDays(7));
                        break;
                }
            }

            switch (querySortBy)
            {
                case QuerySortBy.DateCreated:
                    objects = objects.OrderByDescending(q => q.Created);
                    break;

                    // todo: support more ways of sorting, i.e. by attribute value
            }

            // now just retrieve a page's worth of images from the results
            var grossCount = await objects.CountAsync();
            var offset = (page - 1) * pageSize;
            var itemsToGet = grossCount >= pageSize ? pageSize : grossCount;
            var results = await objects.Skip(offset).Take(itemsToGet).ToListAsync();

            // now with all the ids we know how many total results there are and so can populate paging info
            var pagedResultSet = new PagedResultSet<MetaverseObject>
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
    }
}
