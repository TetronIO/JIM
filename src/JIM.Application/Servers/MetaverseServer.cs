using JIM.Models.Core;
using JIM.Models.Utility;

namespace JIM.Application.Servers
{
    public class MetaverseServer
    {
        #region accessors
        private JimApplication Application { get; }
        #endregion

        #region constructors
        internal MetaverseServer(JimApplication application)
        {
            Application = application;
        }
        #endregion

        #region metaverse object types
        public async Task<IList<MetaverseObjectType>> GetMetaverseObjectTypesAsync(bool includeChildObjects)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectTypesAsync(includeChildObjects);
        }

        public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(int id, bool includeChildObjects)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(id, includeChildObjects);
        }

        public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(string objectTypeName, bool includeChildObjects)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(objectTypeName, includeChildObjects);
        }
        #endregion

        #region metaverse attributes
        public async Task<IList<MetaverseAttribute>?> GetMetaverseAttributesAsync()
        {
            return await Application.Repository.Metaverse.GetMetaverseAttributesAsync();
        }

        public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(int id)
        {
            return await Application.Repository.Metaverse.GetMetaverseAttributeAsync(id);
        }

        public async Task<MetaverseAttribute?> GetMetaverseAttributeAsync(string name)
        {
            return await Application.Repository.Metaverse.GetMetaverseAttributeAsync(name);
        }
        #endregion

        #region metaverse objects
        public async Task<MetaverseObject?> GetMetaverseObjectAsync(int id)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectAsync(id);
        }

        public async Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            await Application.Repository.Metaverse.UpdateMetaverseObjectAsync(metaverseObject);
        }

        public async Task<MetaverseObject?> GetMetaverseObjectByTypeAndAttributeAsync(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(metaverseObjectType, metaverseAttribute, attributeValue);
        }

        public async Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            await Application.Repository.Metaverse.CreateMetaverseObjectAsync(metaverseObject);
        }

        public async Task<int> GetMetaverseObjectCountAsync()
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectCountAsync();
        }

        public async Task<int> GetMetaverseObjectOfTypeCountAsync(MetaverseObjectType metaverseObjectType)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectOfTypeCountAsync(metaverseObjectType.Id);
        }

        public async Task<PagedResultSet<MetaverseObject>> GetMetaverseObjectsOfTypeAsync(
            MetaverseObjectType metaverseObjectType,
            int page = 1,
            int pageSize = 20,
            int maxResults = 500)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectsOfTypeAsync(
                metaverseObjectType.Id,
                page,
                pageSize,
                maxResults);
        }
        #endregion
    }
}
