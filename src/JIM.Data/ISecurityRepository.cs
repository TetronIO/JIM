using JIM.Models.Security;

namespace JIM.Data
{
    public interface ISecurityRepository
    {
        public List<Role> GetRoles();

        public Role? GetRole(string roleName);

        public bool IsUserInRole(int userId, string roleName);

        public List<Role> GetMetaverseObjectRoles(int metaverseObjectId);

        public Task AddUserToRoleAsync(int userId, string roleName);
    }
}
