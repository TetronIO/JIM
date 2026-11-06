// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
    /// The Connected System Object change would have been created after a sync Run Profile execution, access that here if it still exists.
    /// It's worth bearing in mind that sync run history can be cleared down so a reference may not always be present,
    /// depending on how old the Connected System Object change is.
    /// </summary>
    public ActivityRunProfileExecutionItem? ActivityRunProfileExecutionItem { get; set; }
    public Guid? ActivityRunProfileExecutionItemId { get; set; }

    /// <summary>
    /// Which Connected System did/does the Connected System Object in question relate to.
    /// Important information when the change type was DELETE and there's no ConnectedSystemObject to reference any more.
    /// </summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>
    /// The id of the Connected System Object this change relates to, deletion records included. Written once and never
    /// rewritten (#2669): once the object is deleted it names an object that no longer exists, so readers decide whether
    /// the object is live by looking it up, never from this id. Null only on records whose id an earlier release cleared
    /// when the object was deleted.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

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
    /// Enables access to per-attribute value changes for the Connected System Object in question.
    /// </summary>
    public List<ConnectedSystemObjectChangeAttribute> AttributeChanges { get; set; } = new();

    /// <summary>
    /// If the object was deleted, the object type will be copied here to make it possible to identify what type of object was deleted.
    /// </summary>
    public ConnectedSystemObjectType? DeletedObjectType { get; set; }

    /// <summary>
    /// If the object was deleted, its External ID value, preserved as text, because the attribute value itself is
    /// deleted with the object.
    /// </summary>
    public string? DeletedObjectExternalId { get; set; }

    /// <summary>
    /// If the object was deleted, the display name is preserved here for UI display in the deleted objects browser.
    /// This is typically the value of the displayName or name attribute at the time of deletion.
    /// </summary>
    public string? DeletedObjectDisplayName { get; set; }

    /// <summary>
    /// If the object was deleted, the id it had before deletion. Redundant since #2669, which keeps
    /// <see cref="ConnectedSystemObjectId"/> on every record, the deletion record included; it is still written, and read
    /// beside the subject id, until it is retired. It is the Connected System Object counterpart of
    /// <see cref="JIM.Models.Core.MetaverseObjectChange.DeletedMetaverseObjectId"/>.
    /// </summary>
    public Guid? DeletedConnectedSystemObjectId { get; set; }
}
