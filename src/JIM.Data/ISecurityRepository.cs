using JIM.Models.Security;

namespace JIM.Data
{
    public interface ISecurityRepository
    {
        public List<Role> GetRoles();

        public Role? GetRole(string roleName);

        public bool IsObjectInRole(int userId, string roleName);

        public List<Role> GetMetaverseObjectRoles(int metaverseObjectId);

        public Task AddObjectToRole(int userId, string roleName);
    }
}
