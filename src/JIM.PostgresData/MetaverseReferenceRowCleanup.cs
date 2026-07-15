// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace JIM.PostgresData;

/// <summary>
/// Shared clean-up of reference attribute rows on surviving Metaverse Objects when other Metaverse
/// Objects are deleted (#1019). Valueless rows are deleted outright: nulling their FK (the pre-#1019
/// behaviour) left informationless "ghost" rows that rendered as blank entries in member lists,
/// inflated member counts, and staged all-null attribute changes on later exports. Rows carrying any
/// payload keep the legacy behaviour of having only their reference nulled.
/// Used by both the plural (<c>SyncRepository.DeleteMetaverseObjectsAsync</c>) and singular
/// (<c>MetaverseRepository.DeleteMetaverseObjectAsync</c>) deletion paths. The SQL predicate must
/// stay in step with <see cref="MetaverseObjectAttributeValue.IsValuelessReferenceRow"/> and the
/// DeleteGhostMetaverseReferenceAttributeValues clean-up migration; parity is pinned by
/// MvoDeletionGhostReferenceRowDatabaseTests.
/// </summary>
internal static class MetaverseReferenceRowCleanup
{
    /// <summary>
    /// Deletes valueless reference rows on surviving Metaverse Objects that point at the deleted
    /// MVOs, nulls the reference on any remaining (payload-carrying) rows, and surgically removes
    /// tracked instances of the deleted rows from the change tracker. Must run BEFORE the deleted
    /// MVOs are passed to Remove/RemoveRange: EF's client-side cascade fix-up fires at Remove time
    /// and would otherwise mark the raw-deleted rows Modified (an UPDATE against a missing row
    /// throws DbUpdateConcurrencyException and poisons the worker's long-lived context).
    /// </summary>
    internal static async Task CleanUpReferencesToDeletedMvosAsync(JimDbContext context, Guid[] deletedMvoIds)
    {
        // Rows owned by the deleted MVOs themselves are excluded from the DELETE: they die with
        // their owner via the database cascade, and raw-deleting them here would make EF's cascade
        // delete at SaveChanges target already-missing rows (zero rows affected -> concurrency
        // exception). Only rows on SURVIVING owners are ghost candidates.
        await context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""MetaverseObjectAttributeValues""
              WHERE ""ReferenceValueId"" = ANY({0})
                AND NOT (""MetaverseObjectId"" = ANY({0}))
                AND ""StringValue"" IS NULL
                AND ""DateTimeValue"" IS NULL
                AND ""IntValue"" IS NULL
                AND ""LongValue"" IS NULL
                AND ""ByteValue"" IS NULL
                AND ""GuidValue"" IS NULL
                AND ""BoolValue"" IS NULL
                AND ""UnresolvedReferenceValueId"" IS NULL
                AND ""NullValue"" = false",
            deletedMvoIds);

        // Payload-carrying rows on surviving owners, and all rows owned by the co-deleted MVOs,
        // keep the legacy behaviour: reference nulled, row preserved (the co-deleted MVOs' own rows
        // are removed moments later by the database cascade when their owner row is deleted).
        await context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""MetaverseObjectAttributeValues"" SET ""ReferenceValueId"" = NULL WHERE ""ReferenceValueId"" = ANY({0})",
            deletedMvoIds);

        RemoveTrackedValuelessReferenceRows(context, deletedMvoIds.ToHashSet());
    }

    /// <summary>
    /// Removes tracked instances of the raw-deleted ghost rows from every tracked parent's
    /// AttributeValues collection and detaches them. Both halves are load-bearing: a still-tracked
    /// instance is marked Modified by EF's ClientSetNull cascade fix-up when the referenced MVO is
    /// removed (UPDATE against a deleted row throws), and a detached instance still reachable from a
    /// tracked parent's collection is re-attached as Added by the next DetectChanges and silently
    /// re-inserted. Runs with change detection suspended because ChangeTracker.Entries() otherwise
    /// triggers DetectChanges, which mid-sync can attach undetected duplicate-key graphs.
    /// </summary>
    private static void RemoveTrackedValuelessReferenceRows(JimDbContext context, HashSet<Guid> deletedMvoIds)
    {
        var autoDetectChanges = context.ChangeTracker.AutoDetectChangesEnabled;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var ghostEntries = context.ChangeTracker.Entries<MetaverseObjectAttributeValue>()
                .Where(e => e.State != EntityState.Detached &&
                            ReferencesDeletedMvo(e.Entity, deletedMvoIds) &&
                            e.Entity.IsValuelessReferenceRow() &&
                            !IsOwnedByDeletedMvo(e, deletedMvoIds))
                .ToList();
            if (ghostEntries.Count == 0)
                return;

            var ghostInstances = ghostEntries.Select(e => e.Entity).ToHashSet();
            foreach (var mvoEntry in context.ChangeTracker.Entries<MetaverseObject>())
                mvoEntry.Entity.AttributeValues.RemoveAll(ghostInstances.Contains);

            foreach (var ghostEntry in ghostEntries)
                ghostEntry.State = EntityState.Detached;
        }
        finally
        {
            context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    private static bool ReferencesDeletedMvo(MetaverseObjectAttributeValue attributeValue, HashSet<Guid> deletedMvoIds)
    {
        return (attributeValue.ReferenceValueId.HasValue && deletedMvoIds.Contains(attributeValue.ReferenceValueId.Value)) ||
               (attributeValue.ReferenceValue != null && deletedMvoIds.Contains(attributeValue.ReferenceValue.Id));
    }

    private static bool IsOwnedByDeletedMvo(EntityEntry<MetaverseObjectAttributeValue> entry, HashSet<Guid> deletedMvoIds)
    {
        // The owner FK is a shadow property (the model has no MetaverseObjectId scalar), so probe it
        // via the entry; fall back to the navigation for graphs built in memory.
        if (entry.Property("MetaverseObjectId").CurrentValue is Guid ownerId && deletedMvoIds.Contains(ownerId))
            return true;
        return entry.Entity.MetaverseObject != null && deletedMvoIds.Contains(entry.Entity.MetaverseObject.Id);
    }
}
