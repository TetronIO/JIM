using JIM.Models.Core;

namespace JIM.Data
{
    public interface IMetaverseRepository
    {
        #region object types
        public IList<MetaverseObjectType> GetMetaverseObjectTypes();

        public MetaverseObjectType? GetMetaverseObjectType(Guid id);

        public MetaverseObjectType? GetMetaverseObjectType(string name);
        #endregion

        #region objects
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

        #endregion

        #region attributes
        public IList<MetaverseAttribute>? GetMetaverseAttributes();

        public MetaverseAttribute? GetMetaverseAttribute(Guid id);

        public MetaverseAttribute? GetMetaverseAttribute(string name);
        #endregion
    }

}
