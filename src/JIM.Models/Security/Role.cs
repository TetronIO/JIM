using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Security
{
    [Index(nameof(Name))]
    public class Role
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool BuiltIn { get; set; }
        public DateTime Created { get; set; }
        public List<MetaverseObject> StaticMembers { get; set; }

        // todo: resource scope
        // todo: permissions
        // todo: dynamic membership

        public Role()
        {
            StaticMembers = new List<MetaverseObject>();
            Created = DateTime.UtcNow;
        }
    }
}