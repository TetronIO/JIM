using TIM.Models.Security;

namespace TIM.Data
{
    public interface ISecurityRepository
    {
        public IList<Role> GetRoles();
        public Role? GetRole(string roleName);
    }
}
