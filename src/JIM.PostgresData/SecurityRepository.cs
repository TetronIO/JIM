using JIM.Data;
using JIM.Models.Security;

namespace JIM.PostgresData
{
    public class SecurityRepository : ISecurityRepository
    {
        private PostgresDataRepository Repository { get; }

        internal SecurityRepository(PostgresDataRepository dataRepository)
        {
            Repository = dataRepository;
        }

        public List<Role> GetRoles()
        {
            return Repository.Database.Roles.OrderBy(q => q.Name).ToList();
        }

        public Role? GetRole(string roleName)
        {
            return Repository.Database.Roles.SingleOrDefault(q => q.Name == roleName);
        }

        public List<Role> GetMetaverseObjectRoles(int metaverseObjectId)
        {
            return Repository.Database.Roles.Where(q => q.StaticMembers.Any(sm => sm.Id == metaverseObjectId)).ToList();
        }

        public bool IsUserInRole(int userId, string roleName)
        {
            return Repository.Database.Roles.Any(q => q.Name == roleName && q.StaticMembers.Any(sm => sm.Id == userId));
        }

        public async Task AddUserToRoleAsync(int userId, string roleName)
        {
            var dbRole = Repository.Database.Roles.SingleOrDefault(r => r.Name == roleName);
            if (dbRole == null)
                throw new ArgumentException($"No such role found: {roleName}");

            var dbUser = Repository.Database.MetaverseObjects.SingleOrDefault(mo => mo.Id == userId);
            if (dbUser == null)
                throw new ArgumentException($"No such user found: {userId}");

            if (dbRole.StaticMembers.Any(sm => sm.Id == userId))
                throw new ArgumentException($"User is already in that role");

            dbRole.StaticMembers.Add(dbUser);
            await Repository.Database.SaveChangesAsync();
        }
    }
}
