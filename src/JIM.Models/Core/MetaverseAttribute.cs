using JIM.Models.Activities;
using JIM.Models.Interfaces;
using JIM.Models.Search;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Core;

[Index(nameof(Name))]
public class MetaverseAttribute : IAuditable
{
    public int Id { get; set; }

    public DateTime Created { set; get; } = DateTime.UtcNow;

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

    public string Name { get; set; } = null!;

    public AttributeDataType Type { get; set; }

    public AttributePlurality AttributePlurality { get; set; }

    public bool BuiltIn { get; set; }

    /// <summary>
    /// Provides a hint to the UI on how to render this attribute's values.
    /// Drives the display shape (Table, ChipSet, List); CRUD mode (read-only vs editable)
    /// is determined separately by user permissions at runtime.
    /// Only meaningful for multi-valued attributes; ignored for single-valued attributes.
    /// </summary>
    public AttributeRenderingHint RenderingHint { get; set; } = AttributeRenderingHint.Default;

    public List<MetaverseObjectType> MetaverseObjectTypes { get; set; } = null!;

    public List<PredefinedSearchAttribute> PredefinedSearchAttributes { get; set; } = null!;
}