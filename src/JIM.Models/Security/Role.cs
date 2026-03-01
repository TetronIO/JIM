using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Security;

/// <summary>
/// Represents a security role that groups permissions and can have static members assigned.
/// Built-in roles are seeded at initialisation and cannot be deleted.
/// </summary>
[Index(nameof(Name))]
public class Role : IAuditable
{
    public int Id { get; set; }

    /// <summary>
    /// The display name of the role.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Indicates whether this role was seeded by the system and cannot be deleted.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// When the role was created (UTC).
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The type of security principal that created this entity.
    /// </summary>
    public ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that created this entity.
    /// Null for system-created (seeded) entities.
    /// </summary>
    public Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// Retained even if the principal is later deleted.
    /// </summary>
    public string? CreatedByName { get; set; }

    /// <summary>
    /// When the entity was last modified (UTC). Null if never modified after creation.
    /// </summary>
    public DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this entity.
    /// </summary>
    public ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that last modified this entity.
    /// </summary>
    public Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// Metaverse Objects explicitly assigned as members of this role.
    /// </summary>
    public List<MetaverseObject> StaticMembers { get; set; } = new();

    // todo: resource scope
    // todo: permissions
    // todo: dynamic membership
}