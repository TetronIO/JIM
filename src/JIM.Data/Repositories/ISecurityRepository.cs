using JIM.Models.Security;

namespace JIM.Data.Repositories
{
    public interface ISecurityRepository
    {
        public Task<List<Role>> GetRolesAsync();

        public Task<Role?> GetRoleAsync(string roleName);

        public Task<bool> IsObjectInRoleAsync(int userId, string roleName);

        public Task<List<Role>> GetMetaverseObjectRolesAsync(int metaverseObjectId);

        public Task AddObjectToRoleAsync(int userId, string roleName);
    }
}
