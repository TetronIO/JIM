// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers.Scoping;
using JIM.Application.Utilities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Logic.Scoping;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Sync;
using JIM.Models.Transactional;

namespace JIM.Application.Servers;

public partial class MetaverseServer
{
    /// <summary>
    /// Clock skew allowed between the host that created an Activity and the Worker that made a join within it, when
    /// matching a join to its Activity by time.
    /// </summary>
    private static readonly TimeSpan JoinActivityWindowTolerance = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Explains why a Metaverse Object is connected where it is, and, on request, why it is not connected elsewhere
    /// (#348). Each joined connection carries how it was joined and the scoping of every relevant enabled
    /// Synchronisation Rule, evaluated now; each enabled export rule whose Connected System holds no object joined to
    /// this one becomes a not-connected entry with its reason, hint, bullets and summary. Returns null for an unknown
    /// Metaverse Object.
    /// </summary>
    /// <param name="metaverseObjectId">The Metaverse Object to explain.</param>
    /// <param name="includeNotConnected">Whether to evaluate the not-connected entries as well; null in the result
    /// when not.</param>
    /// <remarks>
    /// A fixed number of reads, whatever the number of rules, criteria or connections: the Metaverse Object's header,
    /// its joined objects, the type's enabled rules with their scoping trees, the joined objects' Pending Exports, the
    /// Metaverse Object's values for the criteria attributes, the joined objects' values for theirs, and the join
    /// history. Every evaluation then runs in memory against one instant, so relative dates resolve identically
    /// throughout.
    /// </remarks>
    public async Task<MetaverseObjectConnectionExplanations?> GetMetaverseObjectConnectionExplanationsAsync(Guid metaverseObjectId, bool includeNotConnected)
    {
        var header = await Application.Repository.Metaverse.GetMetaverseObjectHeaderAsync(metaverseObjectId);
        if (header == null)
            return null;

        var evaluatedAt = DateTime.UtcNow;
        var joinedObjects = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectsCoreByMetaverseObjectIdAsync(metaverseObjectId);
        var rules = (await Application.Repository.ConnectedSystems.GetSyncRulesForScopingExplanationAsync(header.TypeId))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var joinedSystemIds = joinedObjects.Select(cso => cso.ConnectedSystemId).ToHashSet();
        var rulesOnJoinedSystems = rules.Where(r => joinedSystemIds.Contains(r.ConnectedSystemId)).ToList();
        var notConnectedRules = includeNotConnected
            ? rules
                .Where(r => r.Direction == SyncRuleDirection.Export
                    && !joinedSystemIds.Contains(r.ConnectedSystemId)
                    && r.ConnectedSystem.Status != ConnectedSystemStatus.Deleting)
                .OrderBy(r => r.ConnectedSystem.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        // Export rules on a joined system explain the connection (same object type) or conflict with it (another
        // type); import rules matter only for the object type they read.
        var exportRulesToEvaluate = rulesOnJoinedSystems.Where(r => r.Direction == SyncRuleDirection.Export).Concat(notConnectedRules).ToList();
        var importRulesByConnection = joinedObjects.ToDictionary(cso => cso.Id, cso => rulesOnJoinedSystems
            .Where(r => r.Direction == SyncRuleDirection.Import && r.ConnectedSystemId == cso.ConnectedSystemId && r.ConnectedSystemObjectTypeId == cso.TypeId)
            .ToList());

        var metaverseObject = new MetaverseObject { Id = metaverseObjectId };
        var metaverseAttributeIds = CriteriaAttributeIds(exportRulesToEvaluate, criterion => criterion.MetaverseAttribute?.Id);
        if (metaverseAttributeIds.Count > 0)
            metaverseObject.AttributeValues = await Application.Repository.Metaverse.GetMetaverseObjectAttributeValuesAsync(metaverseObjectId, metaverseAttributeIds);

        var objectValues = await LoadConnectedSystemObjectScopingValuesAsync(importRulesByConnection);

        var pendingExports = joinedObjects.Count > 0
            ? await Application.Repository.ConnectedSystems.GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(joinedObjects.Select(cso => cso.Id))
            : new Dictionary<Guid, PendingExport>();

        var pendingProvisioningQueuedBy = joinedObjects
            .Where(cso => cso.Status == ConnectedSystemObjectStatus.PendingProvisioning)
            .Select(cso => (cso.Id, QueuedBy: pendingExports.GetValueOrDefault(cso.Id)?.QueuedByRunProfileExecutionItemId))
            .Where(p => p.QueuedBy.HasValue)
            .ToDictionary(p => p.Id, p => p.QueuedBy!.Value);
        var joinHistory = joinedObjects.Count > 0
            ? await Application.Repository.ConnectedSystems.GetJoinHistoryAsync(joinedObjects.Select(cso => cso.Id).ToList(), pendingProvisioningQueuedBy)
            : [];

        var scoping = Application.ScopingEvaluation;
        var exportExplanations = exportRulesToEvaluate
            .DistinctBy(r => r.Id)
            .ToDictionary(r => r.Id, r => scoping.ExplainMvoForExportRule(metaverseObject, r, evaluatedAt));

        var result = new MetaverseObjectConnectionExplanations
        {
            MetaverseObjectId = metaverseObjectId,
            DisplayName = header.Name,
            EvaluatedAt = evaluatedAt
        };

        foreach (var cso in joinedObjects)
        {
            var importRules = importRulesByConnection[cso.Id];
            var exportRules = rulesOnJoinedSystems
                .Where(r => r.Direction == SyncRuleDirection.Export && r.ConnectedSystemId == cso.ConnectedSystemId)
                .ToList();
            pendingExports.TryGetValue(cso.Id, out var pendingExport);

            var row = new MetaverseObjectConnectionExplanation();
            PopulateConnectionRow(row, cso, isSource: importRules.Count > 0,
                isTarget: exportRules.Any(r => r.ConnectedSystemObjectTypeId == cso.TypeId), pendingExport);

            row.Join = BuildJoinRecord(cso, pendingExport, joinHistory, rules);

            var objectForScoping = new ConnectedSystemObject
            {
                Id = cso.Id,
                AttributeValues = objectValues.TryGetValue(cso.Id, out var values) ? values : []
            };
            row.Scoping = importRules.Select(r => scoping.ExplainCsoForImportRule(objectForScoping, r, evaluatedAt))
                .Concat(exportRules.Where(r => r.ConnectedSystemObjectTypeId == cso.TypeId).Select(r => exportExplanations[r.Id]))
                .OrderBy(e => e.SyncRuleName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            row.Conflicts = exportRules
                .Select(r => (Rule: r, Conflict: SyncEngine.DetectExportObjectTypeConflict(metaverseObject, r, cso)))
                .Where(c => c.Conflict != null)
                .Select(c => BuildConflict(c.Rule, c.Conflict!, cso.ConnectedSystem.Name, exportExplanations[c.Rule.Id]))
                .ToList();

            result.Connections.Add(row);
        }

        if (includeNotConnected)
            result.NotConnected = notConnectedRules.Select(r => BuildNotConnectedEntry(r, exportExplanations[r.Id], header.Name)).ToList();

        return result;
    }

    /// <summary>
    /// An enabled export rule that cannot connect because the connection holds the Metaverse Object's slot in its
    /// Connected System (D6), worded as synchronisation reports it.
    /// </summary>
    private static ConnectionObjectTypeConflict BuildConflict(SyncRule rule, ExportObjectTypeConflict conflict, string connectedSystemName,
        ScopingExplanation scoping) => new()
    {
        SyncRuleId = rule.Id,
        SyncRuleName = rule.Name,
        TargetObjectTypeName = conflict.TargetObjectTypeName,
        ExistingObjectTypeName = conflict.ExistingObjectTypeName,
        Description = ScopingExplanationSummariser.DescribeObjectTypeConflict(rule.Name, conflict.TargetObjectTypeName,
            conflict.ExistingObjectTypeName, connectedSystemName),
        Scoping = scoping
    };

    /// <summary>
    /// Loads every joined object's values for the attributes its import rules' criteria compare, in one read.
    /// </summary>
    private async Task<Dictionary<Guid, List<ConnectedSystemObjectAttributeValue>>> LoadConnectedSystemObjectScopingValuesAsync(
        Dictionary<Guid, List<SyncRule>> importRulesByConnection)
    {
        var objectIds = importRulesByConnection.Where(p => p.Value.Count > 0).Select(p => p.Key).ToList();
        var attributeIds = CriteriaAttributeIds(importRulesByConnection.Values.SelectMany(r => r), criterion => criterion.ConnectedSystemAttribute?.Id);
        if (objectIds.Count == 0 || attributeIds.Count == 0)
            return [];

        var values = await Application.Repository.ConnectedSystems.GetConnectedSystemObjectAttributeValuesAsync(objectIds, attributeIds);
        return values.GroupBy(v => v.ConnectedSystemObject.Id).ToDictionary(g => g.Key, g => g.ToList());
    }

    /// <summary>
    /// Every attribute the rules' scoping criteria compare, at any depth.
    /// </summary>
    private static HashSet<int> CriteriaAttributeIds(IEnumerable<SyncRule> rules, Func<SyncRuleScopingCriteria, int?> attributeId)
    {
        var ids = new HashSet<int>();
        foreach (var group in rules.SelectMany(r => r.ObjectScopingCriteriaGroups))
            AddCriteriaAttributeIds(group, attributeId, ids);
        return ids;
    }

    private static void AddCriteriaAttributeIds(SyncRuleScopingCriteriaGroup group, Func<SyncRuleScopingCriteria, int?> attributeId, HashSet<int> ids)
    {
        foreach (var id in group.Criteria.Select(attributeId).Where(id => id.HasValue))
            ids.Add(id!.Value);
        foreach (var child in group.ChildGroups)
            AddCriteriaAttributeIds(child, attributeId, ids);
    }

    /// <summary>
    /// How a connection was joined: the rule recorded on the object when it was joined; for an object joined before
    /// joins were recorded, the rule its history names, where history still holds it; otherwise not recorded. The
    /// Activity link comes from history either way.
    /// </summary>
    private static JoinRecord BuildJoinRecord(ConnectedSystemObject cso, PendingExport? pendingExport, List<JoinHistoryEntry> joinHistory,
        List<SyncRule> rules)
    {
        var evidence = FindJoinEvidence(cso, joinHistory);
        var record = new JoinRecord
        {
            JoinType = cso.JoinType,
            DateJoined = cso.DateJoined,
            ActivityId = evidence?.ActivityId,
            RunProfileExecutionItemId = evidence?.RunProfileExecutionItemId
        };

        if (cso.JoinMethod.HasValue || cso.JoinSyncRuleName != null)
        {
            record.Method = cso.JoinMethod;
            record.SyncRuleId = cso.JoinSyncRuleId;
            record.SyncRuleName = cso.JoinSyncRuleName;
            record.Source = JoinRecordSource.Recorded;
        }
        else if (cso.Status == ConnectedSystemObjectStatus.PendingProvisioning && pendingExport?.ProvisioningSyncRuleId is { } provisioningRuleId)
        {
            // Still pending: the Pending Export names the rule that is provisioning it.
            record.Method = ConnectedSystemObjectJoinMethod.Provisioning;
            record.SyncRuleId = provisioningRuleId;
            record.SyncRuleName = rules.FirstOrDefault(r => r.Id == provisioningRuleId)?.Name ?? evidence?.SyncRuleName;
            record.Source = record.SyncRuleName != null ? JoinRecordSource.Derived : JoinRecordSource.NotRecorded;
        }
        else
        {
            // History says how the object joined; only a projection's or a provisioning's names the rule (export
            // matching leaves no history at all, so a join in history was an inbound match).
            record.Method = evidence?.JoinType switch
            {
                ConnectedSystemObjectJoinType.Projected => ConnectedSystemObjectJoinMethod.Projection,
                ConnectedSystemObjectJoinType.Provisioned => ConnectedSystemObjectJoinMethod.Provisioning,
                ConnectedSystemObjectJoinType.Joined => ConnectedSystemObjectJoinMethod.InboundMatching,
                _ => null
            };
            record.SyncRuleId = evidence?.SyncRuleId;
            record.SyncRuleName = evidence?.SyncRuleName;
            record.Source = record.SyncRuleName != null ? JoinRecordSource.Derived : JoinRecordSource.NotRecorded;
        }

        record.Description = ScopingExplanationSummariser.DescribeJoin(record);
        return record;
    }

    /// <summary>
    /// The history that made the object's current join: of the same kind, from an Activity running when the object was
    /// joined, the latest if several. History from an earlier join of the same object is not this join's.
    /// </summary>
    private static JoinHistoryEntry? FindJoinEvidence(ConnectedSystemObject cso, List<JoinHistoryEntry> joinHistory)
    {
        var candidates = joinHistory.Where(h => h.ConnectedSystemObjectId == cso.Id && h.JoinType == cso.JoinType);
        if (cso.DateJoined is { } dateJoined)
        {
            candidates = candidates.Where(h =>
                h.ActivityCreated - JoinActivityWindowTolerance <= dateJoined
                && (h.ActivityDuration == null || dateJoined <= h.ActivityCreated + h.ActivityDuration.Value + JoinActivityWindowTolerance));
        }

        return candidates.OrderByDescending(h => h.ActivityCreated).FirstOrDefault();
    }

    /// <summary>
    /// Why an enabled export rule has not connected the Metaverse Object to its Connected System (D5): scoping that
    /// cannot be evaluated, out of scope, or in scope but not provisioning or not yet provisioned.
    /// </summary>
    private static NotConnectedEntry BuildNotConnectedEntry(SyncRule rule, ScopingExplanation explanation, string? objectDisplayName)
    {
        var reason = explanation.Outcome switch
        {
            ScopingRuleOutcome.Undetermined => NotConnectedReason.RuleMisconfigured,
            ScopingRuleOutcome.OutOfScope => NotConnectedReason.NotInScope,
            _ => rule.ProvisionToConnectedSystem == true ? NotConnectedReason.NotYetProvisioned : NotConnectedReason.ProvisioningDisabled
        };

        var summary = ScopingExplanationSummariser.SummariseNotConnected(objectDisplayName, rule.ConnectedSystem.Name,
            rule.ConnectedSystem.Status == ConnectedSystemStatus.Disabled, reason, explanation);

        return new NotConnectedEntry
        {
            ConnectedSystemId = rule.ConnectedSystemId,
            ConnectedSystemName = rule.ConnectedSystem.Name,
            ConnectedSystemStatus = rule.ConnectedSystem.Status,
            SyncRuleId = rule.Id,
            SyncRuleName = rule.Name,
            ObjectTypeName = rule.ConnectedSystemObjectType?.Name ?? string.Empty,
            Reason = reason,
            Hint = summary.Hint,
            BulletsTitle = summary.BulletsTitle,
            Bullets = summary.Bullets,
            Summary = summary.Summary,
            Scoping = explanation
        };
    }

    /// <summary>
    /// Fills a Connections tab row: the object's identity, its Connected System, its role and its derived state.
    /// </summary>
    private static void PopulateConnectionRow(MetaverseObjectConnection row, ConnectedSystemObject cso, bool isSource, bool isTarget,
        PendingExport? pendingExport)
    {
        var state = ConnectedSystemObjectConnectionStateResolver.Resolve(cso.Status, pendingExport);
        row.ConnectedSystemObjectId = cso.Id;
        row.DisplayName = cso.ExternalIdAttributeValue?.ToStringNoName() ?? cso.Id.ToString();
        row.ConnectedSystemId = cso.ConnectedSystemId;
        row.ConnectedSystemName = cso.ConnectedSystem.Name;
        row.ObjectTypeName = cso.Type.Name;
        row.JoinType = cso.JoinType;
        row.IsSource = isSource;
        row.IsTarget = isTarget;
        row.State = state;
        row.PendingAttributeChangeCount = state == ConnectedSystemObjectConnectionState.UpdatePending
            ? pendingExport?.AttributeValueChanges.Count
            : null;
        row.LastSynchronised = cso.LastUpdated;
    }
}
