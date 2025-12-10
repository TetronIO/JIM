using JIM.Models.Security;

namespace JIM.Web.Models.Api;

/// <summary>
/// API representation of a Role for list views.
/// </summary>
public class RoleDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public bool BuiltIn { get; set; }
    public DateTime Created { get; set; }
    public int StaticMemberCount { get; set; }

    /// <summary>
    /// Creates a DTO from a Role entity.
    /// </summary>
    public static RoleDto FromEntity(Role entity)
    {
        return new RoleDto
        {
            Id = entity.Id,
            Name = entity.Name,
            BuiltIn = entity.BuiltIn,
            Created = entity.Created,
            StaticMemberCount = entity.StaticMembers?.Count ?? 0
        };
    }
}
