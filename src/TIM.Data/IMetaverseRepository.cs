using TIM.Models.Core;

namespace TIM.Data
{
    public interface IMetaverseRepository
    {
        public IList<MetaverseObjectType> GetMetaverseObjectTypes();

        public MetaverseObjectType? GetMetaverseObjectType(Guid id);

        public MetaverseObjectType? GetMetaverseObjectType(string name);        

        public IList<MetaverseAttribute>? GetMetaverseAttributes();

        public MetaverseAttribute? GetMetaverseAttribute(Guid id);

        public MetaverseAttribute? GetMetaverseAttribute(string name);

        public MetaverseObject? GetMetaverseObject(Guid id);

        public Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject);

        public Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject);

        public MetaverseObject? GetMetaverseObjectByTypeAndAttribute(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue);

        public int GetMetaverseObjectCount();

        public int GetMetaverseObjectOfTypeCount(Guid metaverseObjectTypeId);

        public PagedResultSet<MetaverseObject> GetMetaverseObjectsOfType(
            Guid metaverseObjectTypeId,
            int page,
            int pageSize,
            int maxResults,
            QuerySortBy querySortBy = QuerySortBy.DateCreated,
            QueryRange queryRange = QueryRange.Forever);
    }
}
