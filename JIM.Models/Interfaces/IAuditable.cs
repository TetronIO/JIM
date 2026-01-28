using JIM.Models.Activities;

namespace JIM.Models.Interfaces;

/// <summary>
/// Defines standard audit fields for tracking who created and last modified a configuration object.
/// All configuration objects that security principals can create or modify should implement this interface.
/// Audit fields use the triad pattern (Type + Id + Name) to survive principal deletion.
/// </summary>
public interface IAuditable
{
    /// <summary>
    /// When the entity was created (UTC).
    /// </summary>
    DateTime Created { get; set; }

    /// <summary>
    /// The type of security principal that created this entity.
    /// </summary>
    ActivityInitiatorType CreatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that created this entity.
    /// Null for system-created (seeded) entities.
    /// </summary>
    Guid? CreatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of creation.
    /// Retained even if the principal is later deleted.
    /// </summary>
    string? CreatedByName { get; set; }

    /// <summary>
    /// When the entity was last modified (UTC). Null if never modified after creation.
    /// </summary>
    DateTime? LastUpdated { get; set; }

    /// <summary>
    /// The type of security principal that last modified this entity.
    /// </summary>
    ActivityInitiatorType LastUpdatedByType { get; set; }

    /// <summary>
    /// The unique identifier of the principal that last modified this entity.
    /// </summary>
    Guid? LastUpdatedById { get; set; }

    /// <summary>
    /// The display name of the principal at the time of the last modification.
    /// </summary>
    string? LastUpdatedByName { get; set; }
}
