using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;

namespace JIM.Application.Utilities;

/// <summary>
/// Builds <see cref="ConnectedSystemObjectChange"/> records from pending export attribute data.
/// Used to persist export change history so the Causality Tree can render attribute-level detail
/// for export RPEIs and sync PendingExportCreated outcomes.
/// </summary>
public static class ExportChangeHistoryBuilder
{
    /// <summary>
    /// Creates a <see cref="ConnectedSystemObjectChange"/> from a <see cref="ProcessedExportItem"/>,
    /// mapping the captured <see cref="PendingExportAttributeValueChange"/> data into the normalised
    /// change history model used by the Causality Tree.
    /// </summary>
    public static ConnectedSystemObjectChange BuildFromProcessedExportItem(
        ProcessedExportItem exportItem,
        int connectedSystemId,
        ActivityRunProfileExecutionItem executionItem,
        ActivityInitiatorType initiatedByType,
        Guid? initiatedById,
        string? initiatedByName)
    {
        var changeType = exportItem.ChangeType switch
        {
            PendingExportChangeType.Delete => ObjectChangeType.Deprovisioned,
            _ => ObjectChangeType.Exported
        };

        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObject = exportItem.ConnectedSystemObject,
            ChangeType = changeType,
            ChangeTime = DateTime.UtcNow,
            ActivityRunProfileExecutionItem = executionItem,
            ActivityRunProfileExecutionItemId = executionItem.Id,
            InitiatedByType = initiatedByType,
            InitiatedById = initiatedById,
            InitiatedByName = initiatedByName,
            DeletedObjectExternalId = exportItem.ConnectedSystemObject?.ExternalIdAttributeValue?.ToStringNoName()
        };

        MapAttributeValueChanges(change, exportItem.AttributeValueChanges);
        return change;
    }

    /// <summary>
    /// Creates a <see cref="ConnectedSystemObjectChange"/> from a <see cref="PendingExport"/>,
    /// used to snapshot pending export attribute data at sync time before the pending export
    /// is deleted during export confirmation.
    /// </summary>
    public static ConnectedSystemObjectChange BuildFromPendingExport(
        PendingExport pendingExport,
        ActivityInitiatorType initiatedByType,
        Guid? initiatedById,
        string? initiatedByName,
        Dictionary<Guid, ConnectedSystemObject>? resolvedReferences = null)
    {
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = pendingExport.ConnectedSystemId,
            ConnectedSystemObject = pendingExport.ConnectedSystemObject,
            ChangeType = ObjectChangeType.PendingExport,
            ChangeTime = DateTime.UtcNow,
            InitiatedByType = initiatedByType,
            InitiatedById = initiatedById,
            InitiatedByName = initiatedByName,
            DeletedObjectExternalId = pendingExport.ConnectedSystemObject?.ExternalIdAttributeValue?.ToStringNoName()
        };

        MapAttributeValueChanges(change, pendingExport.AttributeValueChanges, resolvedReferences);
        return change;
    }

    /// <summary>
    /// Maps <see cref="PendingExportAttributeValueChange"/> records into the normalised
    /// <see cref="ConnectedSystemObjectChangeAttribute"/> / <see cref="ConnectedSystemObjectChangeAttributeValue"/>
    /// hierarchy on the given change record.
    /// </summary>
    internal static void MapAttributeValueChanges(
        ConnectedSystemObjectChange change,
        List<PendingExportAttributeValueChange> attributeValueChanges,
        Dictionary<Guid, ConnectedSystemObject>? resolvedReferences = null)
    {
        foreach (var peChange in attributeValueChanges)
        {
            // Skip entries where the Attribute navigation property isn't loaded —
            // this can happen in workflow tests or if the PE was created without
            // eagerly loading the attribute definition.
            if (peChange.Attribute == null)
                continue;

            // Group by attribute — multiple value changes for the same attribute
            // (e.g., multi-valued add/remove) share one ConnectedSystemObjectChangeAttribute
            var attributeChange = change.AttributeChanges
                .SingleOrDefault(ac => ac.Attribute!.Id == peChange.Attribute.Id);

            if (attributeChange == null)
            {
                attributeChange = new ConnectedSystemObjectChangeAttribute
                {
                    Attribute = peChange.Attribute,
                    AttributeName = peChange.Attribute.Name,
                    AttributeType = peChange.Attribute.Type,
                    ConnectedSystemChange = change
                };
                change.AttributeChanges.Add(attributeChange);
            }

            var valueChangeType = MapChangeType(peChange.ChangeType);
            AddValueChange(attributeChange, peChange, valueChangeType, resolvedReferences);
        }
    }

    /// <summary>
    /// Maps <see cref="PendingExportAttributeChangeType"/> to <see cref="ValueChangeType"/>.
    /// </summary>
    internal static ValueChangeType MapChangeType(PendingExportAttributeChangeType changeType) =>
        changeType switch
        {
            PendingExportAttributeChangeType.Add => ValueChangeType.Add,
            PendingExportAttributeChangeType.Update => ValueChangeType.Add, // An update sets a new value
            PendingExportAttributeChangeType.Remove => ValueChangeType.Remove,
            PendingExportAttributeChangeType.RemoveAll => ValueChangeType.Remove,
            _ => ValueChangeType.NotSet
        };

    private static void AddValueChange(
        ConnectedSystemObjectChangeAttribute attributeChange,
        PendingExportAttributeValueChange peChange,
        ValueChangeType valueChangeType,
        Dictionary<Guid, ConnectedSystemObject>? resolvedReferences = null)
    {
        var attrType = peChange.Attribute.Type;

        switch (attrType)
        {
            case AttributeDataType.Text when peChange.StringValue != null:
                attributeChange.ValueChanges.Add(
                    new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, peChange.StringValue));
                break;
            case AttributeDataType.Number when peChange.IntValue != null:
                attributeChange.ValueChanges.Add(
                    new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, peChange.IntValue.Value));
                break;
            case AttributeDataType.LongNumber when peChange.LongValue != null:
                attributeChange.ValueChanges.Add(
                    new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, peChange.LongValue.Value));
                break;
            case AttributeDataType.Guid when peChange.GuidValue != null:
                attributeChange.ValueChanges.Add(
                    new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, peChange.GuidValue.Value));
                break;
            case AttributeDataType.Boolean when peChange.BoolValue != null:
                attributeChange.ValueChanges.Add(
                    new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, peChange.BoolValue.Value));
                break;
            case AttributeDataType.DateTime when peChange.DateTimeValue.HasValue:
                attributeChange.ValueChanges.Add(
                    new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, peChange.DateTimeValue.Value));
                break;
            case AttributeDataType.Binary when peChange.ByteValue != null:
                attributeChange.ValueChanges.Add(
                    new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, true, peChange.ByteValue.Length));
                break;
            case AttributeDataType.Reference when peChange.UnresolvedReferenceValue != null:
                // Try to resolve the MVO GUID to a stub CSO in the target connected system.
                // If resolved, store the display identifier as a StringValue with the IsPendingExportStub
                // flag so the UI can render it meaningfully instead of showing a broken-link icon.
                // We deliberately avoid setting ReferenceValue (navigation property) because the stub CSO
                // may be an in-memory entity not yet persisted, and EF's change tracker would attempt
                // to insert it when the parent entity graph is attached during BulkInsertRpeisAsync.
                if (resolvedReferences != null
                    && Guid.TryParse(peChange.UnresolvedReferenceValue, out var mvoGuid)
                    && resolvedReferences.TryGetValue(mvoGuid, out var stubCso))
                {
                    var displayName = GetCsoDisplayIdentifier(stubCso);
                    var valueChange = new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, displayName)
                    {
                        IsPendingExportStub = true
                    };
                    attributeChange.ValueChanges.Add(valueChange);
                }
                else
                {
                    // Fall back to storing as a raw string value for the change history
                    attributeChange.ValueChanges.Add(
                        new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, peChange.UnresolvedReferenceValue));
                }
                break;
            case AttributeDataType.Reference when peChange.StringValue != null:
                // Resolved reference — deferred exports have their UnresolvedReferenceValue cleared
                // and the resolved identifier (e.g. DN) stored in StringValue during reference resolution.
                // Mark as pending export stub so the UI renders a clock icon (awaiting confirmation)
                // rather than the broken-link "unresolved" icon.
                attributeChange.ValueChanges.Add(
                    new ConnectedSystemObjectChangeAttributeValue(attributeChange, valueChangeType, peChange.StringValue)
                    {
                        IsPendingExportStub = true
                    });
                break;
            case AttributeDataType.Text when peChange.StringValue == null:
            case AttributeDataType.Number when peChange.IntValue == null:
            case AttributeDataType.LongNumber when peChange.LongValue == null:
            case AttributeDataType.Guid when peChange.GuidValue == null:
            case AttributeDataType.Boolean when peChange.BoolValue == null:
            case AttributeDataType.DateTime when !peChange.DateTimeValue.HasValue:
            case AttributeDataType.Binary when peChange.ByteValue == null:
                // RemoveAll changes may have null values — record the attribute change without a value
                break;
            default:
                // Skip unrecognised types rather than throwing — export change history
                // is an audit feature, not a sync-critical path
                break;
        }
    }

    /// <summary>
    /// Returns the best available display identifier for a CSO using the priority:
    /// displayName → External ID → Secondary External ID → CSO ID.
    /// </summary>
    public static string GetCsoDisplayIdentifier(ConnectedSystemObject cso)
    {
        return cso.DisplayNameOrId ?? cso.Id.ToString();
    }
}
