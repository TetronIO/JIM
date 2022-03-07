using JIM.Models.Core;

namespace JIM.Data.Repositories
{
    public interface IMetaverseRepository
    {
        #region object types
        public IList<MetaverseObjectType> GetMetaverseObjectTypes();

        public MetaverseObjectType? GetMetaverseObjectType(int id);

        public Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(string name);
        #endregion

        #region objects
        public MetaverseObject? GetMetaverseObject(int id);

        public Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject);

        public Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject);

        public MetaverseObject? GetMetaverseObjectByTypeAndAttribute(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue);

        public int GetMetaverseObjectCount();

        public int GetMetaverseObjectOfTypeCount(int metaverseObjectTypeId);

        public PagedResultSet<MetaverseObject> GetMetaverseObjectsOfType(
            int metaverseObjectTypeId,
            int page,
            int pageSize,
            int maxResults,
            QuerySortBy querySortBy = QuerySortBy.DateCreated,
            QueryRange queryRange = QueryRange.Forever);
        #endregion

        #region attributes
        public IList<MetaverseAttribute>? GetMetaverseAttributes();

        public MetaverseAttribute? GetMetaverseAttribute(int id);

        public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name);
        #endregion
    }

}
