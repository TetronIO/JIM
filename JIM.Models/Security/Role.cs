using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Security;

[Index(nameof(Name))]
public class Role
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public bool BuiltIn { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public List<MetaverseObject> StaticMembers { get; set; } = new();

    // todo: resource scope
    // todo: permissions
    // todo: dynamic membership
}