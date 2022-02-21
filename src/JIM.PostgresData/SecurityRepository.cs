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
            using var db = new JimDbContext();
            return db.Roles.OrderBy(q => q.Name).ToList();
        }

        public Role? GetRole(string roleName)
        {
            using var db = new JimDbContext();
            return db.Roles.SingleOrDefault(q => q.Name == roleName);
        }

        public List<Role> GetMetaverseObjectRoles(Guid metaverseObjectId)
        {
            using var db = new JimDbContext();
            return db.Roles.Where(q => q.StaticMembers.Any(sm => sm.Id == metaverseObjectId)).ToList();
        }

        public bool IsUserInRole(Guid userId, string roleName)
        {
            using var db = new JimDbContext();
            return db.Roles.Any(q => q.Name == roleName && q.StaticMembers.Any(sm => sm.Id == userId));
        }

        public async Task AddUserToRoleAsync(Guid userId, string roleName)
        {
            using var db = new JimDbContext();
            var dbRole = db.Roles.SingleOrDefault(r => r.Name == roleName);
            if (dbRole == null)
                throw new ArgumentException($"No such role found: {roleName}");

            var dbUser = db.MetaverseObjects.SingleOrDefault(mo => mo.Id == userId);
            if (dbUser == null)
                throw new ArgumentException($"No such user found: {userId}");

            if (dbRole.StaticMembers.Any(sm => sm.Id == userId))
                throw new ArgumentException($"User is already in that role");

            dbRole.StaticMembers.Add(dbUser);
            await db.SaveChangesAsync();
        }
    }
}
