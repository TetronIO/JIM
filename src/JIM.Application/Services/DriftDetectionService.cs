using JIM.Application.Expressions;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Service responsible for detecting drift in target systems and staging corrective pending exports.
/// Drift occurs when a target system's attribute values differ from what the authoritative source dictates.
/// </summary>
/// <remarks>
/// Drift detection is triggered during inbound sync when a CSO is processed from a system that has
/// export rules targeting it. For each export rule with EnforceState = true, the service compares
/// the CSO's actual values against the expected values (calculated from the MVO and export rule mappings).
///
/// Key design decisions:
/// 1. Only evaluates export rules with EnforceState = true
/// 2. Skips attributes where the connected system is a legitimate contributor (has import rules for that attribute)
/// 3. Uses the existing PendingExport infrastructure for corrective exports
/// </remarks>
public class DriftDetectionService
{
    private readonly JimApplication _jim;
    private readonly IExpressionEvaluator _expressionEvaluator;

    /// <summary>
    /// Result of drift detection for a single CSO.
    /// </summary>
    public class DriftDetectionResult
    {
        /// <summary>
        /// Attributes that were detected as drifted and need correction.
        /// </summary>
        public List<DriftedAttribute> DriftedAttributes { get; } = [];

        /// <summary>
        /// Whether any drift was detected.
        /// </summary>
        public bool HasDrift => DriftedAttributes.Count > 0;

        /// <summary>
        /// Pending exports that were created to correct the drift.
        /// </summary>
        public List<PendingExport> CorrectiveExports { get; } = [];
    }

    /// <summary>
    /// Represents a single attribute that has drifted from expected state.
    /// </summary>
    public class DriftedAttribute
    {
        /// <summary>
        /// The CSO attribute that drifted.
        /// </summary>
        public required ConnectedSystemObjectTypeAttribute Attribute { get; init; }

        /// <summary>
        /// The actual value found in the CSO (may be null for missing attributes).
        /// </summary>
        public object? ActualValue { get; init; }

        /// <summary>
        /// The expected value based on MVO and export rule mapping.
        /// </summary>
        public object? ExpectedValue { get; init; }

        /// <summary>
        /// The export rule that defines the expected value.
        /// </summary>
        public required SyncRule ExportRule { get; init; }
    }

    public DriftDetectionService(JimApplication jim)
    {
        _jim = jim;
        _expressionEvaluator = new DynamicExpressoEvaluator();
    }

    /// <summary>
    /// Evaluates drift for a CSO that has been imported/synced against its joined MVO.
    /// Checks all export rules with EnforceState = true that target this CSO's connected system.
    /// </summary>
    /// <param name="cso">The Connected System Object that was just imported/synced.</param>
    /// <param name="mvo">The Metaverse Object the CSO is joined to.</param>
    /// <param name="exportRules">Export rules targeting this CSO's connected system (pre-loaded for efficiency).</param>
    /// <param name="importMappingsByAttribute">Cache of import mappings by (ConnectedSystemId, MvoAttributeId) for checking if system is a contributor.</param>
    /// <returns>Result indicating what drift was detected and corrective exports staged (not yet persisted).</returns>
    public DriftDetectionResult EvaluateDrift(
        ConnectedSystemObject cso,
        MetaverseObject? mvo,
        List<SyncRule> exportRules,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute = null)
    {
        var result = new DriftDetectionResult();

        if (cso.MetaverseObject == null && mvo == null)
        {
            Log.Debug("EvaluateDrift: CSO {CsoId} is not joined to an MVO, skipping drift detection", cso.Id);
            return result;
        }

        // Skip drift detection for CSOs that haven't completed their initial export yet.
        // PendingProvisioning CSOs are newly created via provisioning and haven't been confirmed,
        // so there's nothing to "drift" from - they haven't received their expected values yet.
        if (cso.Status == ConnectedSystemObjectStatus.PendingProvisioning)
        {
            Log.Debug("EvaluateDrift: CSO {CsoId} is in PendingProvisioning status, skipping drift detection", cso.Id);
            return result;
        }

        // Use the provided MVO or fall back to the CSO's joined MVO
        var targetMvo = mvo ?? cso.MetaverseObject!;

        // Defensive check: ensure MVO.Type is loaded (required for export rule filtering)
        // If Type is null, the navigation property wasn't included in the repository query.
        // This prevents silent failures where no export rules would match.
        if (targetMvo.Type == null)
        {
            Log.Warning("EvaluateDrift: MVO {MvoId} has null Type - navigation property not loaded. " +
                "Drift detection cannot filter by MVO type. CSO: {CsoId}. " +
                "Ensure GetConnectedSystemObjectsModifiedSinceAsync includes MVO.Type",
                targetMvo.Id, cso.Id);
            return result;
        }

        // Filter to only export rules that:
        // 1. Target this CSO's connected system
        // 2. Have EnforceState = true
        // 3. Match this CSO's object type
        // Note: targetMvo.Type is guaranteed non-null by defensive check above
        var applicableExportRules = exportRules
            .Where(r => r.EnforceState &&
                       r.ConnectedSystemId == cso.ConnectedSystemId &&
                       r.ConnectedSystemObjectTypeId == cso.TypeId &&
                       r.MetaverseObjectTypeId == targetMvo.Type.Id)
            .ToList();

        if (applicableExportRules.Count == 0)
        {
            Log.Debug("EvaluateDrift: No applicable export rules with EnforceState=true for CSO {CsoId} in system {SystemId}",
                cso.Id, cso.ConnectedSystemId);
            return result;
        }

        Log.Debug("EvaluateDrift: Evaluating {RuleCount} export rules for drift detection on CSO {CsoId}",
            applicableExportRules.Count, cso.Id);

        // Build the MVO attribute dictionary once for all expression evaluations
        // This avoids repeatedly iterating through MVO attributes for each expression
        Dictionary<string, object?>? mvAttributeDictionary = null;

        foreach (var exportRule in applicableExportRules)
        {
            // Check each attribute flow mapping in the export rule
            foreach (var mapping in exportRule.AttributeFlowRules)
            {
                if (mapping.TargetConnectedSystemAttribute == null)
                {
                    Log.Warning("EvaluateDrift: Export mapping has no TargetConnectedSystemAttribute set, skipping");
                    continue;
                }

                // Check if this connected system is a legitimate contributor for this attribute
                // (i.e., has an import rule that maps to the same MVO attribute)
                foreach (var source in mapping.Sources)
                {
                    if (source.MetaverseAttribute == null && string.IsNullOrWhiteSpace(source.Expression))
                        continue;

                    var mvoAttributeId = source.MetaverseAttribute?.Id ?? 0;

                    // Skip if this system has import rules for this attribute (not drift - legitimate change)
                    var isContributor = mvoAttributeId > 0 && HasImportRuleForAttribute(
                        cso.ConnectedSystemId,
                        mvoAttributeId,
                        importMappingsByAttribute);

                    // For expression-based mappings, check if the system is a contributor for any
                    // MVO attribute referenced in the expression. If so, skip drift detection because
                    // the expression output depends on attributes that this system legitimately contributes to.
                    if (!isContributor && !string.IsNullOrWhiteSpace(source.Expression))
                    {
                        isContributor = IsContributorForExpressionAttributes(
                            cso.ConnectedSystemId,
                            source.Expression,
                            importMappingsByAttribute);
                    }

                    Log.Debug("EvaluateDrift: Contributor check for CSO {CsoId}, attribute {AttrName}: " +
                        "mvoAttributeId={MvoAttrId}, csoConnectedSystemId={CsoSystemId}, isContributor={IsContributor}, " +
                        "hasExpression={HasExpression}, cacheKeys=[{CacheKeys}]",
                        cso.Id, mapping.TargetConnectedSystemAttribute.Name,
                        mvoAttributeId, cso.ConnectedSystemId, isContributor,
                        !string.IsNullOrWhiteSpace(source.Expression),
                        importMappingsByAttribute != null
                            ? string.Join(", ", importMappingsByAttribute.Keys.Select(k => $"({k.ConnectedSystemId},{k.MvoAttributeId})"))
                            : "null");

                    if (isContributor)
                    {
                        Log.Debug("EvaluateDrift: Skipping attribute {AttrName} for CSO {CsoId} - system is a contributor (has import rules)",
                            mapping.TargetConnectedSystemAttribute.Name, cso.Id);
                        continue;
                    }

                    // Calculate expected value from MVO based on export rule
                    // Build MVO attribute dictionary lazily (only when needed for expressions)
                    mvAttributeDictionary ??= BuildAttributeDictionary(targetMvo);
                    var expectedValue = GetExpectedValue(targetMvo, source, mvAttributeDictionary, mapping.TargetConnectedSystemAttribute);

                    // Get actual value from CSO
                    var actualValue = GetActualValue(cso, mapping.TargetConnectedSystemAttribute);

                    // Compare values
                    if (!ValuesEqual(expectedValue, actualValue))
                    {
                        Log.Information("EvaluateDrift: Drift detected on CSO {CsoId} attribute {AttrName}. " +
                            "Expected: '{ExpectedValue}', Actual: '{ActualValue}'",
                            cso.Id, mapping.TargetConnectedSystemAttribute.Name,
                            FormatValueForLog(expectedValue), FormatValueForLog(actualValue));

                        result.DriftedAttributes.Add(new DriftedAttribute
                        {
                            Attribute = mapping.TargetConnectedSystemAttribute,
                            ExpectedValue = expectedValue,
                            ActualValue = actualValue,
                            ExportRule = exportRule
                        });
                    }
                }
            }
        }

        // If drift was detected, queue corrective pending exports
        if (result.HasDrift)
        {
            Log.Information("EvaluateDrift: Detected {DriftCount} drifted attributes on CSO {CsoId}. Staging corrective exports.",
                result.DriftedAttributes.Count, cso.Id);

            // Use the existing export evaluation infrastructure to create pending exports
            // The existing logic already handles all the complexity of pending export creation
            var pendingExports = CreateCorrectiveExports(cso, targetMvo, result.DriftedAttributes);
            result.CorrectiveExports.AddRange(pendingExports);
        }

        return result;
    }

    /// <summary>
    /// Checks if a connected system has import rules that flow to the specified MVO attribute.
    /// If so, changes from that system are not "drift" but legitimate updates.
    /// </summary>
    /// <param name="connectedSystemId">The connected system to check.</param>
    /// <param name="mvoAttributeId">The MVO attribute ID to check.</param>
    /// <param name="importMappingsByAttribute">Cached import mappings lookup.</param>
    /// <returns>True if the system is a contributor for this attribute.</returns>
    public bool HasImportRuleForAttribute(
        int connectedSystemId,
        int mvoAttributeId,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute)
    {
        if (importMappingsByAttribute == null)
        {
            // No cache provided - assume no import rules (conservative approach)
            return false;
        }

        var key = (connectedSystemId, mvoAttributeId);
        return importMappingsByAttribute.ContainsKey(key);
    }

    /// <summary>
    /// Checks if a connected system is a contributor for any MVO attribute referenced in an expression.
    /// Expressions may reference MVO attributes using mv["attributeName"] syntax. If the system has
    /// import rules for any of those attributes, it's considered a contributor and drift detection
    /// should be skipped for this expression-based mapping.
    /// </summary>
    /// <param name="connectedSystemId">The connected system ID to check.</param>
    /// <param name="expression">The expression to analyse for MVO attribute references.</param>
    /// <param name="importMappingsByAttribute">The import mapping cache.</param>
    /// <returns>True if the system is a contributor for any attribute referenced in the expression.</returns>
    private static bool IsContributorForExpressionAttributes(
        int connectedSystemId,
        string expression,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute)
    {
        if (importMappingsByAttribute == null || importMappingsByAttribute.Count == 0)
            return false;

        // Find all mv["attributeName"] patterns in the expression
        // Pattern matches: mv["someAttribute"] or mv['someAttribute']
        var regex = new System.Text.RegularExpressions.Regex(@"mv\[""([^""]+)""\]|mv\['([^']+)'\]");
        var matches = regex.Matches(expression);

        if (matches.Count == 0)
        {
            Log.Debug("IsContributorForExpressionAttributes: No MVO attribute references found in expression: {Expression}",
                expression);
            return false;
        }

        // Check if the system is a contributor for any referenced attribute
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            // Group 1 is for double quotes, Group 2 is for single quotes
            var attributeName = !string.IsNullOrEmpty(match.Groups[1].Value)
                ? match.Groups[1].Value
                : match.Groups[2].Value;

            // Look for this attribute in the import mapping cache by name
            foreach (var kvp in importMappingsByAttribute)
            {
                if (kvp.Key.ConnectedSystemId == connectedSystemId)
                {
                    // Check if any of the mappings for this system target this attribute
                    foreach (var mapping in kvp.Value)
                    {
                        if (mapping.TargetMetaverseAttribute?.Name == attributeName)
                        {
                            Log.Debug("IsContributorForExpressionAttributes: System {SystemId} is a contributor " +
                                "for attribute {AttrName} referenced in expression: {Expression}",
                                connectedSystemId, attributeName, expression);
                            return true;
                        }
                    }
                }
            }
        }

        Log.Debug("IsContributorForExpressionAttributes: System {SystemId} is NOT a contributor for any attributes " +
            "referenced in expression: {Expression}",
            connectedSystemId, expression);
        return false;
    }

    /// <summary>
    /// Builds a lookup dictionary of import mappings by (ConnectedSystemId, MvoAttributeId).
    /// Call this once at the start of a sync run for efficient drift detection.
    /// </summary>
    /// <param name="syncRules">All sync rules to process.</param>
    /// <returns>Dictionary keyed by (ConnectedSystemId, MvoAttributeId).</returns>
    public static Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>> BuildImportMappingCache(
        List<SyncRule> syncRules)
    {
        var cache = new Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>();

        var importRules = syncRules.Where(sr => sr.Enabled && sr.Direction == SyncRuleDirection.Import);

        foreach (var importRule in importRules)
        {
            foreach (var mapping in importRule.AttributeFlowRules)
            {
                // For import rules, the target is the MVO attribute
                if (mapping.TargetMetaverseAttribute == null)
                    continue;

                var key = (importRule.ConnectedSystemId, mapping.TargetMetaverseAttribute.Id);

                if (!cache.ContainsKey(key))
                {
                    cache[key] = [];
                }

                cache[key].Add(mapping);
            }
        }

        return cache;
    }

    /// <summary>
    /// Gets the expected value(s) for a CSO attribute based on the MVO and export rule source.
    /// Supports both direct attribute mappings and expression-based mappings.
    /// For multi-valued attributes, returns a HashSet containing all values.
    /// </summary>
    /// <param name="mvo">The Metaverse Object to get the expected value from.</param>
    /// <param name="source">The sync rule mapping source defining the attribute or expression.</param>
    /// <param name="mvAttributeDictionary">Pre-built dictionary of MVO attribute values for expression evaluation.</param>
    /// <param name="targetCsoAttribute">The target CSO attribute - used to determine plurality for consistent comparison.</param>
    private object? GetExpectedValue(MetaverseObject mvo, SyncRuleMappingSource source, Dictionary<string, object?> mvAttributeDictionary, ConnectedSystemObjectTypeAttribute targetCsoAttribute)
    {
        // Handle expression-based mappings
        if (!string.IsNullOrWhiteSpace(source.Expression))
        {
            try
            {
                Log.Debug("GetExpectedValue: Evaluating expression for drift detection. " +
                    "Expression: '{Expression}', Available attributes: [{Attributes}]",
                    source.Expression, string.Join(", ", mvAttributeDictionary.Keys));

                var context = new ExpressionContext(mvAttributeDictionary, null);
                var result = _expressionEvaluator.Evaluate(source.Expression, context);

                if (result == null)
                {
                    Log.Debug("GetExpectedValue: Expression '{Expression}' for MVO {MvoId} returned null",
                        source.Expression, mvo.Id);
                }

                return result;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "GetExpectedValue: Failed to evaluate expression '{Expression}' for drift detection",
                    source.Expression);
                return null;
            }
        }

        // Handle direct attribute mapping
        if (source.MetaverseAttribute == null)
            return null;

        // Use the TARGET CSO attribute's plurality to determine how to return values.
        // This ensures consistency with GetActualValue() which also uses CSO attribute plurality.
        // MVO and CSO attributes may have different plurality settings for the same logical attribute.
        var isMultiValued = targetCsoAttribute.AttributePlurality == AttributePlurality.MultiValued;

        if (isMultiValued)
        {
            // Get ALL values for this attribute
            var mvoAttrValues = mvo.AttributeValues
                .Where(av => av.AttributeId == source.MetaverseAttribute.Id)
                .ToList();

            if (mvoAttrValues.Count == 0)
                return new HashSet<object>(); // Empty set

            // Return a HashSet of all values for set comparison
            var valueSet = new HashSet<object>();
            var nullValueCount = 0;
            var duplicateCount = 0;
            foreach (var av in mvoAttrValues)
            {
                var value = GetTypedValueFromMvoAttributeValue(av, source.MetaverseAttribute.Type);
                if (value != null)
                {
                    if (!valueSet.Add(value))
                        duplicateCount++;
                }
                else
                {
                    nullValueCount++;
                }
            }

            if (nullValueCount > 0 || duplicateCount > 0)
            {
                Log.Warning("GetExpectedValue: MVO {MvoId} attribute {AttrName}: " +
                    "{LoadedCount} attribute values loaded, {NullCount} null values filtered, {DupCount} duplicates",
                    mvo.Id, source.MetaverseAttribute.Name,
                    mvoAttrValues.Count, nullValueCount, duplicateCount);
            }

            Log.Debug("GetExpectedValue: Multi-valued attribute {AttrName} for MVO {MvoId} has {Count} expected values",
                source.MetaverseAttribute.Name, mvo.Id, valueSet.Count);

            return valueSet;
        }
        else
        {
            // Single-valued attribute - use FirstOrDefault
            var mvoAttrValue = mvo.AttributeValues
                .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id);

            if (mvoAttrValue == null)
                return null;

            return GetTypedValueFromMvoAttributeValue(mvoAttrValue, source.MetaverseAttribute.Type);
        }
    }

    /// <summary>
    /// Extracts the typed value from an MVO attribute value based on the attribute data type.
    /// </summary>
    private static object? GetTypedValueFromMvoAttributeValue(MetaverseObjectAttributeValue av, AttributeDataType type)
    {
        return type switch
        {
            AttributeDataType.Text => av.StringValue,
            AttributeDataType.Number => av.IntValue,
            AttributeDataType.LongNumber => av.LongValue,
            AttributeDataType.DateTime => av.DateTimeValue,
            AttributeDataType.Boolean => av.BoolValue,
            AttributeDataType.Guid => av.GuidValue,
            AttributeDataType.Binary => av.ByteValue,
            // Prefer the scalar FK (avoids dependency on ReferenceValue navigation being
            // materialised), fall back to navigation property for compatibility with
            // in-memory test data where FK may not be explicitly set.
            AttributeDataType.Reference => av.ReferenceValueId ?? av.ReferenceValue?.Id,
            _ => null
        };
    }

    /// <summary>
    /// Builds a dictionary of attribute values from a Metaverse Object for expression evaluation.
    /// The dictionary keys are attribute names, and values are the attribute values.
    /// </summary>
    private static Dictionary<string, object?> BuildAttributeDictionary(MetaverseObject mvo)
    {
        var attributes = new Dictionary<string, object?>();

        if (mvo.Type == null)
        {
            Log.Warning("BuildAttributeDictionary: MVO {MvoId} has null Type, cannot build attribute dictionary", mvo.Id);
            return attributes;
        }

        foreach (var attributeValue in mvo.AttributeValues)
        {
            if (attributeValue.Attribute == null)
            {
                Log.Warning("BuildAttributeDictionary: MVO {MvoId} has attribute value with AttributeId={AttrId} but Attribute navigation property is null",
                    mvo.Id, attributeValue.AttributeId);
                continue;
            }

            var attributeName = attributeValue.Attribute.Name;

            // Use the appropriate typed value based on the attribute type
            object? value = attributeValue.Attribute.Type switch
            {
                AttributeDataType.Text => attributeValue.StringValue,
                AttributeDataType.Number => attributeValue.IntValue,
                AttributeDataType.DateTime => attributeValue.DateTimeValue,
                AttributeDataType.Boolean => attributeValue.BoolValue,
                AttributeDataType.Guid => attributeValue.GuidValue,
                AttributeDataType.Binary => attributeValue.ByteValue,
                AttributeDataType.Reference => (attributeValue.ReferenceValueId ?? attributeValue.ReferenceValue?.Id)?.ToString(),
                _ => null
            };

            attributes[attributeName] = value;
        }

        return attributes;
    }

    /// <summary>
    /// Gets the actual value(s) of a CSO attribute.
    /// For reference attributes, returns the MVO ID that the referenced CSO is joined to,
    /// enabling comparison with the expected MVO reference ID.
    /// For multi-valued attributes, returns a HashSet containing all values.
    /// </summary>
    private static object? GetActualValue(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute attribute)
    {
        // Check if this is a multi-valued attribute
        var isMultiValued = attribute.AttributePlurality == AttributePlurality.MultiValued;

        if (isMultiValued)
        {
            // Get ALL values for this attribute
            var csoAttrValues = cso.AttributeValues
                .Where(av => av.AttributeId == attribute.Id)
                .ToList();

            if (csoAttrValues.Count == 0)
                return new HashSet<object>(); // Empty set

            // Return a HashSet of all values for set comparison
            var valueSet = new HashSet<object>();
            foreach (var av in csoAttrValues)
            {
                var value = GetTypedValueFromCsoAttributeValue(av, attribute.Type);
                if (value != null)
                {
                    valueSet.Add(value);
                }
            }

            Log.Debug("GetActualValue: Multi-valued attribute {AttrName} for CSO {CsoId} has {Count} actual values",
                attribute.Name, cso.Id, valueSet.Count);

            return valueSet;
        }
        else
        {
            // Single-valued attribute - use FirstOrDefault
            var csoAttrValue = cso.AttributeValues
                .FirstOrDefault(av => av.AttributeId == attribute.Id);

            if (csoAttrValue == null)
                return null;

            return GetTypedValueFromCsoAttributeValue(csoAttrValue, attribute.Type);
        }
    }

    /// <summary>
    /// Extracts the typed value from a CSO attribute value based on the attribute data type.
    /// For reference attributes, returns the MVO ID that the referenced CSO is joined to.
    /// </summary>
    private static object? GetTypedValueFromCsoAttributeValue(ConnectedSystemObjectAttributeValue av, AttributeDataType type)
    {
        return type switch
        {
            AttributeDataType.Text => av.StringValue,
            AttributeDataType.Number => av.IntValue,
            AttributeDataType.LongNumber => av.LongValue,
            AttributeDataType.DateTime => av.DateTimeValue,
            AttributeDataType.Boolean => av.BoolValue,
            AttributeDataType.Guid => av.GuidValue,
            AttributeDataType.Binary => av.ByteValue,
            // For references, return the MVO ID that the referenced CSO is joined to.
            // This enables comparison with the expected MVO reference ID from GetExpectedValue.
            // The referenced CSO's MetaverseObjectId tells us which MVO it represents.
            // The repository repair (RepairReferenceValueMaterialisationAsync) should ensure
            // ReferenceValue is always populated, but log a warning if it's still null.
            AttributeDataType.Reference => GetCsoReferenceMetaverseObjectId(av),
            _ => null
        };
    }

    /// <summary>
    /// Gets the MetaverseObjectId for a CSO reference attribute value.
    /// Uses the ReferenceValue navigation if loaded; logs a warning if the navigation
    /// is null despite ReferenceValueId being set (indicates the repository-level repair
    /// for AsSplitQuery materialisation failures did not cover this value).
    /// </summary>
    private static Guid? GetCsoReferenceMetaverseObjectId(ConnectedSystemObjectAttributeValue av)
    {
        if (av.ReferenceValue != null)
            return av.ReferenceValue.MetaverseObjectId;

        if (av.ReferenceValueId.HasValue)
        {
            Log.Warning("GetCsoReferenceMetaverseObjectId: ReferenceValueId {RefId} is set but " +
                "ReferenceValue navigation is null on attribute value {AvId}. " +
                "This indicates the AsSplitQuery materialisation repair did not cover this value " +
                "(dotnet/efcore#33826)",
                av.ReferenceValueId.Value, av.Id);
        }

        return null;
    }

    /// <summary>
    /// Compares two attribute values for equality.
    /// Handles both single values and HashSets (for multi-valued attributes).
    /// </summary>
    private static bool ValuesEqual(object? expected, object? actual)
    {
        if (expected == null && actual == null)
            return true;

        if (expected == null || actual == null)
            return false;

        // Handle HashSet comparison (for multi-valued attributes)
        if (expected is HashSet<object> expectedSet && actual is HashSet<object> actualSet)
        {
            // Compare sets by checking if they contain the same elements
            if (expectedSet.Count != actualSet.Count)
            {
                Log.Debug("ValuesEqual: Multi-valued attribute sets have different counts. Expected: {ExpectedCount}, Actual: {ActualCount}",
                    expectedSet.Count, actualSet.Count);
                return false;
            }

            // Check if all expected values are in the actual set
            foreach (var expectedValue in expectedSet)
            {
                var found = actualSet.Any(actualValue => SingleValueEquals(expectedValue, actualValue));
                if (!found)
                {
                    Log.Debug("ValuesEqual: Expected value {ExpectedValue} not found in actual set",
                        FormatValueForLog(expectedValue));
                    return false;
                }
            }

            return true;
        }

        // Handle single value comparison
        return SingleValueEquals(expected, actual);
    }

    /// <summary>
    /// Compares two single values for equality.
    /// </summary>
    private static bool SingleValueEquals(object? expected, object? actual)
    {
        if (expected == null && actual == null)
            return true;

        if (expected == null || actual == null)
            return false;

        // Handle byte array comparison
        if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
            return expectedBytes.SequenceEqual(actualBytes);

        // Handle string comparison (case-sensitive)
        if (expected is string expectedStr && actual is string actualStr)
            return string.Equals(expectedStr, actualStr, StringComparison.Ordinal);

        // Handle Guid comparison (may be stored as string in CSO)
        if (expected is Guid expectedGuid)
        {
            if (actual is Guid actualGuid)
                return expectedGuid == actualGuid;
            if (actual is string actualGuidStr && Guid.TryParse(actualGuidStr, out var parsedGuid))
                return expectedGuid == parsedGuid;
        }

        // Default comparison
        return expected.Equals(actual);
    }

    /// <summary>
    /// Creates corrective pending exports for drifted attributes (in-memory only, not persisted).
    /// The caller is responsible for adding these to the batch list for persistence.
    /// For multi-valued attributes, creates atomic ADD/REMOVE changes for the specific differences.
    /// </summary>
    private List<PendingExport> CreateCorrectiveExports(
        ConnectedSystemObject cso,
        MetaverseObject mvo,
        List<DriftedAttribute> driftedAttributes)
    {
        var pendingExports = new List<PendingExport>();

        // Group drifted attributes by export rule (one pending export per rule)
        var attributesByRule = driftedAttributes
            .GroupBy(d => d.ExportRule)
            .ToList();

        foreach (var ruleGroup in attributesByRule)
        {
            var exportRule = ruleGroup.Key;
            var attributes = ruleGroup.ToList();

            // Create pending export attribute changes
            var attributeChanges = new List<PendingExportAttributeValueChange>();

            foreach (var drifted in attributes)
            {
                // Check if this is a multi-valued attribute
                var isMultiValued = drifted.Attribute.AttributePlurality == AttributePlurality.MultiValued;

                if (isMultiValued)
                {
                    // For multi-valued attributes, compute the diff between expected and actual
                    var expectedSet = drifted.ExpectedValue as HashSet<object> ?? [];
                    var actualSet = drifted.ActualValue as HashSet<object> ?? [];

                    // Find values to ADD (in expected but not in actual)
                    foreach (var expectedValue in expectedSet)
                    {
                        var existsInActual = actualSet.Any(av => SingleValueEquals(expectedValue, av));
                        if (!existsInActual)
                        {
                            var addChange = new PendingExportAttributeValueChange
                            {
                                Id = Guid.NewGuid(),
                                AttributeId = drifted.Attribute.Id,
                                ChangeType = PendingExportAttributeChangeType.Add
                            };
                            SetAttributeChangeValue(addChange, expectedValue, drifted.Attribute.Type);
                            attributeChanges.Add(addChange);

                            Log.Debug("CreateCorrectiveExportsAsync: Adding value {Value} to attribute {AttrName}",
                                FormatValueForLog(expectedValue), drifted.Attribute.Name);
                        }
                    }

                    // Find values to REMOVE (in actual but not in expected)
                    foreach (var actualValue in actualSet)
                    {
                        var existsInExpected = expectedSet.Any(ev => SingleValueEquals(ev, actualValue));
                        if (!existsInExpected)
                        {
                            var removeChange = new PendingExportAttributeValueChange
                            {
                                Id = Guid.NewGuid(),
                                AttributeId = drifted.Attribute.Id,
                                ChangeType = PendingExportAttributeChangeType.Remove
                            };
                            SetAttributeChangeValue(removeChange, actualValue, drifted.Attribute.Type);
                            attributeChanges.Add(removeChange);

                            Log.Debug("CreateCorrectiveExportsAsync: Removing value {Value} from attribute {AttrName}",
                                FormatValueForLog(actualValue), drifted.Attribute.Name);
                        }
                    }
                }
                else
                {
                    // For single-valued attributes, use Update
                    var change = new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        AttributeId = drifted.Attribute.Id,
                        ChangeType = PendingExportAttributeChangeType.Update
                    };

                    // Set the expected value on the change
                    SetAttributeChangeValue(change, drifted.ExpectedValue, drifted.Attribute.Type);
                    attributeChanges.Add(change);
                }
            }

            if (attributeChanges.Count == 0)
                continue;

            // Check if any attribute changes have unresolved reference values
            // This is used to defer exports with reference attributes until the referenced objects have been exported
            var hasUnresolvedReferences = attributeChanges.Any(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue));

            // Create the pending export
            var pendingExport = new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = cso.ConnectedSystemId,
                ConnectedSystemObjectId = cso.Id,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                SourceMetaverseObjectId = mvo.Id,
                AttributeValueChanges = attributeChanges,
                CreatedAt = DateTime.UtcNow,
                HasUnresolvedReferences = hasUnresolvedReferences
            };

            if (hasUnresolvedReferences)
            {
                Log.Debug("CreateCorrectiveExportsAsync: PendingExport {ExportId} has {Count} unresolved reference(s), will be deferred for resolution",
                    pendingExport.Id, attributeChanges.Count(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue)));
            }

            pendingExports.Add(pendingExport);

            Log.Information("CreateCorrectiveExportsAsync: Staged corrective PendingExport {ExportId} for CSO {CsoId} " +
                "with {AttrCount} attribute corrections via rule '{RuleName}' (deferred for batch save)",
                pendingExport.Id, cso.Id, attributeChanges.Count, exportRule.Name);
        }

        return pendingExports;
    }

    /// <summary>
    /// Sets the value on a PendingExportAttributeValueChange based on the attribute data type.
    /// </summary>
    private static void SetAttributeChangeValue(
        PendingExportAttributeValueChange change,
        object? value,
        AttributeDataType dataType)
    {
        if (value == null)
            return;

        switch (dataType)
        {
            case AttributeDataType.Text:
                change.StringValue = value as string;
                break;
            case AttributeDataType.Number:
                change.IntValue = value as int?;
                break;
            case AttributeDataType.LongNumber:
                change.LongValue = value as long?;
                break;
            case AttributeDataType.DateTime:
                change.DateTimeValue = value as DateTime?;
                break;
            case AttributeDataType.Boolean:
                change.BoolValue = value as bool?;
                break;
            case AttributeDataType.Guid:
                change.GuidValue = value as Guid?;
                break;
            case AttributeDataType.Binary:
                change.ByteValue = value as byte[];
                break;
            case AttributeDataType.Reference:
                // For reference attributes, store the MVO ID as unresolved reference
                if (value is Guid guidValue)
                    change.UnresolvedReferenceValue = guidValue.ToString();
                else if (value is string strValue)
                    change.UnresolvedReferenceValue = strValue;
                break;
        }
    }

    /// <summary>
    /// Formats a value for logging purposes.
    /// </summary>
    private static string FormatValueForLog(object? value)
    {
        if (value == null)
            return "(null)";

        if (value is byte[] bytes)
            return $"(binary, {bytes.Length} bytes)";

        // Format HashSet values as a list for readability
        if (value is HashSet<object> hashSet)
        {
            if (hashSet.Count == 0)
                return "(empty set)";

            var formattedValues = hashSet.Select(v => v?.ToString() ?? "(null)").Take(5);
            var result = string.Join(", ", formattedValues);
            if (hashSet.Count > 5)
                result += $"... (+{hashSet.Count - 5} more)";
            return $"[{result}] ({hashSet.Count} values)";
        }

        return value.ToString() ?? "(null)";
    }
}
