using JIM.Models.Security;

namespace JIM.Data
{
    public interface ISecurityRepository
    {
        public IList<Role> GetRoles();
        public Role? GetRole(string roleName);
    }
}
