// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Sync;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// The Metaverse-Derived Attribute Flow mark (#1750, "Position 2"): <c>ConnectedSystemObjects.DerivedInputChangePending</c>,
/// set in bulk on a hosting system's joined objects when another system changes a derived flow's input, and cleared
/// in bulk once the hosting system has re-evaluated them. Both are deliberately narrow, single-column statements
/// (exempt from the bulk column list rule, like the ScopeReviewPending flag's), and both fix up tracked instances:
/// the worker's context lives for the whole run, and a whole-entity save of a stale tracked instance would silently
/// undo the write.
/// </summary>
public partial class SyncRepository
{
    public async Task<int> MarkConnectedSystemObjectsDerivedInputChangePendingAsync(IReadOnlyCollection<DerivedInputChangeMark> marks)
    {
        if (marks.Count == 0)
            return 0;

        var distinct = marks.Distinct().ToList();
        var metaverseObjectIds = distinct.Select(m => m.MetaverseObjectId).ToArray();
        var connectedSystemIds = distinct.Select(m => m.ConnectedSystemId).ToArray();

        // One statement for the page, joined on (ConnectedSystemId, MetaverseObjectId), which the unique join index
        // IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique serves. Already-marked rows are left
        // alone, so the count is of new marks only (the page summary reports it) and no row is rewritten needlessly.
        var marked = await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" AS cso
              SET ""DerivedInputChangePending"" = true
              FROM unnest({0}::uuid[], {1}::integer[]) AS m(""MetaverseObjectId"", ""ConnectedSystemId"")
              WHERE cso.""MetaverseObjectId"" = m.""MetaverseObjectId""
                AND cso.""ConnectedSystemId"" = m.""ConnectedSystemId""
                AND NOT cso.""DerivedInputChangePending""",
            metaverseObjectIds, connectedSystemIds);

        var markSet = distinct.ToHashSet();
        FixUpTrackedDerivedInputChangePending(
            cso => cso.MetaverseObjectId.HasValue && markSet.Contains(new DerivedInputChangeMark(cso.MetaverseObjectId.Value, cso.ConnectedSystemId)),
            value: true);

        return marked;
    }

    public async Task<int> ClearConnectedSystemObjectDerivedInputChangePendingAsync(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0)
            return 0;

        var cleared = await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""ConnectedSystemObjects"" SET ""DerivedInputChangePending"" = false
              WHERE ""Id"" = ANY({0}) AND ""DerivedInputChangePending""",
            ids.ToArray());

        var idSet = ids.ToHashSet();
        FixUpTrackedDerivedInputChangePending(cso => idSet.Contains(cso.Id), value: false);

        return cleared;
    }

    /// <summary>
    /// Brings tracked Connected System Objects matching <paramref name="predicate"/> into line with a raw write of
    /// <c>DerivedInputChangePending</c>: current and original value both set, so the property reads as persisted
    /// rather than as a pending change, and a later whole-entity save writes the value the database already holds.
    /// Change detection is suppressed while enumerating, for the reason <see cref="DetachTrackedEntities{T}"/> gives.
    /// </summary>
    private void FixUpTrackedDerivedInputChangePending(Func<ConnectedSystemObject, bool> predicate, bool value)
    {
        var autoDetectChanges = _context.ChangeTracker.AutoDetectChangesEnabled;
        _context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = _context.ChangeTracker.Entries<ConnectedSystemObject>()
                .Where(e => e.State != EntityState.Detached && e.State != EntityState.Added && predicate(e.Entity))
                .ToList();

            foreach (var property in entries.Select(entry => entry.Property(cso => cso.DerivedInputChangePending)))
            {
                property.CurrentValue = value;
                property.OriginalValue = value;
                property.IsModified = false;
            }
        }
        finally
        {
            _context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }
}
