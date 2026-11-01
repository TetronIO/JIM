// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Deletes Connected System Objects by id with raw SQL, and brings the change tracker into line with what the database's
/// cascades did. The one implementation behind <see cref="ConnectedSystemRepository.DeleteConnectedSystemObjectsAsync"/>
/// and <see cref="SyncRepository.DeleteConnectedSystemObjectsByIdsAsync"/> (#2669).
/// </summary>
/// <remarks>
/// <para>
/// The synchronisation flush used to delete through EF Core (<c>RemoveRange</c> then <c>SaveChangesAsync</c>), which
/// writes one statement per object and per tracked value: 13,002 for a page of 500 departed accounts of 25 values each,
/// about 2.3 ms an object, and most of a leaver cohort's confirming synchronisation. Here the database does the work in
/// three statements whatever the count, and the tracker is updated in one pass over its entries.
/// </para>
/// <para>
/// Every foreign key into <c>ConnectedSystemObjects</c>, with what the database does when an object goes and what this
/// does to the tracker to match (<c>JimDbContext</c> and the model snapshot are the source):
/// <list type="bullet">
/// <item><c>ConnectedSystemObjectAttributeValues.ConnectedSystemObjectId</c>: cascade. Tracked values are detached.</item>
/// <item><c>DeferredReferences.SourceCsoId</c>: cascade. Tracked rows are detached.</item>
/// <item><c>GeneratedValueAssignments.ConnectedSystemObjectId</c>: cascade, after <see cref="RetiredGeneratedValueSql"/>
/// has retired their values. Tracked rows are detached.</item>
/// <item><c>ConnectedSystemObjectAttributeValues.ReferenceValueId</c> and
/// <c>MetaverseObjectAttributeValues.UnresolvedReferenceValueId</c>: no action, so cleared by an UPDATE before the
/// delete. Tracked values are cleared to match.</item>
/// <item><c>PendingExports.ConnectedSystemObjectId</c>, <c>ActivityRunProfileExecutionItems.ConnectedSystemObjectId</c>,
/// <c>ConnectedSystemObjectChanges.ConnectedSystemObjectId</c>, <c>ConnectedSystemObjectChangeAttributeValues.ReferenceValueId</c>,
/// <c>PendingPasswordChanges.ConnectedSystemObjectId</c> and <c>.ProvisionedConnectedSystemObjectId</c>: set null by the
/// database. Tracked rows are cleared to match.</item>
/// </list>
/// Clearing sets a foreign key's current and original value to null and its navigation to null, so the tracker holds
/// what the database holds and a later save writes nothing, whether or not it detects changes first. A row that was
/// already modified keeps its other pending changes and writes the null with them.
/// </para>
/// <para>
/// The other direction too: a deleted row is taken out of every tracked principal's collection navigation that holds
/// it (the Connected System's <c>Objects</c>, a joined Metaverse Object's <c>ConnectedSystemObjects</c>, and any other
/// the model declares), as EF Core's delete fix-up did, so nothing reads a deleted object as connected or walks it back
/// into the tracker.
/// </para>
/// <para>
/// One foreign key points the other way, at a value deleted with its object: <c>ConnectedSystemObjectChanges.DeletedObjectExternalIdAttributeValueId</c>,
/// with no action. Nothing writes it, so it never blocks a delete; a row holding it would fail this delete as it would
/// have failed EF Core's.
/// </para>
/// </remarks>
internal static class ConnectedSystemObjectDeletionSql
{
    private static readonly ActivitySource ActivitySource = new("JIM.PostgresData.ConnectedSystem");

    // The shadow foreign keys the tracker holds for these, which have no CLR property.
    private const string AttributeValueOwnerKey = "ConnectedSystemObjectId";
    private const string ChangeValueReferenceKey = "ReferenceValueId";

    /// <summary>
    /// Deletes the given Connected System Objects, their values and what cascades with them, in one transaction (the
    /// caller's, when it holds one), and returns how many objects were deleted.
    /// </summary>
    internal static async Task<int> DeleteAsync(JimDbContext context, IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        var ids = connectedSystemObjectIds as HashSet<Guid> ?? [.. connectedSystemObjectIds];
        if (ids.Count == 0)
            return 0;

        using var deleteSpan = ActivitySource.StartActivity("Cso.Delete");
        deleteSpan?.SetTag("csoCount", ids.Count);
        var idArray = ids.ToArray();

        await using var ownTransaction = context.Database.CurrentTransaction == null
            ? await context.Database.BeginTransactionAsync()
            : null;

        // Retire any export-mode generated values these objects held, BEFORE the rows go: their assignments are removed
        // by the deletion's foreign-key cascade, so afterwards there is nothing left to read them from (#242, Phase 6).
        using (var retireSpan = ActivitySource.StartActivity("Cso.Delete.RetireGeneratedValues"))
        {
            var retirements = await RetiredGeneratedValueSql.RetireForConnectedSystemObjectsAsync(context, idArray);
            retireSpan?.SetTag("retiredCount", retirements.Count);
        }

        // The two references into the table that do not cascade: another object's value naming one of these (a group's
        // member still pointing at a deprovisioned account before the removal is confirmed) and a Metaverse Object value's
        // reference that never resolved. Left in place, either fails the delete (23503). The raw reference string stays
        // on the row, so the next confirming import still reconciles the value.
        using (var clearSpan = ActivitySource.StartActivity("Cso.Delete.ClearReferenceValues"))
        {
            var rowsAffected = await context.Database.ExecuteSqlRawAsync(
                @"UPDATE ""ConnectedSystemObjectAttributeValues"" SET ""ReferenceValueId"" = NULL WHERE ""ReferenceValueId"" = ANY({0})",
                idArray);
            clearSpan?.SetTag("rowsAffected", rowsAffected);
        }

        using (var clearSpan = ActivitySource.StartActivity("Cso.Delete.ClearUnresolvedReferenceValues"))
        {
            var rowsAffected = await context.Database.ExecuteSqlRawAsync(
                @"UPDATE ""MetaverseObjectAttributeValues"" SET ""UnresolvedReferenceValueId"" = NULL WHERE ""UnresolvedReferenceValueId"" = ANY({0})",
                idArray);
            clearSpan?.SetTag("rowsAffected", rowsAffected);
        }

        int deleted;
        using (var sqlSpan = ActivitySource.StartActivity("Cso.Delete.Delete"))
        {
            deleted = await context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM ""ConnectedSystemObjects"" WHERE ""Id"" = ANY({0})",
                idArray);
            sqlSpan?.SetTag("rowsAffected", deleted);
        }

        // After the statements, inside the transaction: a statement that fails leaves the tracker as it was, matching the
        // rolled-back database.
        using (var trackerSpan = ActivitySource.StartActivity("Cso.Delete.UpdateTracker"))
        {
            var (scanned, detached, cleared, removedFromCollections) = UpdateTracker(context, ids);
            trackerSpan?.SetTag("entriesScanned", scanned);
            trackerSpan?.SetTag("detachedCount", detached);
            trackerSpan?.SetTag("clearedForeignKeyCount", cleared);
            trackerSpan?.SetTag("removedFromCollectionCount", removedFromCollections);
        }

        if (ownTransaction != null)
        {
            using (ActivitySource.StartActivity("Cso.Delete.Commit"))
                await ownTransaction.CommitAsync();
        }

        return deleted;
    }

    /// <summary>
    /// One pass over the tracked entries: detaches what the database deleted, and clears every tracked reference the
    /// database cleared.
    /// </summary>
    /// <remarks>
    /// Change detection is off for the pass, because enumerating the tracker with it on attaches any untracked graph
    /// hanging off a tracked navigation, and mid-synchronisation those carry duplicate-key instances
    /// (<c>src/CLAUDE.md</c>, "Tracker surgery must not trigger DetectChanges"). The entries are materialised first:
    /// clearing a foreign key moves its entry between the tracker's per-state maps, which the enumeration reads.
    /// References are cleared while the deleted objects are still tracked, so EF Core's own fix-up also takes the rows
    /// out of the objects' inverse collections.
    /// </remarks>
    private static (int Scanned, int Detached, int Cleared, int RemovedFromCollections) UpdateTracker(JimDbContext context, HashSet<Guid> ids)
    {
        var detectChanges = context.ChangeTracker.AutoDetectChangesEnabled;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = context.ChangeTracker.Entries().ToList();
            var toDetach = new List<EntityEntry>();
            var cleared = 0;
            foreach (var entry in entries)
            {
                if (IsDeletedWithTheObjects(entry, ids))
                    toDetach.Add(entry);
                else
                    cleared += ClearReferencesToTheObjects(entry, ids);
            }

            var removedFromCollections = RemoveFromTrackedCollections(entries, toDetach);

            foreach (var entry in toDetach)
                entry.State = EntityState.Detached;

            return (entries.Count, toDetach.Count, cleared, removedFromCollections);
        }
        finally
        {
            context.ChangeTracker.AutoDetectChangesEnabled = detectChanges;
        }
    }

    /// <summary>
    /// Whether the entry is one of the deleted objects or a row the database deleted with them.
    /// </summary>
    private static bool IsDeletedWithTheObjects(EntityEntry entry, HashSet<Guid> ids) => entry.Entity switch
    {
        ConnectedSystemObject connectedSystemObject => ids.Contains(connectedSystemObject.Id),
        ConnectedSystemObjectAttributeValue => entry.Property(AttributeValueOwnerKey).CurrentValue is Guid owner && ids.Contains(owner),
        DeferredReference deferredReference => ids.Contains(deferredReference.SourceCsoId),
        GeneratedValueAssignment assignment => assignment.ConnectedSystemObjectId is { } owner && ids.Contains(owner),
        _ => false
    };

    /// <summary>
    /// Takes the deleted instances out of every tracked principal's collection navigation that holds them, as EF Core's
    /// own delete fix-up did when the deletion went through <c>RemoveRange</c>; returns how many it removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Detaching an instance leaves it in any tracked collection that holds it: the run's tracked Connected System
    /// holds every object fix-up met while it was tracked in <c>Objects</c>, and a tracked Metaverse Object holds its
    /// joined objects in <c>ConnectedSystemObjects</c>. A deleted object left there is read as still connected by
    /// anything walking the collection, and is attached again by anything that walks the principal's graph (#2669).
    /// </para>
    /// <para>
    /// The collections are found from the model, not named: every foreign key of a deleted row's entity type whose
    /// principal has a collection navigation onto it, so a navigation added later is covered too. The principal is the
    /// deleted row's reference navigation where it has one, otherwise the tracked row its foreign key names. A
    /// principal deleted in the same pass keeps its collections (a deleted object still lists its values for whoever
    /// reads it afterwards). A plain list is cleared in one pass; any other collection through EF Core's accessor.
    /// </para>
    /// </remarks>
    private static int RemoveFromTrackedCollections(List<EntityEntry> entries, List<EntityEntry> deletedEntries)
    {
        if (deletedEntries.Count == 0)
            return 0;

        var deleted = deletedEntries.Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var trackedEntities = entries.Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var collectionForeignKeys = new Dictionary<IEntityType, List<IForeignKey>>();
        var principalsByKey = new Dictionary<IEntityType, Dictionary<object, object>>();

        // Principal, then the collection on it, then the deleted rows it holds.
        var toRemove = new Dictionary<object, Dictionary<INavigation, List<object>>>(ReferenceEqualityComparer.Instance);
        foreach (var entry in deletedEntries)
        {
            if (!collectionForeignKeys.TryGetValue(entry.Metadata, out var foreignKeys))
            {
                foreignKeys = entry.Metadata.GetForeignKeys().Where(fk => fk.PrincipalToDependent is { IsCollection: true }).ToList();
                collectionForeignKeys[entry.Metadata] = foreignKeys;
            }

            foreach (var foreignKey in foreignKeys)
            {
                var principal = FindTrackedPrincipal(entry, foreignKey, trackedEntities, entries, principalsByKey);
                if (principal == null || deleted.Contains(principal))
                    continue;

                if (!toRemove.TryGetValue(principal, out var byNavigation))
                {
                    byNavigation = [];
                    toRemove[principal] = byNavigation;
                }

                var navigation = foreignKey.PrincipalToDependent!;
                if (!byNavigation.TryGetValue(navigation, out var dependents))
                {
                    dependents = [];
                    byNavigation[navigation] = dependents;
                }

                dependents.Add(entry.Entity);
            }
        }

        var removed = 0;
        foreach (var (principal, byNavigation) in toRemove)
        {
            foreach (var (navigation, dependents) in byNavigation)
                removed += RemoveFromCollection(principal, navigation, dependents, deleted);
        }

        return removed;
    }

    /// <summary>
    /// The tracked principal a deleted row's foreign key points at, or null when none is tracked.
    /// </summary>
    private static object? FindTrackedPrincipal(EntityEntry entry, IForeignKey foreignKey, HashSet<object> trackedEntities,
        List<EntityEntry> entries, Dictionary<IEntityType, Dictionary<object, object>> principalsByKey)
    {
        if (foreignKey.DependentToPrincipal is { } toPrincipal
            && entry.Reference(toPrincipal.Name).CurrentValue is { } referenced
            && trackedEntities.Contains(referenced))
        {
            return referenced;
        }

        // Without a reference to follow, the key: every foreign key into these tables is a single column today.
        if (foreignKey.Properties.Count != 1 || entry.Property(foreignKey.Properties[0].Name).CurrentValue is not { } keyValue)
            return null;

        var principalType = foreignKey.PrincipalEntityType;
        if (!principalsByKey.TryGetValue(principalType, out var byKey))
        {
            var keyName = foreignKey.PrincipalKey.Properties[0].Name;
            byKey = entries
                .Where(e => principalType.IsAssignableFrom(e.Metadata))
                .GroupBy(e => e.Property(keyName).CurrentValue!)
                .ToDictionary(g => g.Key, g => g.First().Entity);
            principalsByKey[principalType] = byKey;
        }

        return byKey.GetValueOrDefault(keyValue);
    }

    /// <summary>
    /// Removes the deleted rows from one collection on one principal; returns how many it held.
    /// </summary>
    private static int RemoveFromCollection(object principal, INavigation navigation, List<object> dependents, HashSet<object> deleted)
    {
        var collection = navigation.GetGetter().GetClrValue(principal);
        if (collection is System.Collections.IList list)
        {
            var removed = 0;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] is { } item && deleted.Contains(item))
                {
                    list.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        if (collection == null)
            return 0;

        var accessor = navigation.GetCollectionAccessor()!;
        return dependents.Count(dependent => accessor.Remove(principal, dependent));
    }

    /// <summary>
    /// Clears every reference the entry holds to a deleted object, as the database cleared it; returns how many.
    /// </summary>
    private static int ClearReferencesToTheObjects(EntityEntry entry, HashSet<Guid> ids)
    {
        // A row already marked for deletion is deleted by its key; what it refers to no longer matters.
        if (entry.State == EntityState.Deleted)
            return 0;

        switch (entry.Entity)
        {
            case ConnectedSystemObjectAttributeValue value when References(value.ReferenceValueId, ids):
                ClearForeignKey(entry, nameof(ConnectedSystemObjectAttributeValue.ReferenceValueId));
                value.ReferenceValue = null;
                return 1;

            case MetaverseObjectAttributeValue value when References(value.UnresolvedReferenceValueId, ids):
                ClearForeignKey(entry, nameof(MetaverseObjectAttributeValue.UnresolvedReferenceValueId));
                value.UnresolvedReferenceValue = null;
                return 1;

            case PendingExport pendingExport when References(pendingExport.ConnectedSystemObjectId, ids):
                ClearForeignKey(entry, nameof(PendingExport.ConnectedSystemObjectId));
                pendingExport.ConnectedSystemObject = null;
                return 1;

            case ActivityRunProfileExecutionItem executionItem when References(executionItem.ConnectedSystemObjectId, ids):
                ClearForeignKey(entry, nameof(ActivityRunProfileExecutionItem.ConnectedSystemObjectId));
                executionItem.ConnectedSystemObject = null;
                return 1;

            case ConnectedSystemObjectChange change when References(change.ConnectedSystemObjectId, ids):
                ClearForeignKey(entry, nameof(ConnectedSystemObjectChange.ConnectedSystemObjectId));
                change.ConnectedSystemObject = null;
                return 1;

            case ConnectedSystemObjectChangeAttributeValue changeValue when References(entry.Property(ChangeValueReferenceKey).CurrentValue as Guid?, ids):
                ClearForeignKey(entry, ChangeValueReferenceKey);
                changeValue.ReferenceValue = null;
                return 1;

            case PendingPasswordChange passwordChange:
                var cleared = 0;
                if (References(passwordChange.ConnectedSystemObjectId, ids))
                {
                    ClearForeignKey(entry, nameof(PendingPasswordChange.ConnectedSystemObjectId));
                    cleared++;
                }
                if (References(passwordChange.ProvisionedConnectedSystemObjectId, ids))
                {
                    ClearForeignKey(entry, nameof(PendingPasswordChange.ProvisionedConnectedSystemObjectId));
                    cleared++;
                }
                return cleared;

            default:
                return 0;
        }
    }

    private static bool References(Guid? foreignKey, HashSet<Guid> ids) => foreignKey is { } id && ids.Contains(id);

    /// <summary>
    /// Sets a foreign key to null as the database did. An unmodified key also takes null as its original value, so the
    /// row stays unchanged and no save writes it; an added row inserts the null, and a key already modified writes it.
    /// </summary>
    private static void ClearForeignKey(EntityEntry entry, string propertyName)
    {
        var property = entry.Property(propertyName);
        var keepUnmodified = (entry.State is EntityState.Unchanged or EntityState.Modified) && !property.IsModified;
        property.CurrentValue = null;
        if (!keepUnmodified)
            return;

        property.OriginalValue = null;
        property.IsModified = false;
    }
}
