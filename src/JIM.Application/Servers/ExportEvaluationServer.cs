// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using DynamicExpresso.Exceptions;
using JIM.Application.Expressions;
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
    /// </summary>
    /// <param name="sourceConnectedSystemId">The source system ID (to exclude from export evaluation via Q3).</param>
    /// <param name="preloadedSyncRules">Optional pre-loaded Synchronisation Rules to avoid redundant database query.</param>
    /// <returns>A cache object to pass to evaluation methods.</returns>
    public async Task<ExportEvaluationCache> BuildExportEvaluationCacheAsync(
        int sourceConnectedSystemId,
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

        // Get all target system IDs (excluding source system - Q3)
        var targetSystemIds = exportRules
            .Select(sr => sr.ConnectedSystemId)
            .Where(id => id != sourceConnectedSystemId)
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
    /// <param name="sourceSystem">The Connected System that caused this change (for Q3 circular prevention)</param>
    /// <returns>List of PendingExports that were created</returns>
    public async Task<List<PendingExport>> EvaluateExportRulesAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ConnectedSystem? sourceSystem = null)
    {
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateExportRulesAsync: MVO {MvoId} has no type set, cannot evaluate export rules", mvo.Id);
            return pendingExports;
        }

        // Get all enabled export rules for this MVO's object type
        var exportRules = await GetExportRulesForObjectTypeAsync(mvo.Type.Id);

        foreach (var exportRule in exportRules)
        {
            // Q3: Skip if this is the source system (circular sync prevention)
            if (sourceSystem != null && exportRule.ConnectedSystemId == sourceSystem.Id)
            {
                Log.Debug("EvaluateExportRulesAsync: Skipping export to {System} - it is the source of these changes (Q3 circular prevention)",
                    exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString());
                continue;
            }

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
    /// <param name="sourceSystem">The Connected System that caused this change (for Q3 circular prevention)</param>
    /// <returns>List of PendingExports for deprovisioning actions</returns>
    public async Task<List<PendingExport>> EvaluateOutOfScopeExportsAsync(
        MetaverseObject mvo,
        ConnectedSystem? sourceSystem = null)
    {
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateOutOfScopeExportsAsync: MVO {MvoId} has no type set, cannot evaluate scope", mvo.Id);
            return pendingExports;
        }

        // Get all enabled export rules for this MVO's object type
        var exportRules = await GetExportRulesForObjectTypeAsync(mvo.Type.Id);

        foreach (var exportRule in exportRules)
        {
            // Q3: Skip if this is the source system (circular sync prevention)
            if (sourceSystem != null && exportRule.ConnectedSystemId == sourceSystem.Id)
            {
                continue;
            }

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

            Log.Information("EvaluateOutOfScopeExportsAsync: MVO {MvoId} is out of scope for export rule {RuleName}. Handling deprovisioning for CSO {CsoId}",
                mvo.Id, exportRule.Name, existingCso.Id);

            // Handle based on OutboundDeprovisionAction
            var pendingExport = await HandleOutboundDeprovisioningAsync(mvo, existingCso, exportRule);
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
    /// <param name="sourceSystem">The Connected System that caused this change (for Q3 circular prevention).</param>
    /// <param name="cache">The pre-loaded cache from BuildExportEvaluationCacheAsync.</param>
    /// <returns>List of PendingExports that were created.</returns>
    public async Task<List<PendingExport>> EvaluateExportRulesAsync(
        MetaverseObject mvo,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ConnectedSystem? sourceSystem,
        ExportEvaluationCache cache)
    {
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateExportRulesAsync: MVO {MvoId} has no type set, cannot evaluate export rules", mvo.Id);
            return pendingExports;
        }

        // Get export rules from cache instead of database query
        if (!cache.ExportRulesByMvoTypeId.TryGetValue(mvo.Type.Id, out var exportRules))
        {
            // No export rules for this MVO type
            return pendingExports;
        }

        foreach (var exportRule in exportRules)
        {
            // Q3: Skip if this is the source system (circular sync prevention)
            if (sourceSystem != null && exportRule.ConnectedSystemId == sourceSystem.Id)
            {
                Log.Debug("EvaluateExportRulesAsync: Skipping export to {System} - it is the source of these changes (Q3 circular prevention)",
                    exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString());
                continue;
            }

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
    /// <param name="sourceSystem">The Connected System that caused this change (for Q3 circular prevention).</param>
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
        ConnectedSystem? sourceSystem,
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

        var skippedDueToSource = 0;
        var skippedDueToScope = 0;

        // Build the MVO attribute dictionary once for all export rules — avoids rebuilding
        // per-rule when the same MVO is evaluated against multiple export rules with expressions.
        Dictionary<string, object?>? mvAttributeDictionary = null;

        using var loopSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("EvaluateExportRuleLoop");
        loopSpan.SetTag("ruleCount", exportRules.Count);
        loopSpan.SetTag("mvoId", mvo.Id);

        foreach (var exportRule in exportRules)
        {
            // Q3: Skip if this is the source system (circular sync prevention)
            if (sourceSystem != null && exportRule.ConnectedSystemId == sourceSystem.Id)
            {
                Log.Debug("EvaluateExportRulesWithNoNetChangeDetectionAsync: Skipping export to {System} - it is the source of these changes (Q3 circular prevention)",
                    exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString());
                skippedDueToSource++;
                continue;
            }

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
                    mvAttributeDictionary, preResolvedForSystem, recallSemantics, result.AttributeFlowErrors);

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

        loopSpan.SetTag("skippedDueToSource", skippedDueToSource);
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
    /// <param name="sourceSystem">The Connected System that caused this change (for Q3 circular prevention).</param>
    /// <param name="cache">The pre-loaded cache from BuildExportEvaluationCacheAsync.</param>
    /// <returns>List of PendingExports for deprovisioning actions.</returns>
    public async Task<List<PendingExport>> EvaluateOutOfScopeExportsAsync(
        MetaverseObject mvo,
        ConnectedSystem? sourceSystem,
        ExportEvaluationCache cache)
    {
        var pendingExports = new List<PendingExport>();

        if (mvo.Type == null)
        {
            Log.Warning("EvaluateOutOfScopeExportsAsync: MVO {MvoId} has no type set, cannot evaluate scope", mvo.Id);
            return pendingExports;
        }

        // Get export rules from cache instead of database query
        if (!cache.ExportRulesByMvoTypeId.TryGetValue(mvo.Type.Id, out var exportRules))
        {
            // No export rules for this MVO type
            return pendingExports;
        }

        foreach (var exportRule in exportRules)
        {
            // Q3: Skip if this is the source system (circular sync prevention)
            if (sourceSystem != null && exportRule.ConnectedSystemId == sourceSystem.Id)
            {
                continue;
            }

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

            Log.Information("EvaluateOutOfScopeExportsAsync: MVO {MvoId} is out of scope for export rule {RuleName}. Handling deprovisioning for CSO {CsoId}",
                mvo.Id, exportRule.Name, existingCso.Id);

            // Handle based on OutboundDeprovisionAction
            var pendingExport = await HandleOutboundDeprovisioningAsync(mvo, existingCso, exportRule);
            if (pendingExport != null)
            {
                pendingExports.Add(pendingExport);
            }
        }

        return pendingExports;
    }

    /// <summary>
    /// Handles deprovisioning based on the Synchronisation Rule's OutboundDeprovisionAction setting.
    /// </summary>
    private async Task<PendingExport?> HandleOutboundDeprovisioningAsync(
        MetaverseObject mvo,
        ConnectedSystemObject cso,
        SyncRule exportRule)
    {
        switch (exportRule.OutboundDeprovisionAction)
        {
            case OutboundDeprovisionAction.Disconnect:
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

                // Check if this was the last connector for the MVO
                if (mvo.ConnectedSystemObjects.Count == 0 && mvo.Origin == MetaverseObjectOrigin.Projected)
                {
                    // Handle MVO deletion rules (set LastConnectorDisconnectedDate)
                    if (mvo.Type?.DeletionRule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected)
                    {
                        mvo.LastConnectorDisconnectedDate = DateTime.UtcNow;
                        Log.Information("HandleOutboundDeprovisioningAsync: MVO {MvoId} has no more connectors. LastConnectorDisconnectedDate set to {Date}",
                            mvo.Id, mvo.LastConnectorDisconnectedDate);
                    }
                }

                return null; // No Pending Export needed for disconnect

            case OutboundDeprovisionAction.Delete:
                // Create (or reclaim) a Delete PendingExport for this CSO. The helper handles
                // the collision case where a previous export's PE is still attached to the CSO
                // because the next confirming import hasn't run yet to reconcile it away.
                Log.Information("HandleOutboundDeprovisioningAsync: Ensuring delete PendingExport for CSO {CsoId} (OutboundDeprovisionAction=Delete)",
                    cso.Id);

                return await EnsureDeletePendingExportAsync(cso, mvo.Id);

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
        ExportEvaluationCache? exportEvaluationCache = null)
        => await EvaluateMvoDeletionsAsync([mvo], exportEvaluationCache);

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
        ExportEvaluationCache? exportEvaluationCache = null)
    {
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
        // have joined CSOs, so no per-MVO lookup or implicit filtering is needed.
        var csoIdsToDisconnect = new List<Guid>();
        var csosToDelete = new List<(ConnectedSystemObject Cso, Guid MvoId)>();
        var disconnectedByRuleCount = 0;
        var noMatchingRuleCount = 0;
        foreach (var (mvoId, joinedCsos) in csosByMvo)
        {
            List<SyncRule>? typeExportRules = null;
            if (!mvoTypeIdsByMvoId.TryGetValue(mvoId, out var mvoTypeId) || mvoTypeId == null)
            {
                Log.Warning("EvaluateMvoDeletionsAsync: MVO {MvoId} has no Type set; cannot match export Synchronisation Rules. Its CSOs will be disconnected only.",
                    mvoId);
            }
            else
            {
                exportRulesByMvoTypeId.TryGetValue(mvoTypeId.Value, out typeExportRules);
            }

            foreach (var cso in joinedCsos)
            {
                csoIdsToDisconnect.Add(cso.Id);

                var matchingRules = typeExportRules?
                    .Where(r => r.ConnectedSystemId == cso.ConnectedSystemId && r.ConnectedSystemObjectTypeId == cso.TypeId)
                    .ToList();
                if (matchingRules == null || matchingRules.Count == 0)
                {
                    noMatchingRuleCount++;
                    Log.Debug("EvaluateMvoDeletionsAsync: No export Synchronisation Rule matches CSO {CsoId} (system {SystemId}, object type {TypeId}); disconnecting only",
                        cso.Id, cso.ConnectedSystemId, cso.TypeId);
                    continue;
                }

                var deleteRule = matchingRules.Find(r => r.OutboundDeprovisionAction == OutboundDeprovisionAction.Delete);
                if (deleteRule == null)
                {
                    disconnectedByRuleCount++;
                    Log.Debug("EvaluateMvoDeletionsAsync: CSO {CsoId} matches export Synchronisation Rule(s) whose action is Disconnect; disconnecting only",
                        cso.Id);
                    continue;
                }

                if (matchingRules.Count > 1 && matchingRules.Exists(r => r.OutboundDeprovisionAction != OutboundDeprovisionAction.Delete))
                {
                    Log.Information("EvaluateMvoDeletionsAsync: {RuleCount} export Synchronisation Rules match CSO {CsoId} with conflicting deprovisioning actions; Delete wins via rule '{RuleName}'",
                        matchingRules.Count, cso.Id, LogSanitiser.Sanitise(deleteRule.Name));
                }

                Log.Information("EvaluateMvoDeletionsAsync: Staging delete Pending Export for CSO {CsoId} (join type {JoinType}) per export Synchronisation Rule '{RuleName}'",
                    cso.Id, cso.JoinType, LogSanitiser.Sanitise(deleteRule.Name));
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
                if (existingPesByCsoId.TryGetValue(cso.Id, out var existingPe))
                {
                    if (existingPe.ChangeType == PendingExportChangeType.Delete)
                    {
                        Log.Information("EvaluateMvoDeletionsAsync: Delete PendingExport {ExistingPeId} already exists for CSO {CsoId} (status: {Status}). Reusing.",
                            existingPe.Id, cso.Id, existingPe.Status);
                        pendingExports.Add(existingPe);
                        continue;
                    }

                    Log.Information("EvaluateMvoDeletionsAsync: Replacing existing {ChangeType} PendingExport {ExistingPeId} for CSO {CsoId} with Delete PE",
                        existingPe.ChangeType, existingPe.Id, cso.Id);
                    replacedPeCsoIds.Add(cso.Id);
                }

                // Build the secondary external ID (e.g. DN for LDAP) as an attribute change to
                // attach to the PE. The CSO will be disconnected from the MVO right after this and
                // may be deleted by housekeeping before the export runs; connectors like LDAP need
                // the DN preserved on the PE to perform the actual delete.
                var attributeValueChanges = new List<PendingExportAttributeValueChange>();
                var secondaryIdAttrValue = cso.SecondaryExternalIdAttributeValue;
                if (secondaryIdAttrValue?.Attribute != null && !string.IsNullOrEmpty(secondaryIdAttrValue.StringValue))
                {
                    attributeValueChanges.Add(new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        Attribute = secondaryIdAttrValue.Attribute,
                        AttributeId = secondaryIdAttrValue.Attribute.Id,
                        StringValue = secondaryIdAttrValue.StringValue,
                        ChangeType = PendingExportAttributeChangeType.Update
                    });

                    Log.Debug("EvaluateMvoDeletionsAsync: Will store secondary external ID '{Value}' (attr {AttrName}) on delete PE for CSO {CsoId}",
                        LogSanitiser.Sanitise(secondaryIdAttrValue.StringValue), secondaryIdAttrValue.Attribute.Name, cso.Id);
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
    /// <param name="attributeValueChanges">
    /// Optional attribute value changes to attach to a freshly-created PE (for example, the
    /// secondary external ID so a connector can still resolve the target DN after the CSO
    /// is detached from the MVO). Ignored when reusing an existing Delete PE.
    /// </param>
    /// <returns>The Delete PendingExport for the CSO — either the existing one reused, or a newly created one.</returns>
    private async Task<PendingExport> EnsureDeletePendingExportAsync(
        ConnectedSystemObject cso,
        Guid sourceMetaverseObjectId,
        List<PendingExportAttributeValueChange>? attributeValueChanges = null)
    {
        // Lean fetch (issue #986): this method only reads ChangeType/Id/Status off the existing
        // Pending Export and passes it to DeletePendingExportAsync, which needs AttributeValueChanges
        // loaded for EF-tracked child-row disposal. The heavy fetch also loaded the CSO's and source
        // Metaverse Object's full attribute value graphs, which for a large group CSO (group
        // deprovisioning) runs into the hundreds of thousands of rows, none of them read here.
        var existingPe = await SyncRepo.GetPendingExportLightweightByConnectedSystemObjectIdAsync(cso.Id);

        if (existingPe != null)
        {
            if (existingPe.ChangeType == PendingExportChangeType.Delete)
            {
                Log.Information("EnsureDeletePendingExportAsync: Delete PendingExport {ExistingPeId} already exists for CSO {CsoId} (status: {Status}). Reusing.",
                    existingPe.Id, cso.Id, existingPe.Status);
                return existingPe;
            }

            Log.Information("EnsureDeletePendingExportAsync: Replacing existing {ChangeType} PendingExport {ExistingPeId} for CSO {CsoId} with Delete PE",
                existingPe.ChangeType, existingPe.Id, cso.Id);
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
    private static string? ResolveCsoReferenceValue(ConnectedSystemObject cso)
    {
        var resolvedAttr =
            cso.AttributeValues.FirstOrDefault(av => av.Attribute?.IsSecondaryExternalId == true) ??
            cso.AttributeValues.FirstOrDefault(av => av.Attribute?.IsExternalId == true);

        return resolvedAttr?.StringValue ??
               resolvedAttr?.GuidValue?.ToString() ??
               resolvedAttr?.IntValue?.ToString();
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

        // Run-scoped cache preferred (#1003): the sync processors build one recall cache per run
        // (sourceConnectedSystemId 0: recall is state assertion, so no source system is excluded;
        // Q3 does not apply to deletions). Callers without one (housekeeping) build it ad hoc.
        var cache = recallCache;
        if (cache == null)
        {
            using var cacheSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("RecallBuildCache");
            cache = await BuildExportEvaluationCacheAsync(sourceConnectedSystemId: 0);
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

                PendingExportAttributeValueChange change;
                if (flow.SourcePlurality == AttributePlurality.MultiValued)
                {
                    // A multi-valued removal must name the value to remove; a matched row with no
                    // resolvable value (matched by resolved reference only) cannot be staged.
                    if (removalValue == null)
                    {
                        result.UnresolvableChangesDropped++;
                        continue;
                    }
                    change = new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        Attribute = flow.TargetAttribute,
                        AttributeId = flow.TargetAttribute.Id,
                        ChangeType = PendingExportAttributeChangeType.Remove,
                        StringValue = removalValue
                    };
                }
                else
                {
                    // Single-valued reference removal: an all-null clearing Update, the same shape
                    // full evaluation produces. Staged only because the target still holds the
                    // deleted reference (the existence query matched it).
                    change = new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        Attribute = flow.TargetAttribute,
                        AttributeId = flow.TargetAttribute.Id,
                        ChangeType = PendingExportAttributeChangeType.Update
                    };
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
            if (existingPendingExports.TryGetValue(csoId, out var existingPendingExport))
            {
                if (existingPendingExport.ChangeType == PendingExportChangeType.Delete)
                {
                    result.SkippedDueToExistingDeletePendingExport++;
                    Log.Information("StageRecallFastPathAsync: CSO {CsoId} has a pending Delete export; skipping " +
                        "{ChangeCount} recall change(s) (deprovisioning supersedes membership updates)",
                        csoId, changesByMergeKey.Count);
                    continue;
                }
                if (existingPendingExport.ChangeType == PendingExportChangeType.Create)
                {
                    Log.Warning("StageRecallFastPathAsync: CSO {CsoId} has a pending Create export but is not " +
                        "PendingProvisioning; skipping recall changes to preserve the provisioning export", csoId);
                    continue;
                }

                // Merge the existing Update export's changes in (recall wins on merge-key
                // collisions), cloning with fresh ids because the delete-then-create persistence
                // removes the old rows. Changes whose unresolved reference is a deleted object are
                // purged: they can never resolve, and would wedge the merged export in
                // deferred-resolution limbo.
                foreach (var existingChange in existingPendingExport.AttributeValueChanges)
                {
                    if (existingChange.UnresolvedReferenceValue != null &&
                        Guid.TryParse(existingChange.UnresolvedReferenceValue, out var unresolvedMvoId) &&
                        deletedIds.Contains(unresolvedMvoId))
                    {
                        Log.Debug("StageRecallFastPathAsync: Purged change {ChangeId} from Pending Export {PendingExportId}: " +
                            "its unresolved reference is deleted Metaverse Object {MvoId}",
                            existingChange.Id, existingPendingExport.Id, unresolvedMvoId);
                        continue;
                    }

                    var mergeKey = GetAttributeChangeMergeKey(existingChange);
                    if (changesByMergeKey.ContainsKey(mergeKey))
                        continue;

                    changesByMergeKey[mergeKey] = new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        AttributeId = existingChange.AttributeId,
                        Attribute = existingChange.Attribute,
                        StringValue = existingChange.StringValue,
                        DateTimeValue = existingChange.DateTimeValue,
                        IntValue = existingChange.IntValue,
                        LongValue = existingChange.LongValue,
                        ByteValue = existingChange.ByteValue,
                        GuidValue = existingChange.GuidValue,
                        BoolValue = existingChange.BoolValue,
                        UnresolvedReferenceValue = existingChange.UnresolvedReferenceValue,
                        ChangeType = existingChange.ChangeType
                    };
                }
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
                    referencingMvo, removedRows, sourceSystem: null, cache, deferSave: true,
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
        var deletedIdStrings = deletedIds.Select(id => id.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pendingExport in fallbackPendingExports)
        {
            var unresolvable = pendingExport.AttributeValueChanges
                .Where(avc => avc.UnresolvedReferenceValue != null && deletedIdStrings.Contains(avc.UnresolvedReferenceValue))
                .ToList();
            foreach (var change in unresolvable)
            {
                pendingExport.AttributeValueChanges.Remove(change);
                result.UnresolvableChangesDropped++;
            }

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

        PendingExportChangeType changeType;
        ConnectedSystemObject? csoForExport = existingCso;

        // A CSO with PendingProvisioning status means the object doesn't exist in the target system yet -
        // it was created by a previous sync to establish the CSO↔MVO relationship before export.
        // Such CSOs need a Create operation, not Update.
        var needsProvisioning = existingCso == null ||
                                existingCso.Status == ConnectedSystemObjectStatus.PendingProvisioning;
        var createdNewCso = false;

        if (needsProvisioning)
        {
            // No CSO exists, or CSO is PendingProvisioning - check if we should provision
            if (exportRule.ProvisionToConnectedSystem != true)
            {
                Log.Debug("CreateOrUpdatePendingExportAsync: No CSO exists (or PendingProvisioning) and ProvisionToConnectedSystem is not enabled for rule {RuleName}",
                    exportRule.Name);
                return null;
            }

            if (existingCso == null)
            {
                // Create CSO with PendingProvisioning status to establish the relationship before export
                csoForExport = await CreatePendingProvisioningCsoAsync(mvo, exportRule);
                createdNewCso = true;
            }
            else
            {
                // Reuse existing PendingProvisioning CSO (already has secondary external ID).
                // A previous sync already created the Create PE with all mapped attributes.
                // Only proceed if the current changedAttributes are relevant to this export rule —
                // otherwise we'd replace the existing PE with an identical one and incorrectly
                // attribute it to this sync in the causality tree.
                if (!HasRelevantChangedAttributes(changedAttributes, exportRule))
                {
                    Log.Debug("CreateOrUpdatePendingExportAsync: Skipping PendingProvisioning CSO {CsoId} for rule {RuleName} — " +
                        "none of the {ChangeCount} changed attributes map to this export rule's Attribute Flow Rules",
                        existingCso.Id, exportRule.Name, changedAttributes.Count);
                    return null;
                }
            }

            changeType = PendingExportChangeType.Create;
        }
        else
        {
            changeType = PendingExportChangeType.Update;
        }

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
            CreatedAt = DateTime.UtcNow
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

        PendingExportChangeType changeType;
        ConnectedSystemObject? csoForExport = existingCso;

        // A CSO with PendingProvisioning status means the object doesn't exist in the target system yet -
        // it was created by a previous sync to establish the CSO↔MVO relationship before export.
        // Such CSOs need a Create operation, not Update.
        var needsProvisioning = existingCso == null ||
                                existingCso.Status == ConnectedSystemObjectStatus.PendingProvisioning;
        var createdNewCso = false;

        if (needsProvisioning)
        {
            // No CSO exists, or CSO is PendingProvisioning - check if we should provision
            if (exportRule.ProvisionToConnectedSystem != true)
            {
                Log.Debug("CreateOrUpdatePendingExportAsync: No CSO exists (or PendingProvisioning) and ProvisionToConnectedSystem is not enabled for rule {RuleName}",
                    exportRule.Name);
                return null;
            }

            if (existingCso == null)
            {
                // Create CSO with PendingProvisioning status to establish the relationship before export
                csoForExport = await CreatePendingProvisioningCsoAsync(mvo, exportRule);
                createdNewCso = true;

                // Update the cache with the newly created CSO so subsequent lookups find it
                cache.CsoLookup[lookupKey] = csoForExport;
            }
            else
            {
                // Reuse existing PendingProvisioning CSO (already has secondary external ID).
                // A previous sync already created the Create PE with all mapped attributes.
                // Only proceed if the current changedAttributes are relevant to this export rule —
                // otherwise we'd replace the existing PE with an identical one and incorrectly
                // attribute it to this sync in the causality tree.
                if (!HasRelevantChangedAttributes(changedAttributes, exportRule))
                {
                    Log.Debug("CreateOrUpdatePendingExportAsync: Skipping PendingProvisioning CSO {CsoId} for rule {RuleName} — " +
                        "none of the {ChangeCount} changed attributes map to this export rule's Attribute Flow Rules",
                        existingCso.Id, exportRule.Name, changedAttributes.Count);
                    return null;
                }
            }

            changeType = PendingExportChangeType.Create;
        }
        else
        {
            changeType = PendingExportChangeType.Update;
        }

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
            CreatedAt = DateTime.UtcNow
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
        List<AttributeFlowError>? flowErrors = null)
    {
        // Find existing CSO using cached lookup instead of database query
        var lookupKey = (mvo.Id, exportRule.ConnectedSystemId);
        cache.CsoLookup.TryGetValue(lookupKey, out var existingCso);

        PendingExportChangeType changeType;
        ConnectedSystemObject? csoForExport = existingCso;
        ConnectedSystemObject? provisioningCso = null;

        // A CSO with PendingProvisioning status means the object doesn't exist in the target system yet -
        // it was created by a previous sync to establish the CSO↔MVO relationship before export.
        // Such CSOs need a Create operation, not Update.
        var needsProvisioning = existingCso == null ||
                                existingCso.Status == ConnectedSystemObjectStatus.PendingProvisioning;
        var createdNewCso = false;

        // Reference recall must never provision (#1003): a referencing object with no presence in
        // the target has nothing there to remove a member from. This also protects a pending
        // Create export: without the guard, the merge below would delete it and the recall
        // caller's Update-only filter would then discard the merged Create, silently losing the
        // provisioning export (a pre-#1003 defect).
        if (recallSemantics && needsProvisioning)
        {
            Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: Recall skipping MVO {MvoId} for system {SystemId}: " +
                "no exportable presence (CSO {CsoStatus})",
                mvo.Id, exportRule.ConnectedSystemId, existingCso?.Status.ToString() ?? "absent");
            return (null, null, 0);
        }

        Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: MVO {MvoId} to system {SystemId}: existingCso={ExistingCso}, csoStatus={CsoStatus}, needsProvisioning={NeedsProvisioning}",
            mvo.Id, exportRule.ConnectedSystemId,
            existingCso != null ? existingCso.Id.ToString() : "null",
            existingCso?.Status.ToString() ?? "N/A",
            needsProvisioning);

        if (needsProvisioning)
        {
            // No CSO exists, or CSO is PendingProvisioning - check if we should provision
            if (exportRule.ProvisionToConnectedSystem != true)
            {
                Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: No CSO exists (or PendingProvisioning) and ProvisionToConnectedSystem is not enabled for rule {RuleName}",
                    exportRule.Name);
                return (null, null, 0);
            }

            if (existingCso == null)
            {
                // Before provisioning, attempt export matching to find an existing CSO in the target system.
                // This prevents creating duplicates when the object already exists in the target.
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
                    needsProvisioning = false;
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
            }
            else
            {
                // Reuse existing PendingProvisioning CSO (already has secondary external ID).
                // A previous sync already created the Create PE with all mapped attributes.
                // Only proceed if the current changedAttributes are relevant to this export rule —
                // otherwise we'd replace the existing PE with an identical one and incorrectly
                // attribute it to this sync in the causality tree.
                if (!HasRelevantChangedAttributes(changedAttributes, exportRule))
                {
                    Log.Debug("CreateOrUpdatePendingExportWithNoNetChangeAsync: Skipping PendingProvisioning CSO {CsoId} for rule {RuleName} — " +
                        "none of the {ChangeCount} changed attributes map to this export rule's Attribute Flow Rules",
                        existingCso.Id, exportRule.Name, changedAttributes.Count);
                    return (null, null, 0);
                }

                changeType = PendingExportChangeType.Create;
            }
        }
        else
        {
            changeType = PendingExportChangeType.Update;
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
                // Merge export eval changes into the existing drift PE.
                // For multi-valued attributes (e.g., member), both sources can have many changes
                // for the same AttributeId, so we must merge at the individual value level.
                // Export eval changes take precedence when both sources target the same value.
                using var inMemoryMergeSpan = JIM.Application.Diagnostics.Diagnostics.Sync.StartSpan("MergeIntoInMemoryPendingExport")
                    .SetTag("existingChangeCount", existingPendingExport.AttributeValueChanges.Count)
                    .SetTag("newChangeCount", attributeChanges.Count);
                var mergedCount = 0;
                var addedCount = 0;

                // Build a lookup of existing drift changes for deduplication.
                // Uses merge keys: single-valued attributes key by attribute ID only (newest wins),
                // multi-valued attributes key by attribute ID + value (each distinct value preserved).
                var existingChangeKeys = new HashSet<string>();
                foreach (var existing in existingPendingExport.AttributeValueChanges)
                    existingChangeKeys.Add(GetAttributeChangeMergeKey(existing));

                foreach (var newChange in attributeChanges)
                {
                    var key = GetAttributeChangeMergeKey(newChange);
                    if (existingChangeKeys.Contains(key))
                    {
                        // Same attribute (single-valued) or attribute+value (multi-valued) exists in drift PE —
                        // remove drift version(s) and add export eval version (newer MVO state takes precedence)
                        var toRemove = existingPendingExport.AttributeValueChanges
                            .Where(avc => GetAttributeChangeMergeKey(avc) == key)
                            .ToList();
                        foreach (var r in toRemove)
                            existingPendingExport.AttributeValueChanges.Remove(r);
                        existingPendingExport.AttributeValueChanges.Add(newChange);
                        existingChangeKeys.Remove(key);
                        existingChangeKeys.Add(GetAttributeChangeMergeKey(newChange));
                        mergedCount++;
                    }
                    else
                    {
                        // New attribute+value not in drift PE — add it
                        existingPendingExport.AttributeValueChanges.Add(newChange);
                        existingChangeKeys.Add(key);
                        addedCount++;
                    }
                }

                // Update unresolved references flag if any new changes have them
                if (attributeChanges.Any(ac => !string.IsNullOrEmpty(ac.UnresolvedReferenceValue)))
                    existingPendingExport.HasUnresolvedReferences = true;

                if (mergedCount > 0 || addedCount > 0)
                {
                    Log.Information("CreateOrUpdatePendingExportWithNoNetChangeAsync: Merged attribute changes into existing PendingExport {ExistingPeId} for CSO {CsoId}: " +
                        "{MergedCount} replaced (export eval wins), {AddedCount} added, total now {TotalCount}. Source: MVO {MvoId}",
                        existingPendingExport.Id, csoId.Value,
                        mergedCount, addedCount, existingPendingExport.AttributeValueChanges.Count, mvo.Id);
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
                // then add any drift-only changes not covered by export eval.
                // Uses merge keys: single-valued attributes key by attribute ID only (so the new
                // export eval change always replaces the old value), multi-valued attributes key by
                // attribute ID + value (each distinct value preserved).
                // Clone drift-only changes with new IDs because DeletePendingExportAsync cascade-deletes
                // child entities, making the tracked instances unusable for a new PE.
                var exportEvalChangeKeys = attributeChanges.Select(GetAttributeChangeMergeKey).ToHashSet();
                var driftOnlyChanges = dbPendingExport.AttributeValueChanges
                    .Where(avc => !exportEvalChangeKeys.Contains(GetAttributeChangeMergeKey(avc)))
                    .Select(avc => new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        AttributeId = avc.AttributeId,
                        Attribute = avc.Attribute,
                        StringValue = avc.StringValue,
                        DateTimeValue = avc.DateTimeValue,
                        IntValue = avc.IntValue,
                        LongValue = avc.LongValue,
                        ByteValue = avc.ByteValue,
                        GuidValue = avc.GuidValue,
                        BoolValue = avc.BoolValue,
                        UnresolvedReferenceValue = avc.UnresolvedReferenceValue,
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
            HasUnresolvedReferences = hasUnresolvedReferences
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
    /// Builds a <see cref="SyncExpressionEvaluationException"/> carrying the failing export expression and
    /// the target connected system attribute name, so the worker can record an ExpressionEvaluationError
    /// RPEI for the Metaverse Object being evaluated.
    /// </summary>
    private static SyncExpressionEvaluationException BuildExportExpressionEvaluationException(
        SyncRuleMapping mapping, SyncRuleMappingSource source, Exception innerException)
    {
        return new SyncExpressionEvaluationException(source.Expression, mapping.TargetConnectedSystemAttribute?.Name, innerException);
    }

    /// <summary>
    /// Creates PendingExportAttributeValueChange objects based on export rule mappings.
    /// Maps MVO attributes → CSO attributes.
    /// For export rules:
    /// - Sources[].MetaverseAttribute = the source MVO attribute
    /// - TargetConnectedSystemAttribute = the target CSO attribute
    /// For Create operations: includes all mapped attributes (to provision the full object)
    /// For Update operations: only includes attributes that actually changed
    /// </summary>
    /// <param name="mvo">The Metaverse Object to create changes for.</param>
    /// <param name="exportRule">The export rule containing attribute mappings.</param>
    /// <param name="changedAttributes">The MVO attributes that changed.</param>
    /// <param name="changeType">Whether this is a Create or Update operation.</param>
    /// <param name="existingCso">The existing CSO (for Update operations only) to compare values against.</param>
    /// <param name="csoAttributeCache">Optional cache of CSO attribute values for no-net-change detection.
    /// Uses ILookup to support multi-valued attributes where a single (CsoId, AttributeId) can have multiple values.</param>
    /// <param name="csoAlreadyCurrentCount">Output: count of attributes skipped because CSO already has the value.</param>
    /// <param name="removedAttributes">Optional set of attribute values that were removed from the MVO.
    /// For multi-valued attributes, values in this set create Remove changes instead of Add changes.
    /// For single-valued attributes, values in this set create null-clearing Update changes.</param>
    /// <param name="mvAttributeDictionary">Optional pre-built MVO attribute dictionary for expression evaluation.</param>
    /// <param name="preResolvedReferenceValues">Optional map of referenced Metaverse Object ID to the resolved
    /// target value for this export rule's Connected System (reference recall, #908). When a reference points
    /// at an object in this map, the change is staged with the resolved value instead of an unresolved
    /// Metaverse Object ID, because export-time resolution cannot resolve a deleted object.</param>
    /// <returns>List of attribute value changes to export.</returns>
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
    {
        var changes = new List<PendingExportAttributeValueChange>();
        var isCreateOperation = changeType == PendingExportChangeType.Create;
        csoAlreadyCurrentCount = 0;

        // For no-net-change detection, we need both the CSO and the attribute cache
        var canDetectNoNetChange = !isCreateOperation && existingCso != null && csoAttributeCache != null;

        // Pre-build O(1) lookup sets from removedAttributes for multi-valued removal detection.
        // Without this, the removal check is O(N) per value — for a 50K-member group that's 50K × 50K = 2.5B comparisons.
        // Three matching strategies in priority order:
        //   1. By ReferenceValueId (most common for group membership)
        //   2. By persisted entity Id (for saved values without ReferenceValueId)
        //   3. By value content (fallback for unsaved non-reference values)
        HashSet<Guid>? removedReferenceValueIds = null;
        HashSet<Guid>? removedEntityIds = null;
        HashSet<(string?, int?, long?, Guid?, bool?, DateTime?)>? removedValueContents = null;

        if (removedAttributes is { Count: > 0 })
        {
            removedReferenceValueIds = new HashSet<Guid>();
            removedEntityIds = new HashSet<Guid>();
            removedValueContents = new HashSet<(string?, int?, long?, Guid?, bool?, DateTime?)>();

            foreach (var rv in removedAttributes)
            {
                if (rv.ReferenceValueId.HasValue)
                    removedReferenceValueIds.Add(rv.ReferenceValueId.Value);

                if (rv.Id != Guid.Empty)
                    removedEntityIds.Add(rv.Id);

                if (!rv.ReferenceValueId.HasValue && rv.Id == Guid.Empty)
                    removedValueContents.Add((rv.StringValue, rv.IntValue, rv.LongValue, rv.GuidValue, rv.BoolValue, rv.DateTimeValue));
            }
        }

        foreach (var mapping in exportRule.AttributeFlowRules)
        {
            // For export rules, the target is the CSO attribute
            if (mapping.TargetConnectedSystemAttribute == null)
            {
                Log.Warning("CreateAttributeValueChanges: Export mapping has no TargetConnectedSystemAttribute set");
                continue;
            }

            foreach (var source in mapping.Sources)
            {
                // Handle expression-based mappings
                if (!string.IsNullOrWhiteSpace(source.Expression))
                {
                    // For Update operations with expressions, we need to check if any source attributes changed
                    // For simplicity, always include expression results for Create, but for Update we include them
                    // because expression results may depend on the changed attributes
                    // TODO (#880): Consider optimising by tracking which MVO attributes the expression depends on

                    // Build expression context with MVO attributes (lazy initialization - only build once)
                    mvAttributeDictionary ??= BuildAttributeDictionary(mvo);
                    var context = new ExpressionContext(mvAttributeDictionary, null);

                    // Only the evaluation itself is guarded. A thrown export expression must be surfaced as
                    // an errored object, never swallowed and never conflated with a deliberate null result.
                    // Known failure modes are rethrown as SyncExpressionEvaluationException for the worker to
                    // record as an ExpressionEvaluationError RPEI; anything else propagates to UnhandledError.
                    object? result;
                    try
                    {
                        result = ExpressionEvaluator.Evaluate(source.Expression, context);
                    }
                    catch (DynamicExpressoException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (ArgumentException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (FormatException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (OverflowException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (InvalidOperationException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (ArithmeticException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (InvalidCastException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }
                    catch (KeyNotFoundException ex) { throw BuildExportExpressionEvaluationException(mapping, source, ex); }

                    if (result == null)
                    {
                        // Null is expected when the referenced attribute doesn't exist on this MVO
                        Log.Debug("CreateAttributeValueChanges: Expression '{Expression}' for MVO {MvoId} returned null. " +
                            "Available attributes: [{Attributes}]",
                            source.Expression, mvo.Id, string.Join(", ", mvAttributeDictionary.Keys));
                    }

                    if (result != null)
                    {
                        var change = new PendingExportAttributeValueChange
                        {
                            Id = Guid.NewGuid(),
                            Attribute = mapping.TargetConnectedSystemAttribute,
                            AttributeId = mapping.TargetConnectedSystemAttribute.Id,
                            ChangeType = PendingExportAttributeChangeType.Update
                        };

                        // Set the value based on the result type
                        switch (result)
                        {
                            case string strValue:
                                change.StringValue = strValue;
                                break;
                            case int intValue:
                                change.IntValue = intValue;
                                break;
                            case long longValue:
                                change.LongValue = longValue;
                                break;
                            case DateTime dtValue:
                                change.DateTimeValue = dtValue;
                                break;
                            case bool boolValue:
                                change.BoolValue = boolValue;
                                break;
                            case Guid guidValue:
                                change.GuidValue = guidValue;
                                break;
                            case byte[] byteValue:
                                change.ByteValue = byteValue;
                                break;
                            default:
                                // Fall back to string representation
                                change.StringValue = result.ToString();
                                break;
                        }

                        // No-net-change detection for expression-based mappings
                        if (canDetectNoNetChange)
                        {
                            var cacheKey = (existingCso!.Id, change.AttributeId);
                            var existingCsoValues = csoAttributeCache![cacheKey];

                            if (IsCsoAttributeAlreadyCurrent(change, existingCsoValues))
                            {
                                Log.Debug("CreateAttributeValueChanges: Skipping attribute {AttrId} for CSO {CsoId} - CSO already has current value (expression)",
                                    change.AttributeId, existingCso.Id);
                                csoAlreadyCurrentCount++;
                                continue;
                            }
                        }

                        changes.Add(change);
                    }

                    continue;
                }

                // Handle direct Attribute Flow mappings
                if (source.MetaverseAttribute == null)
                    continue;

                // MVA -> SVA guard (#435): a multi-valued Metaverse source flowing to a single-valued Connected
                // System attribute can only carry one value. If the Metaverse Object holds more than one value for
                // the source attribute, JIM will not pick one arbitrarily; an arbitrary export could never be
                // reconciled on the next import (JIM would not know which value is authoritative). No Pending Export
                // is generated for this attribute and an error is recorded; the object's other attributes still export.
                if (mapping.TargetConnectedSystemAttribute.AttributePlurality == AttributePlurality.SingleValued &&
                    source.MetaverseAttribute.AttributePlurality == AttributePlurality.MultiValued)
                {
                    var mvoValueCount = mvo.AttributeValues.Count(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue);
                    if (mvoValueCount > 1)
                    {
                        Log.Error("CreateAttributeValueChanges: Multi-valued source attribute '{SourceAttr}' has {ValueCount} values but " +
                            "target attribute '{TargetAttr}' is single-valued. No Pending Export generated for this attribute. MVO {MvoId}",
                            source.MetaverseAttribute.Name, mvoValueCount, mapping.TargetConnectedSystemAttribute.Name, mvo.Id);

                        flowErrors?.Add(new AttributeFlowError
                        {
                            SourceAttributeName = source.MetaverseAttribute.Name,
                            TargetAttributeName = mapping.TargetConnectedSystemAttribute.Name,
                            ValueCount = mvoValueCount
                        });

                        continue;
                    }
                }

                // Get attribute values - handling differs for single-valued vs multi-valued attributes
                // Multi-valued attributes (like member) have multiple MVO attribute values with the same attribute ID
                var isMultiValued = source.MetaverseAttribute.AttributePlurality == AttributePlurality.MultiValued;

                IEnumerable<MetaverseObjectAttributeValue> mvoValues;
                if (isMultiValued)
                {
                    // For multi-valued attributes, get ALL values
                    if (isCreateOperation)
                    {
                        // Exclude asserted-null markers (#91): a NullValue row carries no value and must be
                        // invisible to export sourcing exactly as an absent row (the attribute reads as cleared).
                        var changedValues = changedAttributes
                            .Where(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue)
                            .ToList();

                        if (changedValues.Count > 0)
                        {
                            mvoValues = changedValues;
                        }
                        else
                        {
                            // Fall back to MVO's current attribute values (excluding asserted-null markers, #91)
                            mvoValues = mvo.AttributeValues
                                .Where(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue);
                        }
                    }
                    else
                    {
                        // For Update operations, only include attributes that actually changed (excluding
                        // asserted-null markers, #91, which carry no value to export)
                        var matchingChangedValues = changedAttributes
                            .Where(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue)
                            .ToList();

                        Log.Debug("CreateAttributeValueChanges: Multi-valued Update for attr {AttrName} (Id={AttrId}): " +
                            "changedAttributes has {TotalCount} items, {MatchCount} match this attribute. " +
                            "removedAttributes has {RemovedCount} items",
                            source.MetaverseAttribute.Name, source.MetaverseAttribute.Id,
                            changedAttributes.Count, matchingChangedValues.Count,
                            removedAttributes?.Count ?? 0);

                        mvoValues = matchingChangedValues;
                    }
                }
                else
                {
                    // For single-valued attributes, only get the first value (excluding asserted-null markers, #91)
                    var changedValue = changedAttributes
                        .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue);

                    MetaverseObjectAttributeValue? mvoValue;
                    if (isCreateOperation)
                    {
                        // For Create operations, include all mapped attributes (not just changed ones)
                        mvoValue = changedValue ?? mvo.AttributeValues
                            .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id && !av.NullValue);
                    }
                    else
                    {
                        // For Update operations, only include attributes that actually changed
                        mvoValue = changedValue;
                    }


                    mvoValues = mvoValue != null ? [mvoValue] : [];
                }

                // Process each attribute value (supports multi-valued attributes)
                foreach (var mvoValue in mvoValues)
                {
                    // Note: We only set AttributeId here (not the Attribute navigation property)
                    // to avoid EF Core change tracking overhead during batch evaluation.
                    // The Attribute is loaded via Include when reading Pending Exports.
                    //
                    // For multi-valued attributes, use Add to add each value to the attribute,
                    // or Remove if the value was removed from the MVO.
                    // Using Update (Replace) would cause each value to overwrite the previous one,
                    // resulting in only the last value being exported.
                    // For single-valued attributes, use Update (Replace) for the whole attribute.
                    PendingExportAttributeChangeType attrChangeType;
                    if (isMultiValued)
                    {
                        // Check if this value is in the removals list using pre-built O(1) lookup sets.
                        // Three matching strategies in priority order (same logic as before, now O(1) per check):
                        var isRemoval = removedReferenceValueIds != null && (
                            (mvoValue.ReferenceValueId.HasValue && removedReferenceValueIds.Contains(mvoValue.ReferenceValueId.Value)) ||
                            (mvoValue.Id != Guid.Empty && removedEntityIds!.Contains(mvoValue.Id)) ||
                            (!mvoValue.ReferenceValueId.HasValue && mvoValue.Id == Guid.Empty &&
                                removedValueContents!.Contains((mvoValue.StringValue, mvoValue.IntValue, mvoValue.LongValue,
                                    mvoValue.GuidValue, mvoValue.BoolValue, mvoValue.DateTimeValue))));

                        Log.Debug("CreateAttributeValueChanges: Processing MVO value Id={MvoValueId}, RefValueId={RefValueId}, isRemoval={IsRemoval}",
                            mvoValue.Id, mvoValue.ReferenceValueId, isRemoval);

                        attrChangeType = isRemoval
                            ? PendingExportAttributeChangeType.Remove
                            : PendingExportAttributeChangeType.Add;
                    }
                    else
                    {
                        attrChangeType = PendingExportAttributeChangeType.Update;
                    }

                    // For single-valued attributes, check if this value was removed from the MVO.
                    // Removals occur when an attribute value is no longer contributed by any source
                    // (e.g. attribute recall on CSO obsoletion, source no longer returning the value,
                    // CSO falling out of Synchronisation Rule scope). The changedAttributes list contains the
                    // original values (pre-removal) — we must create a null-clearing export so the
                    // target system clears the attribute, rather than copying the stale old value.
                    var isSingleValuedRemoval = !isMultiValued && removedAttributes?.Contains(mvoValue) == true;

                    if (isSingleValuedRemoval)
                    {
                        Log.Debug("CreateAttributeValueChanges: Single-valued attribute {AttrName} is a removal - " +
                            "creating null-clearing export change",
                            source.MetaverseAttribute.Name);
                    }

                    var attributeChange = new PendingExportAttributeValueChange
                    {
                        Id = Guid.NewGuid(),
                        Attribute = mapping.TargetConnectedSystemAttribute,
                        AttributeId = mapping.TargetConnectedSystemAttribute.Id,
                        ChangeType = attrChangeType
                    };

                    // Set the appropriate value based on data type.
                    // For single-valued removals, skip value assignment — all fields remain
                    // null, which tells the target system to clear the attribute.
                    if (!isSingleValuedRemoval)
                    {
                        switch (source.MetaverseAttribute.Type)
                        {
                            case AttributeDataType.Text:
                                attributeChange.StringValue = mvoValue.StringValue;
                                break;
                            case AttributeDataType.Number:
                                attributeChange.IntValue = mvoValue.IntValue;
                                break;
                            case AttributeDataType.DateTime:
                                attributeChange.DateTimeValue = mvoValue.DateTimeValue;
                                break;
                            case AttributeDataType.Boolean:
                                attributeChange.BoolValue = mvoValue.BoolValue;
                                break;
                            case AttributeDataType.Guid:
                                attributeChange.GuidValue = mvoValue.GuidValue;
                                break;
                            case AttributeDataType.Binary:
                                attributeChange.ByteValue = mvoValue.ByteValue;
                                break;
                            case AttributeDataType.LongNumber:
                                attributeChange.LongValue = mvoValue.LongValue;
                                break;
                            case AttributeDataType.Reference:
                                // For reference attributes, store the MVO ID as unresolved reference — will be
                                // resolved during export execution. Use navigation with scalar FK fallback for
                                // test compatibility.
                                // Reference recall (#908) supplies pre-resolved values instead: the referenced
                                // Metaverse Object is being deleted, so export-time resolution (which walks
                                // MVO -> joined CSO) can never succeed for it. In that case store the resolved
                                // target value (for example the DN) directly, exactly as export execution would.
                                var referencedMvoId = mvoValue.ReferenceValue?.Id ?? mvoValue.ReferenceValueId;
                                if (!referencedMvoId.HasValue)
                                {
                                    // A reference row with no referenced object carries nothing exportable, for
                                    // example a ghost row left by a pre-#1019 Metaverse Object deletion; emitting
                                    // it would stage an all-null change. Single-valued removals never reach here
                                    // (they skip value assignment entirely), so the clearing change is unaffected.
                                    Log.Debug("CreateAttributeValueChanges: Skipping valueless reference row {MvoValueId} for attribute {AttrName}",
                                        mvoValue.Id, source.MetaverseAttribute.Name);
                                    continue;
                                }
                                if (preResolvedReferenceValues != null &&
                                    preResolvedReferenceValues.TryGetValue(referencedMvoId.Value, out var preResolvedValue))
                                {
                                    attributeChange.StringValue = preResolvedValue;
                                }
                                else
                                {
                                    attributeChange.UnresolvedReferenceValue = referencedMvoId.Value.ToString();
                                }
                                break;
                        }
                    }

                    // No-net-change detection for direct attribute mappings
                    // Reference attributes: Pending Export stores MVO GUIDs in UnresolvedReferenceValue,
                    // CSO stores resolved references via ReferenceValue.MetaverseObjectId. The ValuesMatch
                    // method now compares these properly by extracting the MVO ID from both representations.
                    if (canDetectNoNetChange)
                    {
                        var cacheKey = (existingCso!.Id, attributeChange.AttributeId);
                        var existingCsoValues = csoAttributeCache![cacheKey];

                        if (IsCsoAttributeAlreadyCurrent(attributeChange, existingCsoValues))
                        {
                            Log.Debug("CreateAttributeValueChanges: Skipping attribute {AttrId} for CSO {CsoId} - CSO already has current value (direct)",
                                attributeChange.AttributeId, existingCso.Id);
                            csoAlreadyCurrentCount++;
                            continue;
                        }
                    }

                    changes.Add(attributeChange);
                }
            }
        }

        return changes;
    }

    /// <summary>
    /// Compares a Pending Export attribute value change against existing CSO attribute values
    /// to determine if they represent a no-net-change (CSO already has the target state).
    /// Supports multi-valued attributes where a single attribute can have multiple values.
    /// </summary>
    /// <param name="pendingChange">The pending change to export.</param>
    /// <param name="existingValues">The existing CSO attribute values for this attribute, may be empty.</param>
    /// <returns>True if the operation is a no-net-change (should be skipped), false otherwise.</returns>
    public static bool IsCsoAttributeAlreadyCurrent(
        PendingExportAttributeValueChange pendingChange,
        IEnumerable<ConnectedSystemObjectAttributeValue>? existingValues)
    {
        // Convert to list once to avoid multiple enumeration
        var valuesList = existingValues?.ToList() ?? [];

        switch (pendingChange.ChangeType)
        {
            case PendingExportAttributeChangeType.Add:
                // For Add: skip if the value already exists in CSO (no-net-change)
                // If the value doesn't exist, we need to add it (not a no-net-change)
                return valuesList.Any(ev => ValuesMatch(pendingChange, ev));

            case PendingExportAttributeChangeType.Remove:
                // For Remove: skip if the value doesn't exist in CSO (no-net-change)
                // If the value exists, we need to remove it (not a no-net-change)
                return !valuesList.Any(ev => ValuesMatch(pendingChange, ev));

            case PendingExportAttributeChangeType.RemoveAll:
                // For RemoveAll: skip if CSO has no values for this attribute (no-net-change)
                // If CSO has values, we need to remove them (not a no-net-change)
                return valuesList.Count == 0;

            case PendingExportAttributeChangeType.Update:
            default:
                // For Update (single-valued): use existing single-value comparison logic
                var existingValue = valuesList.FirstOrDefault();
                return IsSingleValueMatch(pendingChange, existingValue);
        }
    }

    /// <summary>
    /// Checks if a pending change value matches an existing CSO attribute value.
    /// Used for multi-valued attribute comparison (Add/Remove operations).
    /// </summary>
    /// <remarks>
    /// Reference attributes (like group members) may have their DNs stored in different fields:
    /// - Pending Exports from sync store resolved DNs in StringValue
    /// - CSO values from AD import store DNs in UnresolvedReferenceValue
    /// This method handles cross-field comparison for these cases.
    /// </remarks>
    private static bool ValuesMatch(
        PendingExportAttributeValueChange pendingChange,
        ConnectedSystemObjectAttributeValue existingValue)
    {
        // For reference attributes, the Pending Export stores an MVO GUID in UnresolvedReferenceValue,
        // while the CSO stores a resolved reference to another CSO (which has a MetaverseObjectId).
        // Compare using the MVO ID that both ultimately represent.
        var pendingHasUnresolvedRef = !string.IsNullOrEmpty(pendingChange.UnresolvedReferenceValue);
        var existingHasResolvedRef = existingValue.ReferenceValue != null;

        if (pendingHasUnresolvedRef)
        {
            // Pending Export has an MVO GUID - compare with the MVO that the existing CSO reference points to
            if (existingHasResolvedRef && existingValue.ReferenceValue!.MetaverseObjectId.HasValue)
            {
                // Compare MVO GUIDs
                if (Guid.TryParse(pendingChange.UnresolvedReferenceValue, out var pendingMvoId))
                {
                    return pendingMvoId == existingValue.ReferenceValue.MetaverseObjectId.Value;
                }
            }

            // Fallback: compare as DN strings if the Pending Export has a resolved DN
            // (This handles cases where the Pending Export was created from an already-resolved reference)
            var existingDn = existingValue.UnresolvedReferenceValue;
            if (existingDn != null)
            {
                // DNs are case-insensitive in LDAP
                return string.Equals(pendingChange.UnresolvedReferenceValue, existingDn, StringComparison.OrdinalIgnoreCase);
            }

            // No match possible - CSO doesn't have this reference
            return false;
        }

        // Cross-field reference comparison (see remarks): a pre-resolved Pending Export change stores
        // the reference value (for example a DN) in StringValue, while an imported CSO reference keeps
        // the raw reference string in UnresolvedReferenceValue. This is the only comparison possible
        // when the referenced object's Metaverse Object has been deleted (reference recall, #908), as
        // the CSO-side navigation no longer resolves to a Metaverse Object. DNs are case-insensitive.
        if (pendingChange.StringValue != null && existingValue.UnresolvedReferenceValue != null)
        {
            return string.Equals(pendingChange.StringValue, existingValue.UnresolvedReferenceValue, StringComparison.OrdinalIgnoreCase);
        }

        // Compare based on which value type is set
        // String comparison (case-sensitive for regular attributes)
        if (pendingChange.StringValue != null || existingValue.StringValue != null)
        {
            return string.Equals(pendingChange.StringValue, existingValue.StringValue, StringComparison.Ordinal);
        }

        // Integer comparison
        if (pendingChange.IntValue.HasValue || existingValue.IntValue.HasValue)
        {
            return pendingChange.IntValue == existingValue.IntValue;
        }

        // DateTime comparison
        if (pendingChange.DateTimeValue.HasValue || existingValue.DateTimeValue.HasValue)
        {
            return pendingChange.DateTimeValue == existingValue.DateTimeValue;
        }

        // Binary comparison
        if (pendingChange.ByteValue != null || existingValue.ByteValue != null)
        {
            if (pendingChange.ByteValue == null && existingValue.ByteValue == null)
                return true;
            if (pendingChange.ByteValue == null || existingValue.ByteValue == null)
                return false;
            return pendingChange.ByteValue.SequenceEqual(existingValue.ByteValue);
        }

        // Unresolved reference comparison (only if neither side used cross-field DN)
        if (pendingChange.UnresolvedReferenceValue != null || existingValue.UnresolvedReferenceValue != null)
        {
            return string.Equals(pendingChange.UnresolvedReferenceValue, existingValue.UnresolvedReferenceValue, StringComparison.Ordinal);
        }

        // Guid comparison (pending stores as StringValue, CSO has GuidValue)
        if (existingValue.GuidValue.HasValue)
        {
            if (Guid.TryParse(pendingChange.StringValue, out var pendingGuid))
                return pendingGuid == existingValue.GuidValue.Value;
            return false;
        }

        // Bool comparison (pending stores as StringValue, CSO has BoolValue)
        if (existingValue.BoolValue.HasValue)
        {
            if (bool.TryParse(pendingChange.StringValue, out var pendingBool))
                return pendingBool == existingValue.BoolValue.Value;
            return false;
        }

        // Both null/empty - consider them matching
        return true;
    }

    /// <summary>
    /// Compares a Pending Export attribute value change against a single existing CSO attribute value
    /// to determine if they represent the same value (no-net-change).
    /// Used for single-valued Update operations.
    /// </summary>
    /// <param name="pendingChange">The pending change to export.</param>
    /// <param name="existingValue">The existing CSO attribute value, or null if no value exists.</param>
    /// <returns>True if the values are identical (no-net-change), false otherwise.</returns>
    private static bool IsSingleValueMatch(
        PendingExportAttributeValueChange pendingChange,
        ConnectedSystemObjectAttributeValue? existingValue)
    {
        // If no existing value, this is a new attribute - not a no-net-change
        if (existingValue == null)
        {
            // Check if the pending change is also null/empty
            var isEmpty = IsPendingChangeEmpty(pendingChange);
            Log.Debug("IsSingleValueMatch: existingValue is null, pendingChange empty={IsEmpty}, IntValue={IntValue}, StringValue={StringValue}",
                isEmpty, pendingChange.IntValue, LogSanitiser.Sanitise(pendingChange.StringValue));
            return isEmpty;
        }

        var result = ValuesMatch(pendingChange, existingValue);
        Log.Debug("IsSingleValueMatch: Comparing pendingChange (IntValue={PendingInt}, StringValue={PendingStr}) with existingValue (IntValue={ExistingInt}, StringValue={ExistingStr}). Result={Result}",
            pendingChange.IntValue, LogSanitiser.Sanitise(pendingChange.StringValue), existingValue.IntValue, LogSanitiser.Sanitise(existingValue.StringValue), result);
        return result;
    }

    /// <summary>
    /// Checks if a Pending Export attribute value change represents an empty/null value.
    /// </summary>
    private static bool IsPendingChangeEmpty(PendingExportAttributeValueChange change)
    {
        return change.StringValue == null &&
               !change.IntValue.HasValue &&
               !change.DateTimeValue.HasValue &&
               change.ByteValue == null &&
               change.UnresolvedReferenceValue == null;
    }

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
    {
        if (changedAttributes.Count == 0)
            return false;

        var changedAttributeIds = new HashSet<int>(changedAttributes.Select(av => av.AttributeId));

        foreach (var mapping in exportRule.AttributeFlowRules)
        {
            foreach (var source in mapping.Sources)
            {
                // Expression-based mappings may depend on any MVO attribute, so conservatively
                // treat them as relevant when any attribute has changed.
                if (!string.IsNullOrWhiteSpace(source.Expression))
                    return true;

                // Direct attribute mapping — check if the source MVO attribute is in the changed set
                if (source.MetaverseAttribute != null && changedAttributeIds.Contains(source.MetaverseAttribute.Id))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds a dictionary of attribute values from a Metaverse Object for expression evaluation.
    /// The dictionary keys are attribute names, and values are the attribute values.
    /// </summary>
    private Dictionary<string, object?> BuildAttributeDictionary(MetaverseObject mvo)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (mvo.Type == null)
        {
            Log.Warning("BuildAttributeDictionary: MVO {MvoId} has null Type, cannot build attribute dictionary", mvo.Id);
            return attributes;
        }

        // Exclude asserted-null markers (#91): they carry no value, so the expression context must treat the
        // attribute as absent (mv["x"] resolves to null) rather than seeing a phantom value.
        foreach (var attributeValue in mvo.AttributeValues.Where(av => !av.NullValue))
        {
            if (attributeValue.Attribute == null)
            {
                // Log warning for diagnostic purposes - this indicates a missing Include or EF tracking issue
                Log.Warning("BuildAttributeDictionary: MVO {MvoId} has attribute value with AttributeId={AttrId} but Attribute navigation property is null. " +
                    "This will cause expression-based mappings to fail for this attribute.",
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
                // Fall back to the FK scalar when the navigation is not loaded: reconciler-flagged MVOs
                // (#892) arrive via a no-tracking query that deliberately omits the ReferenceValue Include.
                AttributeDataType.Reference => attributeValue.ReferenceValue?.Id.ToString() ?? attributeValue.ReferenceValueId?.ToString(),
                _ => null
            };

            attributes[attributeName] = value;
        }

        return attributes;
    }

    /// <summary>
    /// Generates a composite key for a PendingExportAttributeValueChange that identifies
    /// the specific attribute+value combination. Used to deduplicate when merging export
    /// evaluation changes with drift correction changes. For multi-valued attributes like
    /// group membership, each individual value (e.g., each member DN) gets a distinct key,
    /// allowing both sources to contribute different values for the same attribute.
    /// </summary>
    internal static string GetAttributeChangeKey(PendingExportAttributeValueChange change)
    {
        // Build a value identifier from whichever value field is populated
        var valueId = change.UnresolvedReferenceValue
            ?? change.StringValue
            ?? change.GuidValue?.ToString()
            ?? change.IntValue?.ToString()
            ?? change.LongValue?.ToString()
            ?? change.DateTimeValue?.ToString("O")
            ?? change.BoolValue?.ToString()
            ?? (change.ByteValue != null ? Convert.ToBase64String(change.ByteValue) : null)
            ?? string.Empty;

        return $"{change.AttributeId}:{valueId}";
    }

    /// <summary>
    /// Returns a merge key for deduplicating attribute changes when combining Pending Exports.
    /// For single-valued attributes, the key is just the attribute ID — the newest change always
    /// wins regardless of value. For multi-valued attributes, the key includes the value so that
    /// distinct values (e.g., different group members) are preserved during merge.
    /// </summary>
    internal static string GetAttributeChangeMergeKey(PendingExportAttributeValueChange change)
    {
        // Single-valued attributes: key by attribute ID only. When merging a stale PE with a new
        // export evaluation, the old value and the new value will share the same key, so the new
        // change replaces the old one. Without this, different values produce different keys and
        // both survive, causing LDAP "SINGLE-VALUE attribute specified more than once" errors.
        if (change.Attribute?.AttributePlurality != AttributePlurality.MultiValued)
            return change.AttributeId.ToString();

        // Multi-valued attributes: include value identity so each distinct value is preserved.
        return GetAttributeChangeKey(change);
    }

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
