// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Models.Utility;

namespace JIM.InMemoryData;

/// <summary>
/// Dictionary-based in-memory implementation of <see cref="ISyncRepository"/> for tests.
/// <para>
/// Provides deterministic behaviour without EF Core quirks (no auto-tracked navigation
/// properties, no change tracker identity conflicts, no try/catch fallback blocks).
/// Tests pre-populate data via public <c>Seed*</c> methods, then exercise sync processor
/// logic against the same <c>ISyncRepository</c> contract used in production.
/// </para>
/// </summary>
public class SyncRepository : ISyncRepository
{
    #region Storage

    private readonly Dictionary<Guid, ConnectedSystemObject> _csos = new();
    private readonly Dictionary<Guid, MetaverseObject> _mvos = new();
    private readonly Dictionary<Guid, PendingExport> _pendingExports = new();
    private readonly Dictionary<Guid, Activity> _activities = new();
    private readonly Dictionary<Guid, ActivityRunProfileExecutionItem> _rpeis = new();

    /// <summary>
    /// When true, <see cref="BulkInsertRpeisAsync"/> returns true (simulating the production
    /// raw-SQL path) so the processor clears <c>_activity.RunProfileExecutionItems</c> after each
    /// page. Off by default because most existing tests assert against that in-memory collection.
    /// Tests exercising cross-page RPEI behaviour should opt in so the test exercises the code
    /// path used in production.
    /// </summary>
    public bool SimulateRawSqlPersistence { get; set; }

    private readonly Dictionary<int, ConnectedSystem> _connectedSystems = new();
    private readonly Dictionary<int, SyncRule> _syncRules = new();
    private readonly Dictionary<int, ConnectedSystemObjectType> _objectTypes = new();
    private readonly Dictionary<Guid, MetaverseObjectChange> _mvoChanges = new();

    // Secondary indexes
    private readonly Dictionary<int, HashSet<Guid>> _csosByConnectedSystem = new();
    private readonly Dictionary<int, HashSet<Guid>> _pendingExportsByCs = new();
    private readonly Dictionary<Guid, Guid> _pendingExportsByCsoId = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _csosByMvo = new();
    private readonly Dictionary<(int ConnectedSystemId, int ExternalIdAttributeId, string ExternalIdValue), Guid> _csoCache = new();

    // Configurable settings
    private int _syncPageSize = 100;
    private ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel _syncOutcomeTrackingLevel =
        ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.None;
    private bool _csoChangeTrackingEnabled = true;
    private bool _mvoChangeTrackingEnabled = true;

    #endregion

    #region Test Inspection — Read-Only Access to Internal State

    /// <summary>All Connected System Objects, keyed by CSO ID.</summary>
    public IReadOnlyDictionary<Guid, ConnectedSystemObject> ConnectedSystemObjects => _csos;

    /// <summary>All Metaverse Objects, keyed by MVO ID.</summary>
    public IReadOnlyDictionary<Guid, MetaverseObject> MetaverseObjects => _mvos;

    /// <summary>All Pending Exports, keyed by Pending Export ID.</summary>
    public IReadOnlyDictionary<Guid, PendingExport> PendingExports => _pendingExports;

    /// <summary>All activities, keyed by activity ID.</summary>
    public IReadOnlyDictionary<Guid, Activity> Activities => _activities;

    /// <summary>All Run Profile execution items, keyed by RPEI ID.</summary>
    public IReadOnlyDictionary<Guid, ActivityRunProfileExecutionItem> Rpeis => _rpeis;

    /// <summary>All Connected Systems, keyed by Connected System ID.</summary>
    public IReadOnlyDictionary<int, ConnectedSystem> ConnectedSystems => _connectedSystems;

    /// <summary>All Synchronisation Rules, keyed by Synchronisation Rule ID.</summary>
    public IReadOnlyDictionary<int, SyncRule> SyncRules => _syncRules;

    /// <summary>All Connected System Object Types, keyed by object type ID.</summary>
    public IReadOnlyDictionary<int, ConnectedSystemObjectType> ObjectTypes => _objectTypes;

    /// <summary>All Metaverse Object change records, keyed by change ID.</summary>
    public IReadOnlyDictionary<Guid, MetaverseObjectChange> MetaverseObjectChanges => _mvoChanges;

    #endregion

    #region Seeding API

    public void SeedConnectedSystem(ConnectedSystem cs)
    {
        _connectedSystems[cs.Id] = cs;
    }

    public void SeedObjectType(ConnectedSystemObjectType objectType)
    {
        _objectTypes[objectType.Id] = objectType;
    }

    public void SeedSyncRule(SyncRule syncRule)
    {
        _syncRules[syncRule.Id] = syncRule;
    }

    public void SeedConnectedSystemObject(ConnectedSystemObject cso)
    {
        _csos[cso.Id] = cso;
        if (!_csosByConnectedSystem.TryGetValue(cso.ConnectedSystemId, out var csSet))
        {
            csSet = new HashSet<Guid>();
            _csosByConnectedSystem[cso.ConnectedSystemId] = csSet;
        }
        csSet.Add(cso.Id);

        if (cso.MetaverseObjectId.HasValue)
            AddToMvoIndex(cso.MetaverseObjectId.Value, cso.Id);
    }

    public void SeedMetaverseObject(MetaverseObject mvo)
    {
        _mvos[mvo.Id] = mvo;
    }

    public void SeedPendingExport(PendingExport pe)
    {
        _pendingExports[pe.Id] = pe;
        if (!_pendingExportsByCs.TryGetValue(pe.ConnectedSystemId, out var csSet))
        {
            csSet = new HashSet<Guid>();
            _pendingExportsByCs[pe.ConnectedSystemId] = csSet;
        }
        csSet.Add(pe.Id);

        if (pe.ConnectedSystemObjectId.HasValue)
            _pendingExportsByCsoId[pe.ConnectedSystemObjectId.Value] = pe.Id;
    }

    public void SeedActivity(Activity activity)
    {
        _activities[activity.Id] = activity;
    }

    /// <summary>
    /// Removes all Pending Exports from the store. Used by tests to reset state between sync cycles.
    /// </summary>
    public void ClearAllPendingExports()
    {
        _pendingExports.Clear();
        _pendingExportsByCs.Clear();
        _pendingExportsByCsoId.Clear();
    }

    public void SetSyncPageSize(int pageSize) => _syncPageSize = pageSize;

    public void SetSyncOutcomeTrackingLevel(ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel level)
        => _syncOutcomeTrackingLevel = level;

    public void SetCsoChangeTrackingEnabled(bool enabled) => _csoChangeTrackingEnabled = enabled;
    public void SetMvoChangeTrackingEnabled(bool enabled) => _mvoChangeTrackingEnabled = enabled;

    #endregion

    #region Connected System Object — Reads

    public Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId, int? partitionId = null)
    {
        var csos = GetCsosForSystem(connectedSystemId);
        if (partitionId != null)
            csos = csos.Where(c => c.PartitionId == partitionId);
        return Task.FromResult(csos.Count());
    }

    public Task<int> GetConnectedSystemObjectModifiedSinceCountAsync(int connectedSystemId, DateTime modifiedSince)
    {
        var count = GetCsosForSystem(connectedSystemId)
            .Count(c => c.LastUpdated.HasValue && c.LastUpdated.Value >= modifiedSince);
        return Task.FromResult(count);
    }

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(
        int connectedSystemId, int page, int pageSize, int? knownTotalCount = null, DateTime? lastSyncTimestamp = null)
    {
        var all = GetCsosForSystem(connectedSystemId)
            .OrderBy(c => c.Created).ThenBy(c => c.Id)
            .ToList();
        return Task.FromResult(BuildPagedResult(all, page, pageSize));
    }

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsModifiedSinceAsync(
        int connectedSystemId, DateTime modifiedSince, int page, int pageSize, int? knownTotalCount = null)
    {
        var filtered = GetCsosForSystem(connectedSystemId)
            .Where(c => c.LastUpdated.HasValue && c.LastUpdated.Value >= modifiedSince)
            .OrderBy(c => c.Created).ThenBy(c => c.Id)
            .ToList();
        return Task.FromResult(BuildPagedResult(filtered, page, pageSize));
    }

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectAsync(int connectedSystemId, Guid csoId)
    {
        _csos.TryGetValue(csoId, out var cso);
        if (cso != null && cso.ConnectedSystemId != connectedSystemId)
            cso = null;
        return Task.FromResult(cso);
    }

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(
        int connectedSystemId, int attributeId, int attributeValue)
    {
        var cso = GetCsosForSystem(connectedSystemId)
            .FirstOrDefault(c => c.AttributeValues
                .Any(av => av.AttributeId == attributeId && av.IntValue == attributeValue));
        return Task.FromResult(cso);
    }

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(
        int connectedSystemId, int attributeId, string attributeValue)
    {
        var cso = GetCsosForSystem(connectedSystemId)
            .FirstOrDefault(c => c.AttributeValues
                .Any(av => av.AttributeId == attributeId && av.StringValue == attributeValue));
        return Task.FromResult(cso);
    }

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(
        int connectedSystemId, int attributeId, Guid attributeValue)
    {
        var cso = GetCsosForSystem(connectedSystemId)
            .FirstOrDefault(c => c.AttributeValues
                .Any(av => av.AttributeId == attributeId && av.GuidValue == attributeValue));
        return Task.FromResult(cso);
    }

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByAttributeAsync(
        int connectedSystemId, int attributeId, long attributeValue)
    {
        var cso = GetCsosForSystem(connectedSystemId)
            .FirstOrDefault(c => c.AttributeValues
                .Any(av => av.AttributeId == attributeId && av.LongValue == attributeValue));
        return Task.FromResult(cso);
    }

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAsync(
        int connectedSystemId, int objectTypeId, string secondaryExternalIdValue)
    {
        var cso = GetCsosForSystem(connectedSystemId)
            .FirstOrDefault(c => c.TypeId == objectTypeId &&
                c.SecondaryExternalIdAttributeId.HasValue &&
                c.AttributeValues.Any(av =>
                    av.AttributeId == c.SecondaryExternalIdAttributeId.Value &&
                    av.StringValue == secondaryExternalIdValue));
        return Task.FromResult(cso);
    }

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectBySecondaryExternalIdAnyTypeAsync(
        int connectedSystemId, string secondaryExternalIdValue)
    {
        var cso = GetCsosForSystem(connectedSystemId)
            .FirstOrDefault(c =>
                c.SecondaryExternalIdAttributeId.HasValue &&
                c.AttributeValues.Any(av =>
                    av.AttributeId == c.SecondaryExternalIdAttributeId.Value &&
                    av.StringValue == secondaryExternalIdValue));
        return Task.FromResult(cso);
    }

    public Task<Dictionary<string, Guid>> GetAllCsoExternalIdMappingsAsync(int connectedSystemId)
    {
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var cso in GetCsosForSystem(connectedSystemId))
        {
            // Try primary external ID first
            var primaryAv = cso.AttributeValues.FirstOrDefault(av => av.AttributeId == cso.ExternalIdAttributeId);
            var primaryValue = GetExternalIdValueString(primaryAv);

            if (primaryValue != null)
            {
                var cacheKey = $"cso:{connectedSystemId}:{cso.ExternalIdAttributeId}:{primaryValue}";
                result.TryAdd(cacheKey, cso.Id);
                continue;
            }

            // Fall back to secondary external ID (PendingProvisioning CSOs)
            if (cso.SecondaryExternalIdAttributeId.HasValue)
            {
                var secondaryAv = cso.AttributeValues.FirstOrDefault(av => av.AttributeId == cso.SecondaryExternalIdAttributeId);
                var secondaryValue = GetExternalIdValueString(secondaryAv);

                if (secondaryValue != null)
                {
                    var cacheKey = $"cso:{connectedSystemId}:{cso.SecondaryExternalIdAttributeId.Value}:{secondaryValue}";
                    result.TryAdd(cacheKey, cso.Id);
                }
            }
        }
        return Task.FromResult(result);
    }

    public virtual Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsAsync(int connectedSystemId, IEnumerable<Guid> csoIds)
    {
        var idSet = new HashSet<Guid>(csoIds);
        var result = GetCsosForSystem(connectedSystemId)
            .Where(cso => idSet.Contains(cso.Id))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsNoTrackingAsync(int connectedSystemId, IEnumerable<Guid> csoIds)
        => GetConnectedSystemObjectsByIdsAsync(connectedSystemId, csoIds);

    public Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsByAttributeValuesAsync(
        int connectedSystemId, int attributeId, IEnumerable<string> attributeValues)
    {
        var valueSet = new HashSet<string>(attributeValues);
        var result = new Dictionary<string, ConnectedSystemObject>();
        foreach (var cso in GetCsosForSystem(connectedSystemId))
        {
            foreach (var av in cso.AttributeValues)
            {
                if (av.AttributeId == attributeId && av.StringValue != null && valueSet.Contains(av.StringValue))
                {
                    result.TryAdd(av.StringValue, cso);
                }
            }
        }
        return Task.FromResult(result);
    }

    public Task<Dictionary<string, ConnectedSystemObject>> GetConnectedSystemObjectsBySecondaryExternalIdAnyTypeValuesAsync(
        int connectedSystemId, IEnumerable<string> secondaryExternalIdValues)
    {
        var valueSet = new HashSet<string>(secondaryExternalIdValues);
        var result = new Dictionary<string, ConnectedSystemObject>();
        foreach (var cso in GetCsosForSystem(connectedSystemId))
        {
            if (!cso.SecondaryExternalIdAttributeId.HasValue) continue;
            var secIdAv = cso.AttributeValues
                .FirstOrDefault(av => av.AttributeId == cso.SecondaryExternalIdAttributeId.Value);
            if (secIdAv?.StringValue != null && valueSet.Contains(secIdAv.StringValue))
                result.TryAdd(secIdAv.StringValue, cso);
        }
        return Task.FromResult(result);
    }

    public Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int objectTypeId, int? partitionId = null)
    {
        var csos = GetCsosForSystem(connectedSystemId).Where(c => c.TypeId == objectTypeId);
        if (partitionId != null)
            csos = csos.Where(c => c.PartitionId == partitionId);
        var values = csos
            .Select(c => c.AttributeValues.FirstOrDefault(av => av.AttributeId == c.ExternalIdAttributeId))
            .Where(av => av?.IntValue != null)
            .Select(av => av!.IntValue!.Value)
            .ToList();
        return Task.FromResult(values);
    }

    public Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int objectTypeId, int? partitionId = null)
    {
        var csos = GetCsosForSystem(connectedSystemId).Where(c => c.TypeId == objectTypeId);
        if (partitionId != null)
            csos = csos.Where(c => c.PartitionId == partitionId);
        var values = csos
            .Select(c => c.AttributeValues.FirstOrDefault(av => av.AttributeId == c.ExternalIdAttributeId))
            .Where(av => av?.StringValue != null)
            .Select(av => av!.StringValue!)
            .ToList();
        return Task.FromResult(values);
    }

    public Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int objectTypeId, int? partitionId = null)
    {
        var csos = GetCsosForSystem(connectedSystemId).Where(c => c.TypeId == objectTypeId);
        if (partitionId != null)
            csos = csos.Where(c => c.PartitionId == partitionId);
        var values = csos
            .Select(c => c.AttributeValues.FirstOrDefault(av => av.AttributeId == c.ExternalIdAttributeId))
            .Where(av => av?.GuidValue != null)
            .Select(av => av!.GuidValue!.Value)
            .ToList();
        return Task.FromResult(values);
    }

    public Task<List<long>> GetAllExternalIdAttributeValuesOfTypeLongAsync(int connectedSystemId, int objectTypeId, int? partitionId = null)
    {
        var csos = GetCsosForSystem(connectedSystemId).Where(c => c.TypeId == objectTypeId);
        if (partitionId != null)
            csos = csos.Where(c => c.PartitionId == partitionId);
        var values = csos
            .Select(c => c.AttributeValues.FirstOrDefault(av => av.AttributeId == c.ExternalIdAttributeId))
            .Where(av => av?.LongValue != null)
            .Select(av => av!.LongValue!.Value)
            .ToList();
        return Task.FromResult(values);
    }

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsForReferenceResolutionAsync(IList<Guid> csoIds)
    {
        var result = csoIds
            .Where(id => _csos.ContainsKey(id))
            .Select(id => _csos[id])
            .ToList();
        return Task.FromResult(result);
    }

    public Task<Dictionary<Guid, string>> GetReferenceExternalIdsAsync(Guid csoId)
    {
        var result = new Dictionary<Guid, string>();
        if (_csos.TryGetValue(csoId, out var cso))
        {
            foreach (var av in cso.AttributeValues)
            {
                if (av.UnresolvedReferenceValue != null)
                    result[av.Id] = av.UnresolvedReferenceValue;
            }
        }
        return Task.FromResult(result);
    }

    public Task<int> GetConnectedSystemObjectCountByMetaverseObjectIdAsync(Guid metaverseObjectId)
    {
        var count = _csosByMvo.TryGetValue(metaverseObjectId, out var ids) ? ids.Count : 0;
        return Task.FromResult(count);
    }

    public Task<int> GetConnectedSystemObjectCountByMvoAsync(int connectedSystemId, Guid metaverseObjectId)
    {
        // Scan _csos directly rather than using the _csosByMvo index so that within-page joins
        // (where MetaverseObjectId has been set in-memory but the index hasn't been flushed yet)
        // are counted correctly. This matches the behaviour of the production EF Core query.
        var count = _csos.Values.Count(c =>
            c.ConnectedSystemId == connectedSystemId &&
            c.MetaverseObjectId == metaverseObjectId);
        return Task.FromResult(count);
    }

    #endregion

    #region Connected System Object — Writes

    public Task CreateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects, HashSet<Guid>? previouslyCommittedCsoIds = null)
    {
        // InMemory implementation does not need previouslyCommittedCsoIds — there are no FK
        // constraints or batch isolation. The parameter is accepted for interface compatibility.
        foreach (var cso in connectedSystemObjects)
        {
            if (cso.Id == Guid.Empty)
                cso.Id = Guid.NewGuid();

            FixupCsoNavigationProperties(cso);
            _csos[cso.Id] = cso;
            AddToCsIndex(cso);
        }
        return Task.CompletedTask;
    }

    public async Task CreateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis,
        Func<int, Task>? onBatchPersisted = null)
    {
        await CreateConnectedSystemObjectsAsync(connectedSystemObjects);
        if (onBatchPersisted != null)
            await onBatchPersisted(connectedSystemObjects.Count);
    }

    public Task UpdateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<(Guid CsoId, ConnectedSystemObjectAttributeValue Value)>? pendingAdditions = null,
        List<Guid>? pendingRemovalIds = null)
    {
        // In the InMemory provider, import processing already modified cso.AttributeValues
        // in-memory (adds/removes). The pendingAdditions/RemovalIds snapshot is for the
        // relational path where AsNoTracking prevents EF from detecting these changes.
        // We just need to persist the CSOs to the in-memory store.
        foreach (var cso in connectedSystemObjects)
        {
            FixupCsoNavigationProperties(cso);
            _csos[cso.Id] = cso;
            UpdateMvoIndex(cso);
        }
        return Task.CompletedTask;
    }

    public Task UpdateConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis)
    {
        // Apply PendingAttributeValueAdditions/Removals — in production this is done by
        // ConnectedSystemServer.ProcessConnectedSystemObjectAttributeValueChanges before
        // delegating to the repository. InMemoryData must do it here.
        foreach (var cso in connectedSystemObjects)
        {
            foreach (var addition in cso.PendingAttributeValueAdditions)
            {
                if (addition.AttributeId == 0 && addition.Attribute != null)
                    addition.AttributeId = addition.Attribute.Id;
                cso.AttributeValues.Add(addition);
            }

            // Single pass over cso.AttributeValues (#988) instead of one RemoveAll/Remove scan per
            // pending removal - the latter is O(removals x AttributeValues), quadratic for a large
            // multi-valued attribute (e.g. a big group's next Full Import replacing membership).
            if (cso.PendingAttributeValueRemovals.Count > 0)
            {
                // Use reference equality when Id is Guid.Empty (newly created, not yet persisted).
                // With EF Core, these objects have DB-generated IDs. In-memory, they remain empty.
                var removalIds = new HashSet<Guid>(cso.PendingAttributeValueRemovals.Where(r => r.Id != Guid.Empty).Select(r => r.Id));
                var removalRefs = new HashSet<ConnectedSystemObjectAttributeValue>(cso.PendingAttributeValueRemovals.Where(r => r.Id == Guid.Empty));
                cso.AttributeValues.RemoveAll(av => (av.Id != Guid.Empty && removalIds.Contains(av.Id)) || removalRefs.Contains(av));
            }

            cso.PendingAttributeValueAdditions = new List<ConnectedSystemObjectAttributeValue>();
            cso.PendingAttributeValueRemovals = new List<ConnectedSystemObjectAttributeValue>();

            // Also fixup the joined MVO's attribute values — ApplyPendingMetaverseObjectAttributeChanges
            // adds values with Attribute nav prop set but AttributeId == 0
            if (cso.MetaverseObject != null)
                FixupMvoAttributeValues(cso.MetaverseObject);
        }

        return UpdateConnectedSystemObjectsAsync(connectedSystemObjects);
    }

    public Task UpdateConnectedSystemObjectJoinStatesAsync(List<ConnectedSystemObject> connectedSystemObjects)
    {
        foreach (var cso in connectedSystemObjects)
        {
            if (_csos.TryGetValue(cso.Id, out var stored))
            {
                stored.MetaverseObjectId = cso.MetaverseObjectId;
                stored.MetaverseObject = cso.MetaverseObject;
                stored.JoinType = cso.JoinType;
                stored.Status = cso.Status;
                UpdateMvoIndex(stored);
            }
        }
        return Task.CompletedTask;
    }

    public Task ClearConnectedSystemObjectScopeReviewPendingAsync(IReadOnlyCollection<Guid> ids)
    {
        foreach (var stored in ids.Select(id => _csos.TryGetValue(id, out var cso) ? cso : null).Where(cso => cso != null))
            stored!.ScopeReviewPending = false;
        return Task.CompletedTask;
    }

    public Task UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(
        List<(ConnectedSystemObject cso, List<ConnectedSystemObjectAttributeValue> newAttributeValues)> updates)
    {
        foreach (var (cso, newAvs) in updates)
        {
            if (_csos.TryGetValue(cso.Id, out var stored))
            {
                // Since InMemoryData stores references (not copies), stored and cso may be the same object.
                // Only add values that aren't already in the collection to avoid duplicates.
                foreach (var av in newAvs)
                {
                    // Fixup FK from nav prop (EF Core does this automatically on SaveChanges)
                    if (av.AttributeId == 0 && av.Attribute != null)
                        av.AttributeId = av.Attribute.Id;

                    if (!stored.AttributeValues.Contains(av))
                        stored.AttributeValues.Add(av);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
    {
        foreach (var cso in connectedSystemObjects)
            RemoveCso(cso);
        return Task.CompletedTask;
    }

    public Task DeleteConnectedSystemObjectsAsync(
        List<ConnectedSystemObject> connectedSystemObjects,
        List<ActivityRunProfileExecutionItem> rpeis)
        => DeleteConnectedSystemObjectsAsync(connectedSystemObjects);

    public Task<int> FixupCrossBatchReferenceIdsAsync(int connectedSystemId)
    {
        var resolved = 0;
        foreach (var cso in GetCsosForSystem(connectedSystemId))
        {
            foreach (var av in cso.AttributeValues)
            {
                if (av.UnresolvedReferenceValue == null || av.ReferenceValueId.HasValue)
                    continue;

                // Try to find a CSO with this external ID value in the same Connected System
                var target = GetCsosForSystem(connectedSystemId)
                    .FirstOrDefault(c => c.AttributeValues
                        .Any(tav => tav.AttributeId == c.ExternalIdAttributeId &&
                                    tav.StringValue == av.UnresolvedReferenceValue));

                if (target != null)
                {
                    av.ReferenceValueId = target.Id;
                    av.ReferenceValue = target;
                    resolved++;
                }
            }
        }
        return Task.FromResult(resolved);
    }

    public Task<int> FixupCrossBatchChangeRecordReferenceIdsAsync(int connectedSystemId)
    {
        // Build a lookup of secondary external ID values → CSO for the Connected System
        var secondaryIdLookup = new Dictionary<string, ConnectedSystemObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var cso in GetCsosForSystem(connectedSystemId))
        {
            foreach (var av in cso.AttributeValues)
            {
                if (av.Attribute?.IsSecondaryExternalId == true && av.StringValue != null)
                    secondaryIdLookup.TryAdd(av.StringValue, cso);
            }
        }

        var resolved = 0;

        // Iterate all RPEIs and resolve change record attribute values
        foreach (var rpei in _rpeis.Values)
        {
            var change = rpei.ConnectedSystemObjectChange;
            if (change == null || change.ConnectedSystemId != connectedSystemId)
                continue;

            foreach (var attrChange in change.AttributeChanges)
            {
                foreach (var valueChange in attrChange.ValueChanges)
                {
                    if (valueChange.ReferenceValue != null || valueChange.StringValue == null)
                        continue;

                    if (secondaryIdLookup.TryGetValue(valueChange.StringValue, out var targetCso))
                    {
                        valueChange.ReferenceValue = targetCso;
                        resolved++;
                    }
                }
            }
        }

        return Task.FromResult(resolved);
    }

    public Task<int> FixupMvoReferenceValueIdsAsync(IReadOnlyList<(Guid MvoId, int AttributeId, Guid TargetMvoId)> fixups)
    {
        // In-memory repo handles this via FixupMvoAttributeValues on read.
        return Task.FromResult(0);
    }

    #endregion

    #region Metaverse Object — Reads

    public Task<MetaverseObject?> FindMatchingMetaverseObjectAsync(
        ConnectedSystemObject cso, List<ObjectMatchingRule> matchingRules)
    {
        foreach (var rule in matchingRules.OrderBy(r => r.Order))
        {
            if (rule.TargetMetaverseAttributeId == null)
                continue;

            // Get the source value from the CSO
            string? sourceValue = null;
            foreach (var source in rule.Sources.OrderBy(s => s.Order))
            {
                if (source.ConnectedSystemAttributeId.HasValue)
                {
                    var av = cso.AttributeValues
                        .FirstOrDefault(a => a.AttributeId == source.ConnectedSystemAttributeId.Value);
                    sourceValue = av?.StringValue ?? av?.IntValue?.ToString() ??
                                  av?.GuidValue?.ToString() ?? av?.LongValue?.ToString();
                    if (sourceValue != null) break;
                }
            }

            if (sourceValue == null)
                continue;

            // Find matching MVOs
            var comparison = rule.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            var targetAttrId = rule.TargetMetaverseAttributeId.Value;
            var matches = _mvos.Values
                .Where(mvo =>
                {
                    if (rule.MetaverseObjectTypeId.HasValue && mvo.Type?.Id != rule.MetaverseObjectTypeId.Value)
                        return false;
                    return mvo.AttributeValues.Any(a =>
                        a.AttributeId == targetAttrId &&
                        string.Equals(
                            a.StringValue ?? a.IntValue?.ToString() ?? a.GuidValue?.ToString() ?? a.LongValue?.ToString(),
                            sourceValue,
                            comparison));
                })
                .ToList();

            if (matches.Count > 1)
                throw new MultipleMatchesException(
                    $"Multiple Metaverse Objects matched for rule {rule.Id}",
                    matches.Select(m => m.Id).ToList());

            if (matches.Count == 1)
                return Task.FromResult<MetaverseObject?>(matches[0]);
        }

        return Task.FromResult<MetaverseObject?>(null);
    }

    public Task<MetaverseObject?> FindMetaverseObjectUsingMatchingRuleAsync(
        ConnectedSystemObject connectedSystemObject,
        MetaverseObjectType metaverseObjectType,
        ObjectMatchingRule rule)
    {
        if (rule.TargetMetaverseAttributeId == null)
            return Task.FromResult<MetaverseObject?>(null);

        // Get the source value from the CSO
        string? sourceValue = null;
        foreach (var source in rule.Sources.OrderBy(s => s.Order))
        {
            if (source.ConnectedSystemAttributeId.HasValue || source.ConnectedSystemAttribute != null)
            {
                var attrId = source.ConnectedSystemAttribute?.Id ?? source.ConnectedSystemAttributeId!.Value;
                var av = connectedSystemObject.AttributeValues
                    .FirstOrDefault(a => a.AttributeId == attrId);
                sourceValue = av?.StringValue ?? av?.IntValue?.ToString() ??
                              av?.GuidValue?.ToString() ?? av?.LongValue?.ToString();
                if (sourceValue != null) break;
            }
        }

        if (sourceValue == null)
            return Task.FromResult<MetaverseObject?>(null);

        var comparison = rule.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var targetAttrId = rule.TargetMetaverseAttribute?.Id ?? rule.TargetMetaverseAttributeId!.Value;
        var matches = _mvos.Values
            .Where(mvo =>
            {
                if (mvo.Type?.Id != metaverseObjectType.Id)
                    return false;
                return mvo.AttributeValues.Any(a =>
                    a.AttributeId == targetAttrId &&
                    string.Equals(
                        a.StringValue ?? a.IntValue?.ToString() ?? a.GuidValue?.ToString() ?? a.LongValue?.ToString(),
                        sourceValue,
                        comparison));
            })
            .ToList();

        if (matches.Count > 1)
            throw new MultipleMatchesException(
                $"Multiple Metaverse Objects matched for rule {rule.Id}",
                matches.Select(m => m.Id).ToList());

        return Task.FromResult(matches.Count == 1 ? matches[0] : null);
    }

    public Task<ConnectedSystemObject?> FindConnectedSystemObjectUsingMatchingRuleAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        ObjectMatchingRule rule)
    {
        if (rule.Sources.Count == 0)
            return Task.FromResult<ConnectedSystemObject?>(null);

        var source = rule.Sources[0];

        // The connector-space side of the comparison always comes from the source's Connected System attribute.
        var csAttrId = source.ConnectedSystemAttribute?.Id ?? source.ConnectedSystemAttributeId;
        if (csAttrId == null)
            return Task.FromResult<ConnectedSystemObject?>(null);

        // The MVO side: an explicit Metaverse attribute on the source wins; otherwise invert the standard
        // inbound rule shape by reading the rule's target Metaverse attribute (mirrors the Postgres implementation).
        var mvoAttrId = source.MetaverseAttribute?.Id ?? source.MetaverseAttributeId
            ?? rule.TargetMetaverseAttribute?.Id ?? rule.TargetMetaverseAttributeId;
        if (mvoAttrId == null)
            return Task.FromResult<ConnectedSystemObject?>(null);

        var mvoAttr = metaverseObject.AttributeValues?
            .FirstOrDefault(av => av.AttributeId == mvoAttrId);
        if (mvoAttr == null)
            return Task.FromResult<ConnectedSystemObject?>(null);

        var mvoVal = mvoAttr.StringValue ?? mvoAttr.GuidValue?.ToString() ?? mvoAttr.IntValue?.ToString();
        if (mvoVal == null)
            return Task.FromResult<ConnectedSystemObject?>(null);

        var comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        bool ValueMatches(ConnectedSystemObject cso)
        {
            var csoAttr = cso.AttributeValues?.FirstOrDefault(av => av.AttributeId == csAttrId);
            if (csoAttr == null)
                return false;
            var csoVal = csoAttr.StringValue ?? csoAttr.GuidValue?.ToString() ?? csoAttr.IntValue?.ToString();
            return string.Equals(mvoVal, csoVal, comparison);
        }

        // Only unjoined, Normal-status CSOs are eligible: matching must never steal a CSO already joined
        // to another Metaverse Object, and an Obsolete or PendingProvisioning CSO does not represent a
        // live, unclaimed object in the target system.
        var match = _csos.Values
            .Where(cso => cso.ConnectedSystemId == connectedSystem.Id &&
                          cso.TypeId == connectedSystemObjectType.Id &&
                          cso.MetaverseObjectId == null &&
                          cso.Status == ConnectedSystemObjectStatus.Normal)
            .OrderBy(cso => cso.Id)
            .FirstOrDefault(ValueMatches);

        return Task.FromResult(match);
    }

    #endregion

    #region Metaverse Object — Writes

    public Task<List<Guid>> GetMetaverseObjectIdsWithScopeReviewPendingAsync(int maxResults)
    {
        var ids = _mvos.Values
            .Where(mvo => mvo.ScopeReviewPending)
            .OrderBy(mvo => mvo.Id)
            .Select(mvo => mvo.Id)
            .Take(maxResults)
            .ToList();
        return Task.FromResult(ids);
    }

    public Task<List<MetaverseObject>> GetMetaverseObjectsByIdsNoTrackingAsync(IEnumerable<Guid> ids)
    {
        var result = ids
            .Select(id => _mvos.TryGetValue(id, out var mvo) ? mvo : null)
            .Where(mvo => mvo != null)
            .Select(mvo => mvo!)
            .ToList();
        return Task.FromResult(result);
    }

    public Task ClearMetaverseObjectScopeReviewPendingAsync(IReadOnlyCollection<Guid> ids)
    {
        foreach (var stored in ids.Select(id => _mvos.TryGetValue(id, out var mvo) ? mvo : null).Where(mvo => mvo != null))
            stored!.ScopeReviewPending = false;
        return Task.CompletedTask;
    }

    public Task<List<MvoReferenceRecallCandidate>> GetMetaverseObjectReferenceRecallCandidatesAsync(
        IReadOnlyCollection<Guid> referencedMetaverseObjectIds)
    {
        var referencedIds = referencedMetaverseObjectIds as HashSet<Guid> ?? [.. referencedMetaverseObjectIds];

        // Navigation fallback mirrors the FK-first convention used elsewhere in tests.
        var candidates = _mvos.Values
            .Where(mvo => !referencedIds.Contains(mvo.Id))
            .SelectMany(mvo => mvo.AttributeValues
                .Select(attributeValue => (Mvo: mvo, AttributeValue: attributeValue,
                    ReferencedId: attributeValue.ReferenceValueId ?? attributeValue.ReferenceValue?.Id))
                .Where(row => row.ReferencedId.HasValue && referencedIds.Contains(row.ReferencedId.Value)))
            .Select(row => new MvoReferenceRecallCandidate
            {
                ReferencingMetaverseObjectId = row.Mvo.Id,
                AttributeValueId = row.AttributeValue.Id,
                MetaverseAttributeId = row.AttributeValue.AttributeId,
                ReferencedMetaverseObjectId = row.ReferencedId!.Value
            })
            .ToList();
        return Task.FromResult(candidates);
    }

    public Task<List<MetaverseObjectRecallSummary>> GetMetaverseObjectRecallSummariesAsync(
        IReadOnlyCollection<Guid> metaverseObjectIds,
        IReadOnlyCollection<int> scopingAttributeIds)
    {
        var scopingIds = scopingAttributeIds as HashSet<int> ?? [.. scopingAttributeIds];
        var summaries = metaverseObjectIds
            .Select(id => _mvos.TryGetValue(id, out var mvo) ? mvo : null)
            .Where(mvo => mvo != null)
            .Select(mvo =>
            {
                var summary = new MetaverseObjectRecallSummary
                {
                    Id = mvo!.Id,
                    TypeId = mvo.Type.Id,
                    DisplayName = mvo.CachedDisplayName
                };
                summary.ScopingAttributeValues.AddRange(
                    mvo.AttributeValues.Where(av => scopingIds.Contains(av.AttributeId)));
                return summary;
            })
            .ToList();
        return Task.FromResult(summaries);
    }

    public Task<List<ConnectedSystemObjectRecallTarget>> GetConnectedSystemObjectRecallTargetsAsync(
        IReadOnlyCollection<Guid> metaverseObjectIds,
        IReadOnlyCollection<int> targetConnectedSystemIds)
    {
        var mvoIds = metaverseObjectIds as HashSet<Guid> ?? [.. metaverseObjectIds];
        var systemIds = targetConnectedSystemIds as HashSet<int> ?? [.. targetConnectedSystemIds];
        var targets = _csos.Values
            .Where(cso => cso.MetaverseObjectId.HasValue && mvoIds.Contains(cso.MetaverseObjectId.Value) &&
                          systemIds.Contains(cso.ConnectedSystemId))
            .Select(cso => new ConnectedSystemObjectRecallTarget
            {
                ConnectedSystemObjectId = cso.Id,
                MetaverseObjectId = cso.MetaverseObjectId!.Value,
                ConnectedSystemId = cso.ConnectedSystemId,
                Status = cso.Status
            })
            .ToList();
        return Task.FromResult(targets);
    }

    public Task<List<CsoReferenceValueMatch>> GetCsoReferenceValueMatchesAsync(
        IReadOnlyCollection<Guid> connectedSystemObjectIds,
        IReadOnlyCollection<int> connectedSystemAttributeIds,
        IReadOnlyCollection<Guid> deletedReferenceCsoIds,
        IReadOnlyCollection<string> loweredReferenceValues)
    {
        var csoIds = connectedSystemObjectIds as HashSet<Guid> ?? [.. connectedSystemObjectIds];
        var attributeIds = connectedSystemAttributeIds as HashSet<int> ?? [.. connectedSystemAttributeIds];
        var deletedCsoIds = deletedReferenceCsoIds as HashSet<Guid> ?? [.. deletedReferenceCsoIds];
        var loweredValues = loweredReferenceValues.ToHashSet(StringComparer.Ordinal);

        // Same comparison semantics as the SQL implementation: exact match on the resolved
        // reference id, or the pre-lowered raw reference string against LOWER(value).
        var matches = csoIds
            .Select(id => _csos.TryGetValue(id, out var cso) ? cso : null)
            .Where(cso => cso != null)
            .SelectMany(cso => cso!.AttributeValues
                .Where(av => attributeIds.Contains(av.AttributeId) &&
                             ((av.ReferenceValueId.HasValue && deletedCsoIds.Contains(av.ReferenceValueId.Value)) ||
                              (av.UnresolvedReferenceValue != null &&
                               loweredValues.Contains(av.UnresolvedReferenceValue.ToLowerInvariant()))))
                .Select(av => new CsoReferenceValueMatch
                {
                    AttributeValueId = av.Id,
                    ConnectedSystemObjectId = cso.Id,
                    AttributeId = av.AttributeId,
                    ReferenceValueId = av.ReferenceValueId,
                    UnresolvedReferenceValue = av.UnresolvedReferenceValue
                }))
            .ToList();
        return Task.FromResult(matches);
    }

    public Task CreateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
    {
        foreach (var mvo in metaverseObjects)
        {
            if (mvo.Id == Guid.Empty)
                mvo.Id = Guid.NewGuid();
            FixupMvoAttributeValues(mvo);
            _mvos[mvo.Id] = mvo;
        }
        return Task.CompletedTask;
    }

    public Task UpdateMetaverseObjectsAsync(IEnumerable<MetaverseObject> metaverseObjects)
    {
        foreach (var mvo in metaverseObjects)
        {
            FixupMvoAttributeValues(mvo);
            _mvos[mvo.Id] = mvo;
        }
        return Task.CompletedTask;
    }

    public Task UpdateMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        FixupMvoAttributeValues(metaverseObject);
        _mvos[metaverseObject.Id] = metaverseObject;
        return Task.CompletedTask;
    }

    // Virtual so tests can spy on per-object deletes; the MVO deletion flush must use the
    // set-based DeleteMetaverseObjectsAsync instead (issue #993).
    public virtual Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        _mvos.Remove(metaverseObject.Id);
        _csosByMvo.Remove(metaverseObject.Id);
        NullReferencesToDeletedMvos(new HashSet<Guid> { metaverseObject.Id });
        return Task.CompletedTask;
    }

    public virtual Task DeleteMetaverseObjectsAsync(IReadOnlyCollection<MetaverseObject> metaverseObjects)
    {
        foreach (var metaverseObject in metaverseObjects)
        {
            _mvos.Remove(metaverseObject.Id);
            _csosByMvo.Remove(metaverseObject.Id);
        }
        NullReferencesToDeletedMvos(metaverseObjects.Select(mvo => mvo.Id).ToHashSet());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors the PostgreSQL deletion path's reference row clean-up (#1003, #1019): valueless rows on
    /// surviving objects that reference deleted MVOs are removed entirely (leaving them behind as
    /// all-null "ghost" rows corrupted member lists and later exports), while rows carrying payload
    /// keep the legacy behaviour of having only their reference nulled. Without this mirror, code that
    /// wrongly reads live reference rows instead of the pre-deletion recall capture passes in-memory
    /// tests and fails only on PostgreSQL. Predicate parity with the raw SQL lives in
    /// <see cref="MetaverseObjectAttributeValue.IsValuelessReferenceRow"/>.
    /// </summary>
    private void NullReferencesToDeletedMvos(IReadOnlySet<Guid> deletedMvoIds)
    {
        foreach (var mvo in _mvos.Values)
        {
            if (deletedMvoIds.Contains(mvo.Id))
                continue;

            mvo.AttributeValues.RemoveAll(av => ReferencesDeletedMvo(av, deletedMvoIds) && av.IsValuelessReferenceRow());

            foreach (var attributeValue in mvo.AttributeValues.Where(av => ReferencesDeletedMvo(av, deletedMvoIds)))
            {
                attributeValue.ReferenceValueId = null;
                attributeValue.ReferenceValue = null;
            }
        }
    }

    private static bool ReferencesDeletedMvo(MetaverseObjectAttributeValue attributeValue, IReadOnlySet<Guid> deletedMvoIds)
    {
        return (attributeValue.ReferenceValueId.HasValue && deletedMvoIds.Contains(attributeValue.ReferenceValueId.Value)) ||
               (attributeValue.ReferenceValue != null && deletedMvoIds.Contains(attributeValue.ReferenceValue.Id));
    }

    public Task DeleteMetaverseObjectAttributeValuesByIdsAsync(IReadOnlyList<Guid> attributeValueIds)
    {
        var idsToDelete = attributeValueIds.ToHashSet();
        foreach (var mvo in _mvos.Values)
        {
            mvo.AttributeValues.RemoveAll(av => idsToDelete.Contains(av.Id));
        }
        return Task.CompletedTask;
    }

    #endregion

    #region Pending Exports

    public Task<List<PendingExport>> GetPendingExportsAsync(int connectedSystemId)
    {
        var result = GetPendingExportsForSystem(connectedSystemId).ToList();
        return Task.FromResult(result);
    }

    public Task<int> GetPendingExportsCountAsync(int connectedSystemId)
    {
        var count = _pendingExportsByCs.TryGetValue(connectedSystemId, out var ids) ? ids.Count : 0;
        return Task.FromResult(count);
    }

    public Task CreatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
    {
        foreach (var pe in pendingExports)
        {
            if (pe.Id == Guid.Empty)
                pe.Id = Guid.NewGuid();

            if (pe.ConnectedSystem == null && pe.ConnectedSystemId > 0 && _connectedSystems.TryGetValue(pe.ConnectedSystemId, out var cs))
                pe.ConnectedSystem = cs;
            if (pe.ConnectedSystemObject == null && pe.ConnectedSystemObjectId.HasValue)
            {
                if (_csos.TryGetValue(pe.ConnectedSystemObjectId.Value, out var cso))
                    pe.ConnectedSystemObject = cso;
            }

            // Fixup AttributeValueChange nav props (production code may set only Attribute or only AttributeId)
            FixupPendingExportAttributeChanges(pe);

            _pendingExports[pe.Id] = pe;
            AddToPeIndex(pe);
        }
        return Task.CompletedTask;
    }

    public Task DeletePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
    {
        foreach (var pe in pendingExports)
            RemovePe(pe);
        return Task.CompletedTask;
    }

    // Virtual for test-support subclasses (see GetPendingExportsByIdsAsync): persisting is the
    // event that makes state visible to fresh per-batch contexts in the parallel export path.
    public virtual Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
    {
        foreach (var pe in pendingExports)
            _pendingExports[pe.Id] = pe;
        return Task.CompletedTask;
    }

    public Task<int> DeletePendingExportsByConnectedSystemObjectIdsAsync(IEnumerable<Guid> connectedSystemObjectIds)
    {
        var deleted = 0;
        foreach (var csoId in connectedSystemObjectIds)
        {
            if (_pendingExportsByCsoId.TryGetValue(csoId, out var peId) && _pendingExports.TryGetValue(peId, out var pe))
            {
                RemovePe(pe);
                deleted++;
            }
        }
        return Task.FromResult(deleted);
    }

    public Task<PendingExport?> GetPendingExportByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId)
    {
        PendingExport? result = null;
        if (_pendingExportsByCsoId.TryGetValue(connectedSystemObjectId, out var peId))
            _pendingExports.TryGetValue(peId, out result);
        return Task.FromResult(result);
    }

    // This fake store has no Include-shape concept (there is no lazy loading and every seeded object
    // is already a fully wired-up graph in memory), so the lean merge-fetch variant is behaviourally
    // identical to the heavy one here. The distinction only exists - and is only provable - at the
    // Postgres repository layer, where Include chains genuinely control what gets loaded.
    public Task<PendingExport?> GetPendingExportLightweightByConnectedSystemObjectIdAsync(Guid connectedSystemObjectId)
        => GetPendingExportByConnectedSystemObjectIdAsync(connectedSystemObjectId);

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(
        IEnumerable<Guid> connectedSystemObjectIds)
    {
        var result = connectedSystemObjectIds
            .Distinct()
            .Select(csoId => (csoId,
                pe: _pendingExportsByCsoId.TryGetValue(csoId, out var peId) && _pendingExports.TryGetValue(peId, out var found)
                    ? found
                    : null))
            .Where(pair => pair.pe != null)
            .ToDictionary(pair => pair.csoId, pair => pair.pe!);
        return Task.FromResult(result);
    }

    public Task<HashSet<Guid>> GetCsoIdsWithPendingExportsByConnectedSystemAsync(int connectedSystemId)
    {
        var result = new HashSet<Guid>();
        foreach (var pe in GetPendingExportsForSystem(connectedSystemId))
        {
            if (pe.ConnectedSystemObjectId.HasValue)
                result.Add(pe.ConnectedSystemObjectId.Value);
        }
        return Task.FromResult(result);
    }

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemIdAsync(int connectedSystemId)
    {
        var result = new Dictionary<Guid, PendingExport>();
        foreach (var pe in GetPendingExportsForSystem(connectedSystemId))
        {
            if (pe.ConnectedSystemObjectId.HasValue)
                result[pe.ConnectedSystemObjectId.Value] = pe;
        }
        return Task.FromResult(result);
    }

    public Task DeleteUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
        => DeletePendingExportsAsync(untrackedPendingExports);

    public Task DeleteUntrackedPendingExportAttributeValueChangesAsync(
        IEnumerable<PendingExportAttributeValueChange> untrackedAttributeValueChanges)
    {
        var changesToRemove = untrackedAttributeValueChanges.ToList();
        foreach (var change in changesToRemove)
        {
            if (change.PendingExportId.HasValue &&
                _pendingExports.TryGetValue(change.PendingExportId.Value, out var pe))
            {
                pe.AttributeValueChanges.RemoveAll(avc => avc.Id == change.Id);
            }
        }
        return Task.CompletedTask;
    }

    public Task UpdateUntrackedPendingExportsAsync(IEnumerable<PendingExport> untrackedPendingExports)
        => UpdatePendingExportsAsync(untrackedPendingExports);

    #endregion

    #region Activity and RPEIs

    public Task UpdateActivityAsync(Activity activity)
    {
        _activities[activity.Id] = activity;
        return Task.CompletedTask;
    }

    public Task UpdateActivityMessageAsync(Activity activity, string message)
    {
        activity.Message = message;
        _activities[activity.Id] = activity;
        return Task.CompletedTask;
    }

    public Task UpdateActivityProgressOutOfBandAsync(Activity activity)
    {
        _activities[activity.Id] = activity;
        return Task.CompletedTask;
    }

    public Task FailActivityWithErrorAsync(Activity activity, string errorMessage)
    {
        activity.Status = ActivityStatus.FailedWithError;
        activity.ErrorMessage = errorMessage;
        _activities[activity.Id] = activity;
        return Task.CompletedTask;
    }

    public Task FailActivityWithErrorAsync(Activity activity, Exception exception)
    {
        activity.Status = ActivityStatus.FailedWithError;
        activity.ErrorMessage = exception.Message;
        activity.ErrorStackTrace = exception.StackTrace;
        _activities[activity.Id] = activity;
        return Task.CompletedTask;
    }

    public Task<bool> BulkInsertRpeisAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        foreach (var rpei in rpeis)
        {
            if (rpei.Id == Guid.Empty)
                rpei.Id = Guid.NewGuid();
            _rpeis[rpei.Id] = rpei;

            // Generate IDs for SyncOutcomes (matches PostgresDataRepository.FlattenSyncOutcomes behaviour)
            AssignSyncOutcomeIds(rpei.SyncOutcomes, rpei.Id, null);
        }
        // When SimulateRawSqlPersistence is off (default), return false so the processor keeps
        // RPEIs in the activity's RunProfileExecutionItems collection for simpler test assertions.
        // When on, return true so the processor clears that collection after each page — matching
        // the production raw-SQL path and exposing cross-page lookup bugs.
        return Task.FromResult(SimulateRawSqlPersistence);
    }

    private static void AssignSyncOutcomeIds(
        List<ActivityRunProfileExecutionItemSyncOutcome> outcomes, Guid rpeiId, Guid? parentId)
    {
        // Process only root outcomes from the flat list to avoid visiting children twice
        var roots = parentId == null
            ? outcomes.Where(o => o.ParentSyncOutcome == null && o.ParentSyncOutcomeId == null).ToList()
            : outcomes;

        foreach (var outcome in roots)
        {
            if (outcome.Id == Guid.Empty)
                outcome.Id = Guid.NewGuid();
            outcome.ActivityRunProfileExecutionItemId = rpeiId;
            outcome.ParentSyncOutcomeId = parentId;

            if (outcome.Children.Count > 0)
                AssignSyncOutcomeIds(outcome.Children, rpeiId, outcome.Id);
        }
    }

    public Task BulkUpdateRpeiOutcomesAsync(
        List<ActivityRunProfileExecutionItem> rpeis,
        List<ActivityRunProfileExecutionItemSyncOutcome> newOutcomes)
    {
        foreach (var rpei in rpeis)
            _rpeis[rpei.Id] = rpei;
        return Task.CompletedTask;
    }

    public Task<List<CrossPageMergeRpei>> GetRpeisWithMvoChangeIdsForCrossPageMergeAsync(
        Guid activityId, IReadOnlyCollection<Guid> csoIds)
    {
        if (csoIds.Count == 0)
            return Task.FromResult(new List<CrossPageMergeRpei>());

        var csoIdSet = csoIds as HashSet<Guid> ?? csoIds.ToHashSet();
        var matchingRpeis = _rpeis.Values
            .Where(r => r.ActivityId == activityId
                        && r.ConnectedSystemObjectId.HasValue
                        && csoIdSet.Contains(r.ConnectedSystemObjectId.Value))
            .ToList();

        var rpeiIds = matchingRpeis.Select(r => r.Id).ToHashSet();
        var mvoChangeIdByRpeiId = _mvoChanges.Values
            .Where(c => c.ActivityRunProfileExecutionItemId.HasValue
                        && rpeiIds.Contains(c.ActivityRunProfileExecutionItemId.Value))
            .ToDictionary(c => c.ActivityRunProfileExecutionItemId!.Value, c => c.Id);

        var results = matchingRpeis
            .Select(r => new CrossPageMergeRpei
            {
                Rpei = r,
                ExistingMvoChangeId = mvoChangeIdByRpeiId.TryGetValue(r.Id, out var mvoChangeId)
                    ? mvoChangeId
                    : null
            })
            .ToList();

        return Task.FromResult(results);
    }

    private void AppendAttributeChildrenToExistingMvoChange(MetaverseObjectChange incoming)
    {
        if (!_mvoChanges.TryGetValue(incoming.Id, out var existing))
        {
            throw new InvalidOperationException(
                $"PersistPendingMvoChangesAsync (append): no existing MvoChange with Id {incoming.Id}");
        }

        foreach (var attrChange in incoming.AttributeChanges)
        {
            if (attrChange.Id == Guid.Empty)
                attrChange.Id = Guid.NewGuid();
            foreach (var valueChange in attrChange.ValueChanges.Where(vc => vc.Id == Guid.Empty))
                valueChange.Id = Guid.NewGuid();
            existing.AttributeChanges.Add(attrChange);
        }
    }

    public void DetachRpeisFromChangeTracker(List<ActivityRunProfileExecutionItem> rpeis)
    {
        // No-op — no EF change tracker in memory
    }

    public Task<(int TotalWithErrors, int TotalRpeis, int TotalUnhandledErrors)> GetActivityRpeiErrorCountsAsync(
        Guid activityId)
    {
        var activityRpeis = _rpeis.Values.Where(r => r.ActivityId == activityId).ToList();
        var totalRpeis = activityRpeis.Count;
        var totalWithErrors = activityRpeis.Count(r =>
            r.ErrorType != null && r.ErrorType != ActivityRunProfileExecutionItemErrorType.NotSet);
        var totalUnhandledErrors = activityRpeis.Count(r =>
            r.ErrorType == ActivityRunProfileExecutionItemErrorType.UnhandledError);
        return Task.FromResult((totalWithErrors, totalRpeis, totalUnhandledErrors));
    }

    public Task PersistRpeiCsoChangesAsync(List<ActivityRunProfileExecutionItem> rpeis)
    {
        // No-op — CSO changes are already in memory on the RPEI objects
        return Task.CompletedTask;
    }

    #endregion

    #region Synchronisation Rules and Configuration

    public Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabled, bool withChangeTracking = false)
    {
        var rules = _syncRules.Values
            .Where(r => r.ConnectedSystemId == connectedSystemId)
            .Where(r => includeDisabled || r.Enabled)
            .ToList();
        return Task.FromResult(rules);
    }

    /// <summary>
    /// Number of times <see cref="GetAllSyncRulesAsync"/> has been called. Lets tests prove that a
    /// pre-built export evaluation cache is honoured (no per-flush rule reloads, #1003).
    /// </summary>
    public int GetAllSyncRulesCallCount { get; private set; }

    public Task<List<SyncRule>> GetAllSyncRulesAsync(bool withChangeTracking = false)
    {
        GetAllSyncRulesCallCount++;
        return Task.FromResult(_syncRules.Values.ToList());
    }

    public Task<DateTime?> GetLatestSyncRuleConfigurationChangeAsync()
    {
        var timestamps = _syncRules.Values
            .Select(r => r.LastUpdated ?? r.Created)
            .Concat(_syncRules.Values.SelectMany(r => r.AttributeFlowRules).Select(m => m.LastUpdated ?? m.Created))
            .ToList();
        return Task.FromResult<DateTime?>(timestamps.Count > 0 ? timestamps.Max() : null);
    }

    public Task<List<ConnectedSystemObjectType>> GetObjectTypesAsync(int connectedSystemId)
    {
        var types = _objectTypes.Values
            .Where(t => t.ConnectedSystemId == connectedSystemId)
            .ToList();
        return Task.FromResult(types);
    }

    public Task UpdateConnectedSystemAsync(ConnectedSystem connectedSystem)
    {
        _connectedSystems[connectedSystem.Id] = connectedSystem;
        return Task.CompletedTask;
    }

    #endregion

    #region Settings

    public Task<int> GetSyncPageSizeAsync()
        => Task.FromResult(_syncPageSize);

    public Task<ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel> GetSyncOutcomeTrackingLevelAsync()
        => Task.FromResult(_syncOutcomeTrackingLevel);

    public Task<bool> GetCsoChangeTrackingEnabledAsync()
        => Task.FromResult(_csoChangeTrackingEnabled);

    public Task<bool> GetMvoChangeTrackingEnabledAsync()
        => Task.FromResult(_mvoChangeTrackingEnabled);

    #endregion

    #region Change Tracker Management

    public void ClearChangeTracker()
    {
        // No-op — no EF change tracker in memory
    }

    public int GetChangeTrackerEntityCount() => 0; // No EF change tracker in memory

    public void DetachSchemaEntitiesFromTracker()
    {
        // No-op — no EF change tracker in memory
    }

    public void SetAutoDetectChangesEnabled(bool enabled)
    {
        // No-op — no EF change tracker in memory
    }

    #endregion

    #region CSO Lookup Cache

    public void AddCsoToCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue, Guid csoId)
    {
        _csoCache[(connectedSystemId, externalIdAttributeId, externalIdValue)] = csoId;
    }

    public void EvictCsoFromCache(int connectedSystemId, int externalIdAttributeId, string externalIdValue)
    {
        _csoCache.Remove((connectedSystemId, externalIdAttributeId, externalIdValue));
    }

    #endregion

    #region Connected System Operations

    public Task RefreshAndAutoSelectContainersWithTriadAsync(
        ConnectedSystem connectedSystem,
        IConnector connector,
        IReadOnlyList<string> createdContainerExternalIds,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName,
        Activity? parentActivity = null)
    {
        // No-op — tests seed their own object types and containers
        return Task.CompletedTask;
    }

    #endregion

    #region MVO Change History

    // Virtual so tests can spy on per-object change record creation; the MVO deletion flush
    // persists its Deleted change records via PersistPendingMvoChangesAsync instead (issue #993).
    public virtual Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change)
    {
        if (change.Id == Guid.Empty)
            change.Id = Guid.NewGuid();
        _mvoChanges[change.Id] = change;
        return Task.CompletedTask;
    }

    public Task PersistPendingMvoChangesAsync(
        List<MetaverseObjectChange> newChanges,
        List<MetaverseObjectChange> attributeAppendsToExistingChanges)
    {
        foreach (var change in newChanges)
        {
            if (change.Id == Guid.Empty)
                change.Id = Guid.NewGuid();

            foreach (var attrChange in change.AttributeChanges)
            {
                if (attrChange.Id == Guid.Empty)
                    attrChange.Id = Guid.NewGuid();

                foreach (var valueChange in attrChange.ValueChanges)
                {
                    if (valueChange.Id == Guid.Empty)
                        valueChange.Id = Guid.NewGuid();
                }
            }

            _mvoChanges[change.Id] = change;

            // Mirror EF Core behaviour: add to the MVO's navigation property so in-memory
            // tests can query mvo.Changes the same way production code would after a reload.
            if (change.MetaverseObject != null && !change.MetaverseObject.Changes.Contains(change))
                change.MetaverseObject.Changes.Add(change);
        }

        foreach (var append in attributeAppendsToExistingChanges)
            AppendAttributeChildrenToExistingMvoChange(append);

        return Task.CompletedTask;
    }

    #endregion

    #region Connected System Object — Singular Convenience Methods

    public Task CreateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        => CreateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { connectedSystemObject });

    public Task UpdateConnectedSystemObjectAsync(ConnectedSystemObject connectedSystemObject)
        => UpdateConnectedSystemObjectsAsync(new List<ConnectedSystemObject> { connectedSystemObject });

    public Task UpdateConnectedSystemObjectWithNewAttributeValuesAsync(
        ConnectedSystemObject connectedSystemObject,
        List<ConnectedSystemObjectAttributeValue> newAttributeValues)
        => UpdateConnectedSystemObjectsWithNewAttributeValuesAsync(
            new List<(ConnectedSystemObject, List<ConnectedSystemObjectAttributeValue>)>
            {
                (connectedSystemObject, newAttributeValues)
            });

    #endregion

    #region Pending Export — Singular Convenience Methods

    public Task CreatePendingExportAsync(PendingExport pendingExport)
        => CreatePendingExportsAsync(new[] { pendingExport });

    public Task DeletePendingExportAsync(PendingExport pendingExport)
        => DeletePendingExportsAsync(new[] { pendingExport });

    public Task UpdatePendingExportAsync(PendingExport pendingExport)
        => UpdatePendingExportsAsync(new[] { pendingExport });

    #endregion

    #region Export Evaluation Support

    // Virtual so tests can spy on per-object fetches; the MVO deletion flush must use the
    // set-based GetConnectedSystemObjectsForMvoDeletionAsync instead (issue #993).
    public virtual Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId)
    {
        var result = new List<ConnectedSystemObject>();
        if (_csosByMvo.TryGetValue(metaverseObjectId, out var csoIds))
        {
            foreach (var csoId in csoIds)
            {
                if (_csos.TryGetValue(csoId, out var cso))
                    result.Add(cso);
            }
        }
        return Task.FromResult(result);
    }

    // In-memory objects carry their full attribute value lists; the Postgres implementation's
    // lean include shape (external ID attribute values only) cannot be modelled here (see the
    // EF in-memory caveat in test/CLAUDE.md).
    public virtual Task<Dictionary<Guid, List<ConnectedSystemObject>>> GetConnectedSystemObjectsForMvoDeletionAsync(
        IReadOnlyCollection<Guid> metaverseObjectIds)
    {
        var result = new Dictionary<Guid, List<ConnectedSystemObject>>();
        foreach (var mvoId in metaverseObjectIds.Where(_csosByMvo.ContainsKey))
        {
            var joinedCsos = _csosByMvo[mvoId]
                .Where(_csos.ContainsKey)
                .Select(csoId => _csos[csoId])
                .ToList();

            if (joinedCsos.Count > 0)
                result[mvoId] = joinedCsos;
        }
        return Task.FromResult(result);
    }

    public virtual Task DisconnectConnectedSystemObjectsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        foreach (var cso in connectedSystemObjectIds
            .Where(_csos.ContainsKey)
            .Select(csoId => _csos[csoId]))
        {
            cso.MetaverseObjectId = null;
            cso.MetaverseObject = null;
            cso.JoinType = ConnectedSystemObjectJoinType.NotJoined;
            cso.DateJoined = null;
            UpdateMvoIndex(cso);
        }
        return Task.CompletedTask;
    }

    public Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByTargetSystemsAsync(
        IEnumerable<int> targetConnectedSystemIds)
    {
        var targetIds = targetConnectedSystemIds.ToHashSet();
        var result = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>();

        foreach (var cso in _csos.Values)
        {
            if (targetIds.Contains(cso.ConnectedSystemId) && cso.MetaverseObjectId.HasValue)
            {
                var key = (cso.MetaverseObjectId.Value, cso.ConnectedSystemId);
                result.TryAdd(key, cso);
            }
        }

        return Task.FromResult(result);
    }

    public Task<Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>> GetConnectedSystemObjectsByMvoIdsAndTargetSystemsAsync(
        IEnumerable<Guid> mvoIds, IEnumerable<int> targetConnectedSystemIds)
    {
        var mvoIdSet = mvoIds.ToHashSet();
        var targetIds = targetConnectedSystemIds.ToHashSet();
        var result = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject>();

        foreach (var cso in _csos.Values)
        {
            if (cso.MetaverseObjectId.HasValue
                && mvoIdSet.Contains(cso.MetaverseObjectId.Value)
                && targetIds.Contains(cso.ConnectedSystemId))
            {
                var key = (cso.MetaverseObjectId.Value, cso.ConnectedSystemId);
                result.TryAdd(key, cso);
            }
        }

        return Task.FromResult(result);
    }

    public Task<List<ConnectedSystemObjectAttributeValue>> GetCsoAttributeValuesByCsoIdsAsync(IEnumerable<Guid> csoIds)
    {
        var ids = csoIds.ToHashSet();
        var result = new List<ConnectedSystemObjectAttributeValue>();

        foreach (var cso in _csos.Values)
        {
            if (ids.Contains(cso.Id) && cso.AttributeValues != null)
            {
                foreach (var av in cso.AttributeValues)
                {
                    av.ConnectedSystemObject ??= cso;
                    result.Add(av);
                }
            }
        }

        return Task.FromResult(result);
    }

    public Task<ConnectedSystemObject?> GetConnectedSystemObjectByMetaverseObjectIdAsync(
        Guid metaverseObjectId, int connectedSystemId)
    {
        ConnectedSystemObject? result = null;

        if (_csosByMvo.TryGetValue(metaverseObjectId, out var csoIds))
        {
            foreach (var csoId in csoIds)
            {
                if (_csos.TryGetValue(csoId, out var cso) && cso.ConnectedSystemId == connectedSystemId)
                {
                    result = cso;
                    break;
                }
            }
        }

        return Task.FromResult(result);
    }

    public Task<Dictionary<Guid, ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdsAsync(
        IEnumerable<Guid> metaverseObjectIds, int connectedSystemId)
    {
        var result = new Dictionary<Guid, ConnectedSystemObject>();

        foreach (var mvoId in metaverseObjectIds)
        {
            if (_csosByMvo.TryGetValue(mvoId, out var csoIds))
            {
                foreach (var csoId in csoIds)
                {
                    if (_csos.TryGetValue(csoId, out var cso) && cso.ConnectedSystemId == connectedSystemId)
                    {
                        result.TryAdd(mvoId, cso);
                        break;
                    }
                }
            }
        }

        return Task.FromResult(result);
    }

    public Task<ConnectedSystemObjectTypeAttribute?> GetAttributeAsync(int id)
    {
        ConnectedSystemObjectTypeAttribute? result = null;

        foreach (var objectType in _objectTypes.Values)
        {
            if (objectType.Attributes != null)
            {
                result = objectType.Attributes.FirstOrDefault(a => a.Id == id);
                if (result != null) break;
            }
        }

        return Task.FromResult(result);
    }

    public Task<Dictionary<int, ConnectedSystemObjectTypeAttribute>> GetAttributesByIdsAsync(IEnumerable<int> ids)
    {
        var idSet = ids.ToHashSet();
        var result = new Dictionary<int, ConnectedSystemObjectTypeAttribute>();

        foreach (var objectType in _objectTypes.Values)
        {
            if (objectType.Attributes == null) continue;
            foreach (var attr in objectType.Attributes)
            {
                if (idSet.Contains(attr.Id))
                    result.TryAdd(attr.Id, attr);
            }
        }

        return Task.FromResult(result);
    }

    public Task<ConnectedSystemObject?> FindMatchingConnectedSystemObjectAsync(
        MetaverseObject metaverseObject,
        ConnectedSystem connectedSystem,
        ConnectedSystemObjectType connectedSystemObjectType,
        List<ObjectMatchingRule> matchingRules)
    {
        // Simplified export matching: iterate rules, find CSO with matching attribute value
        foreach (var rule in matchingRules.OrderBy(r => r.Order))
        {
            if (rule.Sources.Count == 0) continue;
            var source = rule.Sources[0];

            if (source.MetaverseAttribute == null && source.MetaverseAttributeId == null) continue;
            if (source.ConnectedSystemAttribute == null && source.ConnectedSystemAttributeId == null) continue;

            // Get the MVO attribute value to match against
            var mvoAttrId = source.MetaverseAttribute?.Id ?? source.MetaverseAttributeId;
            var mvoAttr = metaverseObject.AttributeValues?
                .FirstOrDefault(av => av.AttributeId == mvoAttrId);
            if (mvoAttr == null) continue;

            var mvoVal = mvoAttr.StringValue ?? mvoAttr.GuidValue?.ToString() ?? mvoAttr.IntValue?.ToString();
            if (mvoVal == null) continue;

            // Find a CSO in the target system with a matching CS attribute value
            var csAttrId = source.ConnectedSystemAttribute?.Id ?? source.ConnectedSystemAttributeId;
            var comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            foreach (var cso in _csos.Values)
            {
                if (cso.ConnectedSystemId != connectedSystem.Id) continue;
                if (cso.TypeId != connectedSystemObjectType.Id) continue;

                var csoAttr = cso.AttributeValues?
                    .FirstOrDefault(av => av.AttributeId == csAttrId);
                if (csoAttr == null) continue;

                var csoVal = csoAttr.StringValue ?? csoAttr.GuidValue?.ToString() ?? csoAttr.IntValue?.ToString();
                if (string.Equals(mvoVal, csoVal, comparison))
                    return Task.FromResult<ConnectedSystemObject?>(cso);
            }
        }

        return Task.FromResult<ConnectedSystemObject?>(null);
    }

    #endregion

    #region Export Execution Support

    public Task<int> GetExecutableExportCountAsync(int connectedSystemId)
    {
        var count = GetExecutableExportsForSystem(connectedSystemId).Count();
        return Task.FromResult(count);
    }

    public Task<List<PendingExport>> GetExecutableExportsAsync(int connectedSystemId)
    {
        var result = GetExecutableExportsForSystem(connectedSystemId).ToList();
        return Task.FromResult(result);
    }

    public virtual Task<List<PendingExport>> GetExecutableExportBatchAsync(int connectedSystemId, int take, DateTime? afterCreatedAt, Guid? afterId)
    {
        // Keyset pagination on (CreatedAt, Id), mirroring the Postgres implementation.
        // Guid ordering only needs to be self-consistent within this store; .NET's
        // Guid comparison is used for both the predicate and the ordering.
        var query = GetExecutableExportsForSystem(connectedSystemId);

        if (afterCreatedAt.HasValue && afterId.HasValue)
        {
            var cursorCreatedAt = afterCreatedAt.Value;
            var cursorId = afterId.Value;
            query = query.Where(pe => pe.CreatedAt > cursorCreatedAt
                || (pe.CreatedAt == cursorCreatedAt && pe.Id.CompareTo(cursorId) > 0));
        }

        var result = query
            .OrderBy(pe => pe.CreatedAt)
            .ThenBy(pe => pe.Id)
            .Take(take)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Collects all remaining executable exports with unresolved references (deferred) strictly
    /// after the given keyset cursor, in a single call, mirroring the Postgres implementation.
    /// Used to fast-path the export batch-collection loop once a batch is discovered to be made
    /// up entirely of deferred exports (issue #985).
    /// </summary>
    public virtual Task<List<PendingExport>> GetRemainingDeferredExportsAsync(int connectedSystemId, DateTime? afterCreatedAt, Guid? afterId)
    {
        var query = GetExecutableExportsForSystem(connectedSystemId)
            .Where(pe => pe.HasUnresolvedReferences);

        if (afterCreatedAt.HasValue && afterId.HasValue)
        {
            var cursorCreatedAt = afterCreatedAt.Value;
            var cursorId = afterId.Value;
            query = query.Where(pe => pe.CreatedAt > cursorCreatedAt
                || (pe.CreatedAt == cursorCreatedAt && pe.Id.CompareTo(cursorId) > 0));
        }

        var result = query
            .OrderBy(pe => pe.CreatedAt)
            .ThenBy(pe => pe.Id)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns whether any executable exports WITHOUT unresolved references exist strictly after
    /// the given keyset cursor, mirroring the Postgres implementation. Guards the
    /// deferred-collection fast path (issue #985): deferred and executable exports interleave in
    /// (CreatedAt, Id) order, so an all-deferred batch does not prove the rest of the queue is
    /// deferred too.
    /// </summary>
    public virtual Task<bool> AnyExecutableNonDeferredExportsAfterAsync(int connectedSystemId, DateTime? afterCreatedAt, Guid? afterId)
    {
        var query = GetExecutableExportsForSystem(connectedSystemId)
            .Where(pe => !pe.HasUnresolvedReferences);

        if (afterCreatedAt.HasValue && afterId.HasValue)
        {
            var cursorCreatedAt = afterCreatedAt.Value;
            var cursorId = afterId.Value;
            query = query.Where(pe => pe.CreatedAt > cursorCreatedAt
                || (pe.CreatedAt == cursorCreatedAt && pe.Id.CompareTo(cursorId) > 0));
        }

        return Task.FromResult(query.Any());
    }

    /// <summary>
    /// Applies the same eligibility filters as the Postgres ExecutableExportsQuery:
    /// status must be Pending, Exported, or ExportNotConfirmed; exports not yet due for retry or
    /// that have exceeded max retries are excluded; Update exports must have at least one Pending or
    /// ExportedNotConfirmed attribute change; Delete exports that were already Exported are excluded
    /// (awaiting import confirmation, not re-execution).
    /// </summary>
    private IEnumerable<PendingExport> GetExecutableExportsForSystem(int connectedSystemId)
    {
        var now = DateTime.UtcNow;
        return GetPendingExportsForSystem(connectedSystemId)
            .Where(pe => pe.Status == PendingExportStatus.Pending
                      || pe.Status == PendingExportStatus.Exported
                      || pe.Status == PendingExportStatus.ExportNotConfirmed)
            // Exclude exports not yet due for retry
            .Where(pe => !pe.NextRetryAt.HasValue || pe.NextRetryAt <= now)
            // Exclude exports that have exceeded max retries
            .Where(pe => pe.ErrorCount < pe.MaxRetries)
            // Update exports must have at least one exportable attribute change
            .Where(pe => pe.ChangeType != PendingExportChangeType.Update
                      || pe.AttributeValueChanges.Any(ac =>
                            ac.Status == PendingExportAttributeChangeStatus.Pending
                         || ac.Status == PendingExportAttributeChangeStatus.ExportedNotConfirmed))
            // Create and Delete exports already exported are awaiting import confirmation — do not re-execute
            .Where(pe => !(pe.ChangeType == PendingExportChangeType.Delete
                        && pe.Status == PendingExportStatus.Exported))
            .Where(pe => !(pe.ChangeType == PendingExportChangeType.Create
                        && pe.Status == PendingExportStatus.Exported));
    }

    public Task<List<PendingExportSummary>> GetExecutableExportSummariesAsync(int connectedSystemId)
    {
        var result = GetExecutableExportsForSystem(connectedSystemId)
            .Select(pe => new PendingExportSummary
            {
                Id = pe.Id,
                ChangeType = pe.ChangeType,
                Status = pe.Status,
                ConnectedSystemObjectId = pe.ConnectedSystemObjectId,
                SourceMetaverseObjectId = pe.SourceMetaverseObjectId
            })
            .ToList();
        return Task.FromResult(result);
    }

    public Task DeletePendingExportsByIdsAsync(IList<Guid> pendingExportIds)
    {
        foreach (var id in pendingExportIds)
        {
            if (_pendingExports.TryGetValue(id, out var pe))
                RemovePe(pe);
        }
        return Task.CompletedTask;
    }

    public Task MarkPendingExportsAsExecutingAsync(IList<PendingExport> pendingExports)
    {
        foreach (var pe in pendingExports)
        {
            pe.Status = PendingExportStatus.Executing;
            pe.LastAttemptedAt = DateTime.UtcNow;
            _pendingExports[pe.Id] = pe;
        }
        return Task.CompletedTask;
    }

    // Virtual for test-support subclasses: the parallel export batch path re-loads Pending
    // Exports by ID on a fresh per-batch context, and tests need to simulate that database
    // isolation (returning last-persisted state rather than live in-memory references).
    public virtual Task<List<PendingExport>> GetPendingExportsByIdsAsync(IList<Guid> pendingExportIds)
    {
        var result = pendingExportIds
            .Where(id => _pendingExports.ContainsKey(id))
            .Select(id => _pendingExports[id])
            .ToList();
        return Task.FromResult(result);
    }

    #endregion

    #region Private Helpers

    private IEnumerable<ConnectedSystemObject> GetCsosForSystem(int connectedSystemId)
    {
        if (!_csosByConnectedSystem.TryGetValue(connectedSystemId, out var ids))
            return Enumerable.Empty<ConnectedSystemObject>();
        return ids.Where(id => _csos.ContainsKey(id)).Select(id =>
        {
            var cso = _csos[id];
            // Lazy fixup: production code may add attribute values with Attribute nav prop
            // but without AttributeId (relying on EF SaveChanges to resolve). Fix on read.
            FixupCsoNavigationProperties(cso);
            if (cso.MetaverseObject != null)
                FixupMvoAttributeValues(cso.MetaverseObject);
            return cso;
        });
    }

    private IEnumerable<PendingExport> GetPendingExportsForSystem(int connectedSystemId)
    {
        if (!_pendingExportsByCs.TryGetValue(connectedSystemId, out var ids))
            return Enumerable.Empty<PendingExport>();
        return ids.Where(id => _pendingExports.ContainsKey(id)).Select(id => _pendingExports[id]);
    }

    private static PagedResultSet<T> BuildPagedResult<T>(List<T> all, int page, int pageSize)
    {
        var results = all.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PagedResultSet<T>
        {
            Results = results,
            TotalResults = all.Count,
            CurrentPage = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Simulates EF Core's automatic FK ↔ navigation property resolution.
    /// Production code may set only FK (TypeId) or only nav prop (Type) — EF resolves
    /// the other side on SaveChanges. InMemoryData must do this explicitly.
    /// Also fixes up AttributeValue FK/nav prop mismatches (AttributeId ↔ Attribute).
    /// </summary>
    private void FixupCsoNavigationProperties(ConnectedSystemObject cso)
    {
        // CSO-level nav props
        if (cso.Type == null && cso.TypeId > 0 && _objectTypes.TryGetValue(cso.TypeId, out var objectType))
            cso.Type = objectType;
        else if (cso.TypeId == 0 && cso.Type != null)
            cso.TypeId = cso.Type.Id; // reverse: nav prop set but FK missing (common in tests)
        if (cso.ConnectedSystem == null && cso.ConnectedSystemId > 0 && _connectedSystems.TryGetValue(cso.ConnectedSystemId, out var cs))
            cso.ConnectedSystem = cs;
        if (cso.MetaverseObject == null && cso.MetaverseObjectId.HasValue && _mvos.TryGetValue(cso.MetaverseObjectId.Value, out var mvo))
            cso.MetaverseObject = mvo;

        // Attribute value FK ↔ nav prop fixup
        foreach (var av in cso.AttributeValues)
        {
            if (av.AttributeId == 0 && av.Attribute != null)
                av.AttributeId = av.Attribute.Id;
            else if (av.Attribute == null && av.AttributeId > 0 && cso.Type != null)
                av.Attribute = cso.Type.Attributes.FirstOrDefault(a => a.Id == av.AttributeId)!;
        }
    }

    /// <summary>
    /// Fixes up PendingExportAttributeValueChange FK/nav prop mismatches.
    /// Drift detection creates changes with only AttributeId; export evaluation creates with both.
    /// </summary>
    private void FixupPendingExportAttributeChanges(PendingExport pe)
    {
        if (pe.AttributeValueChanges == null || pe.AttributeValueChanges.Count == 0) return;

        // Build attribute lookup from the CSO type (if available)
        ConnectedSystemObjectType? csoType = null;
        if (pe.ConnectedSystemObject?.Type != null)
            csoType = pe.ConnectedSystemObject.Type;
        else if (pe.ConnectedSystemObject?.TypeId > 0 && _objectTypes.TryGetValue(pe.ConnectedSystemObject.TypeId, out var ot))
            csoType = ot;

        foreach (var avc in pe.AttributeValueChanges)
        {
            if (avc.AttributeId == 0 && avc.Attribute != null)
                avc.AttributeId = avc.Attribute.Id;
            else if (avc.Attribute == null && avc.AttributeId > 0 && csoType != null)
                avc.Attribute = csoType.Attributes.FirstOrDefault(a => a.Id == avc.AttributeId)!;
        }
    }

    /// <summary>
    /// Fixes up MVO attribute value FK/nav prop mismatches.
    /// Production code sets Attribute nav prop but not AttributeId; EF resolves on SaveChanges.
    /// Similarly, ReferenceValue nav prop may be set without ReferenceValueId.
    /// </summary>
    private static void FixupMvoAttributeValues(MetaverseObject mvo)
    {
        foreach (var av in mvo.AttributeValues)
        {
            if (av.AttributeId == 0 && av.Attribute != null)
                av.AttributeId = av.Attribute.Id;
            if (av.ReferenceValue != null && av.ReferenceValue.Id != Guid.Empty
                && (!av.ReferenceValueId.HasValue || av.ReferenceValueId == Guid.Empty))
                av.ReferenceValueId = av.ReferenceValue.Id;
        }
    }

    /// <summary>
    /// Converts a CSO attribute value to its lowercase string representation for cache key building.
    /// Mirrors the logic in ConnectedSystemRepository.GetExternalIdValueString.
    /// </summary>
    private static string? GetExternalIdValueString(ConnectedSystemObjectAttributeValue? av)
    {
        if (av == null) return null;
        if (av.StringValue != null) return av.StringValue.ToLowerInvariant();
        if (av.IntValue.HasValue) return av.IntValue.Value.ToString();
        if (av.LongValue.HasValue) return av.LongValue.Value.ToString();
        if (av.GuidValue.HasValue) return av.GuidValue.Value.ToString().ToLowerInvariant();
        return null;
    }

    private void AddToCsIndex(ConnectedSystemObject cso)
    {
        if (!_csosByConnectedSystem.TryGetValue(cso.ConnectedSystemId, out var csSet))
        {
            csSet = new HashSet<Guid>();
            _csosByConnectedSystem[cso.ConnectedSystemId] = csSet;
        }
        csSet.Add(cso.Id);

        if (cso.MetaverseObjectId.HasValue)
            AddToMvoIndex(cso.MetaverseObjectId.Value, cso.Id);
    }

    private void AddToMvoIndex(Guid metaverseObjectId, Guid csoId)
    {
        if (!_csosByMvo.TryGetValue(metaverseObjectId, out var mvoSet))
        {
            mvoSet = new HashSet<Guid>();
            _csosByMvo[metaverseObjectId] = mvoSet;
        }
        mvoSet.Add(csoId);
    }

    /// <summary>
    /// Updates the MVO index for a CSO after its MetaverseObjectId has been changed externally.
    /// Call this after manually setting cso.MetaverseObjectId in tests.
    /// </summary>
    public void RefreshCsoMvoIndex(ConnectedSystemObject cso) => UpdateMvoIndex(cso);

    private void UpdateMvoIndex(ConnectedSystemObject cso)
    {
        // Remove from all MVO indexes first
        foreach (var kvp in _csosByMvo)
            kvp.Value.Remove(cso.Id);

        // Re-add if joined
        if (cso.MetaverseObjectId.HasValue)
            AddToMvoIndex(cso.MetaverseObjectId.Value, cso.Id);
    }

    private void RemoveCso(ConnectedSystemObject cso)
    {
        _csos.Remove(cso.Id);
        if (_csosByConnectedSystem.TryGetValue(cso.ConnectedSystemId, out var csSet))
            csSet.Remove(cso.Id);
        foreach (var kvp in _csosByMvo)
            kvp.Value.Remove(cso.Id);
    }

    private void AddToPeIndex(PendingExport pe)
    {
        if (!_pendingExportsByCs.TryGetValue(pe.ConnectedSystemId, out var csSet))
        {
            csSet = new HashSet<Guid>();
            _pendingExportsByCs[pe.ConnectedSystemId] = csSet;
        }
        csSet.Add(pe.Id);

        if (pe.ConnectedSystemObjectId.HasValue)
            _pendingExportsByCsoId[pe.ConnectedSystemObjectId.Value] = pe.Id;
    }

    private void RemovePe(PendingExport pe)
    {
        _pendingExports.Remove(pe.Id);
        if (_pendingExportsByCs.TryGetValue(pe.ConnectedSystemId, out var csSet))
            csSet.Remove(pe.Id);
        if (pe.ConnectedSystemObjectId.HasValue)
            _pendingExportsByCsoId.Remove(pe.ConnectedSystemObjectId.Value);
    }

    #endregion
}
