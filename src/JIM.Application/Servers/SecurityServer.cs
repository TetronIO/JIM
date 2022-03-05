using JIM.Models.Core;
using JIM.Models.Security;

namespace JIM.Application.Servers
{
    public class SecurityServer
    {
        private JimApplication Application { get; }

        internal SecurityServer(JimApplication application)
        {
            Application = application;
        }

        public List<Role> GetRoles()
        {
            return Application.Repository.Security.GetRoles();
        }

        public Role? GetRole(string roleName)
        {
            return Application.Repository.Security.GetRole(roleName);
        }

        public List<Role> GetMetaverseObjectRoles(MetaverseObject metaverseObject)
        {
            return Application.Repository.Security.GetMetaverseObjectRoles(metaverseObject.Id);
        }

        public bool IsObjectInRole(MetaverseObject @object, string roleName)
        { 
            return Application.Repository.Security.IsObjectInRole(@object.Id, roleName);
        }
        public async Task AddObjectToRole(MetaverseObject @object, string roleName)
        {
            await Application.Repository.Security.AddObjectToRole(@object.Id, roleName);
        }
    }
}
