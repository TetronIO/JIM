using JIM.Data;
using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;

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
            return db.MetaverseObjectTypes.Include(q => q.Attributes).OrderBy(x => x.Name).ToList();
        }

        public MetaverseObjectType? GetMetaverseObjectType(int id)
        {
            using var db = new JimDbContext();
            return db.MetaverseObjectTypes.Include(q => q.Attributes).SingleOrDefault(x => x.Id == id);
        }

        public MetaverseObjectType? GetMetaverseObjectType(string name)
        {
            using var db = new JimDbContext();
            return db.MetaverseObjectTypes.Include(q => q.Attributes).SingleOrDefault(q => q.Name == name);
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
            return db.MetaverseObjects.Include(q => q.AttributeValues).Include(q => q.Type).SingleOrDefault(x => x.Id == id);
        }

        //public async Task<MetaverseObject> AddAttributeValueToMetaverseObject(MetaverseObject metaverseObject, MetaverseObjectAttributeValue metaverseObjectAttributeValue)
        //{
        //    using var db = new JimDbContext();
        //    var dbMetaverseObject = db.MetaverseObjects.SingleOrDefault(q => q.Id == metaverseObject.Id);
        //    if (dbMetaverseObject == null)
        //        throw new ArgumentException($"Couldn't find object in db to update: {metaverseObject.Id}");

        //    if (metaverseObject.AttributeValues.Any(q => q.Id == metaverseObjectAttributeValue.Id || q.Attribute.Id == metaverseObjectAttributeValue.Attribute.Id))
        //        throw new ArgumentException("That attribute value object, or the attribute it references has already been added.");
        //}

        public async Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            using var db = new JimDbContext();
            var dbMetaverseObject = db.MetaverseObjects.SingleOrDefault(q => q.Id == metaverseObject.Id);
            if (dbMetaverseObject == null)
                throw new ArgumentException($"Couldn't find object in db to update: {metaverseObject.Id}");

            // map scalar value updates to the db version of the object
            db.Entry(dbMetaverseObject).CurrentValues.SetValues(metaverseObject);

            // now map reference types
            dbMetaverseObject.AttributeValues = metaverseObject.AttributeValues;
            dbMetaverseObject.Type = metaverseObject.Type;

            // now ensure we swap out the attribute value reference property values with ones from this db context, to avoid issues
            foreach (var attributeValue in dbMetaverseObject.AttributeValues.Where(q=>q.Attribute.Id == 0))
            {
                db.MetaverseAttributes.Local

                .
            }

            await db.SaveChangesAsync();
        }

        public async Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            using var db = new JimDbContext();

            // I think we need to go through the reference properties and re-map them from db versions, to avoid EF wanting
            // to insert when the references are for existing objects.

            // this sounds crazy, it would impact performance. Why is EF trying to insert when the objects exist?

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
