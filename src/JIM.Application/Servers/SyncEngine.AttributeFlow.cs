using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Utility;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Attribute flow logic for the sync engine.
/// Contains all sync rule mapping processing — formerly SyncRuleMappingProcessor.
/// </summary>
public partial class SyncEngine
{
    /// <summary>
    /// Processes a sync rule mapping to flow attribute values from CSO to MVO.
    /// </summary>
    internal static void ProcessMapping(
        ConnectedSystemObject cso,
        SyncRuleMapping syncRuleMapping,
        IReadOnlyList<ConnectedSystemObjectType> objectTypes,
        IExpressionEvaluator? expressionEvaluator = null,
        bool skipReferenceAttributes = false,
        bool onlyReferenceAttributes = false,
        bool isFinalReferencePass = false,
        int? contributingSystemId = null,
        List<AttributeFlowWarning>? warnings = null)
    {
        if (cso.MetaverseObject == null)
        {
            Log.Error("ProcessMapping: CSO ({Cso}) is not joined to an MVO!", cso);
            return;
        }

        if (syncRuleMapping.TargetMetaverseAttribute == null)
        {
            Log.Error("ProcessMapping: Sync Rule mapping has no TargetMetaverseAttribute set!");
            return;
        }

        var csoType = objectTypes.Single(t => t.Id == cso.TypeId);
        var mvo = cso.MetaverseObject;

        foreach (var source in syncRuleMapping.Sources.OrderBy(q => q.Order))
        {
            if (source.ConnectedSystemAttributeId.HasValue)
            {
                var csotAttribute = csoType.Attributes.SingleOrDefault(a => a.Id == source.ConnectedSystemAttributeId.Value);
                if (csotAttribute != null)
                {
                    var csoAttributeValues = cso.AttributeValues.Where(av => av.AttributeId == csotAttribute.Id).ToList();
                    if (csoAttributeValues.Count > 0)
                    {
                        var sourceAttributeId = source.ConnectedSystemAttributeId!.Value;
                        if (onlyReferenceAttributes && csotAttribute.Type != AttributeDataType.Reference)
                            continue;

                        // MVA -> SVA truncation: when multiple source values target a single-valued
                        // MV attribute, take only the first value and record a warning (#435)
                        var isMvaToSva = csoAttributeValues.Count > 1 &&
                            syncRuleMapping.TargetMetaverseAttribute.AttributePlurality == AttributePlurality.SingleValued;

                        if (isMvaToSva)
                        {
                            var firstValue = csoAttributeValues.First();
                            var selectedValueDescription = csotAttribute.Type switch
                            {
                                AttributeDataType.Text => firstValue.StringValue ?? "(null)",
                                AttributeDataType.Number => firstValue.IntValue?.ToString() ?? "(null)",
                                AttributeDataType.LongNumber => firstValue.LongValue?.ToString() ?? "(null)",
                                AttributeDataType.DateTime => firstValue.DateTimeValue?.ToString("O") ?? "(null)",
                                AttributeDataType.Boolean => firstValue.BoolValue?.ToString() ?? "(null)",
                                AttributeDataType.Guid => firstValue.GuidValue?.ToString() ?? "(null)",
                                AttributeDataType.Binary => firstValue.ByteValue != null
                                    ? $"[{firstValue.ByteValue.Length} bytes]" : "(null)",
                                _ => "(unknown)"
                            };

                            Log.Warning(
                                "ProcessMapping: Multi-valued source attribute '{SourceAttr}' has {ValueCount} values but target " +
                                "attribute '{TargetAttr}' is single-valued. Using first value: '{SelectedValue}'. CSO {CsoId}",
                                csotAttribute.Name, csoAttributeValues.Count,
                                syncRuleMapping.TargetMetaverseAttribute.Name, selectedValueDescription, cso.Id);

                            warnings?.Add(new AttributeFlowWarning
                            {
                                SourceAttributeName = csotAttribute.Name,
                                TargetAttributeName = syncRuleMapping.TargetMetaverseAttribute.Name,
                                ValueCount = csoAttributeValues.Count,
                                SelectedValue = selectedValueDescription
                            });

                            // Truncate to the first value only
                            csoAttributeValues = new List<ConnectedSystemObjectAttributeValue> { firstValue };
                        }

                        switch (csotAttribute.Type)
                        {
                            case AttributeDataType.Text:
                                ProcessTextAttribute(mvo, syncRuleMapping, sourceAttributeId, cso, csoAttributeValues, csotAttribute, contributingSystemId);
                                break;
                            case AttributeDataType.Number:
                                ProcessNumberAttribute(mvo, syncRuleMapping, sourceAttributeId, cso, csoAttributeValues, contributingSystemId);
                                break;
                            case AttributeDataType.DateTime:
                                ProcessDateTimeAttribute(mvo, syncRuleMapping, csoAttributeValues, contributingSystemId);
                                break;
                            case AttributeDataType.Binary:
                                ProcessBinaryAttribute(mvo, syncRuleMapping, sourceAttributeId, cso, csoAttributeValues, contributingSystemId);
                                break;
                            case AttributeDataType.Reference:
                                if (!skipReferenceAttributes)
                                    ProcessReferenceAttribute(mvo, syncRuleMapping, source, cso, csoAttributeValues, isFinalReferencePass, contributingSystemId);
                                break;
                            case AttributeDataType.Guid:
                                ProcessGuidAttribute(mvo, syncRuleMapping, sourceAttributeId, cso, csoAttributeValues, contributingSystemId);
                                break;
                            case AttributeDataType.Boolean:
                                ProcessBooleanAttribute(mvo, syncRuleMapping, csoAttributeValues, contributingSystemId);
                                break;
                            case AttributeDataType.NotSet:
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    else
                    {
                        var mvoAttributeValuesToDelete = cso.MetaverseObject.AttributeValues.Where(q => q.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id);
                        cso.MetaverseObject.PendingAttributeValueRemovals.AddRange(mvoAttributeValuesToDelete);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(source.Expression))
            {
                ProcessExpressionMapping(cso, mvo, syncRuleMapping, source, csoType, expressionEvaluator, contributingSystemId);
            }
            else if (source.MetaverseAttribute != null)
                throw new InvalidDataException("SyncRuleMappingSource.MetaverseAttribute being populated is not supported for synchronisation operations. " +
                                               "This operation is focused on import flow, so Connected System to Metaverse Object.");
            else
                throw new InvalidDataException("Expected ConnectedSystemAttribute or Expression to be populated in a SyncRuleMappingSource object.");
        }
    }

    /// <summary>
    /// Processes an expression-based sync rule mapping source.
    /// </summary>
    private static void ProcessExpressionMapping(
        ConnectedSystemObject cso,
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        SyncRuleMappingSource source,
        ConnectedSystemObjectType csoType,
        IExpressionEvaluator? expressionEvaluator,
        int? contributingSystemId)
    {
        if (expressionEvaluator == null)
        {
            Log.Warning("ProcessExpressionMapping: Expression-based mapping requires an IExpressionEvaluator but none was provided. Expression: {Expression}", source.Expression);
            return;
        }

        try
        {
            var csAttributeDictionary = BuildCsoAttributeDictionary(cso, csoType);

            Log.Debug("ProcessExpressionMapping: Evaluating expression for CSO {CsoId}. Expression: '{Expression}', Available attributes: [{Attributes}]",
                cso.Id, source.Expression, string.Join(", ", csAttributeDictionary.Keys));

            var context = new ExpressionContext(
                metaverseAttributes: null,
                connectedSystemAttributes: csAttributeDictionary);

            var result = expressionEvaluator.Evaluate(source.Expression!, context);

            if (result == null)
            {
                Log.Debug("ProcessExpressionMapping: Expression '{Expression}' for CSO {CsoId} returned null. Available attributes: [{Attributes}]",
                    source.Expression, cso.Id, string.Join(", ", csAttributeDictionary.Keys));

                var mvoAttributeValuesToDelete = mvo.AttributeValues.Where(q => q.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id);
                mvo.PendingAttributeValueRemovals.AddRange(mvoAttributeValuesToDelete);
                return;
            }

            if (result is string[] stringArrayResult)
            {
                ProcessExpressionArrayResult(mvo, syncRuleMapping.TargetMetaverseAttribute!, stringArrayResult, contributingSystemId);
            }
            else if (result is IEnumerable<string> stringEnumerableResult && result is not string)
            {
                ProcessExpressionArrayResult(mvo, syncRuleMapping.TargetMetaverseAttribute!, stringEnumerableResult.ToArray(), contributingSystemId);
            }
            else
            {
                var existingMvoValue = mvo.AttributeValues.SingleOrDefault(
                    mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id);

                var resultString = result.ToString();
                var valueChanged = existingMvoValue == null ||
                    !string.Equals(existingMvoValue.StringValue, resultString, StringComparison.Ordinal);

                if (valueChanged)
                {
                    if (existingMvoValue != null)
                        mvo.PendingAttributeValueRemovals.Add(existingMvoValue);

                    var newMvoValue = CreateMvoAttributeValueFromExpressionResult(
                        mvo, syncRuleMapping.TargetMetaverseAttribute!, result!, contributingSystemId);

                    if (newMvoValue != null)
                    {
                        mvo.PendingAttributeValueAdditions.Add(newMvoValue);
                        Log.Debug("ProcessExpressionMapping: Expression-based mapping set {AttributeName} to '{Value}' on MVO {MvoId}",
                            syncRuleMapping.TargetMetaverseAttribute!.Name, resultString, mvo.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ProcessExpressionMapping: Error evaluating expression '{Expression}' for CSO {CsoId}: {Error}",
                source.Expression, cso.Id, ex.Message);
        }
    }

    private static void ProcessTextAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject cso,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        ConnectedSystemObjectTypeAttribute csotAttribute,
        int? contributingSystemId)
    {
        var existingMvoValues = mvo.AttributeValues
            .Where(mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id)
            .Select(mvoav => mvoav.StringValue)
            .ToList();
        var incomingCsoValues = csoAttributeValues.Select(csoav => csoav.StringValue).ToList();

        Log.Debug("SyncEngine: Comparing attribute '{AttrName}' for CSO. MVO values: [{MvoValues}], CSO values: [{CsoValues}]",
            csotAttribute.Name,
            string.Join(", ", existingMvoValues.Select(v => v ?? "(null)")),
            string.Join(", ", incomingCsoValues.Select(v => v ?? "(null)")));

        var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
            !csoAttributeValues.Any(csoav => csoav.StringValue != null && csoav.StringValue.Equals(mvoav.StringValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // Use the (possibly truncated) csoAttributeValues list rather than cso.AttributeValues
        // to respect MVA->SVA first-value selection (#435)
        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
            !mvo.AttributeValues.Any(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                mvoav.StringValue != null && mvoav.StringValue.Equals(csoav.StringValue)));

        if (mvoObsoleteAttributeValues.Any() || csoNewAttributeValues.Any())
        {
            Log.Debug("SyncEngine: Attribute '{AttrName}' has changes. Removing {RemoveCount}, Adding {AddCount}",
                csotAttribute.Name, mvoObsoleteAttributeValues.Count(), csoNewAttributeValues.Count());
        }

        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                StringValue = newCsoNewAttributeValue.StringValue,
                ContributedBySystemId = contributingSystemId
            });
        }
    }

    private static void ProcessNumberAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject cso,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId)
    {
        var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
            !csoAttributeValues.Any(csoav => csoav.IntValue != null && csoav.IntValue.Equals(mvoav.IntValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // Use the (possibly truncated) csoAttributeValues list rather than cso.AttributeValues
        // to respect MVA->SVA first-value selection (#435)
        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
            !mvo.AttributeValues.Any(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                mvoav.IntValue != null && mvoav.IntValue.Equals(csoav.IntValue)));

        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                IntValue = newCsoNewAttributeValue.IntValue,
                ContributedBySystemId = contributingSystemId
            });
        }
    }

    private static void ProcessDateTimeAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId)
    {
        var csoValue = csoAttributeValues.FirstOrDefault();
        var mvoValue = mvo.AttributeValues.SingleOrDefault(mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id);

        if (mvoValue != null && csoValue == null)
        {
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
        }
        else if (csoValue != null && mvoValue == null)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                DateTimeValue = csoValue.DateTimeValue,
                ContributedBySystemId = contributingSystemId
            });
        }
        else if (csoValue != null && mvoValue != null && mvoValue.DateTimeValue != csoValue.DateTimeValue)
        {
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                DateTimeValue = csoValue.DateTimeValue,
                ContributedBySystemId = contributingSystemId
            });
        }
    }

    private static void ProcessBinaryAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject cso,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId)
    {
        var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
            !csoAttributeValues.Any(csoav =>
                csoav.ByteValue != null && JIM.Utilities.Utilities.AreByteArraysTheSame(csoav.ByteValue, mvoav.ByteValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // Use the (possibly truncated) csoAttributeValues list rather than cso.AttributeValues
        // to respect MVA->SVA first-value selection (#435)
        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
            !mvo.AttributeValues.Any(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                JIM.Utilities.Utilities.AreByteArraysTheSame(mvoav.ByteValue, csoav.ByteValue)));

        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                ByteValue = newCsoNewAttributeValue.ByteValue,
                ContributedBySystemId = contributingSystemId
            });
        }
    }

    private static void ProcessReferenceAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        SyncRuleMappingSource source,
        ConnectedSystemObject cso,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        bool isFinalReferencePass,
        int? contributingSystemId)
    {
        // Use ResolvedReferenceMetaverseObjectId (populated via direct SQL) as the primary
        // source, falling back to navigation properties for compatibility with in-memory tests.
        static Guid? GetReferencedMvoId(ConnectedSystemObjectAttributeValue csoav)
        {
            if (csoav.ResolvedReferenceMetaverseObjectId.HasValue)
                return csoav.ResolvedReferenceMetaverseObjectId;
            if (csoav.ReferenceValue == null)
                return null;
            return csoav.ReferenceValue.MetaverseObjectId ?? csoav.ReferenceValue.MetaverseObject?.Id;
        }

        // A reference is resolved if we can determine the MVO it points to — either via
        // ResolvedReferenceMetaverseObjectId (direct SQL) or ReferenceValue navigation (in-memory tests).
        static bool IsResolved(ConnectedSystemObjectAttributeValue csoav)
            => (csoav.ReferenceValueId.HasValue || csoav.ReferenceValue != null) && GetReferencedMvoId(csoav).HasValue;

        var unresolvedReferenceValues = csoAttributeValues.Where(csoav =>
            !IsResolved(csoav) &&
            (csoav.ReferenceValueId != null || !string.IsNullOrEmpty(csoav.UnresolvedReferenceValue))).ToList();

        if (unresolvedReferenceValues.Count > 0)
        {
            foreach (var unresolved in unresolvedReferenceValues)
            {
                if (unresolved.ReferenceValueId.HasValue && !unresolved.ResolvedReferenceMetaverseObjectId.HasValue)
                {
                    // Referenced CSO exists but isn't joined to an MVO yet.
                    if (isFinalReferencePass)
                    {
                        Log.Warning("SyncEngine: CSO {CsoId} has reference attribute {AttrName} pointing to CSO {RefCsoId} which is not joined to an MVO. " +
                            "Ensure referenced objects are synced before referencing objects. The reference will not flow to the MVO.",
                            cso.Id, source.ConnectedSystemAttribute?.Name ?? "unknown", unresolved.ReferenceValueId);
                    }
                    else
                    {
                        Log.Debug("SyncEngine: CSO {CsoId} has reference attribute {AttrName} pointing to CSO {RefCsoId} which is not yet joined to an MVO. " +
                            "This will be retried during cross-page reference resolution.",
                            cso.Id, source.ConnectedSystemAttribute?.Name ?? "unknown", unresolved.ReferenceValueId);
                    }
                }
            }
        }

        var hasUnresolvedReferences = csoAttributeValues.Any(csoav =>
            csoav.ReferenceValueId.HasValue && !IsResolved(csoav));

        if (!hasUnresolvedReferences)
        {
            var resolvedMvoIds = new HashSet<Guid>(
                csoAttributeValues
                    .Where(IsResolved)
                    .Select(csoav => GetReferencedMvoId(csoav)!.Value));

            var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                (mvoav.ReferenceValueId ?? mvoav.ReferenceValue?.Id) is Guid mvoRefId &&
                !resolvedMvoIds.Contains(mvoRefId));
            mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);
        }
        else
        {
            Log.Debug("ProcessReferenceAttribute: Skipping MVO reference removal for CSO {CsoId} " +
                "because some reference(s) have unresolved MetaverseObject (cross-page references). " +
                "Removals will be handled in the cross-page resolution pass.",
                cso.Id);
        }

        var existingMvoRefIds = new HashSet<Guid>(
            mvo.AttributeValues
                .Where(mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id)
                .Select(mvoav => mvoav.ReferenceValueId ?? mvoav.ReferenceValue?.Id)
                .Where(id => id.HasValue)
                .Select(id => id!.Value));

        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
        {
            var mvoId = GetReferencedMvoId(csoav);
            return mvoId.HasValue && !existingMvoRefIds.Contains(mvoId.Value);
        }).ToList();

        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            var targetMvoId = GetReferencedMvoId(newCsoNewAttributeValue);
            if (!targetMvoId.HasValue)
                continue;

            var newMvoAv = new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                ContributedBySystemId = contributingSystemId
            };

            if (newCsoNewAttributeValue.ReferenceValue?.MetaverseObject != null)
                newMvoAv.ReferenceValue = newCsoNewAttributeValue.ReferenceValue.MetaverseObject;
            else
                newMvoAv.ReferenceValueId = targetMvoId.Value;

            mvo.PendingAttributeValueAdditions.Add(newMvoAv);
        }
    }

    private static void ProcessGuidAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject cso,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId)
    {
        var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
            !csoAttributeValues.Any(csoav => csoav.GuidValue.HasValue && csoav.GuidValue.Equals(mvoav.GuidValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // Use the (possibly truncated) csoAttributeValues list rather than cso.AttributeValues
        // to respect MVA->SVA first-value selection (#435)
        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
            !mvo.AttributeValues.Any(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                mvoav.GuidValue.HasValue && mvoav.GuidValue.Equals(csoav.GuidValue)));

        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                GuidValue = newCsoNewAttributeValue.GuidValue,
                ContributedBySystemId = contributingSystemId
            });
        }
    }

    private static void ProcessBooleanAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        int? contributingSystemId)
    {
        var csoValue = csoAttributeValues.FirstOrDefault();
        var mvoValue = mvo.AttributeValues.SingleOrDefault(mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id);

        if (mvoValue != null && csoValue == null)
        {
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
        }
        else if (csoValue != null && mvoValue == null)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                BoolValue = csoValue.BoolValue,
                ContributedBySystemId = contributingSystemId
            });
        }
        else if (csoValue != null && mvoValue != null && mvoValue.BoolValue != csoValue.BoolValue)
        {
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                BoolValue = csoValue.BoolValue,
                ContributedBySystemId = contributingSystemId
            });
        }
    }

    private static Dictionary<string, object?> BuildCsoAttributeDictionary(
        ConnectedSystemObject cso,
        ConnectedSystemObjectType csoType)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var attributeValue in cso.AttributeValues)
        {
            var csotAttribute = csoType.Attributes.SingleOrDefault(a => a.Id == attributeValue.AttributeId);
            if (csotAttribute == null)
            {
                Log.Warning("BuildCsoAttributeDictionary: CSO {CsoId} has attribute value with AttributeId={AttrId} but attribute not found in type definition",
                    cso.Id, attributeValue.AttributeId);
                continue;
            }

            var attributeName = csotAttribute.Name;

            object? value = csotAttribute.Type switch
            {
                AttributeDataType.Text => attributeValue.StringValue,
                AttributeDataType.Number => attributeValue.IntValue,
                AttributeDataType.LongNumber => attributeValue.LongValue,
                AttributeDataType.DateTime => attributeValue.DateTimeValue,
                AttributeDataType.Boolean => attributeValue.BoolValue,
                AttributeDataType.Guid => attributeValue.GuidValue,
                AttributeDataType.Binary => attributeValue.ByteValue,
                AttributeDataType.Reference => attributeValue.ReferenceValue?.Id.ToString(),
                _ => null
            };

            if (!attributes.ContainsKey(attributeName))
            {
                attributes[attributeName] = value;
            }
        }

        return attributes;
    }

    private static void ProcessExpressionArrayResult(
        MetaverseObject mvo,
        MetaverseAttribute targetAttribute,
        string[] values,
        int? contributingSystemId)
    {
        if (values.Length == 0)
        {
            var mvoAttributeValuesToDelete = mvo.AttributeValues
                .Where(q => q.AttributeId == targetAttribute.Id);
            mvo.PendingAttributeValueRemovals.AddRange(mvoAttributeValuesToDelete);
            return;
        }

        var existingMvoValues = mvo.AttributeValues
            .Where(mvoav => mvoav.AttributeId == targetAttribute.Id)
            .ToList();

        var valuesToRemove = existingMvoValues
            .Where(existing => !values.Contains(existing.StringValue, StringComparer.Ordinal));
        mvo.PendingAttributeValueRemovals.AddRange(valuesToRemove);

        var existingStringValues = existingMvoValues
            .Select(mvoav => mvoav.StringValue)
            .ToHashSet(StringComparer.Ordinal);

        var valuesToAdd = values
            .Where(v => !existingStringValues.Contains(v));

        foreach (var value in valuesToAdd)
        {
            var newMvoValue = new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = targetAttribute,
                AttributeId = targetAttribute.Id,
                StringValue = value,
                ContributedBySystemId = contributingSystemId
            };
            mvo.PendingAttributeValueAdditions.Add(newMvoValue);
        }

        if (valuesToRemove.Any() || valuesToAdd.Any())
        {
            Log.Debug("ProcessExpressionArrayResult: Expression array result for {AttributeName} - removing {RemoveCount}, adding {AddCount} values on MVO {MvoId}",
                targetAttribute.Name, valuesToRemove.Count(), valuesToAdd.Count(), mvo.Id);
        }
    }

    private static MetaverseObjectAttributeValue? CreateMvoAttributeValueFromExpressionResult(
        MetaverseObject mvo,
        MetaverseAttribute targetAttribute,
        object result,
        int? contributingSystemId)
    {
        var newMvoValue = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = targetAttribute,
            AttributeId = targetAttribute.Id,
            ContributedBySystemId = contributingSystemId
        };

        switch (targetAttribute.Type)
        {
            case AttributeDataType.Text:
                newMvoValue.StringValue = result?.ToString();
                break;
            case AttributeDataType.Number:
                if (result is int intVal)
                    newMvoValue.IntValue = intVal;
                else if (result is long longVal)
                    newMvoValue.IntValue = (int)longVal;
                else if (int.TryParse(result?.ToString(), out var parsedInt))
                    newMvoValue.IntValue = parsedInt;
                else
                {
                    Log.Warning("CreateMvoAttributeValueFromExpressionResult: Could not convert expression result '{Result}' to Number", result);
                    return null;
                }
                break;
            case AttributeDataType.DateTime:
                if (result is DateTime dtVal)
                    newMvoValue.DateTimeValue = dtVal;
                else if (DateTime.TryParse(result?.ToString(), out var parsedDt))
                    newMvoValue.DateTimeValue = parsedDt;
                else
                {
                    Log.Warning("CreateMvoAttributeValueFromExpressionResult: Could not convert expression result '{Result}' to DateTime", result);
                    return null;
                }
                break;
            case AttributeDataType.Boolean:
                if (result is bool boolVal)
                    newMvoValue.BoolValue = boolVal;
                else if (bool.TryParse(result?.ToString(), out var parsedBool))
                    newMvoValue.BoolValue = parsedBool;
                else
                {
                    Log.Warning("CreateMvoAttributeValueFromExpressionResult: Could not convert expression result '{Result}' to Boolean", result);
                    return null;
                }
                break;
            case AttributeDataType.Guid:
                if (result is Guid guidVal)
                    newMvoValue.GuidValue = guidVal;
                else if (Guid.TryParse(result?.ToString(), out var parsedGuid))
                    newMvoValue.GuidValue = parsedGuid;
                else
                {
                    Log.Warning("CreateMvoAttributeValueFromExpressionResult: Could not convert expression result '{Result}' to Guid", result);
                    return null;
                }
                break;
            default:
                Log.Warning("CreateMvoAttributeValueFromExpressionResult: Unsupported target attribute type {Type} for expression result", targetAttribute.Type);
                return null;
        }

        return newMvoValue;
    }
}
