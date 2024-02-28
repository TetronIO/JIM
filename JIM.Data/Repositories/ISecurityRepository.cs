using JIM.Models.Security;

namespace JIM.Data.Repositories
{
    public interface ISecurityRepository
    {
        public Task<List<Role>> GetRolesAsync();

        public Task<Role?> GetRoleAsync(string roleName);

        public Task<bool> IsObjectInRoleAsync(Guid userId, string roleName);

        public Task<List<Role>> GetMetaverseObjectRolesAsync(Guid metaverseObjectId);

        public Task AddObjectToRoleAsync(Guid userId, string roleName);
    }
}
