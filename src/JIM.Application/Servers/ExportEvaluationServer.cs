// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using DynamicExpresso.Exceptions;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data.Repositories;
using JIM.Models.Core;
using JIM.Models.Expressions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;

namespace JIM.Application.Servers;

/// <summary>
/// Evaluates export rules and creates PendingExports when Metaverse Objects change.
/// Implements Q1 decision: evaluate exports immediately when MVO changes.
/// </summary>
public class ExportEvaluationServer
{
    private JimApplication Application { get; }
    private ISyncRepository SyncRepo { get; }
    private IExpressionEvaluator ExpressionEvaluator { get; }
    private ScopingEvaluationServer ScopingEvaluation { get; }

    /// <summary>
    /// The pure decision engine the outbound verdicts are being extracted into (#288). Stateless and
    /// zero-dependency by design, so constructed inline as <see cref="ExportExecutionServer"/> already does.
    /// </summary>
    private readonly ISyncEngine _syncEngine = new SyncEngine();

    internal ExportEvaluationServer(JimApplication application, ISyncRepository syncRepo)
    {
        Application = application;
        SyncRepo = syncRepo;
        ExpressionEvaluator = new DynamicExpressoEvaluator();
        ScopingEvaluation = new ScopingEvaluationServer();
    }

    /// <summary>
    /// Builds a cache of export rules and CSO lookups for optimised batch evaluation.
    /// Call this once at the start of sync, then pass the cache to evaluation methods.
    /// Also loads target CSO attribute values for no-net-change detection during export evaluation.
    /// Every export rule's target system is included, the run's own source system among them: an
    /// outbound rule targeting the system being synchronised is evaluated like any other (#1284), and
    /// excluding the source here would blind the per-page CSO load, making every source-system object
    /// look unprovisioned and no-net-change detection impossible for it.
    /// </summary>
    /// <param name="preloadedSyncRules">Optional pre-loaded Synchronisation Rules to avoid redundant database query.</param>
    /// <returns>A cache object to pass to evaluation methods.</returns>
    public async Task<ExportEvaluationCache> BuildExportEvaluationCacheAsync(
        List<SyncRule>? preloadedSyncRules = null)
    {
        // Use pre-loaded Synchronisation Rules if available, otherwise load from database
        var allSyncRules = preloadedSyncRules
            ?? await SyncRepo.GetAllSyncRulesAsync();

        var exportRules = allSyncRules
            .Where(sr => sr.Enabled && sr.Direction == SyncRuleDirection.Export)
            .ToList();

        var exportRulesByMvoTypeId = exportRules
            .GroupBy(sr => sr.MetaverseObjectTypeId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Every distinct target system, the run's source included (#1284)
        var targetSystemIds = exportRules
            .Select(sr => sr.ConnectedSystemId)
            .Distinct()
            .ToList();

        // CsoLookup and CsoAttributeValues are now populated per-page via RefreshExportEvaluationCacheForPageAsync.
        // This avoids loading ALL target CSOs upfront, which at 100K+ objects consumes multiple GB of memory.
        var emptyCsoLookup = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>();
        var emptyCsoAttributeValues = Array.Empty<ConnectedSystemObjectAttributeValue>()
            .ToLookup(x => (x.ConnectedSystemObject.Id, x.AttributeId));

        Log.Debug("BuildExportEvaluationCacheAsync: Cached {RuleCount} export rules across {TypeCount} MVO types for {SystemCount} target systems (CSO data loaded per-page)",
            exportRules.Count, exportRulesByMvoTypeId.Count, targetSystemIds.Count);

        return new ExportEvaluationCache(exportRulesByMvoTypeId, emptyCsoLookup, emptyCsoAttributeValues, targetSystemIds);
    }

    /// <summary>
    /// Rebuilds the per-page portions of the export evaluation cache (CsoLookup, CsoAttributeValues)
    /// for only the MVOs that changed in the current page. This bounds memory to page size rather than
    /// total dataset size, enabling sync of 100K+ objects without OOM.
    /// </summary>
    /// <param name="cache">The cache to refresh (rules and target system IDs are preserved).</param>
    /// <param name="mvoIds">MVO IDs from the current page's Pending Export evaluations.</param>
    public async Task RefreshExportEvaluationCacheForPageAsync(
        ExportEvaluationCache cache,
        IEnumerable<Guid> mvoIds)
    {
        var mvoIdList = mvoIds.ToList();
        if (mvoIdList.Count == 0 || cache.TargetSystemIds.Count == 0)
        {
            cache.CsoLookup = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>();
            cache.CsoAttributeValues = Array.Empty<ConnectedSystemObjectAttributeValue>()
                .ToLookup(x => (x.ConnectedSystemObject.Id, x.AttributeId));
            return;
        }

        // Load only target CSOs joined to this page's MVOs (AsNoTracking via the repository method)
        cache.CsoLookup = await SyncRepo.GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync(
            mvoIdList, cache.TargetSystemIds);

        // Load attribute values for only these CSOs
        var targetCsoIds = cache.CsoLookup.Values.Select(cso => cso.Id).ToList();
        if (targetCsoIds.Count > 0)
        {
            var csoAttributeValues = await SyncRepo.GetCsoAttributeValuesByCsoIdsAsync(targetCsoIds);
            cache.CsoAttributeValues = csoAttributeValues
                .ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));
        }
        else
        {
            cache.CsoAttributeValues = Array.Empty<ConnectedSystemObjectAttributeValue>()
                .ToLookup(x => (x.ConnectedSystemObject.Id, x.AttributeId));
        }

        Log.Verbose("RefreshExportEvaluationCacheForPageAsync: Loaded {CsoCount} CSOs with attribute values for {MvoCount} MVOs across {SystemCount} target systems",
            cache.CsoLookup.Count, mvoIdList.Count, cache.TargetSystemIds.Count);
    }

    /// <summary>
    /// Evaluates all export rules for an MVO that has changed and creates PendingExports.
    /// This is the main entry point called after inbound sync updates an MVO.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed</param>
    /// <param name="changedAttributes">The attributes that changed on the MVO</param>
    /// <returns>List of PendingExports that were created</returns>
    public async Task<List<PendingExport>> EvaluateExportRulesAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes)
    {
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateExportRulesAsync: MVO {MvoId} has no type set, cannot evaluate export rules", mvo.Id);
            return pendingExports;
        }

        // Get all enabled export rules for this MVO's object type. Rules targeting the system whose
        // synchronisation raised the change are evaluated like any other (#1284): circular sync is
        // prevented at value level by no-net-change detection (an echo of a value the target already
        // holds stages nothing), not by excluding the whole system, which silenced every legitimate
        // writeback into a source system. Q3's original whole-system skip is superseded; see
        // EvaluateExportRulesWithNoNetChangeDetectionAsync for the production path.
        var exportRules = await GetExportRulesForObjectTypeAsync(mvo.Type.Id);

        foreach (var exportRule in exportRules)
        {
            // Check if MVO is in scope for this export rule
            if (!IsMvoInScopeForExportRule(mvo, exportRule))
            {
                Log.Debug("EvaluateExportRulesAsync: MVO {MvoId} is not in scope for export rule {RuleName}",
                    mvo.Id, exportRule.Name);
                continue;
            }

            // Find or create the Pending Export for this MVO → target system
            var pendingExport = await CreateOrUpdatePendingExportAsync(mvo, exportRule, changedAttributes);
            if (pendingExport != null)
            {
                pendingExports.Add(pendingExport);
            }
        }

        return pendingExports;
    }

    /// <summary>
    /// Evaluates if an MVO has fallen out of scope for any export rules and handles deprovisioning.
    /// Called when MVO attributes change to check if scoping criteria no longer match.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed</param>
    /// <param name="workingSet">Optional working set accumulating this run's staging decisions (#288 Phase 1a);
    /// when omitted, a local instance records for this call only.</param>
    /// <returns>List of PendingExports for deprovisioning actions</returns>
    public async Task<List<PendingExport>> EvaluateOutOfScopeExportsAsync(
        MetaverseObject mvo,
        ExportEvaluationWorkingSet? workingSet = null)
    {
        workingSet ??= new ExportEvaluationWorkingSet();
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateOutOfScopeExportsAsync: MVO {MvoId} has no type set, cannot evaluate scope", mvo.Id);
            return pendingExports;
        }

        // Get all enabled export rules for this MVO's object type. Rules targeting the run's own
        // source system are included (#1284): scope-out is a state assertion, and skipping the source
        // silently left objects provisioned that the administrator's scoping said to deprovision.
        var exportRules = await GetExportRulesForObjectTypeAsync(mvo.Type.Id);

        foreach (var exportRule in exportRules)
        {
            // Check if MVO is in scope for this export rule
            if (IsMvoInScopeForExportRule(mvo, exportRule))
            {
                // Still in scope, no deprovisioning needed
                continue;
            }

            // MVO is OUT of scope - check if there's an existing CSO to deprovision
            var existingCso = await SyncRepo.GetConnectedSystemObjectByMetaverseObjectIdAsync(mvo.Id, exportRule.ConnectedSystemId);

            if (existingCso == null)
            {
                // No CSO exists, nothing to deprovision
                continue;
            }

            // The object retrieved is the Metaverse Object's only one in this system whatever its Object Type,
            // so it may not be the one this Rule targets (#1331). Deprovisioning an object this Rule never
            // owned would disconnect or delete another Rule's object, so skip; and skip QUIETLY (#1399),
            // because deprovisioning is always the duty of the Rule targeting the object's own type, and two
            // Rules with disjoint scopes sharing a Connected System make this encounter the configuration's
            // normal state, once per correctly provisioned object per sync. The in-scope staging path keeps
            // the #1331 report, which is where an overlapping-scope misconfiguration actually surfaces.
            var deprovisionConflict = DetectObjectTypeConflict(mvo, exportRule, existingCso);
            if (deprovisionConflict != null)
            {
                Log.Debug("EvaluateOutOfScopeExportsAsync: MVO {MvoId} is out of scope for rule {RuleName}, but its object " +
                    "in system {SystemId} is a '{ExistingType}', not this rule's '{TargetType}'; its own rule owns its lifecycle.",
                    mvo.Id, exportRule.Name, exportRule.ConnectedSystemId,
                    LogSanitiser.Sanitise(deprovisionConflict.ExistingObjectTypeName),
                    LogSanitiser.Sanitise(deprovisionConflict.TargetObjectTypeName));
                continue;
            }

            Log.Information("EvaluateOutOfScopeExportsAsync: MVO {MvoId} is out of scope for export rule {RuleName}. Handling deprovisioning for CSO {CsoId}",
                mvo.Id, exportRule.Name, existingCso.Id);

            // Handle based on OutboundDeprovisionAction
            var pendingExport = await HandleOutboundDeprovisioningAsync(mvo, existingCso, exportRule, workingSet);
            if (pendingExport != null)
            {
                pendingExports.Add(pendingExport);
            }
        }

        return pendingExports;
    }

    /// <summary>
    /// Optimised version of EvaluateExportRulesAsync that uses pre-cached data.
    /// Avoids O(N×M) database queries by using cached export rules and CSO lookups.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed.</param>
    /// <param name="changedAttributes">The attributes that changed on the MVO.</param>
    /// <param name="cache">The pre-loaded cache from BuildExportEvaluationCacheAsync.</param>
    /// <returns>List of PendingExports that were created.</returns>
    public async Task<List<PendingExport>> EvaluateExportRulesAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ExportEvaluationCache cache)
    {
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateExportRulesAsync: MVO {MvoId} has no type set, cannot evaluate export rules", mvo.Id);
            return pendingExports;
        }

        // Get export rules from cache instead of database query. Rules targeting the run's own source
        // system are evaluated like any other (#1284); see the class notes on circular sync prevention.
        if (!cache.ExportRulesByMvoTypeId.TryGetValue(mvo.Type.Id, out var exportRules))
        {
            // No export rules for this MVO type
            return pendingExports;
        }

        foreach (var exportRule in exportRules)
        {
            // Check if MVO is in scope for this export rule
            if (!IsMvoInScopeForExportRule(mvo, exportRule))
            {
                Log.Debug("EvaluateExportRulesAsync: MVO {MvoId} is not in scope for export rule {RuleName}",
                    mvo.Id, exportRule.Name);
                continue;
            }

            // Find or create the Pending Export using cached CSO lookup
            var pendingExport = await CreateOrUpdatePendingExportAsync(mvo, exportRule, changedAttributes, cache);
            if (pendingExport != null)
            {
                pendingExports.Add(pendingExport);
            }
        }

        return pendingExports;
    }

    /// <summary>
    /// Evaluates export rules with no-net-change detection using target CSO attribute cache.
    /// Returns an ExportEvaluationResult that includes both Pending Exports and no-net-change statistics.
    /// No-net-change detection uses target CSO attributes from cache.CsoAttributeValues to avoid creating
    /// duplicate ADD operations for multi-valued attributes (e.g., group members that already exist).
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed.</param>
    /// <param name="changedAttributes">The attributes that changed on the MVO.</param>
    /// <param name="cache">The pre-loaded cache from BuildExportEvaluationCacheAsync (includes target CSO attributes).</param>
    /// <param name="deferSave">When true, Pending Exports are not saved to the database. The caller is responsible
    /// for batch saving the Pending Exports returned in the result. Default is false for backwards compatibility.</param>
    /// <param name="removedAttributes">Optional set of attribute values that were removed (for multi-valued attr handling).</param>
    /// <param name="existingPendingExports">Optional list of Pending Exports already staged for batch save (e.g., from drift detection).
    /// Used to merge attribute changes into existing PEs instead of creating duplicates for the same CSO.
    /// Export evaluation values take precedence over existing values on attribute conflicts.</param>
    /// <returns>ExportEvaluationResult containing Pending Exports and no-net-change counts.</returns>
    public async Task<ExportEvaluationResult> EvaluateExportRulesWithNoNetChangeDetectionAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ExportEvaluationCache cache,
        bool deferSave = false,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null,
        List<PendingExport>? existingPendingExports = null,
        Dictionary<Guid, Dictionary<int, string>>? preResolvedReferences = null,
        bool recallSemantics = false)
    {
        var result = new ExportEvaluationResult();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateExportRulesWithNoNetChangeDetectionAsync: MVO {MvoId} has no type set, cannot evaluate export rules", mvo.Id);
            return result;
        }

        // Get export rules from cache instead of database query
        if (!cache.ExportRulesByMvoTypeId.TryGetValue(mvo.Type.Id, out var exportRules))
        {
            // No export rules for this MVO type
            return result;
        }

        var skippedDueToScope = 0;

        // Build the MVO attribute dictionary once for all export rules — avoids rebuilding
        // per-rule when the same MVO is evaluated against multiple export rules with expressions.
        Dictionary<string, object?>? mvAttributeDictionary = null;

        using var loopSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("EvaluateExportRuleLoop");
        loopSpan.SetTag("ruleCount", exportRules.Count);
        loopSpan.SetTag("mvoId", mvo.Id);

        // Rules targeting the run's own source system are evaluated like any other (#1284). Circular
        // sync is prevented at value level rather than by excluding the system: a flow whose value the
        // target Connected System Object already holds is dropped by no-net-change detection below, so
        // an echo of an imported value stages nothing, while a genuine writeback (a value the source
        // system does not hold, however it was derived) stages normally. The old whole-system skip
        // silenced every writeback into a source system, and consumed the triggering change with it.
        foreach (var exportRule in exportRules)
        {
            // Check if MVO is in scope for this export rule
            if (!IsMvoInScopeForExportRule(mvo, exportRule))
            {
                Log.Debug("EvaluateExportRulesWithNoNetChangeDetectionAsync: MVO {MvoId} is not in scope for export rule {RuleName}",
                    mvo.Id, exportRule.Name);
                skippedDueToScope++;
                continue;
            }

            // Record joined, non-PendingProvisioning target CSOs whose (Metaverse Object, export rule)
            // pair passed the scope gate, whether or not any attribute changes are staged below. The page
            // flush uses these to cancel stale Delete Pending Exports left by an earlier scope-out (#1018).
            // Reference recall is excluded: it is not a desired-state assertion for existence.
            if (!recallSemantics &&
                cache.CsoLookup.TryGetValue((mvo.Id, exportRule.ConnectedSystemId), out var inScopeCso) &&
                inScopeCso.Status != ConnectedSystemObjectStatus.PendingProvisioning)
            {
                result.InScopeJoinedCsoIds.Add(inScopeCso.Id);
            }

            // Flatten the pre-resolved reference values for this rule's target system (reference recall, #908).
            IReadOnlyDictionary<Guid, string>? preResolvedForSystem = null;
            if (preResolvedReferences != null)
            {
                var forSystem = new Dictionary<Guid, string>();
                foreach (var (referencedMvoId, resolvedValue) in preResolvedReferences
                    .Select(kvp => (kvp.Key, Value: kvp.Value.TryGetValue(exportRule.ConnectedSystemId, out var value) ? value : null))
                    .Where(pair => pair.Value != null))
                {
                    forSystem[referencedMvoId] = resolvedValue!;
                }
                preResolvedForSystem = forSystem;
            }

            // Find or create the Pending Export using cached CSO lookup, with no-net-change detection
            using (JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("CreateOrUpdatePendingExport")
                .SetTag("ruleName", exportRule.Name ?? "unnamed")
                .SetTag("targetSystem", exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString()))
            {
                var (pendingExport, provisioningCso, csoAlreadyCurrentCount) = await CreateOrUpdatePendingExportWithNoNetChangeAsync(
                    mvo, exportRule, changedAttributes, cache, deferSave, removedAttributes, existingPendingExports,
                    mvAttributeDictionary, preResolvedForSystem, recallSemantics, result.AttributeFlowErrors,
                    result.ObjectTypeConflicts);

                result.CsoAlreadyCurrentCount += csoAlreadyCurrentCount;

                if (pendingExport != null)
                {
                    result.PendingExports.Add(pendingExport);
                }

                // Collect provisioning CSOs for batch creation when deferSave is true, recording
                // which export Synchronisation Rule caused each provisioning so the worker can
                // attribute the Provisioned sync outcome to it (#1085).
                if (provisioningCso != null)
                {
                    result.ProvisioningCsosToCreate.Add(provisioningCso);
                    result.ProvisioningSyncRulesByCsoId[provisioningCso.Id] = exportRule;
                }
            }
        }

        loopSpan.SetTag("skippedDueToScope", skippedDueToScope);
        loopSpan.SetTag("pendingExportsCreated", result.PendingExports.Count);
        loopSpan.SetSuccess();

        return result;
    }

    /// <summary>
    /// Optimised version of EvaluateOutOfScopeExportsAsync that uses pre-cached data.
    /// Avoids O(N×M) database queries by using cached export rules and CSO lookups.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed.</param>
    /// <param name="cache">The pre-loaded cache from BuildExportEvaluationCacheAsync.</param>
    /// <param name="workingSet">Optional working set accumulating this run's staging decisions (#288 Phase 1a);
    /// when omitted, a local instance records for this call only.</param>
    /// <returns>List of PendingExports for deprovisioning actions.</returns>
    public async Task<List<PendingExport>> EvaluateOutOfScopeExportsAsync(
        MetaverseObject mvo,
        ExportEvaluationCache cache,
        ExportEvaluationWorkingSet? workingSet = null)
    {
        workingSet ??= new ExportEvaluationWorkingSet();
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateOutOfScopeExportsAsync: MVO {MvoId} has no type set, cannot evaluate scope", mvo.Id);
            return pendingExports;
        }

        // Get export rules from cache instead of database query. Rules targeting the run's own source
        // system are included (#1284): scope-out is a state assertion, and skipping the source silently
        // left objects provisioned that the administrator's scoping said to deprovision.
        if (!cache.ExportRulesByMvoTypeId.TryGetValue(mvo.Type.Id, out var exportRules))
        {
            // No export rules for this MVO type
            return pendingExports;
        }

        foreach (var exportRule in exportRules)
        {

            // Check if MVO is in scope for this export rule
            if (IsMvoInScopeForExportRule(mvo, exportRule))
            {
                // Still in scope, no deprovisioning needed
                continue;
            }

            // MVO is OUT of scope - check if there's an existing CSO to deprovision using cache
            var lookupKey = (mvo.Id, exportRule.ConnectedSystemId);
            if (!cache.CsoLookup.TryGetValue(lookupKey, out var existingCso))
            {
                // No CSO exists, nothing to deprovision
                continue;
            }

            // The lookup carries no Object Type, so the object found may belong to a different Object Type
            // than this Rule targets (#1331). Deprovisioning an object this Rule never owned would disconnect
            // or delete another Rule's object, so skip; and skip QUIETLY (#1399), because deprovisioning is
            // always the duty of the Rule targeting the object's own type, and two Rules with disjoint scopes
            // sharing a Connected System make this encounter the configuration's normal state, once per
            // correctly provisioned object per sync. Reporting it here raised a warning RPEI against every
            // clean run of such a configuration. The in-scope staging path keeps the #1331 report, which is
            // where an overlapping-scope misconfiguration actually surfaces.
            var deprovisionConflict = DetectObjectTypeConflict(mvo, exportRule, existingCso);
            if (deprovisionConflict != null)
            {
                Log.Debug("EvaluateOutOfScopeExportsAsync: MVO {MvoId} is out of scope for rule {RuleName}, but its object " +
                    "in system {SystemId} is a '{ExistingType}', not this rule's '{TargetType}'; its own rule owns its lifecycle.",
                    mvo.Id, exportRule.Name, exportRule.ConnectedSystemId,
                    LogSanitiser.Sanitise(deprovisionConflict.ExistingObjectTypeName),
                    LogSanitiser.Sanitise(deprovisionConflict.TargetObjectTypeName));
                continue;
            }

            Log.Information("EvaluateOutOfScopeExportsAsync: MVO {MvoId} is out of scope for export rule {RuleName}. Handling deprovisioning for CSO {CsoId}",
                mvo.Id, exportRule.Name, existingCso.Id);

            // Handle based on OutboundDeprovisionAction
            var pendingExport = await HandleOutboundDeprovisioningAsync(mvo, existingCso, exportRule, workingSet);
            if (pendingExport != null)
            {
                pendingExports.Add(pendingExport);
            }
        }

        return pendingExports;
    }

    /// <summary>
    /// Handles deprovisioning based on the Synchronisation Rule's OutboundDeprovisionAction setting.
    /// The verdict comes from the pure engine (#288 extraction); this method is orchestration: apply the
    /// join-break mutations, persist, and stage the Delete export where the engine says so.
    /// </summary>
    private async Task<PendingExport?> HandleOutboundDeprovisioningAsync(
        MetaverseObject mvo,
        ConnectedSystemObject cso,
        SyncRule exportRule,
        ExportEvaluationWorkingSet workingSet)
    {
        // Verdict call only: the existing Pending Export is resolved inside the staging path, where the
        // engine is consulted again with it (a pure function, so the second call costs nothing).
        var decision = _syncEngine.DecideOutOfScopeDeprovisioning(exportRule, existingPendingExport: null);
        switch (decision.Action)
        {
            case OutOfScopeDeprovisioningAction.Disconnect:
                // Break the join between CSO and MVO, but leave CSO in the target system
                Log.Information("HandleOutboundDeprovisioningAsync: Disconnecting CSO {CsoId} from MVO {MvoId} (OutboundDeprovisionAction=Disconnect)",
                    cso.Id, mvo.Id);

                // Break the join
                cso.MetaverseObject = null;
                cso.MetaverseObjectId = null;
                cso.JoinType = ConnectedSystemObjectJoinType.NotJoined;
                cso.DateJoined = null;

                // Remove from MVO's collection
                mvo.ConnectedSystemObjects.Remove(cso);

                // Update the CSO in the database
                await SyncRepo.UpdateConnectedSystemObjectAsync(cso);

                // Was that the last connector? (Asked after the removal above, per the engine's contract.)
                if (_syncEngine.ShouldMarkLastConnectorDisconnected(mvo))
                {
                    mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;
                    Log.Information("HandleOutboundDeprovisioningAsync: MVO {MvoId} has no more connectors. LastConnectorDisconnectedDate set to {Date}",
                        mvo.Id, mvo.LastConnectorDisconnectedDate);
                }

                return null; // No Pending Export needed for disconnect

            case OutOfScopeDeprovisioningAction.StageDeleteExport:
                // Create (or reclaim) a Delete PendingExport for this CSO. The helper handles
                // the collision case where a previous export's PE is still attached to the CSO
                // because the next confirming import hasn't run yet to reconcile it away.
                Log.Information("HandleOutboundDeprovisioningAsync: Ensuring delete PendingExport for CSO {CsoId} (OutboundDeprovisionAction=Delete)",
                    cso.Id);

                return await EnsureDeletePendingExportAsync(cso, mvo.Id, exportRule, workingSet);

            default:
                Log.Warning("HandleOutboundDeprovisioningAsync: Unknown OutboundDeprovisionAction {Action} for rule {RuleName}",
                    exportRule.OutboundDeprovisionAction, exportRule.Name);
                return null;
        }
    }

    /// <summary>
    /// Evaluates export rules for an MVO that is being deleted.
    /// Deprovisioning is driven by each matching export Synchronisation Rule's
    /// OutboundDeprovisionAction, regardless of the CSO's join type (issue #655).
    /// Stores the secondary external ID (e.g., DN for LDAP) in AttributeValueChanges
    /// so the delete export can be processed even after the CSO is deleted.
    /// Also disconnects CSOs from the MVO to prevent spurious sync processing.
    /// </summary>
    /// <param name="mvo">The Metaverse Object about to be deleted.</param>
    /// <param name="exportEvaluationCache">Optional pre-built cache carrying the export rules;
    /// when omitted, the enabled export Synchronisation Rules are loaded from the repository.</param>
    public async Task<List<PendingExport>> EvaluateMvoDeletionAsync(
        MetaverseObject mvo,
        ExportEvaluationCache? exportEvaluationCache = null,
        ExportEvaluationWorkingSet? workingSet = null)
        => await EvaluateMvoDeletionsAsync([mvo], exportEvaluationCache, workingSet);

    /// <summary>
    /// Set-based form of <see cref="EvaluateMvoDeletionAsync(MetaverseObject, ExportEvaluationCache?)"/>
    /// (issue #993): evaluates all the given MVOs' deletions with one CSO fetch, one existing
    /// Pending Export lookup, one bulk Pending Export replace/create, and one CSO disconnect
    /// statement, instead of several round trips per object. Per-object semantics are identical:
    /// delete Pending Exports are ensured for CSOs matched by an export Synchronisation Rule whose
    /// OutboundDeprovisionAction is Delete (issue #655; reusing an existing Delete PE, replacing
    /// any other change type), and every joined CSO is disconnected from its MVO.
    /// </summary>
    /// <param name="mvos">The Metaverse Objects about to be deleted.</param>
    /// <param name="exportEvaluationCache">Optional pre-built cache carrying the export rules;
    /// when omitted, the enabled export Synchronisation Rules are loaded from the repository.</param>
    /// <returns>The Delete Pending Exports for the CSOs whose export Synchronisation Rule action is
    /// Delete: newly created ones plus any existing Delete Pending Exports that were reused.</returns>
    public async Task<List<PendingExport>> EvaluateMvoDeletionsAsync(
        IReadOnlyCollection<MetaverseObject> mvos,
        ExportEvaluationCache? exportEvaluationCache = null,
        ExportEvaluationWorkingSet? workingSet = null)
    {
        // The working set accumulates this run's decisions so a later evaluation path touching the same CSO
        // consults a dictionary rather than reading this run's own writes back from the database (#288 Phase 1a).
        // Callers that evaluate once may omit it; the local instance then simply records and is discarded.
        workingSet ??= new ExportEvaluationWorkingSet();

        var pendingExports = new List<PendingExport>();
        if (mvos.Count == 0)
            return pendingExports;

        // One query for all CSOs joined to any of the MVOs (lean shape: external ID attribute
        // values only, which is all the delete PE stamping below needs).
        var csosByMvo = await SyncRepo.GetConnectedSystemObjectsForMvoDeletionAsync(
            mvos.Select(m => m.Id).ToList());
        if (csosByMvo.Count == 0)
            return pendingExports;

        // Issue #655: deprovisioning is driven by each matching export Synchronisation Rule's
        // OutboundDeprovisionAction, not by the CSO's join type. A rule matches a CSO on the full
        // (Connected System, Connected System Object Type, Metaverse Object Type) triple; Delete
        // wins when multiple matching rules disagree. CSOs with no matching rule, or whose rules
        // all say Disconnect, are still disconnected to prevent spurious sync processing after the
        // MVO is deleted, but nothing is exported to the Connected System.
        var exportRulesByMvoTypeId = exportEvaluationCache?.ExportRulesByMvoTypeId
            ?? await GetExportRulesByMvoTypeIdAsync();
        var mvoTypeIdsByMvoId = mvos.ToDictionary(m => m.Id, m => m.Type?.Id);

        // The fetched dictionary is iterated directly: its keys are exactly the given MVOs that
        // have joined CSOs, so no per-MVO lookup or implicit filtering is needed. The verdict per CSO comes
        // from the pure engine (#288 extraction); this loop is orchestration: resolve inputs, call, log, sort
        // the CSOs into their fates. The engine is called here without the existing Pending Export (verdict
        // only) and again below with it once the batch pre-read has run; it is a pure function, so the second
        // call costs nothing and keeps one implementation of the semantics.
        var csoIdsToDisconnect = new List<Guid>();
        var csosToDelete = new List<(ConnectedSystemObject Cso, Guid MvoId)>();
        var disconnectedByRuleCount = 0;
        var noMatchingRuleCount = 0;
        foreach (var (mvoId, joinedCsos) in csosByMvo)
        {
            if (!mvoTypeIdsByMvoId.TryGetValue(mvoId, out var mvoTypeId) || mvoTypeId == null)
            {
                mvoTypeId = null;
                Log.Warning("EvaluateMvoDeletionsAsync: MVO {MvoId} has no Type set; cannot match export Synchronisation Rules. Its CSOs will be disconnected only.",
                    mvoId);
            }

            foreach (var cso in joinedCsos)
            {
                csoIdsToDisconnect.Add(cso.Id);

                var verdict = _syncEngine.DecideMvoDeletionExport(cso, mvoTypeId, exportRulesByMvoTypeId, existingPendingExport: null);
                switch (verdict.Reason)
                {
                    case MvoDeletionExportReason.NoMetaverseObjectType:
                    case MvoDeletionExportReason.NoMatchingExportRule:
                        noMatchingRuleCount++;
                        workingSet.RecordDeleteDecision(cso.Id, verdict);
                        Log.Debug("EvaluateMvoDeletionsAsync: No export Synchronisation Rule matches CSO {CsoId} (system {SystemId}, object type {TypeId}); disconnecting only",
                            cso.Id, cso.ConnectedSystemId, cso.TypeId);
                        continue;
                    case MvoDeletionExportReason.MatchingRulesDeclineDeletion:
                        disconnectedByRuleCount++;
                        workingSet.RecordDeleteDecision(cso.Id, verdict);
                        Log.Debug("EvaluateMvoDeletionsAsync: CSO {CsoId} matches export Synchronisation Rule(s) whose action is Disconnect; disconnecting only",
                            cso.Id);
                        continue;
                }

                if (verdict.RulesConflicted)
                {
                    Log.Information("EvaluateMvoDeletionsAsync: {RuleCount} export Synchronisation Rules match CSO {CsoId} with conflicting deprovisioning actions; Delete wins via rule '{RuleName}'",
                        verdict.MatchingRuleCount, cso.Id, LogSanitiser.Sanitise(verdict.WinningRule!.Name));
                }

                Log.Information("EvaluateMvoDeletionsAsync: Staging delete Pending Export for CSO {CsoId} (join type {JoinType}) per export Synchronisation Rule '{RuleName}'",
                    cso.Id, cso.JoinType, LogSanitiser.Sanitise(verdict.WinningRule!.Name));
                csosToDelete.Add((cso, mvoId));
            }
        }

        if (csosToDelete.Count > 0)
        {
            // Delete-PE collision policy, set-based. PendingExports has a unique index on
            // ConnectedSystemObjectId, so only one PE per CSO is allowed: an existing Delete PE
            // is reused; any other change type is deleted and replaced with a Delete PE (the same
            // policy EnsureDeletePendingExportAsync applies on the singular path).
            var existingPesByCsoId = await SyncRepo.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(
                csosToDelete.Select(p => p.Cso.Id).ToList());

            var replacedPeCsoIds = new List<Guid>();
            var newPendingExports = new List<PendingExport>();
            foreach (var (cso, mvoId) in csosToDelete)
            {
                // The definitive decision, now that the existing Pending Export is known; recorded in the
                // working set so anything else this run asks about the CSO gets this answer without a query.
                // A Delete Pending Export this run already staged (working set) takes precedence over the
                // batched pre-read: it is this run's own write, and by construction the reuse case.
                var existingPe = workingSet.TryGetStagedDeleteExport(cso.Id, out var stagedPe)
                    ? stagedPe
                    : existingPesByCsoId.GetValueOrDefault(cso.Id);
                var decision = _syncEngine.DecideMvoDeletionExport(
                    cso, mvoTypeIdsByMvoId[mvoId], exportRulesByMvoTypeId, existingPe);
                workingSet.RecordDeleteDecision(cso.Id, decision);

                if (decision.ExistingPendingExportToReuse is { } reusedPe)
                {
                    Log.Information("EvaluateMvoDeletionsAsync: Delete PendingExport {ExistingPeId} already exists for CSO {CsoId} (status: {Status}). Reusing.",
                        reusedPe.Id, cso.Id, reusedPe.Status);
                    workingSet.RecordStagedDeleteExport(cso.Id, reusedPe);
                    pendingExports.Add(reusedPe);
                    continue;
                }

                if (decision.MustReplaceExistingPendingExport)
                {
                    Log.Information("EvaluateMvoDeletionsAsync: Replacing existing {ChangeType} PendingExport {ExistingPeId} for CSO {CsoId} with Delete PE",
                        existingPe!.ChangeType, existingPe.Id, cso.Id);
                    replacedPeCsoIds.Add(cso.Id);
                }

                // The secondary external ID (e.g. DN for LDAP) was captured on the decision, because the CSO
                // is disconnected right after this and may be deleted by housekeeping before the export runs;
                // connectors like LDAP need the DN preserved on the PE to perform the actual delete.
                var attributeValueChanges = new List<PendingExportAttributeValueChange>();
                if (decision.SecondaryExternalIdAttribute != null && decision.SecondaryExternalIdValue != null)
                {
                    attributeValueChanges.Add(new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        Attribute = decision.SecondaryExternalIdAttribute,
                        AttributeId = decision.SecondaryExternalIdAttribute.Id,
                        StringValue = decision.SecondaryExternalIdValue,
                        ChangeType = PendingExportAttributeChangeType.Update
                    });

                    Log.Debug("EvaluateMvoDeletionsAsync: Will store secondary external ID '{Value}' (attr {AttrName}) on delete PE for CSO {CsoId}",
                        LogSanitiser.Sanitise(decision.SecondaryExternalIdValue), decision.SecondaryExternalIdAttribute.Name, cso.Id);
                }
                else
                {
                    Log.Warning("EvaluateMvoDeletionsAsync: CSO {CsoId} has no secondary external ID - delete export may fail if CSO is deleted before export",
                        cso.Id);
                }

                // Only set the FK property (ConnectedSystemObjectId), NOT the navigation property,
                // matching EnsureDeletePendingExportAsync.
                var pendingExport = new PendingExport
                {
                    Id = Guid.NewGuid(),
                    ConnectedSystemId = cso.ConnectedSystemId,
                    ConnectedSystemObjectId = cso.Id,
                    ChangeType = PendingExportChangeType.Delete,
                    Status = PendingExportStatus.Pending,
                    SourceMetaverseObjectId = mvoId,
                    CreatedAt = DateTime.UtcNow
                };
                foreach (var avc in attributeValueChanges)
                    pendingExport.AttributeValueChanges.Add(avc);

                newPendingExports.Add(pendingExport);
                Log.Information("EvaluateMvoDeletionsAsync: Delete PendingExport {ExportId} staged for CSO {CsoId} in system {SystemId}",
                    pendingExport.Id, cso.Id, cso.ConnectedSystemId);
            }

            if (replacedPeCsoIds.Count > 0)
                await SyncRepo.DeletePendingExportsByConnectedSystemObjectIdsAsync(replacedPeCsoIds);

            if (newPendingExports.Count > 0)
            {
                await SyncRepo.CreatePendingExportsAsync(newPendingExports);
                pendingExports.AddRange(newPendingExports);

                // Recorded after the bulk create succeeds, so a failed write cannot leave the working set
                // claiming Pending Exports that were never persisted (the per-MVO fallback re-evaluates with
                // this same working set and must not reuse a phantom).
                foreach (var newPendingExport in newPendingExports)
                    workingSet.RecordStagedDeleteExport(newPendingExport.ConnectedSystemObjectId!.Value, newPendingExport);
            }
        }

        // Disconnect every joined CSO from its MVO in one statement, to prevent spurious sync
        // processing after the MVOs are deleted. The confirming import will mark target CSOs as
        // Obsolete when the objects are deleted from the target.
        if (csoIdsToDisconnect.Count > 0)
        {
            await SyncRepo.DisconnectConnectedSystemObjectsAsync(csoIdsToDisconnect);
            Log.Information("EvaluateMvoDeletionsAsync: Disconnected {CsoCount} CSO(s) across {MvoCount} MVO(s); {PeCount} delete Pending Export(s) ensured, {ByRuleCount} disconnect-only by rule action, {NoRuleCount} with no matching export Synchronisation Rule",
                csoIdsToDisconnect.Count, mvos.Count, pendingExports.Count, disconnectedByRuleCount, noMatchingRuleCount);
        }

        return pendingExports;
    }

    /// <summary>
    /// Loads all enabled export Synchronisation Rules grouped by Metaverse Object Type ID.
    /// Fallback for deletion-evaluation callers with no <see cref="ExportEvaluationCache"/>
    /// (the housekeeping grace-period path); sync task processors pass their run-scoped cache.
    /// </summary>
    private async Task<Dictionary<int, List<SyncRule>>> GetExportRulesByMvoTypeIdAsync()
    {
        var allSyncRules = await SyncRepo.GetAllSyncRulesAsync();
        return allSyncRules
            .Where(sr => sr.Enabled && sr.Direction == SyncRuleDirection.Export)
            .GroupBy(sr => sr.MetaverseObjectTypeId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Creates a Delete PendingExport for the given CSO, handling collisions with any
    /// existing PendingExport already attached to that CSO.
    /// </summary>
    /// <remarks>
    /// PendingExports has a unique index on ConnectedSystemObjectId (filtered NOT NULL),
    /// so only one PE per CSO is allowed at a time. After a successful export the PE row
    /// stays attached to the CSO until the next import on the target system reconciles
    /// it away; if a sync fires the deprovision cascade or an MVO deletion in that window
    /// (overlapping schedules, late or failed imports), a naive insert hits the unique
    /// constraint. This helper centralises the collision policy so every Delete-PE-creation
    /// path observes it: if an existing Delete PE is found it's reused; any other change
    /// type is deleted and replaced with the Delete PE.
    /// </remarks>
    /// <param name="cso">The CSO to deprovision.</param>
    /// <param name="sourceMetaverseObjectId">The MVO that triggered the deprovisioning, recorded on the PE for causality tracing.</param>
    /// <param name="exportRule">The export Synchronisation Rule whose Delete action asked for this, consulted
    /// (via the pure engine) for the collision policy once the existing Pending Export is known.</param>
    /// <param name="workingSet">The run's working set, consulted for a Delete Pending Export this run already
    /// staged for the CSO before the per-object database read is paid (#288 Phase 1a).</param>
    /// <param name="attributeValueChanges">
    /// Optional attribute value changes to attach to a freshly-created PE (for example, the
    /// secondary external ID so a connector can still resolve the target DN after the CSO
    /// is detached from the MVO). Ignored when reusing an existing Delete PE.
    /// </param>
    /// <returns>The Delete PendingExport for the CSO — either the existing one reused, or a newly created one.</returns>
    private async Task<PendingExport> EnsureDeletePendingExportAsync(
        ConnectedSystemObject cso,
        Guid sourceMetaverseObjectId,
        SyncRule exportRule,
        ExportEvaluationWorkingSet workingSet,
        List<PendingExportAttributeValueChange>? attributeValueChanges = null)
    {
        // This run's own writes are answered from the working set: a Delete Pending Export staged earlier in
        // the run is by construction the reuse case, so the per-object read below is only paid for Pending
        // Exports that existed before the run began.
        if (workingSet.TryGetStagedDeleteExport(cso.Id, out var stagedPe))
        {
            Log.Information("EnsureDeletePendingExportAsync: Delete PendingExport {ExistingPeId} already staged for CSO {CsoId} this run (status: {Status}). Reusing without a database read.",
                stagedPe.Id, cso.Id, stagedPe.Status);
            return stagedPe;
        }

        // Lean fetch (issue #986): this method only reads ChangeType/Id/Status off the existing
        // Pending Export and passes it to DeletePendingExportAsync, which needs AttributeValueChanges
        // loaded for EF-tracked child-row disposal. The heavy fetch also loaded the CSO's and source
        // Metaverse Object's full attribute value graphs, which for a large group CSO (group
        // deprovisioning) runs into the hundreds of thousands of rows, none of them read here.
        var existingPe = await SyncRepo.GetPendingExportLightweightByConnectedSystemObjectIdAsync(cso.Id);

        // The definitive decision, now that the existing Pending Export is known: the engine owns the
        // one-Pending-Export-per-CSO collision policy (reuse a Delete, replace anything else).
        var decision = _syncEngine.DecideOutOfScopeDeprovisioning(exportRule, existingPe);

        if (decision.ExistingPendingExportToReuse is { } reusedPe)
        {
            Log.Information("EnsureDeletePendingExportAsync: Delete PendingExport {ExistingPeId} already exists for CSO {CsoId} (status: {Status}). Reusing.",
                reusedPe.Id, cso.Id, reusedPe.Status);
            workingSet.RecordStagedDeleteExport(cso.Id, reusedPe);
            return reusedPe;
        }

        if (decision.MustReplaceExistingPendingExport)
        {
            Log.Information("EnsureDeletePendingExportAsync: Replacing existing {ChangeType} PendingExport {ExistingPeId} for CSO {CsoId} with Delete PE",
                existingPe!.ChangeType, existingPe.Id, cso.Id);
            await SyncRepo.DeletePendingExportAsync(existingPe);
        }

        // Only set the FK property (ConnectedSystemObjectId), NOT the navigation property (ConnectedSystemObject).
        // Setting both can cause EF Core change tracker conflicts where the FK gets overwritten.
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = cso.ConnectedSystemId,
            ConnectedSystemObjectId = cso.Id,
            ChangeType = PendingExportChangeType.Delete,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = sourceMetaverseObjectId,
            CreatedAt = DateTime.UtcNow
        };

        if (attributeValueChanges != null)
        {
            foreach (var avc in attributeValueChanges)
                pendingExport.AttributeValueChanges.Add(avc);
        }

        await SyncRepo.CreatePendingExportAsync(pendingExport);

        // Recorded after the create succeeds, so the working set never claims a Pending Export that a failed
        // write left unpersisted.
        workingSet.RecordStagedDeleteExport(cso.Id, pendingExport);

        Log.Information("EnsureDeletePendingExportAsync: Created delete PendingExport {ExportId} for CSO {CsoId} in system {SystemId}",
            pendingExport.Id, cso.Id, cso.ConnectedSystemId);

        return pendingExport;
    }

    #region Reference Recall (issue #908)

    /// <summary>
    /// Captures the state reference recall needs BEFORE Metaverse Objects are deleted: which other
    /// Metaverse Objects reference them (deletion nulls the reference FKs), and the per-system
    /// resolved reference values of the deletion candidates (deletion disconnects their Connected
    /// System Objects, after which export-time reference resolution can never succeed for them).
    /// Call before <see cref="EvaluateMvoDeletionAsync"/>; pass the result to
    /// <see cref="StageReferenceRecallExportsAsync"/> after the deletions have been performed.
    /// </summary>
    /// <param name="deletionCandidateMvoIds">The Metaverse Objects about to be deleted.</param>
    public async Task<ReferenceRecallContext> CaptureReferenceRecallContextAsync(
        IReadOnlyCollection<Guid> deletionCandidateMvoIds)
    {
        var context = new ReferenceRecallContext();
        if (deletionCandidateMvoIds.Count == 0)
            return context;

        context.Candidates.AddRange(
            await SyncRepo.GetMetaverseObjectReferenceRecallCandidatesAsync(deletionCandidateMvoIds));
        if (context.Candidates.Count == 0)
            return context;

        // Resolve the deletion candidates' per-system reference values now, while their CSOs are
        // still joined. Preference order matches export-time resolution: secondary external ID
        // (for example the DN for LDAP) first, else the primary external ID. One bulk CSO fetch
        // for all referenced MVOs (issue #993); the lean shape loads exactly the external ID
        // attribute values this resolution reads.
        var referencedIds = context.Candidates
            .Select(c => c.ReferencedMetaverseObjectId)
            .ToHashSet();

        var joinedCsosByReferencedId = await SyncRepo.GetConnectedSystemObjectsForMvoDeletionAsync(referencedIds);
        foreach (var (referencedId, joinedCsos) in joinedCsosByReferencedId)
        {
            foreach (var cso in joinedCsos)
            {
                // Record the joined CSO id per system regardless of value resolution: the set-based
                // fast path (#1003) matches target-side reference rows by these ids, and a match
                // without a resolvable value must be counted as dropped, not silently missed.
                if (!context.DeletedCsoIdsBySystem.TryGetValue(referencedId, out var csoIdsBySystem))
                {
                    csoIdsBySystem = new Dictionary<int, Guid>();
                    context.DeletedCsoIdsBySystem[referencedId] = csoIdsBySystem;
                }
                csoIdsBySystem[cso.ConnectedSystemId] = cso.Id;

                var resolvedValue = ResolveCsoReferenceValue(cso);
                if (resolvedValue == null)
                    continue;

                if (!context.ResolvedReferenceValuesBySystem.TryGetValue(referencedId, out var bySystem))
                {
                    bySystem = new Dictionary<int, string>();
                    context.ResolvedReferenceValuesBySystem[referencedId] = bySystem;
                }
                bySystem[cso.ConnectedSystemId] = resolvedValue;
            }
        }

        Log.Debug("CaptureReferenceRecallContextAsync: {CandidateCount} inbound reference(s) held by other " +
            "Metaverse Objects across {DeletionCount} deletion candidate(s)",
            context.Candidates.Count, deletionCandidateMvoIds.Count);

        return context;
    }

    /// <summary>
    /// Resolves the reference value a target system uses for a CSO: the secondary external ID
    /// (for example the DN for LDAP) when available, else the primary external ID. The same
    /// preference order export execution's reference resolution uses.
    /// </summary>
    internal static string? ResolveCsoReferenceValue(ConnectedSystemObject cso)
    {
        var resolvedAttr =
            cso.AttributeValues.FirstOrDefault(av => av.Attribute?.IsSecondaryExternalId == true) ??
            cso.AttributeValues.FirstOrDefault(av => av.Attribute?.IsExternalId == true);

        return resolvedAttr?.ToReferenceValueString();
    }

    /// <summary>
    /// Stages membership-removal Pending Exports for Metaverse Objects that referenced now-deleted
    /// Metaverse Objects (reference recall, #908). Without this, a target system without referential
    /// integrity keeps the deleted object (for example a leaver) as a group member forever: the
    /// referencing group's CSOs never change, so the unchanged-skip means no sync ever re-evaluates
    /// them. Each referencing Metaverse Object is evaluated ONCE with every reference it lost in this
    /// batch, so a group losing many members in one run gets one Pending Export carrying all removals.
    /// Recall only updates existing target objects; provisioning remains the job of normal sync.
    /// </summary>
    /// <param name="context">State captured by <see cref="CaptureReferenceRecallContextAsync"/> before deletion.</param>
    /// <param name="deletedMvoIds">The Metaverse Objects that were actually deleted (a candidate can be
    /// skipped, for example when re-joined mid-page); only their references are recalled.</param>
    public async Task<ReferenceRecallResult> StageReferenceRecallExportsAsync(
        ReferenceRecallContext context,
        IReadOnlyCollection<Guid> deletedMvoIds,
        ExportEvaluationCache? recallCache = null)
    {
        var result = new ReferenceRecallResult();
        if (context.Candidates.Count == 0 || deletedMvoIds.Count == 0)
            return result;

        var deletedIds = deletedMvoIds as HashSet<Guid> ?? [.. deletedMvoIds];
        var byReferencingMvo = context.Candidates
            .Where(c => deletedIds.Contains(c.ReferencedMetaverseObjectId))
            .GroupBy(c => c.ReferencingMetaverseObjectId)
            .ToDictionary(g => g.Key, g => g.ToList());
        if (byReferencingMvo.Count == 0)
            return result;

        // Run-scoped cache preferred (#1003): the sync processors build one recall cache per run.
        // Callers without one (housekeeping) build it ad hoc.
        var cache = recallCache;
        if (cache == null)
        {
            using var cacheSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("RecallBuildCache");
            cache = await BuildExportEvaluationCacheAsync();
            cacheSpan.SetTag("ruleCount", cache.ExportRulesByMvoTypeId.Values.Sum(rules => rules.Count));
            cacheSpan.SetSuccess();
        }

        // Classify the rule shapes once: types whose recall-relevant flows are all direct
        // single-source reference mappings take the set-based fast path; types where a candidate
        // attribute is sourced through an expression or multi-source chain keep the full
        // per-object evaluation. The split depends only on configuration, never on data.
        var candidateAttributeIds = byReferencingMvo.Values
            .SelectMany(candidates => candidates)
            .Select(candidate => candidate.MetaverseAttributeId)
            .ToHashSet();
        var plan = BuildReferenceRecallRulePlan(cache, candidateAttributeIds);

        // Lean summaries route each referencing object to a path and carry the scoping-criteria
        // attribute values and display names; the fast path never loads anything heavier.
        Dictionary<Guid, MetaverseObjectRecallSummary> summariesById;
        using (var metadataSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("RecallReferencingMetadataFetch"))
        {
            var summaries = await SyncRepo.GetMetaverseObjectRecallSummariesAsync(
                byReferencingMvo.Keys.ToList(), plan.ScopingAttributeIds.ToList());
            summariesById = summaries.ToDictionary(summary => summary.Id);
            metadataSpan.SetTag("referencingCount", summaries.Count);
            metadataSpan.SetTag("scopingAttributeCount", plan.ScopingAttributeIds.Count);
            metadataSpan.SetSuccess();
        }
        foreach (var summary in summariesById.Values)
            result.ReferencingObjectDisplayNames[summary.Id] = summary.DisplayName;

        // Summaries only exist for referencing objects that still exist (a missing summary
        // means a raced deletion), so routing iterates the summaries rather than the keys.
        var fastMvoIds = new List<Guid>();
        var fallbackMvoIds = new List<Guid>();
        foreach (var (referencingId, summary) in summariesById)
        {
            if (plan.FallbackTypeIds.Contains(summary.TypeId))
                fallbackMvoIds.Add(referencingId);
            else
                fastMvoIds.Add(referencingId);
        }

        var stagedPendingExports = new List<PendingExport>();

        if (fastMvoIds.Count > 0)
        {
            using var fastSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("RecallFastPath");
            fastSpan.SetTag("fastCount", fastMvoIds.Count);
            await StageRecallFastPathAsync(context, deletedIds, byReferencingMvo, plan, summariesById,
                fastMvoIds, stagedPendingExports, result);
            fastSpan.SetSuccess();
        }

        if (fallbackMvoIds.Count > 0)
        {
            using var fallbackSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("RecallFallbackEvaluate");
            fallbackSpan.SetTag("fallbackCount", fallbackMvoIds.Count);
            await StageRecallFallbackAsync(context, deletedIds, byReferencingMvo, cache,
                fallbackMvoIds, stagedPendingExports, result);
            fallbackSpan.SetSuccess();
        }

        if (stagedPendingExports.Count > 0)
        {
            using var persistSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("RecallPersistPendingExports");

            // Same delete-then-create pattern as the sync flush: prevents unique-index collisions on
            // ConnectedSystemObjectId. Pre-existing PEs were already merged into these instances.
            var csoIds = stagedPendingExports
                .Where(pe => pe.ConnectedSystemObjectId.HasValue)
                .Select(pe => pe.ConnectedSystemObjectId!.Value)
                .Distinct()
                .ToList();
            if (csoIds.Count > 0)
                await SyncRepo.DeletePendingExportsByConnectedSystemObjectIdsAsync(csoIds);

            await SyncRepo.CreatePendingExportsAsync(stagedPendingExports);

            persistSpan.SetTag("peCount", stagedPendingExports.Count);
            persistSpan.SetTag("changeCount", stagedPendingExports.Sum(pe => pe.AttributeValueChanges.Count));
            persistSpan.SetSuccess();
        }

        result.StagedPendingExports.AddRange(stagedPendingExports);
        result.PendingExportsStaged = stagedPendingExports.Count;
        result.RemovalChangesStaged = stagedPendingExports.Sum(pe => pe.AttributeValueChanges.Count);
        return result;
    }

    /// <summary>
    /// The set-based recall fast path (#1003): synthesises removal changes directly from the
    /// pre-deletion capture and a targeted existence query, sized by the number of deletions
    /// rather than by referencing-group membership. Never loads a referencing object's full
    /// attribute graph and never re-evaluates attribute flows.
    /// </summary>
    private async Task StageRecallFastPathAsync(
        ReferenceRecallContext context,
        HashSet<Guid> deletedIds,
        Dictionary<Guid, List<MvoReferenceRecallCandidate>> byReferencingMvo,
        ReferenceRecallRulePlan plan,
        Dictionary<Guid, MetaverseObjectRecallSummary> summariesById,
        List<Guid> fastMvoIds,
        List<PendingExport> stagedPendingExports,
        ReferenceRecallResult result)
    {
        result.FastPathReferencingObjects += fastMvoIds.Count;
        result.ReferencingObjectsEvaluated += fastMvoIds.Count;

        if (plan.FastTargetSystemIds.Count == 0)
            return; // no direct reference flows anywhere: nothing can be staged

        // The referencing objects' CSOs in the flow target systems, scalars only. CSOs still
        // pending provisioning are excluded: nothing exists in the target to remove a member
        // from, and their pending Create export must be left untouched (recall never provisions).
        List<ConnectedSystemObjectRecallTarget> targets;
        using (var targetsSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("RecallReferencingTargetsFetch"))
        {
            targets = await SyncRepo.GetConnectedSystemObjectRecallTargetsAsync(
                fastMvoIds, plan.FastTargetSystemIds.ToList());
            targetsSpan.SetTag("targetCount", targets.Count);
            targetsSpan.SetSuccess();
        }
        var targetsByMvoAndSystem = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObjectRecallTarget>();
        foreach (var target in targets.Where(t => t.Status != ConnectedSystemObjectStatus.PendingProvisioning))
            targetsByMvoAndSystem[(target.MetaverseObjectId, target.ConnectedSystemId)] = target;

        // Aggregate the existence-query inputs per target system (per-system queries so identical
        // reference values cannot cross-match between systems), remembering how to map matched
        // rows back to their deleted object and flow.
        var csoIdsBySystem = new Dictionary<int, HashSet<Guid>>();
        var attributeIdsBySystem = new Dictionary<int, HashSet<int>>();
        var deletedCsoToMvoBySystem = new Dictionary<int, Dictionary<Guid, Guid>>();
        var loweredValueToMvoBySystem = new Dictionary<int, Dictionary<string, Guid>>();
        var flowBySystemAndAttribute = new Dictionary<(int ConnectedSystemId, int AttributeId), ReferenceRecallDirectFlow>();
        var referencingMvoByCsoId = new Dictionary<Guid, Guid>();
        var scopeResults = new Dictionary<(Guid MvoId, int RuleId), bool>();

        foreach (var mvoId in fastMvoIds)
        {
            var summary = summariesById[mvoId];
            if (!plan.DirectFlowsByTypeThenAttribute.TryGetValue(summary.TypeId, out var flowsByAttribute))
                continue; // no export rule flows any candidate attribute for this type

            foreach (var candidate in byReferencingMvo[mvoId].Where(c => flowsByAttribute.ContainsKey(c.MetaverseAttributeId)))
            {
                var flows = flowsByAttribute[candidate.MetaverseAttributeId];

                foreach (var flow in flows)
                {
                    var systemId = flow.ExportRule.ConnectedSystemId;
                    if (!targetsByMvoAndSystem.TryGetValue((mvoId, systemId), out var target))
                        continue; // the referencing object has no (exportable) presence in this target

                    // Rule scoping survives on the fast path via the lean criteria-only attribute
                    // load; an out-of-scope object must not receive recall exports (parity with
                    // full evaluation). Memoised per (object, rule).
                    if (flow.ExportRule.ObjectScopingCriteriaGroups.Count > 0)
                    {
                        var scopeKey = (mvoId, flow.ExportRule.Id);
                        if (!scopeResults.TryGetValue(scopeKey, out var inScope))
                        {
                            var scopeMvo = new MetaverseObject { Id = summary.Id };
                            scopeMvo.AttributeValues.AddRange(summary.ScopingAttributeValues);
                            inScope = ScopingEvaluation.IsMvoInScopeForExportRule(scopeMvo, flow.ExportRule);
                            scopeResults[scopeKey] = inScope;
                        }
                        if (!inScope)
                            continue;
                    }

                    // The deleted object's identifiers in this system, captured before deletion.
                    // Neither present means it was never provisioned there: nothing to remove.
                    Guid? deletedCsoId = null;
                    if (context.DeletedCsoIdsBySystem.TryGetValue(candidate.ReferencedMetaverseObjectId, out var deletedCsoIds) &&
                        deletedCsoIds.TryGetValue(systemId, out var deletedCsoIdValue))
                        deletedCsoId = deletedCsoIdValue;
                    string? resolvedValue = null;
                    if (context.ResolvedReferenceValuesBySystem.TryGetValue(candidate.ReferencedMetaverseObjectId, out var resolvedBySystem) &&
                        resolvedBySystem.TryGetValue(systemId, out var resolvedValueForSystem))
                        resolvedValue = resolvedValueForSystem;
                    if (deletedCsoId == null && resolvedValue == null)
                        continue;

                    if (!csoIdsBySystem.TryGetValue(systemId, out var csoIds))
                    {
                        csoIds = [];
                        csoIdsBySystem[systemId] = csoIds;
                        attributeIdsBySystem[systemId] = [];
                        deletedCsoToMvoBySystem[systemId] = [];
                        loweredValueToMvoBySystem[systemId] = [];
                    }
                    csoIds.Add(target.ConnectedSystemObjectId);
                    referencingMvoByCsoId[target.ConnectedSystemObjectId] = mvoId;
                    attributeIdsBySystem[systemId].Add(flow.TargetAttribute.Id);
                    var flowKey = (systemId, flow.TargetAttribute.Id);
                    if (flowBySystemAndAttribute.TryGetValue(flowKey, out var existingFlow))
                    {
                        if (existingFlow.SourcePlurality != flow.SourcePlurality)
                            Log.Warning("StageRecallFastPathAsync: Conflicting source pluralities flow into attribute {AttributeId} " +
                                "in system {SystemId}; keeping the first flow's {Plurality} semantics",
                                flow.TargetAttribute.Id, systemId, existingFlow.SourcePlurality);
                    }
                    else
                    {
                        flowBySystemAndAttribute[flowKey] = flow;
                    }
                    if (deletedCsoId.HasValue)
                        deletedCsoToMvoBySystem[systemId][deletedCsoId.Value] = candidate.ReferencedMetaverseObjectId;
                    if (resolvedValue != null)
                        loweredValueToMvoBySystem[systemId][resolvedValue.ToLowerInvariant()] = candidate.ReferencedMetaverseObjectId;
                }
            }
        }

        // One existence query per target system: the rows returned are exactly the values the
        // target still holds for the deleted objects; rows not returned need no removal (this
        // replaces per-group no-net-change detection).
        var changesByCso = new Dictionary<Guid, Dictionary<string, PendingExportAttributeValueChange>>();
        var systemByCso = new Dictionary<Guid, int>();
        foreach (var (systemId, csoIds) in csoIdsBySystem)
        {
            List<CsoReferenceValueMatch> matches;
            using (var existenceSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("RecallExistenceQuery"))
            {
                existenceSpan.SetTag("connectedSystemId", systemId);
                existenceSpan.SetTag("csoCount", csoIds.Count);
                matches = await SyncRepo.GetCsoReferenceValueMatchesAsync(
                    csoIds.ToList(),
                    attributeIdsBySystem[systemId].ToList(),
                    deletedCsoToMvoBySystem[systemId].Keys.ToList(),
                    loweredValueToMvoBySystem[systemId].Keys.ToList());
                existenceSpan.SetTag("matchCount", matches.Count);
                existenceSpan.SetSuccess();
            }

            // A row can match both predicate arms; each target row yields at most one change.
            foreach (var match in matches.DistinctBy(m => m.AttributeValueId))
            {
                Guid deletedMvoId;
                if (match.ReferenceValueId.HasValue &&
                    deletedCsoToMvoBySystem[systemId].TryGetValue(match.ReferenceValueId.Value, out var mvoIdByReference))
                    deletedMvoId = mvoIdByReference;
                else if (match.UnresolvedReferenceValue != null &&
                         loweredValueToMvoBySystem[systemId].TryGetValue(match.UnresolvedReferenceValue.ToLowerInvariant(), out var mvoIdByValue))
                    deletedMvoId = mvoIdByValue;
                else
                    continue; // defensive: the row matched an identifier not registered for this system

                var flow = flowBySystemAndAttribute[(systemId, match.AttributeId)];

                string? removalValue = null;
                if (context.ResolvedReferenceValuesBySystem.TryGetValue(deletedMvoId, out var resolvedBySystem) &&
                    resolvedBySystem.TryGetValue(systemId, out var resolvedValueForSystem))
                    removalValue = resolvedValueForSystem;

                // The removal-change verdict comes from the pure engine (#288 extraction): a value-carrying
                // Remove for a multi-valued source, a null-clearing Update for a single-valued one, or null
                // when a multi-valued removal has no resolvable value and cannot be staged.
                var change = _syncEngine.DecideRecallRemovalChange(flow, removalValue);
                if (change == null)
                {
                    result.UnresolvableChangesDropped++;
                    continue;
                }

                if (!changesByCso.TryGetValue(match.ConnectedSystemObjectId, out var changesByMergeKey))
                {
                    changesByMergeKey = [];
                    changesByCso[match.ConnectedSystemObjectId] = changesByMergeKey;
                    systemByCso[match.ConnectedSystemObjectId] = systemId;
                }
                changesByMergeKey.TryAdd(GetAttributeChangeMergeKey(change), change);
            }
        }

        if (changesByCso.Count == 0)
            return;

        // Merge with any existing unexported Pending Exports for the matched CSOs, honouring the
        // collision policy: Delete wins (deprovisioning supersedes membership updates), Create is
        // untouched (unreachable here after the PendingProvisioning filter; defensive), Update is
        // merged with recall changes taking precedence on merge-key collisions.
        using var mergeSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("RecallPendingExportMerge");
        var existingPendingExports = await SyncRepo.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(changesByCso.Keys.ToList());
        mergeSpan.SetTag("existingPeCount", existingPendingExports.Count);

        foreach (var (csoId, changesByMergeKey) in changesByCso)
        {
            // The collision policy and merge live in the pure engine (#288 extraction): an existing Delete
            // wins, a Create is protected, an Update merges in with recall winning key collisions and
            // deleted-object references purged.
            var existingPendingExport = existingPendingExports.GetValueOrDefault(csoId);
            var mergeResult = _syncEngine.MergeRecallChangesWithExistingPendingExport(
                changesByMergeKey, existingPendingExport, deletedIds);

            if (mergeResult.Outcome == RecallPendingExportMergeOutcome.SkippedDeleteSupersedes)
            {
                result.SkippedDueToExistingDeletePendingExport++;
                Log.Information("StageRecallFastPathAsync: CSO {CsoId} has a pending Delete export; skipping " +
                    "{ChangeCount} recall change(s) (deprovisioning supersedes membership updates)",
                    csoId, changesByMergeKey.Count);
                continue;
            }
            if (mergeResult.Outcome == RecallPendingExportMergeOutcome.SkippedCreateProtected)
            {
                Log.Warning("StageRecallFastPathAsync: CSO {CsoId} has a pending Create export but is not " +
                    "PendingProvisioning; skipping recall changes to preserve the provisioning export", csoId);
                continue;
            }
            if (mergeResult.PurgedChangeCount > 0)
            {
                Log.Debug("StageRecallFastPathAsync: Purged {PurgedCount} change(s) from Pending Export {PendingExportId}: " +
                    "their unresolved references are deleted Metaverse Objects",
                    mergeResult.PurgedChangeCount, existingPendingExport!.Id);
            }

            var attributeChanges = changesByMergeKey.Values.ToList();
            if (attributeChanges.Count == 0)
                continue;

            stagedPendingExports.Add(new PendingExport
            {
                Id = Guid.NewGuid(),
                ConnectedSystemId = systemByCso[csoId],
                ConnectedSystemObjectId = csoId,
                ChangeType = PendingExportChangeType.Update,
                Status = PendingExportStatus.Pending,
                SourceMetaverseObjectId = referencingMvoByCsoId[csoId],
                AttributeValueChanges = attributeChanges,
                CreatedAt = DateTime.UtcNow,
                HasUnresolvedReferences = attributeChanges.Any(avc => !string.IsNullOrEmpty(avc.UnresolvedReferenceValue))
            });
        }
        mergeSpan.SetTag("deleteSkips", result.SkippedDueToExistingDeletePendingExport);
        mergeSpan.SetSuccess();
    }

    /// <summary>
    /// The full-evaluation recall fallback: referencing object types where a candidate reference
    /// attribute is sourced through an expression or multi-source chain keep the pre-#1003
    /// per-object evaluation (full object load, attribute flow recomputation with no-net-change),
    /// with recall semantics applied (no provisioning; an existing Delete Pending Export wins).
    /// </summary>
    private async Task StageRecallFallbackAsync(
        ReferenceRecallContext context,
        HashSet<Guid> deletedIds,
        Dictionary<Guid, List<MvoReferenceRecallCandidate>> byReferencingMvo,
        ExportEvaluationCache cache,
        List<Guid> fallbackMvoIds,
        List<PendingExport> stagedPendingExports,
        ReferenceRecallResult result)
    {
        const int batchSize = 500;
        var fallbackPendingExports = new List<PendingExport>();

        for (var offset = 0; offset < fallbackMvoIds.Count; offset += batchSize)
        {
            var batchIds = fallbackMvoIds.Skip(offset).Take(batchSize).ToList();
            var referencingMvos = await SyncRepo.GetMetaverseObjectsByIdsNoTrackingAsync(batchIds);
            await RefreshExportEvaluationCacheForPageAsync(cache, batchIds);

            foreach (var referencingMvo in referencingMvos)
            {
                // Reconstruct the removed reference rows from the pre-deletion capture; the live rows
                // have had their reference FKs nulled by the deletion and carry no target any more.
                var removedRows = byReferencingMvo[referencingMvo.Id]
                    .Select(candidate => new MetaverseObjectAttributeValue
                    {
                        Id = candidate.AttributeValueId,
                        AttributeId = candidate.MetaverseAttributeId,
                        ReferenceValueId = candidate.ReferencedMetaverseObjectId
                    })
                    .ToList();

                var evaluation = await EvaluateExportRulesWithNoNetChangeDetectionAsync(
                    referencingMvo, removedRows, cache, deferSave: true,
                    removedAttributes: [.. removedRows], existingPendingExports: fallbackPendingExports,
                    preResolvedReferences: context.ResolvedReferenceValuesBySystem,
                    recallSemantics: true);

                result.ReferencingObjectsEvaluated++;
                result.FallbackReferencingObjects++;

                // Recall must not provision: a referencing object with no CSO in a target has nothing
                // there to remove a member from. Only Update Pending Exports are kept.
                fallbackPendingExports.AddRange(
                    evaluation.PendingExports.Where(pe => pe.ChangeType == PendingExportChangeType.Update));
            }
        }

        // Drop changes that could not be pre-resolved: the deleted object had no presence in that
        // target system, so the removal is a no-op there (and could never resolve at export time).
        // The purge lives in the pure engine (#288 extraction).
        foreach (var pendingExport in fallbackPendingExports)
        {
            result.UnresolvableChangesDropped += _syncEngine.PurgeChangesReferencingDeletedObjects(pendingExport, deletedIds);

            if (pendingExport.AttributeValueChanges.Count == 0)
                continue;

            pendingExport.HasUnresolvedReferences = pendingExport.AttributeValueChanges
                .Any(avc => !string.IsNullOrEmpty(avc.UnresolvedReferenceValue));
            stagedPendingExports.Add(pendingExport);
        }
    }

    /// <summary>
    /// Classifies the export rule shapes for reference recall (#1003): per Metaverse Object Type,
    /// either a map of direct single-source reference flows (fast path) or a fallback marker when
    /// any rule sources a candidate attribute through an expression or multi-source chain. Also
    /// collects the scoping-criteria attribute ids and target systems the fast path needs.
    /// Expressions are matched by attribute-name mention (an expression can only read an attribute
    /// via mv["Name"]); dynamically constructed attribute names are not supported by this
    /// classification and land on the fallback only when the name literal appears.
    /// </summary>
    private static ReferenceRecallRulePlan BuildReferenceRecallRulePlan(
        ExportEvaluationCache cache,
        HashSet<int> candidateAttributeIds)
    {
        var plan = new ReferenceRecallRulePlan();

        // Resolve the candidate attributes' names for the expression mention check from wherever
        // the cached rule graph carries them. A candidate attribute that appears nowhere as a
        // direct source and is absent from the loaded type attribute lists cannot be name-checked;
        // in that case any expression rule is conservatively routed to the fallback.
        var candidateNamesById = new Dictionary<int, string>();
        foreach (var rule in cache.ExportRulesByMvoTypeId.Values.SelectMany(rules => rules))
        {
            foreach (var source in rule.AttributeFlowRules
                         .SelectMany(mapping => mapping.Sources)
                         .Where(s => s.MetaverseAttribute != null && candidateAttributeIds.Contains(s.MetaverseAttribute.Id)))
                candidateNamesById[source.MetaverseAttribute!.Id] = source.MetaverseAttribute.Name;

            foreach (var attribute in (rule.MetaverseObjectType?.Attributes ?? [])
                         .Where(a => candidateAttributeIds.Contains(a.Id)))
                candidateNamesById[attribute.Id] = attribute.Name;
        }
        var hasUnresolvableCandidateNames = candidateAttributeIds.Count > candidateNamesById.Count;

        foreach (var (typeId, rules) in cache.ExportRulesByMvoTypeId)
        {
            var flowsByAttribute = new Dictionary<int, List<ReferenceRecallDirectFlow>>();
            var routeToFallback = false;

            foreach (var rule in rules)
            {
                foreach (var mapping in rule.AttributeFlowRules)
                {
                    var singleSource = mapping.Sources.Count == 1 ? mapping.Sources[0] : null;
                    var isDirectCandidateFlow =
                        mapping.TargetConnectedSystemAttribute != null &&
                        singleSource?.MetaverseAttribute != null &&
                        string.IsNullOrWhiteSpace(singleSource.Expression) &&
                        candidateAttributeIds.Contains(singleSource.MetaverseAttribute.Id);
                    if (isDirectCandidateFlow)
                    {
                        // Synchronisation integrity: recall stages Update exports, and a WritableOnCreate
                        // attribute must never reach one. Clearing or removing a value from it would rewrite
                        // the Connected System's identifier for the object and sever the link to the row or
                        // entry the Connected System Object is anchored to, which is the exact corruption
                        // that writability state exists to prevent. The reference is deliberately left as the
                        // target holds it; this is not routed to the fallback path, because the fallback
                        // would only reach the same exclusion in CreateAttributeValueChanges.
                        if (mapping.TargetConnectedSystemAttribute!.Writability == AttributeWritability.WritableOnCreate)
                            continue;

                        if (!flowsByAttribute.TryGetValue(singleSource!.MetaverseAttribute!.Id, out var flows))
                        {
                            flows = [];
                            flowsByAttribute[singleSource.MetaverseAttribute.Id] = flows;
                        }
                        flows.Add(new ReferenceRecallDirectFlow
                        {
                            ExportRule = rule,
                            TargetAttribute = mapping.TargetConnectedSystemAttribute!,
                            SourcePlurality = singleSource.MetaverseAttribute.AttributePlurality
                        });
                        continue;
                    }

                    if (MappingTouchesCandidateNonDirectly(mapping, candidateAttributeIds, candidateNamesById, hasUnresolvableCandidateNames))
                    {
                        routeToFallback = true;
                        break;
                    }
                }
                if (routeToFallback)
                    break;
            }

            if (routeToFallback)
            {
                plan.FallbackTypeIds.Add(typeId);
                continue;
            }
            if (flowsByAttribute.Count == 0)
                continue; // no rule of this type touches a candidate attribute: fast no-op

            plan.DirectFlowsByTypeThenAttribute[typeId] = flowsByAttribute;
            foreach (var flow in flowsByAttribute.Values.SelectMany(flows => flows))
            {
                plan.FastTargetSystemIds.Add(flow.ExportRule.ConnectedSystemId);
                CollectScopingAttributeIds(flow.ExportRule, plan.ScopingAttributeIds);
            }
        }

        return plan;
    }

    /// <summary>
    /// True when the mapping consumes a candidate reference attribute in a way the direct fast
    /// path cannot reproduce: as part of a multi-source chain, or inside an expression that
    /// mentions the attribute's name (or any expression when a candidate attribute's name could
    /// not be resolved for checking).
    /// </summary>
    private static bool MappingTouchesCandidateNonDirectly(
        SyncRuleMapping mapping,
        HashSet<int> candidateAttributeIds,
        Dictionary<int, string> candidateNamesById,
        bool hasUnresolvableCandidateNames)
    {
        if (mapping.Sources.Count > 1 && mapping.Sources.Any(source =>
                (source.MetaverseAttributeId.HasValue && candidateAttributeIds.Contains(source.MetaverseAttributeId.Value)) ||
                (source.MetaverseAttribute != null && candidateAttributeIds.Contains(source.MetaverseAttribute.Id))))
            return true;

        foreach (var expression in mapping.Sources
                     .Select(source => source.Expression)
                     .Where(expression => !string.IsNullOrWhiteSpace(expression)))
        {
            if (hasUnresolvableCandidateNames)
                return true;
            if (candidateNamesById.Values.Any(name => expression!.Contains(name, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Collects the Metaverse Attribute ids an export rule's scoping criteria evaluate, including
    /// nested criteria groups, so the recall fast path can lean-load exactly those values.
    /// </summary>
    private static void CollectScopingAttributeIds(SyncRule exportRule, HashSet<int> scopingAttributeIds)
    {
        foreach (var group in exportRule.ObjectScopingCriteriaGroups)
            Collect(group);
        return;

        void Collect(SyncRuleScopingCriteriaGroup group)
        {
            foreach (var criterion in group.Criteria)
            {
                var attributeId = criterion.MetaverseAttribute?.Id ?? criterion.MetaverseAttributeId;
                if (attributeId.HasValue)
                    scopingAttributeIds.Add(attributeId.Value);
            }
            foreach (var childGroup in group.ChildGroups)
                Collect(childGroup);
        }
    }

    #endregion

    /// <summary>
    /// Gets all enabled export Synchronisation Rules for a given MVO object type.
    /// </summary>
    private async Task<List<SyncRule>> GetExportRulesForObjectTypeAsync(int metaverseObjectTypeId)
    {
        var allSyncRules = await SyncRepo.GetAllSyncRulesAsync();

        return allSyncRules
            .Where(sr => sr.Enabled &&
                         sr.Direction == SyncRuleDirection.Export &&
                         sr.MetaverseObjectTypeId == metaverseObjectTypeId)
            .ToList();
    }

    /// <summary>
    /// Checks if an MVO is in scope for an export rule based on scoping criteria.
    /// No scoping criteria means all objects of the type are in scope.
    /// </summary>
    public bool IsMvoInScopeForExportRule(MetaverseObject mvo, SyncRule exportRule)
    {
        return ScopingEvaluation.IsMvoInScopeForExportRule(mvo, exportRule);
    }

    /// <summary>
    /// Records which Synchronisation Rule's provisioning decision produced an export, for a Create only.
    /// <para>
    /// This is the one moment the answer is known for certain. Delivering an initial password happens much
    /// later, once the account exists in the target and has an external id, and by then working out which rule
    /// was responsible would mean re-evaluating scope against rules that may have been edited in the meantime:
    /// expensive, and capable of reaching a different answer than the one that created the account.
    /// </para>
    /// <para>
    /// Deliberately null for updates and deletes. Only a create brings an account into existence, and a stamp on
    /// anything else would let delivery fire again later in that account's life.
    /// </para>
    /// </summary>
    private static int? ProvisioningRuleFor(PendingExportChangeType changeType, SyncRule exportRule) =>
        changeType == PendingExportChangeType.Create ? exportRule.Id : null;

    /// <summary>
    /// Creates or updates a PendingExport for an MVO change to a target system.
    /// For provisioning (Create) scenarios, also creates a CSO with Status=PendingProvisioning
    /// to establish the CSO↔MVO relationship before the object exists in the target system.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed.</param>
    /// <param name="exportRule">The export rule to evaluate.</param>
    /// <param name="changedAttributes">The attributes that changed on the MVO.</param>
    /// <param name="removedAttributes">Optional set of attribute values that were removed (for multi-valued attr handling).</param>
    private async Task<PendingExport?> CreateOrUpdatePendingExportAsync(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null)
    {
        // Find existing CSO for this MVO in the target system
        var existingCso = await SyncRepo.GetConnectedSystemObjectByMetaverseObjectIdAsync(mvo.Id, exportRule.ConnectedSystemId);

        // The verdict comes from the pure engine (#288 extraction); this method is orchestration: resolve
        // the CSO, act on the verdict, compute the delta and persist.
        var decision = _syncEngine.DecideOutboundStaging(mvo, exportRule, existingCso, changedAttributes, recallSemantics: false);

        ConnectedSystemObject? csoForExport = existingCso;
        var createdNewCso = false;

        switch (decision.Outcome)
        {
            case OutboundStagingOutcome.ObjectTypeConflict:
                // This overload has no channel to a Run Profile Execution Item, so the conflict (#1331) is
                // logged rather than reported; the cached entry point used by the synchronisation engine
                // raises a CouldNotExportDueToExistingConnectedSystemObject RPEI.
                LogObjectTypeConflict(nameof(CreateOrUpdatePendingExportAsync), decision.Conflict!, exportRule.ConnectedSystemId);
                return null;

            case OutboundStagingOutcome.ProvisioningDeclined:
                Log.Debug("CreateOrUpdatePendingExportAsync: No CSO exists (or PendingProvisioning) and ProvisionToConnectedSystem is not enabled for rule {RuleName}",
                    exportRule.Name);
                return null;

            case OutboundStagingOutcome.PendingProvisioningChangesIrrelevant:
                Log.Debug("CreateOrUpdatePendingExportAsync: Skipping PendingProvisioning CSO {CsoId} for rule {RuleName} — " +
                    "none of the {ChangeCount} changed attributes map to this export rule's Attribute Flow Rules",
                    existingCso!.Id, exportRule.Name, changedAttributes.Count);
                return null;

            case OutboundStagingOutcome.ProvisionNewCso:
                // Create CSO with PendingProvisioning status to establish the relationship before export
                csoForExport = await CreatePendingProvisioningCsoAsync(mvo, exportRule);
                createdNewCso = true;
                break;

                // ReusePendingProvisioningCso and UpdateExistingCso: export onto the existing CSO.
        }

        var changeType = decision.ChangeType!.Value;

        // Create attribute value changes based on the export rule mappings
        // Note: No CSO attribute cache available in non-optimised path, so no-net-change detection is disabled
        var attributeChanges = CreateAttributeValueChanges(mvo, exportRule, changedAttributes, changeType,
            existingCso: existingCso, csoAttributeCache: null, out _, removedAttributes: removedAttributes);

        if (attributeChanges.Count == 0 && changeType == PendingExportChangeType.Update)
        {
            Log.Debug("CreateOrUpdatePendingExportAsync: No attribute changes for MVO {MvoId} to system {SystemId}",
                mvo.Id, exportRule.ConnectedSystemId);
            return null;
        }

        // For newly provisioned CSOs, add the secondary external ID value so confirming import can match
        // Don't add it for reused PendingProvisioning CSOs - they already have it from when they were created
        if (createdNewCso && csoForExport != null)
        {
            await AddSecondaryExternalIdToCsoAsync(csoForExport, attributeChanges, exportRule);
        }

        // Only set the FK property (ConnectedSystemObjectId), NOT the navigation property (ConnectedSystemObject).
        // Setting both can cause EF Core change tracker conflicts where the FK gets overwritten.
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = exportRule.ConnectedSystemId,
            ConnectedSystemObjectId = csoForExport?.Id,
            ChangeType = changeType,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = mvo.Id,
            AttributeValueChanges = attributeChanges,
            CreatedAt = DateTime.UtcNow,
            ProvisioningSyncRuleId = ProvisioningRuleFor(changeType, exportRule)
        };

        await SyncRepo.CreatePendingExportAsync(pendingExport);

        Log.Information("CreateOrUpdatePendingExportAsync: Created {ChangeType} PendingExport {ExportId} for MVO {MvoId} to system {SystemName} with {AttrCount} attribute changes",
            changeType, pendingExport.Id, mvo.Id, exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString(), attributeChanges.Count);

        return pendingExport;
    }

    /// <summary>
    /// Optimised version of CreateOrUpdatePendingExportAsync that uses pre-cached CSO lookup.
    /// Also updates the cache when new CSOs are created for provisioning.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed.</param>
    /// <param name="exportRule">The export rule to evaluate.</param>
    /// <param name="changedAttributes">The attributes that changed on the MVO.</param>
    /// <param name="cache">The pre-loaded cache for CSO lookups.</param>
    /// <param name="removedAttributes">Optional set of attribute values that were removed (for multi-valued attr handling).</param>
    private async Task<PendingExport?> CreateOrUpdatePendingExportAsync(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ExportEvaluationCache cache,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null)
    {
        // Find existing CSO using cached lookup instead of database query
        var lookupKey = (mvo.Id, exportRule.ConnectedSystemId);
        cache.CsoLookup.TryGetValue(lookupKey, out var existingCso);

        // The verdict comes from the pure engine (#288 extraction); this method is orchestration: resolve
        // the CSO from the cache, act on the verdict, compute the delta and persist.
        var decision = _syncEngine.DecideOutboundStaging(mvo, exportRule, existingCso, changedAttributes, recallSemantics: false);

        ConnectedSystemObject? csoForExport = existingCso;
        var createdNewCso = false;

        switch (decision.Outcome)
        {
            case OutboundStagingOutcome.ObjectTypeConflict:
                // This overload has no channel to a Run Profile Execution Item, so the conflict (#1331) is
                // logged rather than reported; the cached entry point used by the synchronisation engine
                // raises a CouldNotExportDueToExistingConnectedSystemObject RPEI.
                LogObjectTypeConflict(nameof(CreateOrUpdatePendingExportAsync), decision.Conflict!, exportRule.ConnectedSystemId);
                return null;

            case OutboundStagingOutcome.ProvisioningDeclined:
                Log.Debug("CreateOrUpdatePendingExportAsync: No CSO exists (or PendingProvisioning) and ProvisionToConnectedSystem is not enabled for rule {RuleName}",
                    exportRule.Name);
                return null;

            case OutboundStagingOutcome.PendingProvisioningChangesIrrelevant:
                Log.Debug("CreateOrUpdatePendingExportAsync: Skipping PendingProvisioning CSO {CsoId} for rule {RuleName} — " +
                    "none of the {ChangeCount} changed attributes map to this export rule's Attribute Flow Rules",
                    existingCso!.Id, exportRule.Name, changedAttributes.Count);
                return null;

            case OutboundStagingOutcome.ProvisionNewCso:
                // Create CSO with PendingProvisioning status to establish the relationship before export
                csoForExport = await CreatePendingProvisioningCsoAsync(mvo, exportRule);
                createdNewCso = true;

                // Update the cache with the newly created CSO so subsequent lookups find it
                cache.CsoLookup[lookupKey] = csoForExport;
                break;

                // ReusePendingProvisioningCso and UpdateExistingCso: export onto the existing CSO.
        }

        var changeType = decision.ChangeType!.Value;

        // Create attribute value changes based on the export rule mappings
        // Note: CSO attribute cache is not available in the global ExportEvaluationCache -
        // the per-page cache is managed by sync processors and passed via the overload below
        var attributeChanges = CreateAttributeValueChanges(mvo, exportRule, changedAttributes, changeType,
            existingCso: existingCso, csoAttributeCache: null, out _, removedAttributes: removedAttributes);

        if (attributeChanges.Count == 0 && changeType == PendingExportChangeType.Update)
        {
            Log.Debug("CreateOrUpdatePendingExportAsync: No attribute changes for MVO {MvoId} to system {SystemId}",
                mvo.Id, exportRule.ConnectedSystemId);
            return null;
        }

        // For newly provisioned CSOs, add the secondary external ID value so confirming import can match
        // Don't add it for reused PendingProvisioning CSOs - they already have it from when they were created
        if (createdNewCso && csoForExport != null)
        {
            await AddSecondaryExternalIdToCsoAsync(csoForExport, attributeChanges, exportRule);
        }

        var csoId = csoForExport?.Id;
        Log.Verbose("CreateOrUpdatePendingExportAsync: Creating Pending Export. csoForExport={CsoForExport}, csoId={CsoId}, changeType={ChangeType}",
            csoForExport != null ? csoForExport.Id.ToString() : "null", csoId?.ToString() ?? "null", changeType);

        // Only set the FK property (ConnectedSystemObjectId), NOT the navigation property (ConnectedSystemObject).
        // Setting both can cause EF Core change tracker conflicts where the FK gets overwritten.
        // When both are set, EF Core's relationship fixup may use the navigation property's tracking state
        // to determine the FK value, which can result in null FKs for entities loaded from different contexts.
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = exportRule.ConnectedSystemId,
            ConnectedSystemObjectId = csoId,
            ChangeType = changeType,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = mvo.Id,
            AttributeValueChanges = attributeChanges,
            CreatedAt = DateTime.UtcNow,
            ProvisioningSyncRuleId = ProvisioningRuleFor(changeType, exportRule)
        };

        // Save immediately - batching causes memory pressure with large datasets (5000+ objects)
        // which leads to worse performance than individual saves due to GC overhead
        await SyncRepo.CreatePendingExportAsync(pendingExport);

        Log.Debug("CreateOrUpdatePendingExportAsync: Created {ChangeType} PendingExport {ExportId} for MVO {MvoId} to system {SystemName} with {AttrCount} attribute changes, CsoId={CsoId}",
            changeType, pendingExport.Id, mvo.Id, exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString(), attributeChanges.Count, pendingExport.ConnectedSystemObjectId);

        return pendingExport;
    }

    /// <summary>
    /// Creates or updates a Pending Export with no-net-change detection.
    /// Returns both the Pending Export (if created) and the count of attributes skipped due to no-net-change.
    /// Uses cache.CsoAttributeValues for no-net-change detection against target CSO attributes.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed.</param>
    /// <param name="exportRule">The export rule to evaluate.</param>
    /// <param name="changedAttributes">The attributes that changed on the MVO.</param>
    /// <param name="cache">The pre-loaded cache from BuildExportEvaluationCacheAsync (includes target CSO attributes).</param>
    /// <param name="deferSave">When true, Pending Exports and provisioning CSOs are not saved to the database
    /// and the caller is responsible for batch saving. Default is false for backwards compatibility.</param>
    /// <param name="removedAttributes">Optional set of attribute values that were removed (for multi-valued attr handling).</param>
    /// <param name="existingPendingExports">Optional list of Pending Exports already staged for batch save (e.g., from drift detection).
    /// Used to merge attribute changes in-memory instead of creating duplicates. Export evaluation values win on conflict.</param>
    /// <returns>Tuple containing the Pending Export (if created), CSO created for provisioning (if any), and no-net-change count.</returns>
    /// <summary>
    /// Decides whether an outbound Synchronisation Rule may export to the Connected System Object already
    /// occupying a Metaverse Object's single slot in the target Connected System, returning a conflict when
    /// that Object is of a different Connected System Object Type than the Rule targets.
    /// </summary>
    /// <remarks>
    /// A Metaverse Object holds at most one Connected System Object per Connected System (an application
    /// invariant that IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique also backs), so a
    /// second Rule wanting a different Object Type has nowhere to put one. Pending Provisioning Objects
    /// count as occupying the slot: the Rule would otherwise take the provisioning path and try to create a
    /// second Object for the Metaverse Object, which that index rejects. An Object joined to no Metaverse
    /// Object occupies nothing, so export matching is still free to claim it.
    /// </remarks>
    /// <summary>
    /// Logs an Object Type conflict identically wherever it is detected, so the three export entry points
    /// read the same way in a service log.
    /// </summary>
    /// <remarks>
    /// Warning, not Error. This is a handled, per-object configuration outcome, not a failure of the
    /// synchronisation run: the authoritative report is the CouldNotExportDueToExistingConnectedSystemObject
    /// Run Profile Execution Item raised against the Activity. Logging it at Error would emit one application
    /// error per object per Rule (a single misconfiguration over fifty objects yields a hundred lines), burying
    /// genuine errors and tripping any consumer that treats an Error line as a run-level failure.
    /// </remarks>
    private static void LogObjectTypeConflict(string caller, ExportObjectTypeConflict conflict, int connectedSystemId)
    {
        Log.Warning("{Caller}: Synchronisation Rule '{SyncRule}' targets Connected System Object Type '{TargetType}', but " +
            "Metaverse Object {MvoId} already holds Connected System Object {CsoId} of type '{ExistingType}' in Connected " +
            "System {SystemId}. A Metaverse Object can hold only one Connected System Object per Connected System, so " +
            "nothing was staged for this Rule.",
            caller, LogSanitiser.Sanitise(conflict.SyncRuleName), LogSanitiser.Sanitise(conflict.TargetObjectTypeName),
            conflict.MetaverseObjectId, conflict.ExistingConnectedSystemObjectId,
            LogSanitiser.Sanitise(conflict.ExistingObjectTypeName), connectedSystemId);
    }

    internal static ExportObjectTypeConflict? DetectObjectTypeConflict(
        MetaverseObject mvo,
        SyncRule exportRule,
        ConnectedSystemObject? existingCso)
        => SyncEngine.DetectExportObjectTypeConflict(mvo, exportRule, existingCso);

    private async Task<(PendingExport? PendingExport, ConnectedSystemObject? ProvisioningCso, int CsoAlreadyCurrentCount)> CreateOrUpdatePendingExportWithNoNetChangeAsync(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ExportEvaluationCache cache,
        bool deferSave = false,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null,
        List<PendingExport>? existingPendingExports = null,
        Dictionary<string, object?>? mvAttributeDictionary = null,
        IReadOnlyDictionary<Guid, string>? preResolvedReferenceValues = null,
        bool recallSemantics = false,
        List<AttributeFlowError>? flowErrors = null,
        List<ExportObjectTypeConflict>? objectTypeConflicts = null)
    {
        // Find existing CSO using cached lookup instead of database query
        var lookupKey = (mvo.Id, exportRule.ConnectedSystemId);
        cache.CsoLookup.TryGetValue(lookupKey, out var existingCso);

        // The verdict comes from the pure engine (#288 extraction); this method is orchestration: resolve
        // the CSO, attempt export matching where the verdict provisions, compute the delta and persist. The
        // lookup is keyed by (Metaverse Object, Connected System) with no Object Type in it, so a Rule
        // targeting a different Object Type resolves to whichever Object holds that slot; the engine reports
        // that conflict (#1331) and the Metaverse Object's other export Rules are unaffected.
        var decision = _syncEngine.DecideOutboundStaging(mvo, exportRule, existingCso, changedAttributes, recallSemantics);

        if (decision.Outcome == OutboundStagingOutcome.ObjectTypeConflict)
        {
            objectTypeConflicts?.Add(decision.Conflict!);
            LogObjectTypeConflict(nameof(CreateOrUpdatePendingExportWithNoNetChangeAsync), decision.Conflict!, exportRule.ConnectedSystemId);
            return (null, null, 0);
        }

        // Reference recall must never provision (#1003): a referencing object with no presence in
        // the target has nothing there to remove a member from. This also protects a pending
        // Create export: without the guard, the merge below would delete it and the recall
        // caller's Update-only filter would then discard the merged Create, silently losing the
        // provisioning export (a pre-#1003 defect).
        if (decision.Outcome == OutboundStagingOutcome.RecallSkippedNoTargetPresence)
        {
            Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: Recall skipping MVO {MvoId} for system {SystemId}: " +
                "no exportable presence (CSO {CsoStatus})",
                mvo.Id, exportRule.ConnectedSystemId, existingCso?.Status.ToString() ?? "absent");
            return (null, null, 0);
        }

        PendingExportChangeType changeType;
        ConnectedSystemObject? csoForExport = existingCso;
        ConnectedSystemObject? provisioningCso = null;
        var createdNewCso = false;

        Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: MVO {MvoId} to system {SystemId}: existingCso={ExistingCso}, csoStatus={CsoStatus}, needsProvisioning={NeedsProvisioning}",
            mvo.Id, exportRule.ConnectedSystemId,
            existingCso != null ? existingCso.Id.ToString() : "null",
            existingCso?.Status.ToString() ?? "N/A",
            decision.Outcome != OutboundStagingOutcome.UpdateExistingCso);

        switch (decision.Outcome)
        {
            case OutboundStagingOutcome.ProvisioningDeclined:
                Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: No CSO exists (or PendingProvisioning) and ProvisionToConnectedSystem is not enabled for rule {RuleName}",
                    exportRule.Name);
                return (null, null, 0);

            case OutboundStagingOutcome.PendingProvisioningChangesIrrelevant:
                // Restaging the existing Create Pending Export from changes irrelevant to this rule would
                // misattribute it to this sync in the causality tree.
                Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: Skipping PendingProvisioning CSO {CsoId} for rule {RuleName} — " +
                    "none of the {ChangeCount} changed attributes map to this export rule's Attribute Flow Rules",
                    existingCso!.Id, exportRule.Name, changedAttributes.Count);
                return (null, null, 0);

            case OutboundStagingOutcome.ReusePendingProvisioningCso:
                // Reuse existing PendingProvisioning CSO (already has secondary external ID); a previous
                // sync already created the Create PE with all mapped attributes.
                changeType = PendingExportChangeType.Create;
                break;

            case OutboundStagingOutcome.ProvisionNewCso:
                // Before provisioning, attempt export matching to find an existing CSO in the target system.
                // This prevents creating duplicates when the object already exists in the target. Matching is
                // data access, so it stays orchestrator-side; a matched and claimed CSO turns the verdict's
                // Create into an Update.
                ConnectedSystemObject? matchedCso = null;
                using (JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("ExportMatching"))
                {
                    matchedCso = await AttemptExportMatchingAsync(mvo, exportRule);
                }

                if (matchedCso != null)
                {
                    // Claim the CSO atomically: the eligibility check above is a point-in-time read,
                    // so two Metaverse Objects can both reach here for the same CSO in overlapping
                    // evaluations (#1051). The conditional UPDATE re-checks MetaverseObjectId IS NULL
                    // at write time, so only one caller wins the join; on failure, fall through to
                    // provisioning below by clearing matchedCso.
                    var dateJoined = DateTime.UtcNow;
                    var claimed = await SyncRepo.TryClaimConnectedSystemObjectForJoinAsync(matchedCso.Id, mvo.Id, dateJoined);
                    if (!claimed)
                    {
                        Log.Warning("CreateOrUpdatePendingExportWithNoNetChangeAsync: Export matching found Connected System Object {CsoId} for Metaverse Object {MvoId} in system {SystemId}, but another Metaverse Object claimed it first; falling back to provisioning",
                            matchedCso.Id, mvo.Id, exportRule.ConnectedSystemId);
                        matchedCso = null;
                    }
                    else
                    {
                        // Fix up the tracked instance to match the conditional UPDATE: raw SQL bypasses
                        // the change tracker, so without this the next SaveChangesAsync could write
                        // stale values back over the claimed row.
                        matchedCso.MetaverseObjectId = mvo.Id;
                        matchedCso.Status = ConnectedSystemObjectStatus.Normal;
                        matchedCso.JoinType = ConnectedSystemObjectJoinType.Joined;
                        matchedCso.DateJoined = dateJoined;

                        Log.Information("CreateOrUpdatePendingExportWithNoNetChangeAsync: Export matching found existing CSO {CsoId} for MVO {MvoId} in system {SystemId}: joined instead of provisioning",
                            matchedCso.Id, mvo.Id, exportRule.ConnectedSystemId);
                    }
                }

                if (matchedCso != null)
                {
                    // Join the MVO to the existing CSO instead of provisioning
                    // Update cache so subsequent lookups find the joined CSO
                    cache.CsoLookup[lookupKey] = matchedCso;

                    csoForExport = matchedCso;
                    changeType = PendingExportChangeType.Update;
                }
                else
                {
                    // No match found: create CSO with PendingProvisioning status to establish the relationship before export
                    // When deferSave is true, CSO is created in-memory and the caller batch-saves it
                    using (JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("CreateProvisioningCso"))
                    {
                        csoForExport = await CreatePendingProvisioningCsoAsync(mvo, exportRule, deferSave);
                        provisioningCso = csoForExport; // Track for batch saving
                    }

                    // Update the cache with the newly created CSO so subsequent lookups find it
                    cache.CsoLookup[lookupKey] = csoForExport;
                    createdNewCso = true;
                    changeType = PendingExportChangeType.Create;
                }
                break;

            default:
                changeType = PendingExportChangeType.Update;
                break;
        }

        // Create attribute value changes with no-net-change detection.
        // Use cache.CsoAttributeValues which contains TARGET CSO attribute values (loaded at sync start).
        // The csoAttributeCache parameter (from sync processor) contains SOURCE CSO values which is incorrect
        // for detecting no-net-change on exports to target systems.
        List<PendingExportAttributeValueChange> attributeChanges;
        int csoAlreadyCurrentCount;
        using (var attrSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("CreateAttributeValueChanges"))
        {
            attrSpan.SetTag("mappingCount", exportRule.AttributeFlowRules.Count);
            attrSpan.SetTag("changeType", changeType.ToString());

            attributeChanges = CreateAttributeValueChanges(mvo, exportRule, changedAttributes, changeType,
                existingCso: existingCso, csoAttributeCache: cache.CsoAttributeValues, out csoAlreadyCurrentCount,
                removedAttributes: removedAttributes, mvAttributeDictionary: mvAttributeDictionary,
                preResolvedReferenceValues: preResolvedReferenceValues, flowErrors: flowErrors);

            attrSpan.SetTag("changeCount", attributeChanges.Count);
            attrSpan.SetTag("skippedNoNetChange", csoAlreadyCurrentCount);
            attrSpan.SetSuccess();
        }

        if (attributeChanges.Count == 0 && changeType == PendingExportChangeType.Update)
        {
            Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: No attribute changes for MVO {MvoId} to system {SystemId} (skipped {SkippedCount} no-net-change attributes)",
                mvo.Id, exportRule.ConnectedSystemId, csoAlreadyCurrentCount);
            return (null, null, csoAlreadyCurrentCount);
        }

        // For newly provisioned CSOs, add the secondary external ID value so confirming import can match
        // Don't add it for reused PendingProvisioning CSOs - they already have it from when they were created
        // When deferSave is true, the CSO (with its attribute values) is batch-saved later
        if (createdNewCso && csoForExport != null)
        {
            await AddSecondaryExternalIdToCsoAsync(csoForExport, attributeChanges, exportRule, deferSave);
        }

        var csoId = csoForExport?.Id;

        // Check if a Pending Export already exists for this CSO in the in-memory batch list
        // (e.g., created by drift detection earlier in the same page). If so, merge our attribute
        // changes into the existing one to avoid duplicates. Export evaluation values take precedence
        // over drift values on attribute conflicts (export eval uses the latest MVO state).
        if (csoId.HasValue && changeType == PendingExportChangeType.Update && existingPendingExports != null)
        {
            var existingPendingExport = existingPendingExports
                .FirstOrDefault(pe => pe.ConnectedSystemObjectId == csoId.Value);

            if (existingPendingExport != null)
            {
                // Merge export eval changes into the existing drift PE. The merge semantics live in the pure
                // engine (#288 extraction): value-level merging for multi-valued attributes, export
                // evaluation winning a collision, and the #1199 whole-attribute supersede.
                using var inMemoryMergeSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("MergeIntoInMemoryPendingExport")
                    .SetTag("existingChangeCount", existingPendingExport.AttributeValueChanges.Count)
                    .SetTag("newChangeCount", attributeChanges.Count);

                var mergeResult = _syncEngine.MergeAttributeChangesIntoPendingExport(existingPendingExport, attributeChanges);

                if (mergeResult.ReplacedCount > 0 || mergeResult.AddedCount > 0)
                {
                    Log.Information("CreateOrUpdatePendingExportWithNoNetChangeAsync: Merged attribute changes into existing PendingExport {ExistingPeId} for CSO {CsoId}: " +
                        "{MergedCount} replaced (export eval wins), {AddedCount} added, total now {TotalCount}. Source: MVO {MvoId}",
                        existingPendingExport.Id, csoId.Value,
                        mergeResult.ReplacedCount, mergeResult.AddedCount, existingPendingExport.AttributeValueChanges.Count, mvo.Id);
                }
                else
                {
                    Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: All attribute changes for CSO {CsoId} already present in existing PendingExport {ExistingPeId}. No merge needed.",
                        csoId.Value, existingPendingExport.Id);
                }

                // Return null for PendingExport since we merged into an existing one (no new PE to batch-create)
                return (null, provisioningCso, csoAlreadyCurrentCount);
            }
        }

        // Fallback: check if a Pending Export exists in the database from a previous activity
        // (e.g., drift detection ran in a previous sync step and its PE hasn't been exported yet,
        // or a previous sync created a pending Create export that hasn't been exported yet).
        // If found, delete the old PE and return a new merged PE for batch creation.
        if (csoId.HasValue && (changeType == PendingExportChangeType.Update || changeType == PendingExportChangeType.Create))
        {
            PendingExport? dbPendingExport;
            using (var peLookupSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("GetPendingExportByCsoIdForMerge")
                .SetTag("leanFetch", true))
            {
                // Lean fetch (issue #986): the merge logic below only ever reads Id and
                // AttributeValueChanges off dbPendingExport, never ConnectedSystemObject,
                // SourceMetaverseObject or ConnectedSystem. The heavy GetPendingExportByConnectedSystemObjectIdAsync
                // also loads those CSO/MVO attribute value graphs, which for a large group can run into
                // the hundreds of thousands of rows and dominated this fetch (measured 99.5% of merge cost).
                dbPendingExport = await SyncRepo.GetPendingExportLightweightByConnectedSystemObjectIdAsync(csoId.Value);
                peLookupSpan.SetTag("found", dbPendingExport != null);
                peLookupSpan.SetTag("existingChangeCount", dbPendingExport?.AttributeValueChanges.Count ?? 0);
                peLookupSpan.SetSuccess();
            }

            if (dbPendingExport != null)
            {
                // Reference recall: an existing Delete export wins (#1003). The object is being
                // deprovisioned from the target, so a membership removal is moot; merging would
                // replace the Delete with an Update and leave the object alive in the target
                // forever (a pre-#1003 defect).
                if (recallSemantics && dbPendingExport.ChangeType == PendingExportChangeType.Delete)
                {
                    Log.Information("CreateOrUpdatePendingExportWithNoNetChangeAsync: CSO {CsoId} has a pending Delete export; " +
                        "recall skipping {ChangeCount} change(s) (deprovisioning supersedes membership updates)",
                        csoId, attributeChanges.Count);
                    return (null, provisioningCso, csoAlreadyCurrentCount);
                }

                // Build merged attribute changes: start with export eval changes (takes precedence),
                // then add any drift-only changes not superseded by export eval (see
                // SelectSurvivingDriftChanges).
                // Clone drift-only changes with new IDs because DeletePendingExportAsync cascade-deletes
                // child entities, making the tracked instances unusable for a new PE.
                var driftOnlyChanges = SelectSurvivingDriftChanges(attributeChanges, dbPendingExport.AttributeValueChanges)
                    .Select(avc => new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        AttributeId = avc.AttributeId,
                        Attribute = avc.Attribute,
                        StringValue = avc.StringValue,
                        DateTimeValue = avc.DateTimeValue,
                        IntValue = avc.IntValue,
                        LongValue = avc.LongValue,
                        DecimalValue = avc.DecimalValue,
                        ByteValue = avc.ByteValue,
                        GuidValue = avc.GuidValue,
                        BoolValue = avc.BoolValue,
                        UnresolvedReferenceValue = avc.UnresolvedReferenceValue,
                        ResolvedReferenceCsoId = avc.ResolvedReferenceCsoId,
                        ChangeType = avc.ChangeType
                    })
                    .ToList();

                var mergedChanges = new List<PendingExportAttributeValueChange>(attributeChanges);
                mergedChanges.AddRange(driftOnlyChanges);

                Log.Information("CreateOrUpdatePendingExportWithNoNetChangeAsync: Found existing PendingExport {ExistingPeId} in database for CSO {CsoId}. " +
                    "Deleting old PE and creating merged replacement with {ExportEvalCount} export eval + {DriftOnlyCount} drift-only = {TotalCount} attribute changes. Source: MVO {MvoId}",
                    dbPendingExport.Id, csoId.Value,
                    attributeChanges.Count, driftOnlyChanges.Count, mergedChanges.Count, mvo.Id);

                // Delete the old PE from the database
                using (var deleteSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("DeletePendingExportForMerge")
                    .SetTag("attributeChangeCount", dbPendingExport.AttributeValueChanges.Count))
                {
                    await SyncRepo.DeletePendingExportAsync(dbPendingExport);
                    deleteSpan.SetSuccess();
                }

                // Replace attributeChanges with merged set so the new PE created below includes everything
                attributeChanges = mergedChanges;
            }
        }

        // Check if any attribute changes have unresolved reference values
        // This is used to defer exports with reference attributes until the referenced objects have been exported
        var hasUnresolvedReferences = attributeChanges.Any(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue));

        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = exportRule.ConnectedSystemId,
            ConnectedSystemObjectId = csoId,
            ChangeType = changeType,
            Status = PendingExportStatus.Pending,
            SourceMetaverseObjectId = mvo.Id,
            AttributeValueChanges = attributeChanges,
            CreatedAt = DateTime.UtcNow,
            HasUnresolvedReferences = hasUnresolvedReferences,
            ProvisioningSyncRuleId = ProvisioningRuleFor(changeType, exportRule)
        };

        if (hasUnresolvedReferences)
        {
            Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: PendingExport {ExportId} has {Count} unresolved reference(s), will be deferred for resolution",
                pendingExport.Id, attributeChanges.Count(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue)));
        }

        // Save immediately unless caller requested deferred saving for batch operations
        if (!deferSave)
        {
            using (JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("SavePendingExport"))
            {
                await SyncRepo.CreatePendingExportAsync(pendingExport);
            }
        }

        Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: Created {ChangeType} PendingExport {ExportId} for MVO {MvoId} with {AttrCount} attribute changes (skipped {SkippedCount} no-net-change, deferSave={DeferSave})",
            changeType, pendingExport.Id, mvo.Id, attributeChanges.Count, csoAlreadyCurrentCount, deferSave);

        return (pendingExport, provisioningCso, csoAlreadyCurrentCount);
    }

    /// <summary>
    /// Creates a Connected System Object with PendingProvisioning status for provisioning scenarios.
    /// This establishes the CSO↔MVO relationship before the object exists in the target system,
    /// ensuring that the subsequent import will correctly join rather than create a duplicate.
    /// </summary>
    /// <param name="mvo">The Metaverse Object being provisioned.</param>
    /// <param name="exportRule">The export rule triggering the provisioning.</param>
    /// <param name="deferSave">When true, the CSO is not saved to the database. The caller is responsible
    /// for batch saving the CSO. Default is false for backwards compatibility.</param>
    private async Task<ConnectedSystemObject> CreatePendingProvisioningCsoAsync(
        MetaverseObject mvo,
        SyncRule exportRule,
        bool deferSave = false)
    {
        if (exportRule.ConnectedSystemObjectType == null)
            throw new InvalidOperationException($"Export rule {exportRule.Name} has no ConnectedSystemObjectType configured.");

        // Find the external ID and secondary external ID attributes from the object type
        var externalIdAttribute = exportRule.ConnectedSystemObjectType.Attributes
            .FirstOrDefault(a => a.IsExternalId);
        var secondaryExternalIdAttribute = exportRule.ConnectedSystemObjectType.Attributes
            .FirstOrDefault(a => a.IsSecondaryExternalId);

        // Only set FK properties, not navigation properties, to avoid EF Core change tracker conflicts.
        // When both are set on a new entity, EF Core might try to track the related entity (MVO)
        // which can cause issues if that entity is already tracked in a different state.
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = exportRule.ConnectedSystemId,
            TypeId = exportRule.ConnectedSystemObjectType.Id,
            Status = ConnectedSystemObjectStatus.PendingProvisioning,
            JoinType = ConnectedSystemObjectJoinType.Provisioned,
            MetaverseObjectId = mvo.Id,
            DateJoined = DateTime.UtcNow,
            Created = DateTime.UtcNow,
            ExternalIdAttributeId = externalIdAttribute?.Id ?? 0,
            SecondaryExternalIdAttributeId = secondaryExternalIdAttribute?.Id
        };

        // Note: We don't add the CSO to the MVO's collection here because:
        // 1. The MVO might be loaded with tracking, which could interfere with the save
        // 2. The navigation collection is not needed for our purposes - we use the FK
        // The relationship is established via MetaverseObjectId = mvo.Id

        // Save immediately unless caller requested deferred saving for batch operations
        if (!deferSave)
        {
            await SyncRepo.CreateConnectedSystemObjectAsync(cso);
        }

        Log.Debug("CreatePendingProvisioningCsoAsync: Created PendingProvisioning CSO {CsoId} for MVO {MvoId} in system {SystemId} (deferSave={DeferSave})",
            cso.Id, mvo.Id, exportRule.ConnectedSystemId, deferSave);

        return cso;
    }

    /// <summary>
    /// Adds the secondary external ID value to a PendingProvisioning CSO so that confirming import
    /// can find the CSO by secondary external ID (e.g. distinguishedName) when matching.
    /// This is essential for the confirming import to match PendingProvisioning CSOs that don't yet
    /// have a primary external ID (which is typically system-assigned, like objectGUID in AD).
    /// </summary>
    /// <param name="cso">The CSO to add the secondary external ID to.</param>
    /// <param name="attributeChanges">The attribute changes containing the secondary ID value.</param>
    /// <param name="exportRule">The export rule (unused but kept for signature consistency).</param>
    /// <param name="deferSave">When true, the CSO update is not persisted. The caller is responsible
    /// for batch saving the CSO. Default is false for backwards compatibility.</param>
    private async Task AddSecondaryExternalIdToCsoAsync(
        ConnectedSystemObject cso,
        List<PendingExportAttributeValueChange> attributeChanges,
        SyncRule exportRule,
        bool deferSave = false)
    {
        if (cso.SecondaryExternalIdAttributeId == null)
        {
            Log.Debug("AddSecondaryExternalIdToCsoAsync: CSO {CsoId} has no secondary external ID attribute configured",
                cso.Id);
            return;
        }

        // Find the secondary external ID value in the attribute changes
        var secondaryIdChange = attributeChanges.FirstOrDefault(ac =>
            ac.AttributeId == cso.SecondaryExternalIdAttributeId);

        if (secondaryIdChange == null)
        {
            Log.Warning("AddSecondaryExternalIdToCsoAsync: No secondary external ID value found in attribute changes for CSO {CsoId}. " +
                "Confirming import may not be able to match this CSO.",
                cso.Id);
            return;
        }

        // Create the attribute value on the CSO
        var attributeValue = new ConnectedSystemObjectAttributeValue
        {
            ConnectedSystemObject = cso,
            AttributeId = secondaryIdChange.AttributeId,
            StringValue = secondaryIdChange.StringValue,
            IntValue = secondaryIdChange.IntValue,
            DateTimeValue = secondaryIdChange.DateTimeValue,
            ByteValue = secondaryIdChange.ByteValue
        };

        // Add to CSO in-memory
        cso.AttributeValues ??= new List<ConnectedSystemObjectAttributeValue>();
        cso.AttributeValues.Add(attributeValue);

        // SPEC-1082 D9: this writes an attribute value outside the Full Import stamp path (D6/D7).
        // Null both columns in-memory (for the !deferSave EF-tracked SaveChanges path below) AND via
        // an explicit StampImportStateAsync(null, null) call, which persists regardless of
        // deferSave/persistence mechanism - the deferred batch flush path does not read these
        // in-memory properties, so relying on the in-memory assignment alone would leave a stale
        // hash in the database when deferSave is true. Guarded on the loaded values: this method
        // runs once per provisioned object, and freshly provisioned PendingProvisioning CSOs are
        // born with NULL import state (D6 creates NULL-write both columns), so an unguarded call
        // would add one no-op UPDATE round trip per object at bulk provisioning scale.
        if (cso.ImportStateHash != null || cso.ImportStateFingerprint != null)
        {
            cso.ImportStateHash = null;
            cso.ImportStateFingerprint = null;
            await SyncRepo.StampImportStateAsync([(cso.Id, (Guid?)null, (Guid?)null)]);
        }

        // Persist immediately unless caller requested deferred saving for batch operations
        if (!deferSave)
        {
            await SyncRepo.UpdateConnectedSystemObjectAsync(cso);

            // Add to lookup cache so confirming imports can find this PendingProvisioning CSO
            // by secondary external ID without a DB round-trip.
            // When deferSave=true, the caller (FlushPendingExportOperationsAsync) handles cache population.
            if (secondaryIdChange.StringValue != null)
                Application.ConnectedSystems.AddCsoToCache(cso.ConnectedSystemId, cso.SecondaryExternalIdAttributeId.Value, secondaryIdChange.StringValue, cso.Id);
        }

        Log.Debug("AddSecondaryExternalIdToCsoAsync: Added secondary external ID value '{SecondaryIdValue}' to CSO {CsoId} for confirming import matching (deferSave={DeferSave})",
            LogSanitiser.Sanitise(secondaryIdChange.StringValue ?? secondaryIdChange.IntValue?.ToString() ?? "unknown"), cso.Id, deferSave);
    }

    /// <summary>
    /// Creates PendingExportAttributeValueChange objects based on export rule mappings, mapping MVO
    /// attributes to CSO attributes. The computation lives in the pure engine (#288 extraction:
    /// <c>SyncEngine.ComputeAttributeValueChanges</c>); this wrapper supplies the server's expression
    /// evaluator so existing callers and characterisation tests keep one entry point with unchanged
    /// semantics.
    /// </summary>
    internal List<PendingExportAttributeValueChange> CreateAttributeValueChanges(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes,
        PendingExportChangeType changeType,
        ConnectedSystemObject? existingCso,
        ILookup<(Guid CsoId, int AttributeId), ConnectedSystemObjectAttributeValue>? csoAttributeCache,
        out int csoAlreadyCurrentCount,
        HashSet<MetaverseObjectAttributeValue>? removedAttributes = null,
        Dictionary<string, object?>? mvAttributeDictionary = null,
        IReadOnlyDictionary<Guid, string>? preResolvedReferenceValues = null,
        List<AttributeFlowError>? flowErrors = null)
        => _syncEngine.ComputeAttributeValueChanges(mvo, exportRule, changedAttributes, changeType,
            existingCso, csoAttributeCache, out csoAlreadyCurrentCount, ExpressionEvaluator,
            removedAttributes, mvAttributeDictionary, preResolvedReferenceValues, flowErrors);

    /// <summary>
    /// Compares a Pending Export attribute value change against existing CSO attribute values to determine
    /// if they represent a no-net-change (the CSO already has the target state). The comparison lives in the
    /// pure engine (#288 extraction); this wrapper keeps the established entry point.
    /// </summary>
    public static bool IsCsoAttributeAlreadyCurrent(
        PendingExportAttributeValueChange pendingChange,
        IEnumerable<ConnectedSystemObjectAttributeValue>? existingValues)
        => SyncEngine.IsCsoAttributeAlreadyCurrent(pendingChange, existingValues);

    /// <summary>
    /// Builds a dictionary of attribute values from a Metaverse Object for expression evaluation. The
    /// computation lives in the pure engine (#288 extraction); this wrapper keeps the established entry point.
    /// </summary>
    internal Dictionary<string, object?> BuildAttributeDictionary(MetaverseObject mvo)
        => SyncEngine.BuildAttributeDictionary(mvo);

    /// <summary>
    /// Checks whether any of the changed MVO attributes are relevant to the given export rule.
    /// An attribute is relevant if it is a direct source for one of the rule's Attribute Flow mappings,
    /// or if the rule has expression-based mappings (which may depend on any changed attribute).
    /// Used to avoid replacing an existing Create PE on a PendingProvisioning CSO when the current
    /// sync's changes are entirely unrelated to this export rule.
    /// </summary>
    internal static bool HasRelevantChangedAttributes(
        List<MetaverseObjectAttributeValue> changedAttributes,
        SyncRule exportRule)
        => SyncEngine.HasRelevantChangedAttributes(changedAttributes, exportRule);

    /// <summary>
    /// Generates a composite key for a PendingExportAttributeValueChange that identifies
    /// the specific attribute+value combination. Used to deduplicate when merging export
    /// evaluation changes with drift correction changes. For multi-valued attributes like
    /// group membership, each individual value (e.g., each member DN) gets a distinct key,
    /// allowing both sources to contribute different values for the same attribute.
    /// </summary>
    internal static string GetAttributeChangeKey(PendingExportAttributeValueChange change)
        => SyncEngine.GetAttributeChangeKey(change);

    /// <summary>
    /// Returns a merge key for deduplicating attribute changes when combining Pending Exports.
    /// For single-valued attributes, the key is just the attribute ID — the newest change always
    /// wins regardless of value. For multi-valued attributes, the key includes the value so that
    /// distinct values (e.g., different group members) are preserved during merge.
    /// </summary>
    internal static string GetAttributeChangeMergeKey(PendingExportAttributeValueChange change)
        => SyncEngine.GetAttributeChangeMergeKey(change);

    /// <summary>
    /// Selects the changes on an existing (stale, typically drift-staged) Pending Export that survive a merge with a
    /// newly evaluated set of export changes. Export evaluation always wins on a collision, because it derives from
    /// the latest Metaverse Object state.
    /// </summary>
    /// <param name="incomingChanges">The newly evaluated export changes, which take precedence.</param>
    /// <param name="existingChanges">The changes already staged on the Pending Export being merged into.</param>
    /// <returns>The existing changes that are not superseded, in their original order.</returns>
    internal static List<PendingExportAttributeValueChange> SelectSurvivingDriftChanges(
        IReadOnlyCollection<PendingExportAttributeValueChange> incomingChanges,
        IEnumerable<PendingExportAttributeValueChange> existingChanges)
        => SyncEngine.SelectSurvivingDriftChanges(incomingChanges, existingChanges);

    /// <summary>
    /// The attribute ids among a set of changes whose change type sets the attribute's ENTIRE value set rather than
    /// one value within it (#1199). <see cref="PendingExportAttributeChangeType.Update"/> and
    /// <see cref="PendingExportAttributeChangeType.RemoveAll"/> both export as a replace, so every other staged change
    /// for the same attribute is superseded, whatever its value or change type.
    /// </summary>
    /// <remarks>
    /// The merge key alone is not enough to catch this. It keys multi-valued attributes by value, which is right for
    /// genuine per-value adds and removals, but a change's type follows the Metaverse attribute's plurality while the
    /// key follows the Connected System attribute's: a single-valued Metaverse attribute flowing to a multi-valued
    /// Connected System attribute (a Metaverse "Job Title" flowing to LDAP's multi-valued <c>title</c>) therefore
    /// produces an Update whose key still carries a value. A stale per-value Remove for the same attribute then
    /// survives the merge, and the connector emits the replace followed by a delete of a value the replace has already
    /// removed. LDAP rejects that modify atomically ("modify/delete: title: no such value"), so the export never
    /// applies and retries until it exhausts its attempts.
    /// </remarks>
    internal static HashSet<int> GetWholeAttributeReplacementAttributeIds(IEnumerable<PendingExportAttributeValueChange> changes)
        => SyncEngine.GetWholeAttributeReplacementAttributeIds(changes);

    /// <summary>
    /// Attempts to find an existing CSO in the target system that matches the MVO using Object Matching Rules.
    /// This prevents provisioning duplicates when the object already exists in the target.
    /// </summary>
    private async Task<ConnectedSystemObject?> AttemptExportMatchingAsync(MetaverseObject mvo, SyncRule exportRule)
    {
        if (exportRule.ConnectedSystem == null || exportRule.ConnectedSystemObjectType == null)
            return null;

        // Resolve matching rules based on mode
        List<ObjectMatchingRule> matchingRules;
        if (exportRule.ConnectedSystem.ObjectMatchingRuleMode == ObjectMatchingRuleMode.ConnectedSystem)
        {
            // Simple mode: rules from the object type
            matchingRules = exportRule.ConnectedSystemObjectType.ObjectMatchingRules?.ToList() ?? new List<ObjectMatchingRule>();
        }
        else
        {
            // Advanced mode: rules from the Synchronisation Rule
            matchingRules = exportRule.ObjectMatchingRules?.ToList() ?? new List<ObjectMatchingRule>();
        }

        if (matchingRules.Count == 0)
            return null;

        try
        {
            return await Application.ObjectMatching.FindMatchingConnectedSystemObjectAsync(
                mvo,
                exportRule.ConnectedSystem,
                exportRule.ConnectedSystemObjectType,
                matchingRules);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AttemptExportMatchingAsync: Error during export matching for MVO {MvoId} to system {SystemId}",
                mvo.Id, exportRule.ConnectedSystemId);
            return null;
        }
    }
}
