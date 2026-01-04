using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Service responsible for reconciling PendingExport attribute changes during import.
/// Compares imported CSO values against pending export assertions to confirm or mark for retry.
/// </summary>
public class PendingExportReconciliationService
{
    private readonly JimApplication _jim;

    /// <summary>
    /// Default maximum number of export attempts before marking an attribute change as Failed.
    /// </summary>
    public const int DefaultMaxRetries = 5;

    public PendingExportReconciliationService(JimApplication jim)
    {
        _jim = jim;
    }

    /// <summary>
    /// Reconciles a Connected System Object's imported attribute values against any pending exports.
    /// This should be called after CSO attribute values are updated during import.
    /// </summary>
    /// <param name="connectedSystemObject">The CSO that was just imported/updated.</param>
    /// <returns>A result indicating what reconciliation actions were taken.</returns>
    public async Task<PendingExportReconciliationResult> ReconcileAsync(ConnectedSystemObject connectedSystemObject)
    {
        var result = new PendingExportReconciliationResult();

        // Get any pending export for this CSO
        var pendingExport = await _jim.Repository.ConnectedSystems.GetPendingExportByConnectedSystemObjectIdAsync(connectedSystemObject.Id);

        if (pendingExport == null)
        {
            Log.Debug("ReconcileAsync: No pending export found for CSO {CsoId}", connectedSystemObject.Id);
            return result;
        }

        // Only process exports that have been executed and are awaiting confirmation
        if (pendingExport.Status != PendingExportStatus.Exported)
        {
            Log.Debug("ReconcileAsync: PendingExport {ExportId} status is {Status}, not Exported. Skipping reconciliation.",
                pendingExport.Id, pendingExport.Status);
            return result;
        }

        Log.Debug("ReconcileAsync: Found pending export {ExportId} with {Count} attribute changes for CSO {CsoId}",
            pendingExport.Id, pendingExport.AttributeValueChanges.Count, connectedSystemObject.Id);

        // Process each attribute change that is awaiting confirmation
        var changesAwaitingConfirmation = pendingExport.AttributeValueChanges
            .Where(ac => ac.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation)
            .ToList();

        foreach (var attrChange in changesAwaitingConfirmation)
        {
            var confirmed = IsAttributeChangeConfirmed(connectedSystemObject, attrChange);

            // Verbose logging for detailed troubleshooting - shows all comparison details
            Log.Verbose("ReconcileAsync: Comparing attribute {AttrName} (ChangeType: {ChangeType}) for CSO {CsoId}. " +
                "Expected: '{ExpectedValue}', Found: '{ActualValue}', Confirmed: {Confirmed}",
                attrChange.Attribute?.Name ?? "unknown",
                attrChange.ChangeType,
                connectedSystemObject.Id,
                GetExpectedValueAsString(attrChange),
                GetImportedValueAsString(connectedSystemObject, attrChange),
                confirmed);

            if (confirmed)
            {
                result.ConfirmedChanges.Add(attrChange);
                Log.Debug("ReconcileAsync: Attribute change {AttrChangeId} (Attr: {AttrName}) confirmed",
                    attrChange.Id, attrChange.Attribute?.Name ?? "unknown");
            }
            else
            {
                // Not confirmed - mark for retry or fail
                attrChange.Status = ShouldMarkAsFailed(attrChange)
                    ? PendingExportAttributeChangeStatus.Failed
                    : PendingExportAttributeChangeStatus.ExportedNotConfirmed;

                // Capture what was imported for debugging
                attrChange.LastImportedValue = GetImportedValueAsString(connectedSystemObject, attrChange);

                var expectedValue = GetExpectedValueAsString(attrChange);

                if (attrChange.Status == PendingExportAttributeChangeStatus.Failed)
                {
                    result.FailedChanges.Add(attrChange);
                    Log.Warning("ReconcileAsync: Attribute change {AttrChangeId} (Attr: {AttrName}) failed after {Attempts} attempts. " +
                        "Expected: '{ExpectedValue}', Actual: '{ImportedValue}'",
                        attrChange.Id, attrChange.Attribute?.Name ?? "unknown", attrChange.ExportAttemptCount,
                        expectedValue, attrChange.LastImportedValue);
                }
                else
                {
                    result.RetryChanges.Add(attrChange);
                    Log.Information("ReconcileAsync: Attribute change {AttrChangeId} (Attr: {AttrName}) not confirmed, will retry (attempt {Attempt}). " +
                        "Expected: '{ExpectedValue}', Actual: '{ImportedValue}'",
                        attrChange.Id, attrChange.Attribute?.Name ?? "unknown", attrChange.ExportAttemptCount,
                        expectedValue, attrChange.LastImportedValue);
                }
            }
        }

        // Remove confirmed changes from the pending export
        foreach (var confirmed in result.ConfirmedChanges)
        {
            pendingExport.AttributeValueChanges.Remove(confirmed);
        }

        // If this was a Create and the Secondary External ID attribute was confirmed, transition to Update
        // This ensures remaining attribute changes are processed as updates, not creates
        TransitionCreateToUpdateIfSecondaryExternalIdConfirmed(pendingExport, result);

        // If all attribute changes are confirmed/removed, delete the pending export
        // (Only delete if there are no pending, not confirmed, or failed changes left)
        var hasRemainingChanges = pendingExport.AttributeValueChanges.Any(ac =>
            ac.Status == PendingExportAttributeChangeStatus.Pending ||
            ac.Status == PendingExportAttributeChangeStatus.ExportedPendingConfirmation ||
            ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed ||
            ac.Status == PendingExportAttributeChangeStatus.Failed);

        if (!hasRemainingChanges)
        {
            await _jim.Repository.ConnectedSystems.DeletePendingExportAsync(pendingExport);
            result.PendingExportDeleted = true;
            Log.Information("ReconcileAsync: All attribute changes confirmed. Deleted pending export {ExportId}", pendingExport.Id);
        }
        else
        {
            // Check if we should update the pending export status based on attribute change statuses
            UpdatePendingExportStatus(pendingExport);
            await _jim.Repository.ConnectedSystems.UpdatePendingExportAsync(pendingExport);
            Log.Debug("ReconcileAsync: Updated pending export {ExportId} with {Confirmed} confirmed, {Retry} for retry, {Failed} failed",
                pendingExport.Id, result.ConfirmedChanges.Count, result.RetryChanges.Count, result.FailedChanges.Count);
        }

        return result;
    }

    /// <summary>
    /// Determines if an attribute change has been confirmed by comparing the exported value
    /// against the imported CSO attribute value.
    /// </summary>
    private static bool IsAttributeChangeConfirmed(ConnectedSystemObject cso, PendingExportAttributeValueChange attrChange)
    {
        if (attrChange.Attribute == null)
            return false;

        // Find the corresponding attribute value on the CSO
        var csoAttrValues = cso.AttributeValues
            .Where(av => av.AttributeId == attrChange.AttributeId)
            .ToList();

        switch (attrChange.ChangeType)
        {
            case PendingExportAttributeChangeType.Add:
            case PendingExportAttributeChangeType.Update:
                // For Add/Update, the value should exist on the CSO
                return ValueExistsOnCso(csoAttrValues, attrChange);

            case PendingExportAttributeChangeType.Remove:
                // For Remove, the value should NOT exist on the CSO
                return !ValueExistsOnCso(csoAttrValues, attrChange);

            case PendingExportAttributeChangeType.RemoveAll:
                // For RemoveAll, there should be no values for this attribute
                return csoAttrValues.Count == 0;

            default:
                return false;
        }
    }

    /// <summary>
    /// Checks if the pending export attribute change value exists in the CSO's attribute values.
    /// </summary>
    private static bool ValueExistsOnCso(List<ConnectedSystemObjectAttributeValue> csoValues, PendingExportAttributeValueChange attrChange)
    {
        if (csoValues.Count == 0)
            return false;

        // Check based on the data type of the attribute
        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        return attrType switch
        {
            AttributeDataType.Text =>
                !string.IsNullOrEmpty(attrChange.StringValue) &&
                csoValues.Any(v => string.Equals(v.StringValue, attrChange.StringValue, StringComparison.Ordinal)),

            AttributeDataType.Number =>
                attrChange.IntValue.HasValue &&
                csoValues.Any(v => v.IntValue == attrChange.IntValue),

            AttributeDataType.LongNumber =>
                attrChange.LongValue.HasValue &&
                csoValues.Any(v => v.LongValue == attrChange.LongValue),

            AttributeDataType.DateTime =>
                attrChange.DateTimeValue.HasValue &&
                csoValues.Any(v => v.DateTimeValue == attrChange.DateTimeValue),

            AttributeDataType.Binary =>
                attrChange.ByteValue != null &&
                csoValues.Any(v => v.ByteValue != null && v.ByteValue.SequenceEqual(attrChange.ByteValue)),

            AttributeDataType.Boolean =>
                attrChange.BoolValue.HasValue &&
                csoValues.Any(v => v.BoolValue == attrChange.BoolValue),

            AttributeDataType.Guid =>
                attrChange.GuidValue.HasValue &&
                csoValues.Any(v => v.GuidValue == attrChange.GuidValue),

            AttributeDataType.Reference =>
                !string.IsNullOrEmpty(attrChange.UnresolvedReferenceValue) &&
                csoValues.Any(v => string.Equals(v.UnresolvedReferenceValue, attrChange.UnresolvedReferenceValue, StringComparison.Ordinal)),

            _ => false
        };
    }

    /// <summary>
    /// Gets the imported value as a string for debugging purposes.
    /// </summary>
    private static string? GetImportedValueAsString(ConnectedSystemObject cso, PendingExportAttributeValueChange attrChange)
    {
        var csoAttrValues = cso.AttributeValues
            .Where(av => av.AttributeId == attrChange.AttributeId)
            .ToList();

        if (csoAttrValues.Count == 0)
            return "(no values)";

        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        var values = attrType switch
        {
            AttributeDataType.Text => csoAttrValues.Select(v => v.StringValue).Where(v => v != null),
            AttributeDataType.Number => csoAttrValues.Select(v => v.IntValue?.ToString()).Where(v => v != null),
            AttributeDataType.LongNumber => csoAttrValues.Select(v => v.LongValue?.ToString()).Where(v => v != null),
            AttributeDataType.DateTime => csoAttrValues.Select(v => v.DateTimeValue?.ToString("O")).Where(v => v != null),
            AttributeDataType.Boolean => csoAttrValues.Select(v => v.BoolValue?.ToString()).Where(v => v != null),
            AttributeDataType.Guid => csoAttrValues.Select(v => v.GuidValue?.ToString()).Where(v => v != null),
            AttributeDataType.Reference => csoAttrValues.Select(v => v.UnresolvedReferenceValue).Where(v => v != null),
            _ => Enumerable.Empty<string?>()
        };

        var valueList = values.ToList();
        return valueList.Count > 0 ? string.Join(", ", valueList) : "(no matching type values)";
    }

    /// <summary>
    /// Gets the expected (exported) value as a string for debugging purposes.
    /// </summary>
    private static string? GetExpectedValueAsString(PendingExportAttributeValueChange attrChange)
    {
        var attrType = attrChange.Attribute?.Type ?? AttributeDataType.NotSet;

        return attrType switch
        {
            AttributeDataType.Text => attrChange.StringValue ?? "(null)",
            AttributeDataType.Number => attrChange.IntValue?.ToString() ?? "(null)",
            AttributeDataType.LongNumber => attrChange.LongValue?.ToString() ?? "(null)",
            AttributeDataType.DateTime => attrChange.DateTimeValue?.ToString("O") ?? "(null)",
            AttributeDataType.Boolean => attrChange.BoolValue?.ToString() ?? "(null)",
            AttributeDataType.Guid => attrChange.GuidValue?.ToString() ?? "(null)",
            AttributeDataType.Reference => attrChange.UnresolvedReferenceValue ?? "(null)",
            AttributeDataType.Binary => attrChange.ByteValue != null ? $"(binary, {attrChange.ByteValue.Length} bytes)" : "(null)",
            _ => "(unknown type)"
        };
    }

    /// <summary>
    /// Determines if an attribute change should be marked as Failed based on retry count.
    /// </summary>
    private static bool ShouldMarkAsFailed(PendingExportAttributeValueChange attrChange)
    {
        // ExportAttemptCount was already incremented during export, so compare directly
        return attrChange.ExportAttemptCount >= DefaultMaxRetries;
    }

    /// <summary>
    /// If the pending export was a Create and the Secondary External ID attribute has been confirmed,
    /// transition it to an Update. This is necessary because once an object is created, any remaining
    /// unconfirmed attribute changes should be applied as updates, not as part of a create operation.
    /// Connectors require the Secondary External ID (e.g., distinguishedName for LDAP) in the attribute
    /// changes for Create operations, but once confirmed, it is removed. Without this transition, retry
    /// attempts would fail because the connector cannot determine where to create the object.
    /// </summary>
    private static void TransitionCreateToUpdateIfSecondaryExternalIdConfirmed(PendingExport pendingExport, PendingExportReconciliationResult result)
    {
        // Only applies to Create pending exports
        if (pendingExport.ChangeType != PendingExportChangeType.Create)
            return;

        // Check if the Secondary External ID attribute was among the confirmed changes
        var secondaryExternalIdWasConfirmed = result.ConfirmedChanges.Any(ac =>
            ac.Attribute?.IsSecondaryExternalId == true);

        if (!secondaryExternalIdWasConfirmed)
            return;

        // The object was successfully created (Secondary External ID confirmed), but there are remaining
        // attribute changes that weren't confirmed. Transition to Update so these can be applied as modifications.
        if (pendingExport.AttributeValueChanges.Count > 0)
        {
            var confirmedAttrName = result.ConfirmedChanges
                .FirstOrDefault(ac => ac.Attribute?.IsSecondaryExternalId == true)?.Attribute?.Name ?? "unknown";

            pendingExport.ChangeType = PendingExportChangeType.Update;
            Log.Information("ReconcileAsync: Transitioned pending export {ExportId} from Create to Update. " +
                "Secondary External ID attribute '{AttributeName}' was confirmed but {RemainingCount} attribute changes remain.",
                pendingExport.Id, confirmedAttrName, pendingExport.AttributeValueChanges.Count);
        }
    }

    /// <summary>
    /// Updates the PendingExport status based on its attribute change statuses.
    /// </summary>
    private static void UpdatePendingExportStatus(PendingExport pendingExport)
    {
        var allFailed = pendingExport.AttributeValueChanges.All(ac => ac.Status == PendingExportAttributeChangeStatus.Failed);
        var anyFailed = pendingExport.AttributeValueChanges.Any(ac => ac.Status == PendingExportAttributeChangeStatus.Failed);
        var anyPendingOrRetry = pendingExport.AttributeValueChanges.Any(ac =>
            ac.Status == PendingExportAttributeChangeStatus.Pending ||
            ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed);

        if (allFailed)
        {
            pendingExport.Status = PendingExportStatus.Failed;
        }
        else if (anyPendingOrRetry)
        {
            // There are changes that need to be exported/re-exported
            pendingExport.Status = PendingExportStatus.ExportNotImported;
        }
        else if (anyFailed)
        {
            // Some failed, but some are still pending confirmation
            pendingExport.Status = PendingExportStatus.Exported;
        }
    }
}

/// <summary>
/// Result of a pending export reconciliation operation.
/// </summary>
public class PendingExportReconciliationResult
{
    /// <summary>
    /// Attribute changes that were confirmed (value matched imported value).
    /// These have been removed from the PendingExport.
    /// </summary>
    public List<PendingExportAttributeValueChange> ConfirmedChanges { get; } = new();

    /// <summary>
    /// Attribute changes that were not confirmed and will be retried.
    /// </summary>
    public List<PendingExportAttributeValueChange> RetryChanges { get; } = new();

    /// <summary>
    /// Attribute changes that exceeded max retries and are now Failed.
    /// </summary>
    public List<PendingExportAttributeValueChange> FailedChanges { get; } = new();

    /// <summary>
    /// Whether the PendingExport was deleted (all changes confirmed).
    /// </summary>
    public bool PendingExportDeleted { get; set; }

    /// <summary>
    /// True if any reconciliation was performed.
    /// </summary>
    public bool HasChanges => ConfirmedChanges.Count > 0 || RetryChanges.Count > 0 || FailedChanges.Count > 0;
}
