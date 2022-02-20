using JIM.Models.Core;

namespace JIM.Application.Servers
{
    public class MetaverseServer
    {
        private JimApplication Application { get; }

        internal MetaverseServer(JimApplication application)
        {
            Application = application;
        }

        public IList<MetaverseObjectType> GetMetaverseObjectTypes()
        {
            return Application.Repository.Metaverse.GetMetaverseObjectTypes();
        }

        public MetaverseObjectType? GetMetaverseObjectType(Guid id)
        {
            return Application.Repository.Metaverse.GetMetaverseObjectType(id);
        }

        public MetaverseObjectType? GetMetaverseObjectType(string objectTypeName)
        {
            return Application.Repository.Metaverse.GetMetaverseObjectType(objectTypeName);
        }

        public IList<MetaverseAttribute>? GetMetaverseAttributes()
        {
            return Application.Repository.Metaverse.GetMetaverseAttributes();
        }

        public MetaverseAttribute? GetMetaverseAttribute(Guid id)
        {
            return Application.Repository.Metaverse.GetMetaverseAttribute(id);
        }

        public MetaverseObject? GetMetaverseObject(Guid id)
        {
            return Application.Repository.Metaverse.GetMetaverseObject(id);
        }

        public MetaverseObject? GetMetaverseObjectByTypeAndAttribute(MetaverseObjectType metaverseObjectType, MetaverseAttribute metaverseAttribute, string attributeValue)
        {
            return Application.Repository.Metaverse.GetMetaverseObjectByTypeAndAttribute(metaverseObjectType, metaverseAttribute, attributeValue);
        }

        public async Task CreateMetaverseObjectAsync(MetaverseObject metaverseObject)
        {
            await Application.Repository.Metaverse.CreateMetaverseObjectAsync(metaverseObject);
        }

        public int GetMetaverseObjectCount()
        {
            return Application.Repository.Metaverse.GetMetaverseObjectCount();
        }

        public int GetMetaverseObjectOfTypeCount(MetaverseObjectType metaverseObjectType)
        {
            return Application.Repository.Metaverse.GetMetaverseObjectOfTypeCount(metaverseObjectType.Id);
        }

        public PagedResultSet<MetaverseObject> GetMetaverseObjectsOfType(
            MetaverseObjectType metaverseObjectType,
            int page = 1,
            int pageSize = 20,
            int maxResults = 500)
        {
            return Application.Repository.Metaverse.GetMetaverseObjectsOfType(
                metaverseObjectType.Id,
                page,
                pageSize,
                maxResults);
        }
    }
}
