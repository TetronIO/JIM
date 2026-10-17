// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Linq.Expressions;
using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Brings tracked Connected System Objects into line with a raw write of one of their marks (<c>SynchronisationPending</c>,
/// #2092; <c>DerivedInputChangePending</c>, #1750): current and original value both set, so the property reads as
/// persisted rather than as a pending change. The worker's context lives for the whole run, so a raw mark or clear
/// that left a tracked instance stale would mislead whatever reads it later. Change detection is suppressed while
/// enumerating, for the reason <c>SyncRepository.DetachTrackedEntities</c> gives (src/CLAUDE.md, "Tracker surgery must
/// not trigger DetectChanges").
/// </summary>
internal static class ConnectedSystemObjectMarkFixUp
{
    internal static void Apply(DbContext context, Func<ConnectedSystemObject, bool> predicate,
        Expression<Func<ConnectedSystemObject, bool>> mark, bool value)
    {
        var autoDetectChanges = context.ChangeTracker.AutoDetectChangesEnabled;
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var entries = context.ChangeTracker.Entries<ConnectedSystemObject>()
                .Where(e => e.State != EntityState.Detached && e.State != EntityState.Added && predicate(e.Entity))
                .ToList();

            foreach (var property in entries.Select(entry => entry.Property(mark)))
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
