using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Logic;
namespace JIM.Models.Core;

/// <summary>
/// Represents a change to a Metaverse Object, i.e. what was changed, when and by what/whom.
/// </summary>
public class MetaverseObjectChange
{
    public Guid Id { get; set; }

    /// <summary>
    /// What Metaverse Object does this change relate to?
    /// Will be null if the operation was DELETE.
    /// </summary>
    public MetaverseObject? MetaverseObject { get; set; }

    /// <summary>
    /// When was this change made?
    /// </summary>
    public DateTime ChangeTime { get; set; }

    /// <summary>
    /// The run profile execution item that caused this change (for sync-initiated changes).
    /// May be null if run history has been cleared or for non-sync changes.
    /// </summary>
    public Activities.ActivityRunProfileExecutionItem? ActivityRunProfileExecutionItem { get; set; }
    public Guid? ActivityRunProfileExecutionItemId { get; set; }

    // -----------------------------------------------------------------------------------------------------------------
    // Initiator tracking - mirrors Activity's pattern for audit trail
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The type of security principal that initiated this change.
    /// </summary>
    public ActivityInitiatorType InitiatedByType { get; set; } = ActivityInitiatorType.NotSet;

    /// <summary>
    /// The unique identifier of the security principal (MetaverseObject or ApiKey) that initiated this change.
    /// Retained even if the principal is deleted to support audit investigations.
    /// </summary>
    public Guid? InitiatedById { get; set; }

    /// <summary>
    /// The display name of the security principal at the time of the change.
    /// Retained even if the principal is deleted to maintain audit trail readability.
    /// </summary>
    public string? InitiatedByName { get; set; }

    /// <summary>
    /// What mechanism triggered this change (sync rule, workflow, direct user action, etc.).
    /// </summary>
    public MetaverseObjectChangeInitiatorType ChangeInitiatorType { get; set; }

    /// <summary>
    /// What was the change type?
    /// Acceptable values: UPDATE and DELETE. There would be no change object for a create scenario.
    /// </summary>
    public ObjectChangeType ChangeType { get; set; }

    /// <summary>
    /// The sync rule that caused this change (for sync-initiated changes).
    /// Nullable FK - if sync rule is deleted, this becomes null.
    /// </summary>
    public SyncRule? SyncRule { get; set; }
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of sync rule name at time of change.
    /// Preserved even if sync rule is deleted for audit trail.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// Enables access to per-attribute value changes for the metaverse object in question.
    /// </summary>
    public List<MetaverseObjectChangeAttribute> AttributeChanges { get; set; } = new List<MetaverseObjectChangeAttribute>();

    // -----------------------------------------------------------------------------------------------------------------
    // Deleted object tracking - preserved for audit trail when MVO is deleted
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// If the object was deleted, the object type ID is preserved here.
    /// </summary>
    public int? DeletedObjectTypeId { get; set; }

    /// <summary>
    /// If the object was deleted, the object type is preserved here for display.
    /// </summary>
    public MetaverseObjectType? DeletedObjectType { get; set; }

    /// <summary>
    /// If the object was deleted, the display name is preserved here for UI display in the deleted objects browser.
    /// </summary>
    public string? DeletedObjectDisplayName { get; set; }
}