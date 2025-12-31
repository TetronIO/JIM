using JIM.Application.Expressions;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Serilog;
namespace JIM.Application.Servers;

/// <summary>
/// Evaluates export rules and creates PendingExports when Metaverse Objects change.
/// Implements Q1 decision: evaluate exports immediately when MVO changes.
/// </summary>
public class ExportEvaluationServer
{
    private JimApplication Application { get; }
    private IExpressionEvaluator ExpressionEvaluator { get; }
    private ScopingEvaluationServer ScopingEvaluation { get; }

    internal ExportEvaluationServer(JimApplication application)
    {
        Application = application;
        ExpressionEvaluator = new DynamicExpressoEvaluator();
        ScopingEvaluation = new ScopingEvaluationServer();
    }

    /// <summary>
    /// Cache class for pre-loaded export evaluation data.
    /// Pass this to the optimised evaluation methods to avoid O(N×M) database queries.
    /// </summary>
    public class ExportEvaluationCache
    {
        /// <summary>
        /// Pre-loaded export rules, keyed by MVO type ID.
        /// </summary>
        public Dictionary<int, List<SyncRule>> ExportRulesByMvoTypeId { get; }

        /// <summary>
        /// Pre-loaded CSO lookup, keyed by (MvoId, ConnectedSystemId).
        /// </summary>
        public Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> CsoLookup { get; }

        /// <summary>
        /// Creates a new export evaluation cache with pre-loaded data.
        /// </summary>
        public ExportEvaluationCache(
            Dictionary<int, List<SyncRule>> exportRulesByMvoTypeId,
            Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> csoLookup)
        {
            ExportRulesByMvoTypeId = exportRulesByMvoTypeId;
            CsoLookup = csoLookup;
        }
    }

    /// <summary>
    /// Builds a cache of export rules and CSO lookups for optimised batch evaluation.
    /// Call this once at the start of sync, then pass the cache to evaluation methods.
    /// </summary>
    /// <param name="sourceConnectedSystemId">The source system ID (to exclude from export evaluation via Q3).</param>
    /// <param name="preloadedSyncRules">Optional pre-loaded sync rules to avoid redundant database query.</param>
    /// <returns>A cache object to pass to evaluation methods.</returns>
    public async Task<ExportEvaluationCache> BuildExportEvaluationCacheAsync(
        int sourceConnectedSystemId,
        List<SyncRule>? preloadedSyncRules = null)
    {
        // Use pre-loaded sync rules if available, otherwise load from database
        var allSyncRules = preloadedSyncRules
            ?? await Application.Repository.ConnectedSystems.GetSyncRulesAsync();

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

        // Load all CSOs for target systems in a single query
        var csoLookup = await Application.Repository.ConnectedSystems
            .GetConnectedSystemObjectsByTargetSystemsAsync(targetSystemIds);

        Log.Debug("BuildExportEvaluationCacheAsync: Cached {RuleCount} export rules across {TypeCount} MVO types, {CsoCount} CSOs for {SystemCount} target systems",
            exportRules.Count, exportRulesByMvoTypeId.Count, csoLookup.Count, targetSystemIds.Count);

        return new ExportEvaluationCache(exportRulesByMvoTypeId, csoLookup);
    }

    /// <summary>
    /// Evaluates all export rules for an MVO that has changed and creates PendingExports.
    /// This is the main entry point called after inbound sync updates an MVO.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed</param>
    /// <param name="changedAttributes">The attributes that changed on the MVO</param>
    /// <param name="sourceSystem">The connected system that caused this change (for Q3 circular prevention)</param>
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

            // Find or create the pending export for this MVO → target system
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
    /// <param name="sourceSystem">The connected system that caused this change (for Q3 circular prevention)</param>
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
            var existingCso = await Application.Repository.ConnectedSystems
                .GetConnectedSystemObjectByMetaverseObjectIdAsync(mvo.Id, exportRule.ConnectedSystemId);

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
    /// <param name="sourceSystem">The connected system that caused this change (for Q3 circular prevention).</param>
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

            // Find or create the pending export using cached CSO lookup
            var pendingExport = await CreateOrUpdatePendingExportAsync(mvo, exportRule, changedAttributes, cache);
            if (pendingExport != null)
            {
                pendingExports.Add(pendingExport);
            }
        }

        return pendingExports;
    }

    /// <summary>
    /// Optimised version of EvaluateOutOfScopeExportsAsync that uses pre-cached data.
    /// Avoids O(N×M) database queries by using cached export rules and CSO lookups.
    /// </summary>
    /// <param name="mvo">The Metaverse Object that changed.</param>
    /// <param name="sourceSystem">The connected system that caused this change (for Q3 circular prevention).</param>
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
    /// Handles deprovisioning based on the sync rule's OutboundDeprovisionAction setting.
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
                await Application.Repository.ConnectedSystems.UpdateConnectedSystemObjectAsync(cso);

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

                return null; // No pending export needed for disconnect

            case OutboundDeprovisionAction.Delete:
                // Create a pending export to delete the CSO from the target system
                Log.Information("HandleOutboundDeprovisioningAsync: Creating delete PendingExport for CSO {CsoId} (OutboundDeprovisionAction=Delete)",
                    cso.Id);

                // Only set the FK property (ConnectedSystemObjectId), NOT the navigation property (ConnectedSystemObject).
                // Setting both can cause EF Core change tracker conflicts where the FK gets overwritten.
                var pendingExport = new PendingExport
                {
                    Id = Guid.NewGuid(),
                    ConnectedSystemId = cso.ConnectedSystemId,
                    ConnectedSystemObjectId = cso.Id,
                    ChangeType = PendingExportChangeType.Delete,
                    Status = PendingExportStatus.Pending,
                    SourceMetaverseObjectId = mvo.Id,
                    CreatedAt = DateTime.UtcNow
                };

                await Application.Repository.ConnectedSystems.CreatePendingExportAsync(pendingExport);

                Log.Information("HandleOutboundDeprovisioningAsync: Created delete PendingExport {ExportId} for CSO {CsoId} in system {SystemId}",
                    pendingExport.Id, cso.Id, cso.ConnectedSystemId);

                return pendingExport;

            default:
                Log.Warning("HandleOutboundDeprovisioningAsync: Unknown OutboundDeprovisionAction {Action} for rule {RuleName}",
                    exportRule.OutboundDeprovisionAction, exportRule.Name);
                return null;
        }
    }

    /// <summary>
    /// Evaluates export rules for an MVO that is being deleted.
    /// Implements Q4 decision: only create delete exports for Provisioned CSOs.
    /// </summary>
    public async Task<List<PendingExport>> EvaluateMvoDeletionAsync(MetaverseObject mvo)
    {
        var pendingExports = new List<PendingExport>();

        // Get all CSOs joined to this MVO
        var joinedCsos = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsByMetaverseObjectIdAsync(mvo.Id);

        foreach (var cso in joinedCsos)
        {
            // Q4 Decision: Only create delete exports for Provisioned CSOs
            if (cso.JoinType != ConnectedSystemObjectJoinType.Provisioned)
            {
                Log.Debug("EvaluateMvoDeletionAsync: Skipping delete for CSO {CsoId} - JoinType is {JoinType}, not Provisioned",
                    cso.Id, cso.JoinType);
                continue;
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
                SourceMetaverseObjectId = mvo.Id,
                CreatedAt = DateTime.UtcNow
            };

            await Application.Repository.ConnectedSystems.CreatePendingExportAsync(pendingExport);
            pendingExports.Add(pendingExport);

            Log.Information("EvaluateMvoDeletionAsync: Created delete PendingExport {ExportId} for CSO {CsoId} in system {SystemId}",
                pendingExport.Id, cso.Id, cso.ConnectedSystemId);
        }

        return pendingExports;
    }

    /// <summary>
    /// Gets all enabled export sync rules for a given MVO object type.
    /// </summary>
    private async Task<List<SyncRule>> GetExportRulesForObjectTypeAsync(int metaverseObjectTypeId)
    {
        var allSyncRules = await Application.Repository.ConnectedSystems.GetSyncRulesAsync();

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
    private async Task<PendingExport?> CreateOrUpdatePendingExportAsync(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes)
    {
        // Find existing CSO for this MVO in the target system
        var existingCso = await Application.Repository.ConnectedSystems
            .GetConnectedSystemObjectByMetaverseObjectIdAsync(mvo.Id, exportRule.ConnectedSystemId);

        PendingExportChangeType changeType;
        ConnectedSystemObject? csoForExport = existingCso;

        if (existingCso == null)
        {
            // No CSO exists - check if we should provision
            if (exportRule.ProvisionToConnectedSystem != true)
            {
                Log.Debug("CreateOrUpdatePendingExportAsync: No CSO exists and ProvisionToConnectedSystem is not enabled for rule {RuleName}",
                    exportRule.Name);
                return null;
            }

            // Create CSO with PendingProvisioning status to establish the relationship before export
            csoForExport = await CreatePendingProvisioningCsoAsync(mvo, exportRule);
            changeType = PendingExportChangeType.Create;
        }
        else
        {
            changeType = PendingExportChangeType.Update;
        }

        // Create attribute value changes based on the export rule mappings
        var attributeChanges = CreateAttributeValueChanges(mvo, exportRule, changedAttributes, changeType);

        if (attributeChanges.Count == 0 && changeType == PendingExportChangeType.Update)
        {
            Log.Debug("CreateOrUpdatePendingExportAsync: No attribute changes for MVO {MvoId} to system {SystemId}",
                mvo.Id, exportRule.ConnectedSystemId);
            return null;
        }

        // For provisioning (Create) scenarios, add the secondary external ID value to the CSO
        // so the confirming import can find it by secondary external ID (e.g. distinguishedName)
        if (changeType == PendingExportChangeType.Create && csoForExport != null)
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

        await Application.Repository.ConnectedSystems.CreatePendingExportAsync(pendingExport);

        Log.Information("CreateOrUpdatePendingExportAsync: Created {ChangeType} PendingExport {ExportId} for MVO {MvoId} to system {SystemName} with {AttrCount} attribute changes",
            changeType, pendingExport.Id, mvo.Id, exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString(), attributeChanges.Count);

        return pendingExport;
    }

    /// <summary>
    /// Optimised version of CreateOrUpdatePendingExportAsync that uses pre-cached CSO lookup.
    /// Also updates the cache when new CSOs are created for provisioning.
    /// </summary>
    private async Task<PendingExport?> CreateOrUpdatePendingExportAsync(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes,
        ExportEvaluationCache cache)
    {
        // Find existing CSO using cached lookup instead of database query
        var lookupKey = (mvo.Id, exportRule.ConnectedSystemId);
        cache.CsoLookup.TryGetValue(lookupKey, out var existingCso);

        PendingExportChangeType changeType;
        ConnectedSystemObject? csoForExport = existingCso;

        if (existingCso == null)
        {
            // No CSO exists - check if we should provision
            if (exportRule.ProvisionToConnectedSystem != true)
            {
                Log.Debug("CreateOrUpdatePendingExportAsync: No CSO exists and ProvisionToConnectedSystem is not enabled for rule {RuleName}",
                    exportRule.Name);
                return null;
            }

            // Create CSO with PendingProvisioning status to establish the relationship before export
            csoForExport = await CreatePendingProvisioningCsoAsync(mvo, exportRule);
            changeType = PendingExportChangeType.Create;

            // Update the cache with the newly created CSO so subsequent lookups find it
            cache.CsoLookup[lookupKey] = csoForExport;
        }
        else
        {
            changeType = PendingExportChangeType.Update;
        }

        // Create attribute value changes based on the export rule mappings
        var attributeChanges = CreateAttributeValueChanges(mvo, exportRule, changedAttributes, changeType);

        if (attributeChanges.Count == 0 && changeType == PendingExportChangeType.Update)
        {
            Log.Debug("CreateOrUpdatePendingExportAsync: No attribute changes for MVO {MvoId} to system {SystemId}",
                mvo.Id, exportRule.ConnectedSystemId);
            return null;
        }

        // For provisioning (Create) scenarios, add the secondary external ID value to the CSO
        // so the confirming import can find it by secondary external ID (e.g. distinguishedName)
        if (changeType == PendingExportChangeType.Create && csoForExport != null)
        {
            await AddSecondaryExternalIdToCsoAsync(csoForExport, attributeChanges, exportRule);
        }

        var csoId = csoForExport?.Id;
        Log.Verbose("CreateOrUpdatePendingExportAsync: Creating pending export. csoForExport={CsoForExport}, csoId={CsoId}, changeType={ChangeType}",
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
        await Application.Repository.ConnectedSystems.CreatePendingExportAsync(pendingExport);

        Log.Debug("CreateOrUpdatePendingExportAsync: Created {ChangeType} PendingExport {ExportId} for MVO {MvoId} to system {SystemName} with {AttrCount} attribute changes, CsoId={CsoId}",
            changeType, pendingExport.Id, mvo.Id, exportRule.ConnectedSystem?.Name ?? exportRule.ConnectedSystemId.ToString(), attributeChanges.Count, pendingExport.ConnectedSystemObjectId);

        return pendingExport;
    }

    /// <summary>
    /// Creates a Connected System Object with PendingProvisioning status for provisioning scenarios.
    /// This establishes the CSO↔MVO relationship before the object exists in the target system,
    /// ensuring that the subsequent import will correctly join rather than create a duplicate.
    /// </summary>
    private async Task<ConnectedSystemObject> CreatePendingProvisioningCsoAsync(MetaverseObject mvo, SyncRule exportRule)
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

        await Application.Repository.ConnectedSystems.CreateConnectedSystemObjectAsync(cso);

        Log.Information("CreatePendingProvisioningCsoAsync: Created PendingProvisioning CSO {CsoId} for MVO {MvoId} in system {SystemId}",
            cso.Id, mvo.Id, exportRule.ConnectedSystemId);

        return cso;
    }

    /// <summary>
    /// Adds the secondary external ID value to a PendingProvisioning CSO so that confirming import
    /// can find the CSO by secondary external ID (e.g. distinguishedName) when matching.
    /// This is essential for the confirming import to match PendingProvisioning CSOs that don't yet
    /// have a primary external ID (which is typically system-assigned, like objectGUID in AD).
    /// </summary>
    private async Task AddSecondaryExternalIdToCsoAsync(
        ConnectedSystemObject cso,
        List<PendingExportAttributeValueChange> attributeChanges,
        SyncRule exportRule)
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

        // Add to CSO and persist
        cso.AttributeValues ??= new List<ConnectedSystemObjectAttributeValue>();
        cso.AttributeValues.Add(attributeValue);

        await Application.Repository.ConnectedSystems.UpdateConnectedSystemObjectAsync(cso);

        Log.Information("AddSecondaryExternalIdToCsoAsync: Added secondary external ID value '{SecondaryIdValue}' to CSO {CsoId} for confirming import matching",
            secondaryIdChange.StringValue ?? secondaryIdChange.IntValue?.ToString() ?? "unknown", cso.Id);
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
    private List<PendingExportAttributeValueChange> CreateAttributeValueChanges(
        MetaverseObject mvo,
        SyncRule exportRule,
        List<MetaverseObjectAttributeValue> changedAttributes,
        PendingExportChangeType changeType)
    {
        var changes = new List<PendingExportAttributeValueChange>();
        var isCreateOperation = changeType == PendingExportChangeType.Create;

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
                    // TODO: Consider optimising by tracking which MVO attributes the expression depends on

                    try
                    {
                        // Build expression context with MVO attributes
                        var mvAttributes = BuildAttributeDictionary(mvo);

                        Log.Debug("CreateAttributeValueChanges: Evaluating expression for MVO {MvoId}. " +
                            "Expression: '{Expression}', Available attributes: [{Attributes}]",
                            mvo.Id, source.Expression, string.Join(", ", mvAttributes.Keys));

                        var context = new ExpressionContext(mvAttributes, null);

                        // Evaluate the expression
                        var result = ExpressionEvaluator.Evaluate(source.Expression, context);

                        if (result == null)
                        {
                            Log.Warning("CreateAttributeValueChanges: Expression '{Expression}' for MVO {MvoId} returned null. " +
                                "Available attributes: [{Attributes}]",
                                source.Expression, mvo.Id, string.Join(", ", mvAttributes.Keys));
                        }

                        if (result != null)
                        {
                            // Note: We only set AttributeId here (not the Attribute navigation property)
                            // to avoid EF Core change tracking overhead during batch evaluation.
                            // The Attribute is loaded via Include when reading pending exports.
                            var change = new PendingExportAttributeValueChange
                            {
                                Id = Guid.NewGuid(),
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
                                case DateTime dtValue:
                                    change.DateTimeValue = dtValue;
                                    break;
                                case bool boolValue:
                                    change.StringValue = boolValue.ToString();
                                    break;
                                case Guid guidValue:
                                    change.StringValue = guidValue.ToString();
                                    break;
                                case byte[] byteValue:
                                    change.ByteValue = byteValue;
                                    break;
                                default:
                                    // Fall back to string representation
                                    change.StringValue = result.ToString();
                                    break;
                            }

                            changes.Add(change);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "CreateAttributeValueChanges: Failed to evaluate expression '{Expression}' for attribute {AttributeName}",
                            source.Expression, mapping.TargetConnectedSystemAttribute.Name);
                    }

                    continue;
                }

                // Handle direct attribute flow mappings
                if (source.MetaverseAttribute == null)
                    continue;

                // Check if this attribute was changed
                var changedValue = changedAttributes
                    .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id);

                MetaverseObjectAttributeValue? mvoValue;
                if (isCreateOperation)
                {
                    // For Create operations, include all mapped attributes (not just changed ones)
                    // to ensure the new object is fully provisioned
                    mvoValue = changedValue ?? mvo.AttributeValues
                        .FirstOrDefault(av => av.AttributeId == source.MetaverseAttribute.Id);
                }
                else
                {
                    // For Update operations, only include attributes that actually changed
                    mvoValue = changedValue;
                }

                if (mvoValue == null)
                    continue;

                // Note: We only set AttributeId here (not the Attribute navigation property)
                // to avoid EF Core change tracking overhead during batch evaluation.
                // The Attribute is loaded via Include when reading pending exports.
                var attributeChange = new PendingExportAttributeValueChange
                {
                    Id = Guid.NewGuid(),
                    AttributeId = mapping.TargetConnectedSystemAttribute.Id,
                    ChangeType = PendingExportAttributeChangeType.Update
                };

                // Set the appropriate value based on data type
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
                        // Convert bool to string for now (model doesn't have BoolValue)
                        attributeChange.StringValue = mvoValue.BoolValue?.ToString();
                        break;
                    case AttributeDataType.Guid:
                        // Convert Guid to string for now (model doesn't have GuidValue)
                        attributeChange.StringValue = mvoValue.GuidValue?.ToString();
                        break;
                    case AttributeDataType.Binary:
                        attributeChange.ByteValue = mvoValue.ByteValue;
                        break;
                    case AttributeDataType.Reference:
                        // For reference attributes, store the MVO ID as unresolved reference - will be resolved during export execution
                        if (mvoValue.ReferenceValue != null)
                        {
                            attributeChange.UnresolvedReferenceValue = mvoValue.ReferenceValue.Id.ToString();
                        }
                        break;
                }

                changes.Add(attributeChange);
            }
        }

        return changes;
    }

    /// <summary>
    /// Builds a dictionary of attribute values from a Metaverse Object for expression evaluation.
    /// The dictionary keys are attribute names, and values are the attribute values.
    /// </summary>
    private Dictionary<string, object?> BuildAttributeDictionary(MetaverseObject mvo)
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
                AttributeDataType.Reference => attributeValue.ReferenceValue?.Id.ToString(),
                _ => null
            };

            attributes[attributeName] = value;
        }

        return attributes;
    }
}
