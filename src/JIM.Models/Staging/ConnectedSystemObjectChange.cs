using JIM.Models.Activities;
using JIM.Models.Enums;
namespace JIM.Models.Staging;

/// <summary>
/// Represents a change to a Connected System Object, i.e. what was changed, when and by what.
/// </summary>
public class ConnectedSystemObjectChange
{
    public Guid Id { get; set; }

    /// <summary>
    /// The connected system object change would have been created after a sync run profile execution, access that here if it still exists.
    /// It's worth bearing in mind that sync run history can be cleared down so a reference may not always be present,
    /// depending on how old the connected system object change is.
    /// </summary>
    public ActivityRunProfileExecutionItem? ActivityRunProfileExecutionItem { get; set; }
    public Guid? ActivityRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// Which Connected System did/does the Connected System Object in question relate to.
    /// Important information when the change type was DELETE and there's no ConnectedSystemObject to reference any more.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// What Connected System Object does this change relate to?
    /// Will be null if the CSO has since been deleted.
    /// </summary>
    public ConnectedSystemObject? ConnectedSystemObject { get; set; }

    /// <summary>
    /// When was this change made?
    /// </summary>
    public DateTime ChangeTime { get; set; }

    /// <summary>
    /// What was the change type?
    /// Acceptable values: UPDATE and DELETE. There would be no change object for a create scenario.
    /// </summary>
    public ObjectChangeType ChangeType { get; set; }

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
    /// Enables access to per-attribute value changes for the connected system object in question.
    /// </summary>
    public List<ConnectedSystemObjectChangeAttribute> AttributeChanges { get; set; } = new();

    /// <summary>
    /// If the object was deleted, the object type will be copied here to make it possible to identify what type of object was deleted.
    /// </summary>
    public ConnectedSystemObjectType? DeletedObjectType { get; set; }

    /// <summary>
    /// If the object was deleted, the External Id attribute will be copied here to make it possible to identify which object was deleted.
    /// Note: This FK reference may be null even for deleted objects because the attribute value is cascade deleted with the CSO.
    /// Use DeletedObjectExternalId instead for reliable access to the external ID value.
    /// </summary>
    public ConnectedSystemObjectAttributeValue? DeletedObjectExternalIdAttributeValue { get; set; }

    /// <summary>
    /// If the object was deleted, the external ID value is preserved here as a string for UI display.
    /// This field is populated because the FK reference (DeletedObjectExternalIdAttributeValue) is cascade deleted with the CSO.
    /// </summary>
    public string? DeletedObjectExternalId { get; set; }

    /// <summary>
    /// If the object was deleted, the display name is preserved here for UI display in the deleted objects browser.
    /// This is typically the value of the displayName or name attribute at the time of deletion.
    /// </summary>
    public string? DeletedObjectDisplayName { get; set; }
}
