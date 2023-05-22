using JIM.Data.Repositories;
using JIM.Models.Security;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories
{
    public class SecurityRepository : ISecurityRepository
    {
        private PostgresDataRepository Repository { get; }

        internal SecurityRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public async Task<List<Role>> GetRolesAsync()
        {
            return await Repository.Database.Roles.OrderBy(q => q.Name).ToListAsync();
        }

        public async Task<Role?> GetRoleAsync(string roleName)
        {
            return await Repository.Database.Roles.SingleOrDefaultAsync(q => q.Name == roleName);
        }

        public async Task<List<Role>> GetMetaverseObjectRolesAsync(Guid metaverseObjectId)
        {
            return await Repository.Database.Roles.Where(q => q.StaticMembers.Any(sm => sm.Id == metaverseObjectId)).ToListAsync();
        }

        public async Task<bool> IsObjectInRoleAsync(Guid userId, string roleName)
        {
            return await Repository.Database.Roles.AnyAsync(q => q.Name == roleName && q.StaticMembers.Any(sm => sm.Id == userId));
        }

        public async Task AddObjectToRoleAsync(Guid objectId, string roleName)
        {
            var dbRole = await Repository.Database.Roles.SingleOrDefaultAsync(r => r.Name == roleName);
            if (dbRole == null)
                throw new ArgumentException($"No such role found: {roleName}");

            var dbObject = await Repository.Database.MetaverseObjects.SingleOrDefaultAsync(mo => mo.Id == objectId);
            if (dbObject == null)
                throw new ArgumentException($"No such object found: {objectId}");

            if (dbRole.StaticMembers.Any(sm => sm.Id == dbObject.Id))
                throw new ArgumentException($"Object is already in that role");

            dbRole.StaticMembers.Add(dbObject);
            await Repository.Database.SaveChangesAsync();
        }
    }
}
