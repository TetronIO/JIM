using JIM.Models.Core;

namespace JIM.Data.Repositories
{
    public interface IMetaverseRepository
    {
        #region object type groups
        public Task<List<MetaverseObjectTypeGroup>> GetMetaverseObjectTypeGroupsAsync(bool includeChildObjects);
        public Task<MetaverseObjectTypeGroup?> GetMetaverseObjectTypeGroupAsync(int id, bool includeChildObjects);
        public Task CreateMetaverseObjectTypeGroupAsync(MetaverseObjectTypeGroup metaverseObjectTypeGroup);
        public Task UpdateMetaverseObjectTypeGroupAsync(MetaverseObjectTypeGroup metaverseObjectTypeGroup);
        #endregion

        #region object types
        public Task<IList<MetaverseObjectType>> GetMetaverseObjectTypesAsync();

        public Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(int id);

        public Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(string name);
        #endregion

        #region objects
        public Task<MetaverseObject?> GetMetaverseObjectAsync(int id);

        public Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject);

        public Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject);

        public Task<MetaverseObject?> GetMetaverseObjectByTypeAndAttributeAsync(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue);

        public Task<int> GetMetaverseObjectCountAsync();

        public Task<int> GetMetaverseObjectOfTypeCountAsync(int metaverseObjectTypeId);

        public Task<PagedResultSet<MetaverseObject>> GetMetaverseObjectsOfTypeAsync(
            int metaverseObjectTypeId,
            int page,
            int pageSize,
            int maxResults,
            QuerySortBy querySortBy = QuerySortBy.DateCreated,
            QueryRange queryRange = QueryRange.Forever);
        #endregion

        #region attributes
        public Task<IList<MetaverseAttribute>?> GetMetaverseAttributesAsync();

        public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id);

        public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name);
        #endregion
    }
}
