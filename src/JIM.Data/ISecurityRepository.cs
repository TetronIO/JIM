using JIM.Models.Security;

namespace JIM.Data
{
    public interface ISecurityRepository
    {
        public List<Role> GetRoles();

        public Role? GetRole(string roleName);

        public bool DoesUserHaveRole(Guid userId, string roleName);

        public List<Role> GetMetaverseObjectRoles(Guid metaverseObjectId);

        public Task AddUserToRoleAsync(Guid userId, string roleName);
    }
}
