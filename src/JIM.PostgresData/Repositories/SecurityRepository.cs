using JIM.Data.Repositories;
using JIM.Models.Security;

namespace JIM.PostgresData.Repositories
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

        public bool IsObjectInRole(int userId, string roleName)
        {
            return Repository.Database.Roles.Any(q => q.Name == roleName && q.StaticMembers.Any(sm => sm.Id == userId));
        }

        public async Task AddObjectToRole(int objectId, string roleName)
        {
            var dbRole = Repository.Database.Roles.SingleOrDefault(r => r.Name == roleName);
            if (dbRole == null)
                throw new ArgumentException($"No such role found: {roleName}");

            var dbObject = Repository.Database.MetaverseObjects.SingleOrDefault(mo => mo.Id == objectId);
            if (dbObject == null)
                throw new ArgumentException($"No such object found: {objectId}");

            if (dbRole.StaticMembers.Any(sm => sm.Id == dbObject.Id))
                throw new ArgumentException($"Object is already in that role");

            dbRole.StaticMembers.Add(dbObject);
            await Repository.Database.SaveChangesAsync();
        }
    }
}
