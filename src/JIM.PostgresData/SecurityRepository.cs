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

        public IList<Role> GetRoles()
        {
            using var db = new JimDbContext();
            return db.Roles.OrderBy(q => q.Name).ToList();
        }

        public Role? GetRole(string roleName)
        {
            using var db = new JimDbContext();
            return db.Roles.SingleOrDefault(q => q.Name.Equals(roleName, StringComparison.InvariantCultureIgnoreCase));
        }
    }
}
