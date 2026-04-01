using JIM.Data.Repositories;
using JIM.Models.Activities;
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

    /// <summary>All connected system objects, keyed by CSO ID.</summary>
    public IReadOnlyDictionary<Guid, ConnectedSystemObject> ConnectedSystemObjects => _csos;

    /// <summary>All metaverse objects, keyed by MVO ID.</summary>
    public IReadOnlyDictionary<Guid, MetaverseObject> MetaverseObjects => _mvos;

    /// <summary>All pending exports, keyed by pending export ID.</summary>
    public IReadOnlyDictionary<Guid, PendingExport> PendingExports => _pendingExports;

    /// <summary>All activities, keyed by activity ID.</summary>
    public IReadOnlyDictionary<Guid, Activity> Activities => _activities;

    /// <summary>All run profile execution items, keyed by RPEI ID.</summary>
    public IReadOnlyDictionary<Guid, ActivityRunProfileExecutionItem> Rpeis => _rpeis;

    /// <summary>All connected systems, keyed by connected system ID.</summary>
    public IReadOnlyDictionary<int, ConnectedSystem> ConnectedSystems => _connectedSystems;

    /// <summary>All sync rules, keyed by sync rule ID.</summary>
    public IReadOnlyDictionary<int, SyncRule> SyncRules => _syncRules;

    /// <summary>All connected system object types, keyed by object type ID.</summary>
    public IReadOnlyDictionary<int, ConnectedSystemObjectType> ObjectTypes => _objectTypes;

    /// <summary>All metaverse object change records, keyed by change ID.</summary>
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
    /// Removes all pending exports from the store. Used by tests to reset state between sync cycles.
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

    public Task<int> GetConnectedSystemObjectCountAsync(int connectedSystemId)
    {
        var count = _csosByConnectedSystem.TryGetValue(connectedSystemId, out var ids) ? ids.Count : 0;
        return Task.FromResult(count);
    }

    public Task<int> GetConnectedSystemObjectModifiedSinceCountAsync(int connectedSystemId, DateTime modifiedSince)
    {
        var count = GetCsosForSystem(connectedSystemId)
            .Count(c => c.LastUpdated.HasValue && c.LastUpdated.Value >= modifiedSince);
        return Task.FromResult(count);
    }

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsAsync(
        int connectedSystemId, int page, int pageSize)
    {
        var all = GetCsosForSystem(connectedSystemId)
            .OrderBy(c => c.Created).ThenBy(c => c.Id)
            .ToList();
        return Task.FromResult(BuildPagedResult(all, page, pageSize));
    }

    public Task<PagedResultSet<ConnectedSystemObject>> GetConnectedSystemObjectsModifiedSinceAsync(
        int connectedSystemId, DateTime modifiedSince, int page, int pageSize)
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

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByIdsAsync(int connectedSystemId, IEnumerable<Guid> csoIds)
    {
        var idSet = new HashSet<Guid>(csoIds);
        var result = GetCsosForSystem(connectedSystemId)
            .Where(cso => idSet.Contains(cso.Id))
            .ToList();
        return Task.FromResult(result);
    }

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

    public Task<List<int>> GetAllExternalIdAttributeValuesOfTypeIntAsync(int connectedSystemId, int objectTypeId)
    {
        var values = GetCsosForSystem(connectedSystemId)
            .Where(c => c.TypeId == objectTypeId)
            .Select(c => c.AttributeValues.FirstOrDefault(av => av.AttributeId == c.ExternalIdAttributeId))
            .Where(av => av?.IntValue != null)
            .Select(av => av!.IntValue!.Value)
            .ToList();
        return Task.FromResult(values);
    }

    public Task<List<string>> GetAllExternalIdAttributeValuesOfTypeStringAsync(int connectedSystemId, int objectTypeId)
    {
        var values = GetCsosForSystem(connectedSystemId)
            .Where(c => c.TypeId == objectTypeId)
            .Select(c => c.AttributeValues.FirstOrDefault(av => av.AttributeId == c.ExternalIdAttributeId))
            .Where(av => av?.StringValue != null)
            .Select(av => av!.StringValue!)
            .ToList();
        return Task.FromResult(values);
    }

    public Task<List<Guid>> GetAllExternalIdAttributeValuesOfTypeGuidAsync(int connectedSystemId, int objectTypeId)
    {
        var values = GetCsosForSystem(connectedSystemId)
            .Where(c => c.TypeId == objectTypeId)
            .Select(c => c.AttributeValues.FirstOrDefault(av => av.AttributeId == c.ExternalIdAttributeId))
            .Where(av => av?.GuidValue != null)
            .Select(av => av!.GuidValue!.Value)
            .ToList();
        return Task.FromResult(values);
    }

    public Task<List<long>> GetAllExternalIdAttributeValuesOfTypeLongAsync(int connectedSystemId, int objectTypeId)
    {
        var values = GetCsosForSystem(connectedSystemId)
            .Where(c => c.TypeId == objectTypeId)
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

    public Task UpdateConnectedSystemObjectsAsync(List<ConnectedSystemObject> connectedSystemObjects)
    {
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

            foreach (var removal in cso.PendingAttributeValueRemovals)
            {
                // Use reference equality when Id is Guid.Empty (newly created, not yet persisted).
                // With EF Core, these objects have DB-generated IDs. In-memory, they remain empty.
                if (removal.Id == Guid.Empty)
                    cso.AttributeValues.Remove(removal);
                else
                    cso.AttributeValues.RemoveAll(av => av.Id == removal.Id);
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

                // Try to find a CSO with this external ID value in the same connected system
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
        // Build a lookup of secondary external ID values → CSO for the connected system
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
                    $"Multiple metaverse objects matched for rule {rule.Id}",
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
                $"Multiple metaverse objects matched for rule {rule.Id}",
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
        if (source.MetaverseAttribute == null && source.MetaverseAttributeId == null)
            return Task.FromResult<ConnectedSystemObject?>(null);
        if (source.ConnectedSystemAttribute == null && source.ConnectedSystemAttributeId == null)
            return Task.FromResult<ConnectedSystemObject?>(null);

        var mvoAttrId = source.MetaverseAttribute?.Id ?? source.MetaverseAttributeId;
        var mvoAttr = metaverseObject.AttributeValues?
            .FirstOrDefault(av => av.AttributeId == mvoAttrId);
        if (mvoAttr == null)
            return Task.FromResult<ConnectedSystemObject?>(null);

        var mvoVal = mvoAttr.StringValue ?? mvoAttr.GuidValue?.ToString() ?? mvoAttr.IntValue?.ToString();
        if (mvoVal == null)
            return Task.FromResult<ConnectedSystemObject?>(null);

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

        return Task.FromResult<ConnectedSystemObject?>(null);
    }

    #endregion

    #region Metaverse Object — Writes

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

    public Task DeleteMetaverseObjectAsync(MetaverseObject metaverseObject)
    {
        _mvos.Remove(metaverseObject.Id);
        _csosByMvo.Remove(metaverseObject.Id);
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

    public Task UpdatePendingExportsAsync(IEnumerable<PendingExport> pendingExports)
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

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsByConnectedSystemObjectIdsAsync(
        IEnumerable<Guid> connectedSystemObjectIds)
    {
        var result = new Dictionary<Guid, PendingExport>();
        foreach (var csoId in connectedSystemObjectIds)
        {
            if (_pendingExportsByCsoId.TryGetValue(csoId, out var peId) && _pendingExports.TryGetValue(peId, out var pe))
                result[csoId] = pe;
        }
        return Task.FromResult(result);
    }

    public Task<Dictionary<Guid, PendingExport>> GetPendingExportsLightweightByConnectedSystemObjectIdsAsync(
        IEnumerable<Guid> connectedSystemObjectIds)
        => GetPendingExportsByConnectedSystemObjectIdsAsync(connectedSystemObjectIds);

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
        // Return false to indicate "not raw SQL" — tells the processor to keep RPEIs in
        // the activity's RunProfileExecutionItems collection for test assertions, rather
        // than clearing them (which is the production raw SQL path).
        return Task.FromResult(false);
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

    #region Sync Rules and Configuration

    public Task<List<SyncRule>> GetSyncRulesAsync(int connectedSystemId, bool includeDisabled)
    {
        var rules = _syncRules.Values
            .Where(r => r.ConnectedSystemId == connectedSystemId)
            .Where(r => includeDisabled || r.Enabled)
            .ToList();
        return Task.FromResult(rules);
    }

    public Task<List<SyncRule>> GetAllSyncRulesAsync()
    {
        return Task.FromResult(_syncRules.Values.ToList());
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

    public Task UpdateConnectedSystemWithTriadAsync(
        ConnectedSystem connectedSystem,
        ActivityInitiatorType initiatorType,
        Guid? initiatorId,
        string? initiatorName)
    {
        // No-op — tests seed their own object types and containers
        return Task.CompletedTask;
    }

    #endregion

    #region MVO Change History

    public Task CreateMetaverseObjectChangeDirectAsync(MetaverseObjectChange change)
    {
        if (change.Id == Guid.Empty)
            change.Id = Guid.NewGuid();
        _mvoChanges[change.Id] = change;
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

    public Task<List<ConnectedSystemObject>> GetConnectedSystemObjectsByMetaverseObjectIdAsync(Guid metaverseObjectId)
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

    public Task<List<PendingExport>> GetExecutableExportBatchAsync(int connectedSystemId, int skip, int take)
    {
        var result = GetExecutableExportsForSystem(connectedSystemId)
            .Skip(skip)
            .Take(take)
            .ToList();
        return Task.FromResult(result);
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
            // Delete exports already exported are awaiting import confirmation — do not re-execute
            .Where(pe => !(pe.ChangeType == PendingExportChangeType.Delete
                        && pe.Status == PendingExportStatus.Exported));
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

    public Task<List<PendingExport>> GetPendingExportsByIdsAsync(IList<Guid> pendingExportIds)
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
