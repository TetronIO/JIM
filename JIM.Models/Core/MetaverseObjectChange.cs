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
    /// Links to Activity for initiator context (User, ApiKey, System).
    /// May be null if run history has been cleared or for non-sync changes.
    /// </summary>
    public Activities.ActivityRunProfileExecutionItem? ActivityRunProfileExecutionItem { get; set; }
    public Guid? ActivityRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// Which user initiated this change, if any?
    /// Deprecated: Use ActivityRunProfileExecutionItem.Activity.InitiatedBy* fields instead.
    /// </summary>
    public MetaverseObject? ChangeInitiator { get; set; }

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
}