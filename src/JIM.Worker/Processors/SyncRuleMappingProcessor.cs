using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Serilog;

namespace JIM.Worker.Processors;

public static class SyncRuleMappingProcessor
{
    /// <summary>
    /// Processes a sync rule mapping to flow attribute values from CSO to MVO.
    /// </summary>
    /// <param name="connectedSystemObject">The source CSO.</param>
    /// <param name="syncRuleMapping">The sync rule mapping defining the attribute flow.</param>
    /// <param name="connectedSystemObjectTypes">Object types for attribute lookup.</param>
    /// <param name="expressionEvaluator">Optional expression evaluator for expression-based mappings.</param>
    /// <param name="skipReferenceAttributes">If true, skip reference attribute processing (deferred to second pass).</param>
    /// <param name="onlyReferenceAttributes">If true, process ONLY reference attributes (for deferred second pass). Takes precedence over skipReferenceAttributes.</param>
    public static void Process(
        ConnectedSystemObject connectedSystemObject,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectType> connectedSystemObjectTypes,
        IExpressionEvaluator? expressionEvaluator = null,
        bool skipReferenceAttributes = false,
        bool onlyReferenceAttributes = false)
    {
        if (connectedSystemObject.MetaverseObject == null)
        {
            // why is this an error?
            Log.Error($"Process: CSO ({connectedSystemObject}) is not joined to an MVO!");
            return;
        }

        if (syncRuleMapping.TargetMetaverseAttribute == null)
        {
            Log.Error("Process: Sync Rule mapping has no TargetMetaverseAttribute set!");
            return;
        }

        // use the Connected System Object Type from our reference list, as the CSO will not contain any attributes (to keep their size down on retrieval).
        var csoType = connectedSystemObjectTypes.Single(t => t.Id == connectedSystemObject.TypeId);

        // working with a MVO reference directly to make this function easier to understand.
        var mvo = connectedSystemObject.MetaverseObject;

        foreach (var source in syncRuleMapping.Sources.OrderBy(q => q.Order))
        {
            // goal:
            // generate or select a source value to assign to a target MVO attribute.
            // inbound sync rule mappings can have one or more sources and will compound if numerous. those sources can be of different types, i.e:
            // - direct attribute flow
            // - generated from functions
            // - generated from expressions

            // NOTE: attribute priority has not been implemented yet and will come in a later effort.
            // for now, all mappings will be applied, meaning if there are multiple mapping to a MVO attribute, the last to be processed will win.

            if (source.ConnectedSystemAttributeId.HasValue)
            {
                // process the specific CSO attribute defined in this sync rule mapping source
                var csotAttribute = csoType.Attributes.SingleOrDefault(a => a.Id == source.ConnectedSystemAttributeId.Value);
                if (csotAttribute != null)
                {
                    // are there matching attribute values on the CSO for this attribute?
                    // this might return multiple objects if the attribute is multivalued, i.e. a member attribute on a group.
                    // use AttributeId instead of GetAttributeValues because Attribute navigation property may not be loaded
                    var csoAttributeValues = connectedSystemObject.AttributeValues.Where(av => av.AttributeId == csotAttribute.Id).ToList();
                    if (csoAttributeValues.Count > 0)
                    {
                        // Process based on attribute type - MVA types process all values at once,
                        // SVA types process a single value
                        var sourceAttributeId = source.ConnectedSystemAttributeId!.Value;
                        // If onlyReferenceAttributes is set, skip all non-reference types
                        if (onlyReferenceAttributes && csotAttribute.Type != AttributeDataType.Reference)
                            continue;

                        switch (csotAttribute.Type)
                        {
                            case AttributeDataType.Text:
                                ProcessTextAttribute(mvo, syncRuleMapping, sourceAttributeId, connectedSystemObject, csoAttributeValues, csotAttribute);
                                break;

                            case AttributeDataType.Number:
                                ProcessNumberAttribute(mvo, syncRuleMapping, sourceAttributeId, connectedSystemObject, csoAttributeValues);
                                break;

                            case AttributeDataType.DateTime:
                                ProcessDateTimeAttribute(mvo, syncRuleMapping, csoAttributeValues);
                                break;

                            case AttributeDataType.Binary:
                                ProcessBinaryAttribute(mvo, syncRuleMapping, sourceAttributeId, connectedSystemObject, csoAttributeValues);
                                break;

                            case AttributeDataType.Reference:
                                // Reference attributes may need to be deferred when processing objects in the same page,
                                // because group member references may point to user CSOs that haven't been processed yet.
                                // By skipping references in the first pass and processing them after all CSOs have MVOs,
                                // we ensure all referenced MVOs exist.
                                if (!skipReferenceAttributes)
                                {
                                    ProcessReferenceAttribute(mvo, syncRuleMapping, source, connectedSystemObject, csoAttributeValues);
                                }
                                break;

                            case AttributeDataType.Guid:
                                ProcessGuidAttribute(mvo, syncRuleMapping, sourceAttributeId, connectedSystemObject, csoAttributeValues);
                                break;

                            case AttributeDataType.Boolean:
                                ProcessBooleanAttribute(mvo, syncRuleMapping, csoAttributeValues);
                                break;

                            case AttributeDataType.NotSet:
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    else
                    {
                        // there are no CSO values for this attribute. reflect this by deleting all MVO attribute values for this attribute.
                        var mvoAttributeValuesToDelete = connectedSystemObject.MetaverseObject.AttributeValues.Where(q => q.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id);
                        connectedSystemObject.MetaverseObject.PendingAttributeValueRemovals.AddRange(mvoAttributeValuesToDelete);
                    }
                }
            }
            else if (!string.IsNullOrWhiteSpace(source.Expression))
            {
                // Process expression-based mapping
                if (expressionEvaluator == null)
                {
                    Log.Warning("Process: Expression-based mapping requires an IExpressionEvaluator but none was provided. Expression: {Expression}", source.Expression);
                    continue;
                }

                try
                {
                    // Build CSO attribute dictionary for expression evaluation
                    var csAttributeDictionary = BuildCsoAttributeDictionary(connectedSystemObject, csoType);

                    Log.Debug("Process: Evaluating expression for CSO {CsoId}. Expression: '{Expression}', Available attributes: [{Attributes}]",
                        connectedSystemObject.Id, source.Expression, string.Join(", ", csAttributeDictionary.Keys));

                    // Create expression context with CSO attributes (and empty MV attributes for inbound)
                    var context = new ExpressionContext(
                        metaverseAttributes: null,
                        connectedSystemAttributes: csAttributeDictionary);

                    // Evaluate the expression
                    var result = expressionEvaluator.Evaluate(source.Expression, context);

                    if (result == null)
                    {
                        Log.Debug("Process: Expression '{Expression}' for CSO {CsoId} returned null. Available attributes: [{Attributes}]",
                            source.Expression, connectedSystemObject.Id, string.Join(", ", csAttributeDictionary.Keys));

                        // If expression returns null, remove any existing MVO attribute value
                        var mvoAttributeValuesToDelete = mvo.AttributeValues.Where(q => q.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id);
                        mvo.PendingAttributeValueRemovals.AddRange(mvoAttributeValuesToDelete);
                        continue;
                    }

                    // Check if result is an array/collection (e.g., from Split() function)
                    // This enables multi-valued attribute flow from expressions
                    if (result is string[] stringArrayResult)
                    {
                        ProcessExpressionArrayResult(mvo, syncRuleMapping.TargetMetaverseAttribute, stringArrayResult);
                    }
                    else if (result is IEnumerable<string> stringEnumerableResult && result is not string)
                    {
                        ProcessExpressionArrayResult(mvo, syncRuleMapping.TargetMetaverseAttribute, stringEnumerableResult.ToArray());
                    }
                    else
                    {
                        // Single value result - existing logic
                        var existingMvoValue = mvo.AttributeValues.SingleOrDefault(
                            mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute.Id);

                        // Determine if the value has changed (result is non-null here due to check above)
                        var resultString = result.ToString();
                        var valueChanged = existingMvoValue == null ||
                            !string.Equals(existingMvoValue.StringValue, resultString, StringComparison.Ordinal);

                        if (valueChanged)
                        {
                            // Remove existing value if present
                            if (existingMvoValue != null)
                                mvo.PendingAttributeValueRemovals.Add(existingMvoValue);

                            // Add the new value based on the target attribute type (result is non-null)
                            var newMvoValue = CreateMvoAttributeValueFromExpressionResult(
                                mvo, syncRuleMapping.TargetMetaverseAttribute, result!);

                            if (newMvoValue != null)
                            {
                                mvo.PendingAttributeValueAdditions.Add(newMvoValue);
                                Log.Debug("Process: Expression-based mapping set {AttributeName} to '{Value}' on MVO {MvoId}",
                                    syncRuleMapping.TargetMetaverseAttribute.Name, resultString, mvo.Id);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Process: Error evaluating expression '{Expression}' for CSO {CsoId}: {Error}",
                        source.Expression, connectedSystemObject.Id, ex.Message);
                }
            }
            else if (source.MetaverseAttribute != null)
                throw new InvalidDataException("SyncRuleMappingSource.MetaverseAttribute being populated is not supported for synchronisation operations. " +
                                               "This operation is focused on import flow, so Connected System to Metaverse Object.");
            else
                throw new InvalidDataException("Expected ConnectedSystemAttribute or Expression to be populated in a SyncRuleMappingSource object.");
        }
    }

    /// <summary>
    /// Processes text attribute flow from CSO to MVO.
    /// Handles multi-valued attributes by processing all values at once.
    /// </summary>
    private static void ProcessTextAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues,
        ConnectedSystemObjectTypeAttribute csotAttribute)
    {
        // Debug: Log comparison for all text attributes
        var existingMvoValues = mvo.AttributeValues
            .Where(mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id)
            .Select(mvoav => mvoav.StringValue)
            .ToList();
        var incomingCsoValues = csoAttributeValues.Select(csoav => csoav.StringValue).ToList();

        Log.Debug("SyncRuleMappingProcessor: Comparing attribute '{AttrName}' for CSO. MVO values: [{MvoValues}], CSO values: [{CsoValues}]",
            csotAttribute.Name,
            string.Join(", ", existingMvoValues.Select(v => v ?? "(null)")),
            string.Join(", ", incomingCsoValues.Select(v => v ?? "(null)")));

        // find values on the MVO of type string that aren't on the CSO and remove them.
        var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
            !csoAttributeValues.Any(csoav => csoav.StringValue != null && csoav.StringValue.Equals(mvoav.StringValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // find values on the CSO of type string that aren't on the MVO according to the sync rule mapping.
        var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
            csoav.AttributeId == sourceAttributeId &&
            !mvo.AttributeValues.Any(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                mvoav.StringValue != null && mvoav.StringValue.Equals(csoav.StringValue)));

        if (mvoObsoleteAttributeValues.Any() || csoNewAttributeValues.Any())
        {
            Log.Debug("SyncRuleMappingProcessor: Attribute '{AttrName}' has changes. Removing {RemoveCount}, Adding {AddCount}",
                csotAttribute.Name, mvoObsoleteAttributeValues.Count(), csoNewAttributeValues.Count());
        }

        // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                StringValue = newCsoNewAttributeValue.StringValue
            });
        }
    }

    /// <summary>
    /// Processes number attribute flow from CSO to MVO.
    /// Handles multi-valued attributes by processing all values at once.
    /// </summary>
    private static void ProcessNumberAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues)
    {
        // find values on the MVO of type int that aren't on the CSO and remove them
        var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
            !csoAttributeValues.Any(csoav => csoav.IntValue != null && csoav.IntValue.Equals(mvoav.IntValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // find values on the CSO of type int that aren't on the MVO according to the sync rule mapping.
        var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
            csoav.AttributeId == sourceAttributeId &&
            !mvo.AttributeValues.Any(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                mvoav.IntValue != null && mvoav.IntValue.Equals(csoav.IntValue)));

        // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                IntValue = newCsoNewAttributeValue.IntValue
            });
        }
    }

    /// <summary>
    /// Processes DateTime attribute flow from CSO to MVO.
    /// DateTime is typically single-valued.
    /// </summary>
    private static void ProcessDateTimeAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues)
    {
        var csoValue = csoAttributeValues.FirstOrDefault();
        var mvoValue = mvo.AttributeValues.SingleOrDefault(mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id);

        if (mvoValue != null && csoValue == null)
        {
            // there is a value on the MVO that isn't on the CSO. remove it.
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
        }
        else if (csoValue != null && mvoValue == null)
        {
            // no MVO value set, but we have a CSO value, so set the MVO value.
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                DateTimeValue = csoValue.DateTimeValue
            });
        }
        else if (csoValue != null && mvoValue != null && mvoValue.DateTimeValue != csoValue.DateTimeValue)
        {
            // there are both MVO and CSO values, but they're different, update the MVO from the CSO.
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                DateTimeValue = csoValue.DateTimeValue
            });
        }
    }

    /// <summary>
    /// Processes binary attribute flow from CSO to MVO.
    /// Handles multi-valued attributes by processing all values at once.
    /// </summary>
    private static void ProcessBinaryAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues)
    {
        // find values on the MVO of type binary that aren't on the CSO and remove them
        var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
            !csoAttributeValues.Any(csoav =>
                csoav.ByteValue != null && Utilities.Utilities.AreByteArraysTheSame(csoav.ByteValue, mvoav.ByteValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // find values on the CSO of type byte that aren't on the MVO according to the sync rule mapping.
        var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
            csoav.AttributeId == sourceAttributeId &&
            !mvo.AttributeValues.Any(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                Utilities.Utilities.AreByteArraysTheSame(mvoav.ByteValue, csoav.ByteValue)));

        // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                ByteValue = newCsoNewAttributeValue.ByteValue
            });
        }
    }

    /// <summary>
    /// Processes reference attribute flow from CSO to MVO.
    /// Handles multi-valued attributes (e.g., group members) by processing all values at once.
    /// </summary>
    private static void ProcessReferenceAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        SyncRuleMappingSource source,
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues)
    {
        // Log warning for CSO reference values that cannot be resolved due to missing navigation properties.
        // This can happen if:
        // 1. EF Core didn't include ReferenceValue navigation (bug in repository query)
        // 2. Referenced CSO hasn't been joined to an MVO yet (sync ordering issue)
        // 3. ReferenceValue.MetaverseObject navigation wasn't loaded
        var unresolvedReferenceValues = csoAttributeValues.Where(csoav =>
            (csoav.ReferenceValue == null || csoav.ReferenceValue.MetaverseObject == null) &&
            (csoav.ReferenceValueId != null || !string.IsNullOrEmpty(csoav.UnresolvedReferenceValue))).ToList();

        if (unresolvedReferenceValues.Count > 0)
        {
            foreach (var unresolved in unresolvedReferenceValues)
            {
                if (unresolved.ReferenceValue == null && unresolved.ReferenceValueId != null)
                {
                    // ReferenceValueId is set but ReferenceValue navigation wasn't loaded - this is a bug
                    Log.Warning("SyncRuleMappingProcessor: CSO {CsoId} has reference attribute {AttrName} with ReferenceValueId {RefId} but ReferenceValue navigation is null. " +
                        "This indicates the EF Core query is missing .Include(av => av.ReferenceValue). The reference will not flow to the MVO.",
                        connectedSystemObject.Id, source.ConnectedSystemAttribute?.Name ?? "unknown", unresolved.ReferenceValueId);
                }
                else if (unresolved.ReferenceValue != null && unresolved.ReferenceValue.MetaverseObject == null)
                {
                    // ReferenceValue loaded but MetaverseObject is null - referenced CSO not yet joined
                    Log.Warning("SyncRuleMappingProcessor: CSO {CsoId} has reference attribute {AttrName} pointing to CSO {RefCsoId} which is not joined to an MVO. " +
                        "Ensure referenced objects are synced before referencing objects. The reference will not flow to the MVO.",
                        connectedSystemObject.Id, source.ConnectedSystemAttribute?.Name ?? "unknown", unresolved.ReferenceValue.Id);
                }
            }
        }

        // Find reference values on the MVO that aren't on the CSO and remove.
        // IMPORTANT: Only perform removal logic when ALL CSO references are resolved.
        // If any CSO reference has an unresolved MetaverseObject (cross-page reference where
        // the referenced CSO is on a different page and hasn't been joined/projected yet),
        // we cannot correctly determine which MVO references are obsolete. Skipping removal
        // is safe because the cross-page resolution pass will re-run this logic once all
        // MVOs exist and all references can be resolved.
        var hasUnresolvedReferences = csoAttributeValues.Any(csoav =>
            csoav.ReferenceValueId.HasValue &&
            (csoav.ReferenceValue == null || csoav.ReferenceValue.MetaverseObject == null));

        if (!hasUnresolvedReferences)
        {
            var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                mvoav.ReferenceValue != null &&
                !csoAttributeValues.Any(csoav => csoav.ReferenceValue is { MetaverseObject: not null } && csoav.ReferenceValue.MetaverseObject.Id == mvoav.ReferenceValue.Id));
            mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);
        }
        else
        {
            Log.Debug("ProcessReferenceAttribute: Skipping MVO reference removal for CSO {CsoId} " +
                "because some reference(s) have unresolved MetaverseObject (cross-page references). " +
                "Removals will be handled in the cross-page resolution pass.",
                connectedSystemObject.Id);
        }

        // find values on the CSO of type reference that aren't on the MVO according to the sync rule mapping.
        var csoNewAttributeValues = csoAttributeValues.Where(csoav =>
            csoav.ReferenceValue is { MetaverseObject: not null } &&
            !mvo.AttributeValues.Any(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                mvoav.ReferenceValue != null && mvoav.ReferenceValue.Id.Equals(csoav.ReferenceValue.MetaverseObject.Id)));

        // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            // This check should now rarely trigger due to the filter above,
            // but kept as defensive programming
            if (newCsoNewAttributeValue.ReferenceValue?.MetaverseObject == null)
                continue;

            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                ReferenceValue = newCsoNewAttributeValue.ReferenceValue.MetaverseObject
            });
        }
    }

    /// <summary>
    /// Processes GUID attribute flow from CSO to MVO.
    /// Handles multi-valued attributes by processing all values at once.
    /// </summary>
    private static void ProcessGuidAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        int sourceAttributeId,
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues)
    {
        // find values on the MVO of type guid that aren't on the CSO and remove them.
        var mvoObsoleteAttributeValues = mvo.AttributeValues.Where(mvoav =>
            mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
            !csoAttributeValues.Any(csoav => csoav.GuidValue.HasValue && csoav.GuidValue.Equals(mvoav.GuidValue)));
        mvo.PendingAttributeValueRemovals.AddRange(mvoObsoleteAttributeValues);

        // find values on the CSO of type guid that aren't on the MVO according to the sync rule mapping.
        var csoNewAttributeValues = connectedSystemObject.AttributeValues.Where(csoav =>
            csoav.AttributeId == sourceAttributeId &&
            !mvo.AttributeValues.Any(mvoav =>
                mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id &&
                mvoav.GuidValue.HasValue && mvoav.GuidValue.Equals(csoav.GuidValue)));

        // now turn the new CSO attribute values into MVO attribute values we can add to the MVO.
        foreach (var newCsoNewAttributeValue in csoNewAttributeValues)
        {
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                GuidValue = newCsoNewAttributeValue.GuidValue
            });
        }
    }

    /// <summary>
    /// Processes boolean attribute flow from CSO to MVO.
    /// Boolean is typically single-valued.
    /// </summary>
    private static void ProcessBooleanAttribute(
        MetaverseObject mvo,
        SyncRuleMapping syncRuleMapping,
        List<ConnectedSystemObjectAttributeValue> csoAttributeValues)
    {
        var csoValue = csoAttributeValues.FirstOrDefault();
        var mvoValue = mvo.AttributeValues.SingleOrDefault(mvoav => mvoav.AttributeId == syncRuleMapping.TargetMetaverseAttribute!.Id);

        if (mvoValue != null && csoValue == null)
        {
            // there is a value on the MVO that isn't on the CSO. remove it.
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
        }
        else if (csoValue != null && mvoValue == null)
        {
            // no MVO value set, but we have a CSO value, so set the MVO value.
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                BoolValue = csoValue.BoolValue
            });
        }
        else if (csoValue != null && mvoValue != null && mvoValue.BoolValue != csoValue.BoolValue)
        {
            // there are both MVO and CSO values, but they're different, update the MVO from the CSO.
            mvo.PendingAttributeValueRemovals.Add(mvoValue);
            mvo.PendingAttributeValueAdditions.Add(new MetaverseObjectAttributeValue
            {
                MetaverseObject = mvo,
                Attribute = syncRuleMapping.TargetMetaverseAttribute!,
                AttributeId = syncRuleMapping.TargetMetaverseAttribute!.Id,
                BoolValue = csoValue.BoolValue
            });
        }
    }

    /// <summary>
    /// Builds a dictionary of attribute values from a Connected System Object for expression evaluation.
    /// The dictionary keys are attribute names, and values are the attribute values.
    /// </summary>
    private static Dictionary<string, object?> BuildCsoAttributeDictionary(
        ConnectedSystemObject connectedSystemObject,
        ConnectedSystemObjectType csoType)
    {
        var attributes = new Dictionary<string, object?>();

        foreach (var attributeValue in connectedSystemObject.AttributeValues)
        {
            // Find the attribute definition from the CSO type
            var csotAttribute = csoType.Attributes.SingleOrDefault(a => a.Id == attributeValue.AttributeId);
            if (csotAttribute == null)
            {
                Log.Warning("BuildCsoAttributeDictionary: CSO {CsoId} has attribute value with AttributeId={AttrId} but attribute not found in type definition",
                    connectedSystemObject.Id, attributeValue.AttributeId);
                continue;
            }

            var attributeName = csotAttribute.Name;

            // Use the appropriate typed value based on the attribute type
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

            // For multi-valued attributes, we just take the first value for now
            // TODO: Support multi-valued attribute access in expressions
            if (!attributes.ContainsKey(attributeName))
            {
                attributes[attributeName] = value;
            }
        }

        return attributes;
    }

    /// <summary>
    /// Processes an array result from an expression (e.g., from Split() function) into
    /// multiple MVO attribute values for multi-valued attributes.
    /// This enables conversion of delimited strings like "A|B|C" into individual MVA values.
    /// </summary>
    private static void ProcessExpressionArrayResult(
        MetaverseObject mvo,
        MetaverseAttribute targetAttribute,
        string[] values)
    {
        if (values.Length == 0)
        {
            // Empty array - remove all existing values for this attribute
            var mvoAttributeValuesToDelete = mvo.AttributeValues
                .Where(q => q.AttributeId == targetAttribute.Id);
            mvo.PendingAttributeValueRemovals.AddRange(mvoAttributeValuesToDelete);
            return;
        }

        // Get existing MVO values for this attribute
        var existingMvoValues = mvo.AttributeValues
            .Where(mvoav => mvoav.AttributeId == targetAttribute.Id)
            .ToList();

        // Find values to remove (exist on MVO but not in expression result)
        var valuesToRemove = existingMvoValues
            .Where(existing => !values.Contains(existing.StringValue, StringComparer.Ordinal));
        mvo.PendingAttributeValueRemovals.AddRange(valuesToRemove);

        // Find values to add (exist in expression result but not on MVO)
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
                StringValue = value
            };
            mvo.PendingAttributeValueAdditions.Add(newMvoValue);
        }

        if (valuesToRemove.Any() || valuesToAdd.Any())
        {
            Log.Debug("Process: Expression array result for {AttributeName} - removing {RemoveCount}, adding {AddCount} values on MVO {MvoId}",
                targetAttribute.Name, valuesToRemove.Count(), valuesToAdd.Count(), mvo.Id);
        }
    }

    /// <summary>
    /// Creates a MetaverseObjectAttributeValue from an expression result, handling type conversion.
    /// </summary>
    private static MetaverseObjectAttributeValue? CreateMvoAttributeValueFromExpressionResult(
        MetaverseObject mvo,
        MetaverseAttribute targetAttribute,
        object result)
    {
        var newMvoValue = new MetaverseObjectAttributeValue
        {
            MetaverseObject = mvo,
            Attribute = targetAttribute,
            AttributeId = targetAttribute.Id
        };

        // Convert result to appropriate type based on target attribute type
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
