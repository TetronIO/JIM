// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Expressions;
using JIM.Application.Servers;
using JIM.Application.Staging;
using JIM.Application.UniqueValues;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Service responsible for detecting drift in target systems and staging corrective Pending Exports.
/// Drift occurs when a target system's attribute values differ from what the authoritative source dictates.
/// </summary>
/// <remarks>
/// Drift detection is triggered during inbound sync when a CSO is processed from a system that has
/// export rules targeting it. For each export rule with EnforceState = true, the service compares
/// the CSO's actual values against the expected values (calculated from the MVO and export rule mappings).
///
/// Key design decisions:
/// 1. Only evaluates export rules with EnforceState = true
/// 2. Skips attributes where the Connected System is a legitimate contributor (has import rules for that attribute)
/// 3. Uses the existing PendingExport infrastructure for corrective exports
/// 4. A generated export Attribute Flow (Unique Value Generation, #242) is checked like any other, against its
///    export-mode assignment for the Connected System Object rather than its base expression
/// </remarks>
public class DriftDetectionService
{
    private readonly JimApplication _jim;
    private readonly IExpressionEvaluator _expressionEvaluator;

    public DriftDetectionService(JimApplication jim)
    {
        _jim = jim;
        _expressionEvaluator = new DynamicExpressoEvaluator();
    }

    /// <summary>
    /// Evaluates drift for a CSO that has been imported/synced against its joined MVO.
    /// Checks all export rules with EnforceState = true that target this CSO's Connected System.
    /// </summary>
    /// <param name="cso">The Connected System Object that was just imported/synced.</param>
    /// <param name="mvo">The Metaverse Object the CSO is joined to.</param>
    /// <param name="exportRules">Export rules targeting this CSO's Connected System (pre-loaded for efficiency).</param>
    /// <param name="importMappingsByAttribute">Cache of import mappings by (ConnectedSystemId, MvoAttributeId) for checking if system is a contributor.</param>
    /// <param name="priorityContext">Attribute priority context, for the priority-aware contributor check (#91).</param>
    /// <param name="generatedValueAssignments">The run's Unique Value Generation cache, prefetched for this page's Connected
    /// System Objects. A generated export Attribute Flow's expected value is read from here (#242); without it, generated
    /// flows are not checked.</param>
    /// <returns>Result indicating what drift was detected and corrective exports staged (not yet persisted), and the
    /// mapping-level errors found on the way (<see cref="DriftDetectionResult.AttributeFlowErrors"/>) for the caller to
    /// record.</returns>
    /// <exception cref="SyncExpressionEvaluationException">An export expression failed to evaluate. Raised exactly as
    /// export staging raises it (#2180): the caller records it against the object, and nothing is staged for it.</exception>
    /// <exception cref="SyncExpressionMissingInputException">An export expression's input is missing and its Missing
    /// Input Behaviour is Fail the object; recorded and staged for as above.</exception>
    public DriftDetectionResult EvaluateDrift(
        ConnectedSystemObject cso,
        MetaverseObject? mvo,
        List<SyncRule> exportRules,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute = null,
        AttributePriorityContext? priorityContext = null,
        UniqueValueResolveOptions? generatedValueAssignments = null)
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
        // 1. Target this CSO's Connected System
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
            // Check each Attribute Flow mapping in the export rule. Mappings that do not flow on an Update
            // export are excluded, because Drift Correction stages exactly that kind of export: Initial
            // Export Only mappings (#223), whose target attribute is unmanaged by JIM once the Connected
            // System Object is past provisioning, so a diverged value is external ownership rather than
            // drift; and mappings targeting a WritableOnCreate attribute, where re-asserting the value
            // would rewrite the Connected System's identifier for the object. A disabled mapping (#1485) does
            // not run at all, so a diverged value on its target is not drift either.
            foreach (var mapping in exportRule.AttributeFlowRules.Where(m => m.Enabled && m.FlowsOnUpdateExport()))
            {
                if (mapping.TargetConnectedSystemAttribute == null)
                {
                    Log.Warning("EvaluateDrift: Export mapping has no TargetConnectedSystemAttribute set, skipping");
                    continue;
                }

                // A credential attribute is never exported, so a difference on one is never drift to correct (#2091);
                // the synchronisation's export evaluation reports the mapping.
                if (CredentialAttributes.IsCredentialAttribute(mapping.TargetConnectedSystemAttribute.Name))
                    continue;

                // Unique Value Generation (#242): a Generated Value is an ordinary Attribute Flow that generates
                // once (product-owner decision 2026-10-01), so it is drift-checked exactly like any export flow,
                // against the value JIM actually asserts: the export-mode assignment for this Connected System
                // Object, never the mapping's base expression (which lacks the uniqueness token).
                if (mapping.Generation != null)
                {
                    EvaluateGeneratedMappingDrift(cso, targetMvo, exportRule, mapping, importMappingsByAttribute, priorityContext, generatedValueAssignments, result);
                    continue;
                }

                // Check if this Connected System is a legitimate contributor for this attribute
                // (i.e., has an import rule that maps to the same MVO attribute)
                foreach (var source in mapping.Sources)
                {
                    if (source.MetaverseAttribute == null && string.IsNullOrWhiteSpace(source.Expression))
                        continue;

                    var mvoAttributeId = source.MetaverseAttribute?.Id ?? 0;
                    var isContributor = IsLegitimateContributor(cso, targetMvo, source, mapping.TargetConnectedSystemAttribute, importMappingsByAttribute, priorityContext);

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

                    // An expression mapping's expected state is worked out by export staging's own code (#2180), so drift
                    // never asserts a state export staging would not stage. The Metaverse Object's expression dictionary
                    // is built once, by the same builder export staging uses, and only when an expression needs it.
                    if (!string.IsNullOrWhiteSpace(source.Expression))
                    {
                        mvAttributeDictionary ??= SyncEngine.BuildAttributeDictionary(targetMvo);
                        EvaluateExpressionMappingDrift(cso, targetMvo, exportRule, mapping, source, mvAttributeDictionary, result);
                        continue;
                    }

                    // Several values of a multi-valued source cannot be written to a single-valued target, and export
                    // staging refuses to pick one (#435); drift refuses on exactly the same terms, through the same
                    // predicate, rather than correcting the target to whichever value happened to load first.
                    var multiValuedToSingleValuedError = SyncEngine.FindMultiValuedToSingleValuedError(targetMvo, mapping, source);
                    if (multiValuedToSingleValuedError != null)
                    {
                        result.AttributeFlowErrors.Add(multiValuedToSingleValuedError);
                        continue;
                    }

                    var expectedValue = GetExpectedAttributeValue(targetMvo, source, mapping.TargetConnectedSystemAttribute);

                    // Get actual value from CSO
                    var actualValue = GetActualValue(cso, mapping.TargetConnectedSystemAttribute);

                    // Compare values
                    // A target held only under another data type reads as no value; it is not drift (#2319).
                    if (!ValuesEqual(expectedValue, actualValue, AttributeValueComparison.ComparerFor(mapping.TargetConnectedSystemAttribute)) &&
                        !IsTargetHeldOnlyUnderAnotherDataType(cso, mapping.TargetConnectedSystemAttribute))
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

        // If drift was detected, queue corrective Pending Exports
        if (result.DriftedAttributes.Count > 0)
        {
            var (pendingExports, correctedAttributes) = CreateCorrectiveExports(cso, targetMvo, result.DriftedAttributes);
            result.CorrectiveExports.AddRange(pendingExports);

            // Drift is reported only where a correction is staged (#2180). A difference no correction can be staged for
            // would otherwise raise a Drift Correction item on every synchronisation while changing nothing.
            var uncorrectedCount = result.DriftedAttributes.RemoveAll(drifted => !correctedAttributes.Contains(drifted));
            if (uncorrectedCount > 0)
            {
                Log.Debug("EvaluateDrift: {Count} difference(s) on CSO {CsoId} staged no correction and are not reported as drift",
                    uncorrectedCount, cso.Id);
            }

            if (result.HasDrift)
            {
                Log.Information("EvaluateDrift: Detected {DriftCount} drifted attributes on CSO {CsoId}; staged {ExportCount} corrective Pending Exports.",
                    result.DriftedAttributes.Count, cso.Id, result.CorrectiveExports.Count);
            }
        }

        return result;
    }

    /// <summary>
    /// Drift Detection for an expression mapping (#2180): what the Connected System Object should hold is worked out by
    /// export staging's own code, and a correction is exactly what export staging would stage for the same Metaverse
    /// Object and the same object, after its own no-net-change check. So the two can never disagree:
    /// <list type="bullet">
    /// <item>Missing Input Behaviour applies as it does to export staging: Fail this mapping records an error and
    /// stages nothing, Contribute no value stages nothing, and Fail the object throws.</item>
    /// <item>A null result, or one with no value, stages nothing, so the Connected System keeps what it holds.</item>
    /// <item>The result is converted to the target's type, so "512" is the number 512; a value the type cannot hold,
    /// or several values for a single-valued target, records an error and stages nothing.</item>
    /// <item>A single value into a multi-valued target is the whole set (an Update), and a collection is the set of its
    /// elements, each compared with what the object holds as a set.</item>
    /// </list>
    /// </summary>
    /// <exception cref="SyncExpressionEvaluationException">The expression failed to evaluate.</exception>
    /// <exception cref="SyncExpressionMissingInputException">An input is missing and Missing Input Behaviour is Fail
    /// the object.</exception>
    private void EvaluateExpressionMappingDrift(
        ConnectedSystemObject cso,
        MetaverseObject targetMvo,
        SyncRule exportRule,
        SyncRuleMapping mapping,
        SyncRuleMappingSource source,
        Dictionary<string, object?> mvAttributeDictionary,
        DriftDetectionResult result)
    {
        var target = mapping.TargetConnectedSystemAttribute!;
        var heldValues = cso.AttributeValues.Where(av => av.AttributeId == target.Id).ToList();

        // Exactly what export staging stages for this mapping: the evaluation pipeline (#2055) and the same handling of
        // its outcome. Nothing staged (no value, or a mapping-level error already recorded) leaves the target as it is.
        // Dates the target holds as one are reported by export staging when they are sent (#2153, D2); a drift check
        // re-reading the same result would report them again on every synchronisation, so that report is not kept here.
        var stagingErrors = new List<AttributeFlowError>();
        var staged = SyncEngine.StageExportExpressionMapping(mapping, source, exportRule, mvAttributeDictionary,
            _expressionEvaluator, heldValues, stagingErrors);
        result.AttributeFlowErrors.AddRange(stagingErrors.Where(error => error.Kind != AttributeFlowErrorKind.DatesCollapseAtTargetResolution));
        if (staged.Count == 0)
        {
            Log.Debug("EvaluateDrift: Expression for attribute {AttrName} stages nothing for MVO {MvoId}, so the Connected System keeps its value",
                target.Name, targetMvo.Id);
            return;
        }

        var corrections = staged.Where(change => !SyncEngine.IsCsoAttributeAlreadyCurrent(change, heldValues)).ToList();
        if (corrections.Count == 0 || IsTargetHeldOnlyUnderAnotherDataType(cso, target))
            return;

        var expectedValue = ExpectedValueOf(staged, target);
        var actualValue = GetActualValue(cso, target);
        Log.Information("EvaluateDrift: Drift detected on CSO {CsoId} attribute {AttrName}. Expected: '{ExpectedValue}', Actual: '{ActualValue}'",
            cso.Id, target.Name, FormatValueForLog(expectedValue), FormatValueForLog(actualValue));

        result.DriftedAttributes.Add(new DriftedAttribute
        {
            Attribute = target,
            ExpectedValue = expectedValue,
            ActualValue = actualValue,
            ExportRule = exportRule,
            CorrectiveChanges = corrections
        });
    }

    /// <summary>
    /// What an expression mapping's staged changes say the target should hold, shaped as <see cref="GetActualValue"/>
    /// reads the target: the set of the values written for a multi-valued target, or the one value for a single-valued
    /// one. Used to describe the drift, never to decide it.
    /// </summary>
    private static object? ExpectedValueOf(IEnumerable<PendingExportAttributeValueChange> staged, ConnectedSystemObjectTypeAttribute target)
    {
        var values = staged
            .Where(change => change.ChangeType != PendingExportAttributeChangeType.Remove)
            .Select(ChangeValue)
            .OfType<object>()
            .ToList();

        return target.AttributePlurality == AttributePlurality.MultiValued
            ? new HashSet<object>(values)
            : values.FirstOrDefault();
    }

    /// <summary>
    /// The one value a Pending Export change carries, from whichever typed field holds it.
    /// </summary>
    private static object? ChangeValue(PendingExportAttributeValueChange change)
    {
        // Each field is boxed on its own: chaining nullable value types with ?? would widen an int to a long.
        object?[] fields =
        [
            change.StringValue, change.IntValue, change.LongValue, change.DecimalValue, SyncEngine.CanonicalDate(change.DateTimeValue),
            change.BoolValue, change.GuidValue, change.ByteValue, change.UnresolvedReferenceValue
        ];
        return Array.Find(fields, field => field != null);
    }

    /// <summary>
    /// Whether the Connected System Object's own system is a legitimate contributor for an export mapping source,
    /// in which case a divergence on <paramref name="divergedAttribute"/> is a legitimate change rather than drift.
    /// Shared by ordinary and generated export mappings (#242), so the two are exempted on exactly the same terms.
    /// </summary>
    private bool IsLegitimateContributor(
        ConnectedSystemObject cso,
        MetaverseObject targetMvo,
        SyncRuleMappingSource source,
        ConnectedSystemObjectTypeAttribute divergedAttribute,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute,
        AttributePriorityContext? priorityContext)
    {
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

        // Priority-aware refinement (#91): for a multi-contributor attribute, having an import rule is not
        // enough to be drift-exempt. The system is a legitimate contributor only if its contribution WON
        // resolution, i.e. the Metaverse Object's current value for the attribute was contributed by this
        // system. A losing contributor's diverged local value is drift, to be corrected by the EnforceState
        // export. Single-contributor attributes (and runs without a priority context) keep the existing
        // has-import-rule behaviour. (Expression sources, mvoAttributeId == 0, are unaffected here.)
        if (isContributor && priorityContext != null && mvoAttributeId > 0
            && priorityContext.GetContributorCount(targetMvo.Type!.Id, mvoAttributeId) > 1
            && !AttributeWonByConnectedSystem(targetMvo, mvoAttributeId, cso.ConnectedSystemId))
        {
            isContributor = false;
        }

        // #1864: a contributor is a legitimate source for THIS divergence only if its import flow reads the
        // diverged Connected System attribute; only then does the out-of-band edit flow into the Metaverse.
        // An import flow reading a different attribute (Display Name built from givenName and sn, exported
        // to displayName), or only Metaverse attributes (a Metaverse-Derived Attribute Flow, #1750), leaves
        // the edit stranded on the Connected System Object, so skipping it would leave the two sides
        // disagreeing permanently. That is drift, and is corrected.
        if (isContributor && !AnyContributingImportFlowReads(
                cso.ConnectedSystemId,
                mvoAttributeId,
                source.Expression,
                divergedAttribute,
                importMappingsByAttribute))
        {
            isContributor = false;
        }


        return isContributor;
    }

    /// <summary>
    /// Drift Detection for a generated export Attribute Flow (Unique Value Generation, #242; product-owner decision
    /// 2026-10-01). A Generated Value is an ordinary Attribute Flow that generates once, so it is checked on the same
    /// terms as any export flow (the caller has already applied EnforceState, Enabled and
    /// <see cref="SyncRuleMapping.FlowsOnUpdateExport"/>, so Initial Export Only and write-on-create targets never
    /// reach here) with one difference: the expected value is the export-mode assignment JIM holds for this
    /// Connected System Object, read from the run-scoped cache the worker prefetched for the page, never the
    /// mapping's base expression and never a per-object query.
    /// <para>
    /// Nothing is compared, and so nothing is staged, when there is no assignment yet (export evaluation generates
    /// one; a drift correction would have no value to assert and must never be staged blank), when the run's cache
    /// was not supplied, or when this object was not prefetched (a missed prefetch is logged, not queried for).
    /// </para>
    /// </summary>
    private void EvaluateGeneratedMappingDrift(
        ConnectedSystemObject cso,
        MetaverseObject targetMvo,
        SyncRule exportRule,
        SyncRuleMapping mapping,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute,
        AttributePriorityContext? priorityContext,
        UniqueValueResolveOptions? generatedValueAssignments,
        DriftDetectionResult result)
    {
        var targetAttribute = mapping.TargetConnectedSystemAttribute!;

        // The base expression's inputs make the system a legitimate contributor on exactly the terms an ordinary
        // expression mapping's would. A generation with no base expression (Sequence, Random) reads nothing.
        var isContributor = mapping.Sources
            .Where(source => source.MetaverseAttribute != null || !string.IsNullOrWhiteSpace(source.Expression))
            .Any(source => IsLegitimateContributor(cso, targetMvo, source, targetAttribute, importMappingsByAttribute, priorityContext));
        if (isContributor)
        {
            Log.Debug("EvaluateDrift: Skipping generated attribute {AttrName} for CSO {CsoId} - system is a contributor (has import rules)",
                targetAttribute.Name, cso.Id);
            return;
        }

        if (generatedValueAssignments == null)
        {
            Log.Warning("EvaluateDrift: No Unique Value Generation assignment cache was supplied, so generated attribute {AttrName} on CSO {CsoId} " +
                "cannot be checked for drift. Its value is reasserted when export evaluation next runs for the object.",
                targetAttribute.Name, cso.Id);
            return;
        }

        if (!generatedValueAssignments.TryGetKnownConnectedSystemAssignment(cso.Id, targetAttribute.Id, out var assignment))
        {
            Log.Warning("EvaluateDrift: Generated value assignments for CSO {CsoId} were not prefetched for this page, so generated attribute " +
                "{AttrName} is not checked for drift (drift detection never queries per object).",
                cso.Id, targetAttribute.Name);
            return;
        }

        if (assignment == null)
        {
            Log.Debug("EvaluateDrift: CSO {CsoId} has no generated value assignment for {AttrName} yet; export evaluation generates it, so there is nothing to compare against",
                cso.Id, targetAttribute.Name);
            return;
        }

        if (!TryGetExpectedGeneratedValue(assignment, targetAttribute, out var expectedValue))
            return;

        var actualValue = GetActualValue(cso, targetAttribute);

        // The same comparison every export flow uses (exact for text, unless the attribute is an identifier that ignores
        // case; AttributeValueComparison), so drift and export staging agree on what counts as a difference.
        if (ValuesEqual(expectedValue, actualValue, AttributeValueComparison.ComparerFor(targetAttribute)) ||
            IsTargetHeldOnlyUnderAnotherDataType(cso, targetAttribute))
            return;

        Log.Information("EvaluateDrift: Drift detected on CSO {CsoId} generated attribute {AttrName}. Expected (assigned): '{ExpectedValue}', Actual: '{ActualValue}'",
            cso.Id, targetAttribute.Name, FormatValueForLog(expectedValue), FormatValueForLog(actualValue));

        result.DriftedAttributes.Add(new DriftedAttribute
        {
            Attribute = targetAttribute,
            ExpectedValue = expectedValue,
            ActualValue = actualValue,
            ExportRule = exportRule
        });
    }

    /// <summary>
    /// The value a generated export Attribute Flow asserts for its Connected System Object: always the assignment's
    /// current <see cref="GeneratedValueAssignment.Value"/>, typed for the target attribute the way
    /// <see cref="GetActualValue"/> reads the target, so the two compare like for like.
    /// <para>
    /// Collision Remediation hook (release 4): remediation rewrites the assignment's value in place (keeping the old
    /// one as <see cref="GeneratedValueAssignment.PreviousValue"/>), so reading the current assignment here is what
    /// makes drift follow a remediated value with no further change. Never derive the expected value from anything
    /// else (the base expression, a retired value, or what the target held when the value was issued).
    /// </para>
    /// </summary>
    private static bool TryGetExpectedGeneratedValue(GeneratedValueAssignment assignment, ConnectedSystemObjectTypeAttribute targetAttribute, out object? expectedValue)
    {
        object? value = null;
        switch (targetAttribute.Type)
        {
            case AttributeDataType.Text:
                value = assignment.Value;
                break;
            case AttributeDataType.Number when int.TryParse(assignment.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var intValue):
                value = intValue;
                break;
            case AttributeDataType.LongNumber when long.TryParse(assignment.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var longValue):
                value = longValue;
                break;
        }

        if (value == null)
        {
            Log.Warning("EvaluateDrift: Generated value assignment {AssignmentId} cannot be read as the {AttrType} type of attribute {AttrName}; not checked for drift",
                assignment.Id, targetAttribute.Type, targetAttribute.Name);
            expectedValue = null;
            return false;
        }

        // A multi-valued target is compared as a set, as GetActualValue reads it.
        expectedValue = targetAttribute.AttributePlurality == AttributePlurality.MultiValued
            ? new HashSet<object> { value }
            : value;
        return true;
    }

    /// <summary>
    /// Whether the given Connected System contributed the Metaverse Object attribute's current value(s) (#91). Under
    /// winner-takes-all resolution every value row (and any asserted-null marker) for an attribute carries the winning
    /// system's provenance (<see cref="MetaverseObjectAttributeValue.ContributedBySystemId"/>, denormalised from the
    /// winning rule), so the system "won" resolution iff it stamped the current value. Used to make the drift
    /// contributor check priority-aware: a system with an import rule that nonetheless lost resolution is not a
    /// legitimate contributor, and its diverged value is drift to be corrected.
    ///
    /// Granularity note: priority is per Synchronisation Rule, but this check is keyed on the winning rule's *system*
    /// because drift correction re-aligns a system's object, and JIM's standard topology is one Connected System Object
    /// per system per Metaverse Object, where "winning system" and "winning rule" coincide. The two diverge only when a
    /// single Metaverse Object is joined to multiple objects of the *same* system contributing the same attribute via
    /// different-priority rules (e.g. two accounts in one directory); resolving that precisely would need a scope
    /// evaluation here to identify the specific rule each object contributes through. Deferred as an exotic case.
    /// </summary>
    private static bool AttributeWonByConnectedSystem(MetaverseObject mvo, int mvoAttributeId, int connectedSystemId) =>
        mvo.AttributeValues.Any(av => av.AttributeId == mvoAttributeId && av.ContributedBySystemId == connectedSystemId);

    /// <summary>
    /// Checks if a Connected System has import rules that flow to the specified MVO attribute.
    /// If so, changes from that system are not "drift" but legitimate updates.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System to check.</param>
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
    /// Whether any of the Connected System's import flows that make it a contributor for the export source reads
    /// <paramref name="divergedAttribute"/>, so that an out-of-band edit to that attribute flows into the Metaverse
    /// (#1864). The contributing flows are those targeting <paramref name="mvoAttributeId"/> for a direct export source,
    /// or any Metaverse attribute an export expression reads. A flow reads the attribute through a direct Connected
    /// System attribute source (matched by id), or a <c>cs["..."]</c> input of an expression source (matched by name,
    /// case-insensitively, as the expression evaluator resolves it).
    /// </summary>
    /// <remarks>
    /// Keyed on the system, not the object type, as the contributor check above is: a system with import rules for
    /// the same Metaverse attribute on two object types matches on either. JIM's standard topology is one object type
    /// per system per Metaverse Object Type, so this is deferred with the same rationale as
    /// <see cref="AttributeWonByConnectedSystem"/>.
    /// </remarks>
    private static bool AnyContributingImportFlowReads(
        int connectedSystemId,
        int mvoAttributeId,
        string? exportExpression,
        ConnectedSystemObjectTypeAttribute divergedAttribute,
        Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>? importMappingsByAttribute)
    {
        if (importMappingsByAttribute == null)
            return false;

        IEnumerable<SyncRuleMapping> contributingMappings;
        if (mvoAttributeId > 0)
        {
            contributingMappings = importMappingsByAttribute.TryGetValue((connectedSystemId, mvoAttributeId), out var mappings)
                ? mappings
                : [];
        }
        else
        {
            var expressionMvInputs = ExpressionInputResolver.ResolveCached(exportExpression)
                .Where(i => i.Source == ExpressionInputSource.Metaverse)
                .Select(i => i.AttributeName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            contributingMappings = importMappingsByAttribute
                .Where(kvp => kvp.Key.ConnectedSystemId == connectedSystemId)
                .SelectMany(kvp => kvp.Value)
                .Where(m => m.TargetMetaverseAttribute != null && expressionMvInputs.Contains(m.TargetMetaverseAttribute.Name));
        }

        return contributingMappings.Any(m => m.Sources.Any(s => SourceReads(s, divergedAttribute)));
    }

    /// <summary>
    /// Whether an import mapping source reads the given Connected System attribute: directly, or as a <c>cs["..."]</c>
    /// input of its expression.
    /// </summary>
    private static bool SourceReads(SyncRuleMappingSource source, ConnectedSystemObjectTypeAttribute attribute)
    {
        var directAttributeId = source.ConnectedSystemAttributeId ?? source.ConnectedSystemAttribute?.Id;
        if (directAttributeId == attribute.Id)
            return true;

        return ExpressionInputResolver.ResolveCached(source.Expression).Any(i =>
            i.Source == ExpressionInputSource.ConnectedSystem
            && string.Equals(i.AttributeName, attribute.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if a Connected System is a contributor for any MVO attribute referenced in an expression.
    /// Expressions may reference MVO attributes using mv["attributeName"] syntax. If the system has
    /// import rules for any of those attributes, it's considered a contributor and drift detection
    /// should be skipped for this expression-based mapping.
    /// </summary>
    /// <param name="connectedSystemId">The Connected System ID to check.</param>
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
                        if (string.Equals(mapping.TargetMetaverseAttribute?.Name, attributeName, StringComparison.OrdinalIgnoreCase))
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
    /// <param name="syncRules">All Synchronisation Rules to process.</param>
    /// <returns>Dictionary keyed by (ConnectedSystemId, MvoAttributeId).</returns>
    public static Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>> BuildImportMappingCache(
        List<SyncRule> syncRules)
    {
        var cache = new Dictionary<(int ConnectedSystemId, int MvoAttributeId), List<SyncRuleMapping>>();

        var importRules = syncRules.Where(sr => sr.Enabled && sr.Direction == SyncRuleDirection.Import);

        foreach (var importRule in importRules)
        {
            // A disabled mapping (#1485) contributes nothing, so it does not make its system a legitimate
            // contributor for drift purposes.
            foreach (var mapping in importRule.AttributeFlowRules.Where(m => m.Enabled))
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
    /// The expected value(s) of a direct attribute mapping: the Metaverse Object's value(s) for the source attribute,
    /// shaped by the target attribute's plurality.
    /// </summary>
    private static object? GetExpectedAttributeValue(MetaverseObject mvo, SyncRuleMappingSource source, ConnectedSystemObjectTypeAttribute targetCsoAttribute)
    {
        // Handle direct attribute mapping
        if (source.MetaverseAttribute == null)
            return null;

        // Use the TARGET CSO attribute's plurality to determine how to return values.
        // This ensures consistency with GetActualValue() which also uses CSO attribute plurality.
        // MVO and CSO attributes may have different plurality settings for the same logical attribute.
        var isMultiValued = targetCsoAttribute.AttributePlurality == AttributePlurality.MultiValued;

        if (isMultiValued)
        {
            // Get ALL values for this attribute (excluding asserted-null markers, #91, which carry no value:
            // the expected state of a target attribute must be derived from real values only)
            var mvoAttrValues = mvo.AttributeValues
                .Where(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue)
                .ToList();

            if (mvoAttrValues.Count == 0)
                return new HashSet<object>(); // Empty set

            // Return a HashSet of all values for set comparison
            var valueSet = new HashSet<object>();
            var nullValueCount = 0;
            var duplicateCount = 0;
            foreach (var av in mvoAttrValues)
            {
                var value = GetTypedValueFromMvoAttributeValue(av, source.MetaverseAttribute.Type, targetCsoAttribute.DateTimeResolution);
                if (value != null)
                {
                    // Dates one at the target's resolution are expected to merge (#2153, D2); export staging reports it.
                    if (!valueSet.Add(value) && value is not DateTime)
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
            // Single-valued attribute - use FirstOrDefault (excluding asserted-null markers, #91)
            var mvoAttrValue = mvo.AttributeValues
                .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue);

            if (mvoAttrValue == null)
                return null;

            return GetTypedValueFromMvoAttributeValue(mvoAttrValue, source.MetaverseAttribute.Type, targetCsoAttribute.DateTimeResolution);
        }
    }

    /// <summary>
    /// Extracts the typed value from an MVO attribute value based on the attribute data type. A date is taken as the
    /// target holds it, floored to its <paramref name="targetResolution"/> (#2153, D2), so a difference the target cannot
    /// hold is never drift, and dates the target holds as one are one expected value.
    /// </summary>
    private static object? GetTypedValueFromMvoAttributeValue(MetaverseObjectAttributeValue av, AttributeDataType type, DateTimeResolution? targetResolution)
    {
        return type switch
        {
            AttributeDataType.Text => av.StringValue,
            AttributeDataType.Number => av.IntValue,
            AttributeDataType.LongNumber => av.LongValue,
            AttributeDataType.Decimal => av.DecimalValue,
            AttributeDataType.DateTime => SyncEngine.DateAtResolution(av.DateTimeValue, targetResolution),
            AttributeDataType.Boolean => av.BoolValue,
            AttributeDataType.Guid => av.GuidValue,
            AttributeDataType.Binary => av.ByteValue,
            // Use scalar FK with navigation fallback for test compatibility (tests may
            // set ReferenceValue directly without setting ReferenceValueId).
            AttributeDataType.Reference => av.ReferenceValueId ?? av.ReferenceValue?.Id,
            _ => null
        };
    }

    /// <summary>
    /// Whether the Connected System Object holds the target attribute only under another data type (#2319): a schema
    /// refresh changed its type and no import has read it since. <see cref="GetActualValue"/> reads that as no value, so a
    /// difference found against it is not drift, and a correction would only rewrite what the next import reads anyway.
    /// The attribute is checked again once an import has read it as its new type. Called only once a difference has been
    /// found, so the usual case costs nothing.
    /// </summary>
    private static bool IsTargetHeldOnlyUnderAnotherDataType(ConnectedSystemObject cso, ConnectedSystemObjectTypeAttribute target)
    {
        if (SyncEngine.HeldOnlyUnderAnotherDataType(cso.AttributeValues, target.Id, target.Type) is not { } heldType)
            return false;

        Log.Warning("EvaluateDrift: Connected System Object {CsoId} holds attribute '{AttrName}' only as {HeldType}, not as its {AttrType} " +
            "type, so it is not checked for drift until an import reads the attribute again.",
            cso.Id, LogSanitiser.Sanitise(target.Name), heldType, target.Type);
        return true;
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
                var value = GetTypedValueFromCsoAttributeValue(av, attribute.Type, attribute.DateTimeResolution);
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

            return GetTypedValueFromCsoAttributeValue(csoAttrValue, attribute.Type, attribute.DateTimeResolution);
        }
    }

    /// <summary>
    /// Extracts the typed value from a CSO attribute value based on the attribute data type.
    /// For reference attributes, returns the MVO ID that the referenced CSO is joined to.
    /// </summary>
    private static object? GetTypedValueFromCsoAttributeValue(ConnectedSystemObjectAttributeValue av, AttributeDataType type, DateTimeResolution? resolution)
    {
        return type switch
        {
            AttributeDataType.Text => av.StringValue,
            AttributeDataType.Number => av.IntValue,
            AttributeDataType.LongNumber => av.LongValue,
            AttributeDataType.Decimal => av.DecimalValue,
            // At the attribute's resolution (#2153, D2), as the expected value is taken.
            AttributeDataType.DateTime => SyncEngine.DateAtResolution(av.DateTimeValue, resolution),
            AttributeDataType.Boolean => av.BoolValue,
            AttributeDataType.Guid => av.GuidValue,
            AttributeDataType.Binary => av.ByteValue,
            // For references, return the MVO ID that the referenced CSO is joined to.
            // ResolvedReferenceMetaverseObjectId (direct SQL) is the primary source;
            // fall back to ReferenceValue navigation for in-memory test compatibility.
            AttributeDataType.Reference => av.ResolvedReferenceMetaverseObjectId ?? av.ReferenceValue?.MetaverseObjectId,
            _ => null
        };
    }

    /// <summary>
    /// Compares two attribute values for equality.
    /// Handles both single values and HashSets (for multi-valued attributes).
    /// </summary>
    /// <param name="expected">The value the Metaverse Object says the target should hold.</param>
    /// <param name="actual">The value the target holds.</param>
    /// <param name="textComparer">How text compares, from <see cref="AttributeValueComparison"/>: exact, except for an
    /// identifier that ignores case, which the directory may report in the case it stored (#2195, #2051).</param>
    private static bool ValuesEqual(object? expected, object? actual, StringComparer textComparer)
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

            // Fast path: hash-based set equality. Covers the overwhelmingly common case where both
            // sets hold same-typed values (reference attributes always yield Guids on both sides) and
            // the target has not drifted. The pairwise scan below is O(n^2); on a 20,000-member group
            // that is 400M comparisons (~36s of CPU per group measured at Scale500k25kGroups), where
            // this is O(n). Hash equality implies SingleValueEquals equality for every type produced
            // by the value extractors (ordinal equality for strings, which implies equality under either
            // text comparison; value equality for Guids and numerics,
            // reference equality for byte arrays), so a true result here is always safe.
            if (expectedSet.SetEquals(actualSet))
                return true;

            // Fallback: pairwise scan preserving the cross-type semantics hash equality cannot see
            // (Guid vs its string representation, byte array content). The O(1) hash lookup filters
            // first, so only genuinely hash-missing values pay for a linear scan.
            foreach (var expectedValue in expectedSet.Where(v => !actualSet.Contains(v)))
            {
                var found = actualSet.Any(actualValue => SingleValueEquals(expectedValue, actualValue, textComparer));
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
        return SingleValueEquals(expected, actual, textComparer);
    }

    /// <summary>
    /// Compares two single values for equality.
    /// </summary>
    private static bool SingleValueEquals(object? expected, object? actual, StringComparer textComparer)
    {
        if (expected == null && actual == null)
            return true;

        if (expected == null || actual == null)
            return false;

        // Handle byte array comparison
        if (expected is byte[] expectedBytes && actual is byte[] actualBytes)
            return expectedBytes.SequenceEqual(actualBytes);

        // Handle string comparison (exact unless the attribute is an identifier that ignores case)
        if (expected is string expectedStr && actual is string actualStr)
            return textComparer.Equals(expectedStr, actualStr);

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
    /// Creates corrective Pending Exports for drifted attributes (in-memory only, not persisted).
    /// The caller is responsible for adding these to the batch list for persistence.
    /// For multi-valued attributes, creates atomic ADD/REMOVE changes for the specific differences. An expression
    /// mapping's drift carries the changes export staging's own code staged for it (#2180), and those are used as they are.
    /// </summary>
    /// <returns>The corrective Pending Exports, and the drifted attributes a correction was staged for.</returns>
    private (List<PendingExport> PendingExports, List<DriftedAttribute> Corrected) CreateCorrectiveExports(
        ConnectedSystemObject cso,
        MetaverseObject mvo,
        List<DriftedAttribute> driftedAttributes)
    {
        var pendingExports = new List<PendingExport>();
        var corrected = new List<DriftedAttribute>();

        // Group drifted attributes by export rule (one Pending Export per rule)
        var attributesByRule = driftedAttributes
            .GroupBy(d => d.ExportRule)
            .ToList();

        foreach (var ruleGroup in attributesByRule)
        {
            var exportRule = ruleGroup.Key;

            // Create Pending Export attribute changes
            var attributeChanges = new List<PendingExportAttributeValueChange>();
            var attributes = new List<DriftedAttribute>();

            foreach (var (drifted, changes) in ruleGroup
                .Select(drifted => (Drifted: drifted, Changes: drifted.CorrectiveChanges?.ToList() ?? BuildCorrectiveChanges(drifted, exportRule)))
                .Where(correction => correction.Changes.Count > 0))
            {
                attributeChanges.AddRange(changes);
                attributes.Add(drifted);
            }

            if (attributeChanges.Count == 0)
                continue;

            corrected.AddRange(attributes);

            // The class membership values this correction obliges (#492): a corrective export whose drifted
            // attribute is a merged auxiliary class's contribution must add the class in the same export,
            // exactly as ordinary export staging does, or the Connected System refuses the whole modify.
            // Enforce State is on by default, so this is the path most exports to an already-joined object
            // stage through.
            AddClassMembershipCorrections(exportRule, cso, attributes, attributeChanges);

            // Check if any attribute changes have unresolved reference values
            // This is used to defer exports with reference attributes until the referenced objects have been exported
            var hasUnresolvedReferences = attributeChanges.Any(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue));

            // Create the Pending Export
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

        return (pendingExports, corrected);
    }

    /// <summary>
    /// The changes that correct one drifted attribute of an ordinary (direct) mapping: an Add for each expected value the
    /// target lacks and a Remove for each value it holds that is not expected, for a multi-valued target; one Update
    /// carrying the expected value for a single-valued one.
    /// </summary>
    private static List<PendingExportAttributeValueChange> BuildCorrectiveChanges(DriftedAttribute drifted, SyncRule exportRule)
    {
        var changes = new List<PendingExportAttributeValueChange>();

        // Check if this is a multi-valued attribute
        var isMultiValued = drifted.Attribute.AttributePlurality == AttributePlurality.MultiValued;
        var textComparer = AttributeValueComparison.ComparerFor(drifted.Attribute);

        if (isMultiValued)
        {
            // For multi-valued attributes, compute the diff between expected and actual
            var expectedSet = drifted.ExpectedValue as HashSet<object> ?? [];
            var actualSet = drifted.ActualValue as HashSet<object> ?? [];

            // Find values to ADD (in expected but not in actual)
            foreach (var expectedValue in expectedSet)
            {
                var existsInActual = actualSet.Any(av => SingleValueEquals(expectedValue, av, textComparer));
                if (!existsInActual)
                {
                    var addChange = new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        AttributeId = drifted.Attribute.Id,
                        ChangeType = PendingExportAttributeChangeType.Add,
                        SyncRuleId = exportRule.Id,
                        SyncRuleName = exportRule.Name
                    };
                    SetAttributeChangeValue(addChange, expectedValue, drifted.Attribute.Type);
                    changes.Add(addChange);

                    Log.Debug("CreateCorrectiveExportsAsync: Adding value {Value} to attribute {AttrName}",
                        FormatValueForLog(expectedValue), drifted.Attribute.Name);
                }
            }

            // Find values to REMOVE (in actual but not in expected)
            foreach (var actualValue in actualSet)
            {
                var existsInExpected = expectedSet.Any(ev => SingleValueEquals(ev, actualValue, textComparer));
                if (!existsInExpected)
                {
                    var removeChange = new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        AttributeId = drifted.Attribute.Id,
                        ChangeType = PendingExportAttributeChangeType.Remove,
                        SyncRuleId = exportRule.Id,
                        SyncRuleName = exportRule.Name
                    };
                    SetAttributeChangeValue(removeChange, actualValue, drifted.Attribute.Type);
                    changes.Add(removeChange);

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
                ChangeType = PendingExportAttributeChangeType.Update,
                SyncRuleId = exportRule.Id,
                SyncRuleName = exportRule.Name
            };

            // Set the expected value on the change
            SetAttributeChangeValue(change, drifted.ExpectedValue, drifted.Attribute.Type);
            changes.Add(change);
        }

        return changes;
    }

    /// <summary>
    /// Adds the class membership values a corrective export must write, where the Connected System has the
    /// concept (#492). The drift path's changes carry attribute ids without navigations, so the plan is
    /// computed from the drifted attributes themselves rather than from the changes.
    /// </summary>
    private static void AddClassMembershipCorrections(
        SyncRule exportRule,
        ConnectedSystemObject cso,
        List<DriftedAttribute> driftedAttributes,
        List<PendingExportAttributeValueChange> attributeChanges)
    {
        var objectType = exportRule.ConnectedSystemObjectType;
        if (objectType == null)
            return;

        // The names the object already holds values for, and the classes it already carries, both read by
        // attribute id: the drift path loads Connected System Object attribute values without their attribute
        // navigations, so a name-based read would see nothing.
        var attributeNamesById = (objectType.Attributes ?? []).ToDictionary(a => a.Id, a => a.Name);
        var attributesAlreadyOnTheObject = cso.AttributeValues
            .Select(value => attributeNamesById.GetValueOrDefault(value.AttributeId))
            .OfType<string>()
            .ToList();

        var classAttributeName = objectType.Tags
            .FirstOrDefault(tag => tag.Key == ObjectTypeTags.Keys.ClassMembershipAttribute)?.Value;
        if (string.IsNullOrEmpty(classAttributeName))
            return;

        var classAttribute = (objectType.Attributes ?? [])
            .FirstOrDefault(a => a.Name.Equals(classAttributeName, StringComparison.OrdinalIgnoreCase));
        if (classAttribute == null)
        {
            Log.Error("AddClassMembershipCorrections: Object Type '{ObjectType}' declares '{AttributeName}' as its class membership attribute, but no such attribute is in its schema. Class membership cannot be corrected; refresh the schema.",
                objectType.Name, classAttributeName);
            return;
        }

        var currentClasses = cso.AttributeValues
            .Where(value => value.AttributeId == classAttribute.Id && !string.IsNullOrEmpty(value.StringValue))
            .Select(value => value.StringValue!)
            .ToList();

        var plan = ClassMembershipPlanner.Plan(
            objectType,
            currentClasses,
            attributesBeingWritten: driftedAttributes.Select(drifted => drifted.Attribute.Name),
            isCreate: false,
            attributesAlreadyOnTheObject: attributesAlreadyOnTheObject);

        foreach (var className in plan.ClassesToWrite)
        {
            attributeChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(),
                AttributeId = classAttribute.Id,
                StringValue = className,

                // Add rather than Update: class membership is multi-valued, and restating the classes the
                // object already carries is a change the Connected System has no reason to accept.
                ChangeType = PendingExportAttributeChangeType.Add,
                SyncRuleId = exportRule.Id,
                SyncRuleName = exportRule.Name
            });

            Log.Debug("AddClassMembershipCorrections: Corrective export for Object Type '{ObjectType}' will write {AttributeName}: {ClassName}",
                objectType.Name, classAttributeName, className);
        }
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
            case AttributeDataType.Decimal:
                change.DecimalValue = value as decimal?;
                break;
            case AttributeDataType.DateTime:
                change.DateTimeValue = SyncEngine.CanonicalDate(value as DateTime?);
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

            var formattedValues = hashSet.Select(v => LogSanitiser.Sanitise(v?.ToString()) ?? "(null)").Take(5);
            var result = string.Join(", ", formattedValues);
            if (hashSet.Count > 5)
                result += $"... (+{hashSet.Count - 5} more)";
            return $"[{result}] ({hashSet.Count} values)";
        }

        return LogSanitiser.Sanitise(value.ToString()) ?? "(null)";
    }
}
