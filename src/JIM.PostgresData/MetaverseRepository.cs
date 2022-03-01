using JIM.Data;
using JIM.Models.Core;

namespace JIM.PostgresData
{
    public class MetaverseRepository : IMetaverseRepository
    {
        private PostgresDataRepository Repository { get; }

        internal MetaverseRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public IList<MetaverseObjectType> GetMetaverseObjectTypes()
        {
            using var db = new JimDbContext();
            return db.MetaverseObjectTypes.OrderBy(x => x.Name).ToList();
        }

        public MetaverseObjectType? GetMetaverseObjectType(int id)
        {
            using var db = new JimDbContext();
            return db.MetaverseObjectTypes.SingleOrDefault(x => x.Id == id);
        }

        public MetaverseObjectType? GetMetaverseObjectType(string name)
        {
            using var db = new JimDbContext();
            return db.MetaverseObjectTypes.SingleOrDefault(q => q.Name == name);
        }

        public IList<MetaverseAttribute> GetMetaverseAttributes()
        {
            using var db = new JimDbContext();
            return db.MetaverseAttributes.OrderBy(x => x.Name).ToList();
        }

        public MetaverseAttribute? GetMetaverseAttribute(int id)
        {
            using var db = new JimDbContext();
            return db.MetaverseAttributes.SingleOrDefault(x => x.Id == id);
        }

        public MetaverseAttribute? GetMetaverseAttribute(string name)
        {
            using var db = new JimDbContext();
            return db.MetaverseAttributes.SingleOrDefault(x => x.Name == name);
        }

        public MetaverseObject? GetMetaverseObject(int id)
        {
            using var db = new JimDbContext();
            return db.MetaverseObjects.SingleOrDefault(x => x.Id == id);
        }

        public async Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            using var db = new JimDbContext();
            var dbMetaverseObject = db.MetaverseObjects.SingleOrDefault(q => q.Id == metaverseObject.Id);
            if (dbMetaverseObject == null)
                throw new ArgumentException($"Couldn't find object in db to update: {metaverseObject.Id}");

            db.Entry(dbMetaverseObject).CurrentValues.SetValues(metaverseObject);
            await db.SaveChangesAsync();
        }

        public async Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            using var db = new JimDbContext();
            db.MetaverseObjects.Add(metaverseObject);
            await db.SaveChangesAsync();
        }

        public MetaverseObject? GetMetaverseObjectByTypeAndAttribute(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue)
        {
            using var db = new JimDbContext();
            var av = db.MetaverseObjectAttributeValues.SingleOrDefault(av =>
               av.Attribute.Id == metaverseAttribute.Id &&
               av.StringValue != null && av.StringValue == attributeValue &&
               av.MetaverseObject.Type.Id == metaverseObjectType.Id);

            if (av != null)
                return av.MetaverseObject;

            return null;
        }

        public int GetMetaverseObjectCount()
        {
            using var db = new JimDbContext();
            return db.MetaverseObjects.Count();
        }

        public int GetMetaverseObjectOfTypeCount(int metaverseObjectTypeId)
        {
            using var db = new JimDbContext();
            return db.MetaverseObjects.Where(x => x.Type.Id == metaverseObjectTypeId).Count();
        }

        public PagedResultSet<MetaverseObject> GetMetaverseObjectsOfType(
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

            using var db = new JimDbContext();
            var objects = from o in db.MetaverseObjects.Where(q => q.Type.Id == metaverseObjectTypeId)
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
            var grossCount = objects.Count();
            var offset = (page - 1) * pageSize;
            var itemsToGet = grossCount >= pageSize ? pageSize : grossCount;
            objects = objects.Skip(offset).Take(itemsToGet);

            // now with all the ids we know how many total results there are and so can populate paging info
            var pagedResultSet = new PagedResultSet<MetaverseObject>
            {
                PageSize = pageSize,
                TotalResults = grossCount,
                CurrentPage = page,
                QuerySortBy = querySortBy,
                QueryRange = queryRange
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
