using JIM.Models.Core;

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

        #region metaverse object type groups
        public async Task<IList<MetaverseObjectTypeGroup>> GetMetaverseObjectTypeGroupsAsync(bool includeChildObjects)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectTypeGroupsAsync(includeChildObjects);
        }

        public async Task<MetaverseObjectTypeGroup?> GetMetaverseObjectTypeGroupAsync(int id, bool includeChildObjects)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectTypeGroupAsync(id, includeChildObjects);
        }
        #endregion

        #region metaverse object types
        public async Task<IList<MetaverseObjectType>> GetMetaverseObjectTypesAsync()
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectTypesAsync();
        }

        public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(int id)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(id);
        }

        public async Task<MetaverseObjectType?> GetMetaverseObjectTypeAsync(string objectTypeName)
        {
            return await Application.Repository.Metaverse.GetMetaverseObjectTypeAsync(objectTypeName);
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
