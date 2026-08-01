// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData;

/// <summary>
/// Guards a load-mutate-save repository path against silent data loss.
///
/// Three of JIM's four hosts run the DbContext with <see cref="QueryTrackingBehavior.NoTracking"/>: JIM.Web
/// (Program.cs), JIM.Scheduler (Program.cs), and <see cref="JimDbContext"/>'s own default. Only JIM.Worker tracks by
/// default. So a repository method that loads an entity, mutates it and calls SaveChanges works perfectly from the sync
/// engine and does *nothing at all* from the portal, while reporting success: no exception, no log, no row written. The
/// same happens when a caller hands over an entity it loaded on an earlier, now-disposed context.
///
/// That failure mode has been paid for three times now: a Synchronisation Rule's Enabled toggle silently reverting
/// (<c>ConnectedSystemRepository.UpdateSyncRuleAsync</c>), Predefined Search criteria (<c>SearchRepository</c>), and a
/// Metaverse Object Type's Deletion Rules (<c>MetaverseRepository.UpdateMetaverseObjectTypeAsync</c>). It is invisible
/// to the unit suite, because the in-memory provider is configured to track. Only a real database, driven through the
/// portal, shows it, and even then only by the absence of a change.
///
/// Every mutating repository method that relies on the change tracker must therefore either load with an explicit
/// <c>AsTracking()</c> or accept an entity the caller loaded tracked, and then assert that contract here. Fast, loud
/// failure over silent data loss, per the Synchronisation Integrity rules.
/// </summary>
internal static class TrackedEntityGuard
{
    /// <summary>
    /// Throws when <paramref name="entity"/> is not tracked by <paramref name="context"/>, and so would be silently
    /// discarded by the next SaveChanges.
    /// </summary>
    /// <param name="context">The context the change will be saved on.</param>
    /// <param name="entity">The entity about to be mutated.</param>
    /// <param name="operation">The calling method's name, for the message.</param>
    /// <param name="remedy">
    /// What the caller should do instead, named concretely (which retrieval method tracks, or which query needs
    /// AsTracking). A guard that only says "it is detached" leaves the next person to work out the fix.
    /// </param>
    internal static void RequireTracked<T>(this JimDbContext context, T entity, string operation, string remedy)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);

        if (context.Entry(entity).State != EntityState.Detached)
            return;

        throw new InvalidOperationException(
            $"{operation} requires a change-tracked {typeof(T).Name}, but the supplied instance is detached from " +
            $"this DbContext, so no changes would be persisted. {remedy}");
    }
}
