// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq;
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
    /// The Run Profile execution item that caused this change (for sync-initiated changes).
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
    /// What mechanism triggered this change (Synchronisation Rule, workflow, direct user action, etc.).
    /// </summary>
    public MetaverseObjectChangeInitiatorType ChangeInitiatorType { get; set; }

    /// <summary>
    /// What was the change type?
    /// </summary>
    public ObjectChangeType ChangeType { get; set; }

    /// <summary>
    /// The Synchronisation Rule that caused this change (for sync-initiated changes).
    /// Nullable FK - if Synchronisation Rule is deleted, this becomes null.
    /// </summary>
    public SyncRule? SyncRule { get; set; }
    public int? SyncRuleId { get; set; }

    /// <summary>
    /// Snapshot of Synchronisation Rule name at time of change.
    /// Preserved even if Synchronisation Rule is deleted for audit trail.
    /// </summary>
    public string? SyncRuleName { get; set; }

    /// <summary>
    /// Enables access to per-attribute value changes for the Metaverse Object in question.
    /// </summary>
    public List<MetaverseObjectChangeAttribute> AttributeChanges { get; set; } = new List<MetaverseObjectChangeAttribute>();

    // -----------------------------------------------------------------------------------------------------------------
    // Deleted object tracking - preserved for audit trail when MVO is deleted
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The original MetaverseObject ID, preserved on ALL change records (including prior changes)
    /// when an MVO is deleted. This enables GetDeletedMvoChangeHistoryAsync to correlate the
    /// deletion record with earlier changes after the MetaverseObject FK has been nulled.
    /// </summary>
    public Guid? DeletedMetaverseObjectId { get; set; }

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

    /// <summary>
    /// Records an attribute value change on this change record: finds (or creates) the
    /// <see cref="MetaverseObjectChangeAttribute"/> for the value's attribute, then appends a typed
    /// <see cref="MetaverseObjectChangeAttributeValue"/> describing the addition or removal. This is the single
    /// source of truth for building a change record's attribute graph; both the synchronisation engine and the
    /// portal-driven Metaverse Object operations route through it.
    /// </summary>
    /// <param name="value">The Metaverse Object attribute value being added or removed.</param>
    /// <param name="valueChangeType">Whether the value is being added or removed.</param>
    /// <exception cref="InvalidOperationException">
    /// The attribute has no data type configured (<see cref="AttributeDataType.NotSet"/>), or a known data type's
    /// value holder is null on a row that is not an asserted-null marker (a corrupt Metaverse Object Attribute Value).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The attribute's data type is not handled by this method.</exception>
    public void AddAttributeValueChange(MetaverseObjectAttributeValue value, ValueChangeType valueChangeType)
    {
        // Asserted-null markers (#91) carry no value; their addition/removal is an internal representation of
        // "asserted null", not a tracked value. The meaningful change (the real value being cleared) is recorded
        // via the removal of the real value, so skip the marker itself here. Richer asserted-null history is the
        // SyncOutcome surface (#363), not the attribute value-change log.
        if (value.NullValue)
            return;

        var attributeChange = AttributeChanges.SingleOrDefault(ac => ac.Attribute!.Id == value.Attribute.Id);
        if (attributeChange == null)
        {
            attributeChange = new MetaverseObjectChangeAttribute
            {
                Attribute = value.Attribute,
                AttributeName = value.Attribute.Name,
                AttributeType = value.Attribute.Type,
                MetaverseObjectChange = this
            };
            AttributeChanges.Add(attributeChange);
        }

        switch (value.Attribute.Type)
        {
            case AttributeDataType.Text when value.StringValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, value.StringValue));
                break;
            case AttributeDataType.Number when value.IntValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, (int)value.IntValue));
                break;
            case AttributeDataType.LongNumber when value.LongValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, value.LongValue.Value));
                break;
            case AttributeDataType.Decimal when value.DecimalValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, value.DecimalValue.Value));
                break;
            case AttributeDataType.Guid when value.GuidValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, (Guid)value.GuidValue));
                break;
            case AttributeDataType.Boolean when value.BoolValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, (bool)value.BoolValue));
                break;
            case AttributeDataType.DateTime when value.DateTimeValue.HasValue:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, value.DateTimeValue.Value));
                break;
            case AttributeDataType.Binary when value.ByteValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, true, value.ByteValue.Length));
                break;
            case AttributeDataType.Reference when value.ReferenceValue != null:
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(
                    attributeChange, valueChangeType, value.ReferenceValue));
                break;
            case AttributeDataType.Reference when value.ReferenceValueId.HasValue:
                // Navigation property not loaded but FK is set — record the FK directly on the
                // change record so the ReferenceValue navigation can be materialised later via
                // .Include. Happens during MVO deletion (referenced MVOs not in change tracker)
                // and during projection (reference values populated via direct SQL).
                attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue
                {
                    MetaverseObjectChangeAttribute = attributeChange,
                    ValueChangeType = valueChangeType,
                    ReferenceValueId = value.ReferenceValueId.Value
                });
                break;
            case AttributeDataType.Reference when value.UnresolvedReferenceValue != null:
                // Don't track unresolved references
                break;
            case AttributeDataType.Reference:
                // Reference attribute with no resolved or unresolved value — nothing to track
                break;
            case AttributeDataType.NotSet:
                // The attribute has no data type configured; we cannot record a typed value change for it.
                throw new InvalidOperationException(
                    $"Attribute '{value.Attribute.Name}' (id {value.Attribute.Id}) has no data type configured (NotSet); cannot record a Metaverse Object change for it.");
            default:
                // Reached only when a *known*, switch-handled data type's value holder was unexpectedly null on a
                // row that is not an asserted-null marker (data corruption; the NullValue guard above already
                // returned for legitimate asserted nulls), or when the AttributeDataType enum has gained a member
                // this switch does not yet handle. Distinguish the two so the failure is honest.
                throw Enum.IsDefined(value.Attribute.Type)
                    ? new InvalidOperationException(
                        $"Attribute '{value.Attribute.Name}' (id {value.Attribute.Id}) of type {value.Attribute.Type} has no value but is not an asserted-null marker; the Metaverse Object Attribute Value is corrupt.")
                    : new ArgumentOutOfRangeException(
                        nameof(value),
                        value.Attribute.Type,
                        "Unhandled attribute data type for Metaverse Object change tracking.");
        }
    }
}