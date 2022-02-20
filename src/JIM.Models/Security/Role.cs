using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;

namespace JIM.Models.Security
{
    [Index(nameof(Name))]
    public class Role
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public bool BuiltIn { get; set; }
        public DateTime Created { get; set; }
        public MetaverseObject? CreatedBy { get; set; }
        public List<MetaverseObject> StaticMembers { get; set; }

        // todo: resource scope
        // todo: permissions
        // todo: dynamic membership

        public Role(string name)
        {
            Name = name;
            StaticMembers = new List<MetaverseObject>();
        }
    }
}