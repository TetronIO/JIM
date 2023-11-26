using JIM.Models.Core;
using JIM.Models.Core.DTOs;
using JIM.Models.Enums;
using JIM.Models.Search;
using JIM.Models.Utility;

namespace JIM.Data.Repositories
{
    public interface IMetaverseRepository
    {
        #region object types
        public Task<IList<MetaverseObjectType>> GetMetaverseObjectTypesAsync(bool includeChildObjects);

        public Task<IList<MetaverseObjectTypeHeader>> GetMetaverseObjectTypeHeadersAsync();

        public Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(int id, bool includeChildObjects);

        public Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(string name, bool includeChildObjects);
        #endregion

        #region objects
        public Task<MetaverseObject?> GetMetaverseObjectAsync(Guid id);

        public Task<MetaverseObjectHeader?> GetMetaverseObjectHeaderAsync(Guid id);

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

        public Task<PagedResultSet<MetaverseObjectHeader>> GetMetaverseObjectsOfTypeAsync(
            PredefinedSearch predefinedSearch,
            int page,
            int pageSize,
            int maxResults,
            QuerySortBy querySortBy = QuerySortBy.DateCreated,
            QueryRange queryRange = QueryRange.Forever);
        #endregion

        #region attributes
        public Task<IList<MetaverseAttribute>?> GetMetaverseAttributesAsync();

        public Task<IList<MetaverseAttributeHeader>?> GetMetaverseAttributeHeadersAsync();

        public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id);

        public Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name);
        
        #endregion
    }
}
