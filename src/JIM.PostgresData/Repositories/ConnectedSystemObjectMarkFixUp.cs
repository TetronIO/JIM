// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq.Expressions;
using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Brings tracked Connected System Objects into line with a raw write of one of their columns: a mark
/// (<c>SynchronisationPending</c>, #2092; <c>DerivedInputChangePending</c>, #1750) or the Partition (#2723). Current and
/// original value are both set, so the property reads as persisted rather than as a pending change. The worker's
/// context lives for the whole run, so a raw write that left a tracked instance stale would mislead whatever reads it
/// later, and a whole-entity save of it would write the stale value back. Change detection is suppressed while
/// enumerating, for the reason <c>SyncRepository.DetachTrackedEntities</c> gives (src/CLAUDE.md, "Tracker surgery must
/// not trigger DetectChanges").
/// </summary>
internal static class ConnectedSystemObjectMarkFixUp
{
    internal static void Apply(DbContext context, Func<ConnectedSystemObject, bool> predicate,
        Expression<Func<ConnectedSystemObject, bool>> mark, bool value) =>
        Apply(context, predicate, mark, _ => value);

    /// <summary>
    /// As the mark overload, for a column whose written value can differ from one object to the next.
    /// </summary>
    /// <param name="context">The context whose tracked instances to bring into line.</param>
    /// <param name="predicate">Which tracked objects the raw write wrote.</param>
    /// <param name="column">The column it wrote.</param>
    /// <param name="valueFor">The value it wrote for an object the predicate matched.</param>
    internal static void Apply<TValue>(DbContext context, Func<ConnectedSystemObject, bool> predicate,
        Expression<Func<ConnectedSystemObject, TValue>> column, Func<ConnectedSystemObject, TValue> valueFor)
    {
        var autoDetectChanges = context.ChangeTracker.AutoDetectChangesEnabled;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = context.ChangeTracker.Entries<ConnectedSystemObject>()
                .Where(e => e.State != EntityState.Detached && e.State != EntityState.Added && predicate(e.Entity))
                .ToList();

            foreach (var (property, value) in entries.Select(entry => (entry.Property(column), valueFor(entry.Entity))))
            {
                property.CurrentValue = value;
                property.OriginalValue = value;
                property.IsModified = false;
            }
        }
        finally
        {
            context.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }
}
