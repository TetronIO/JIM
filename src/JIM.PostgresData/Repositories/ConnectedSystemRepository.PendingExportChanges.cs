// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Reading many Pending Exports' changes at once (#2884).
/// </summary>
/// <remarks>
/// <para>
/// A load that includes <see cref="PendingExport.AttributeValueChanges"/> reaches the changes through a join from
/// <c>PendingExports</c>, so how it reads them is left to the planner's statistics. Those are routinely stale on exactly the
/// paths that read changes most: an Export removes the changes it executes, autovacuum analyses the table holding pages
/// but no live row, and from then on the planner takes it to hold one row however many the next synchronisation stages.
/// Any plan reads one row as cheaply as another, so the planner is free to read the whole table to keep the few the load
/// wants: on a 500,000-user leaver step the Export's batch load read all 41,281 changes through the
/// <c>PendingExportId</c> index with no index condition, once per batch of 100.
/// </para>
/// <para>
/// So the changes are read on their own, from one table, with a condition on their own <c>PendingExportId</c> and no join
/// for the planner to order: reading the table or another of its indexes whole then costs more than seeking the
/// <c>PendingExportId</c> index by value, whatever the row estimate. Their attributes are a second statement, by primary
/// key. A join to the attributes is what let a merge join read the changes in attribute order instead.
/// </para>
/// </remarks>
public partial class ConnectedSystemRepository
{
    /// <summary>
    /// The Pending Exports a load reads, with their changes Included only where the context runs no SQL: the EF test
    /// doubles hold each export's changes on it already and have no changes table to read, so they are handed the changes
    /// through the Include, as before. Everywhere else the changes are read on their own
    /// (<see cref="GetPendingExportChangesAsync"/>).
    /// </summary>
    internal static IQueryable<PendingExport> WithChangesForTestDoubles(JimDbContext context, IQueryable<PendingExport> pendingExports) =>
        RunsSql(context)
            ? pendingExports
            : pendingExports.Include(pe => pe.AttributeValueChanges).ThenInclude(avc => avc.Attribute);

    private static bool RunsSql(JimDbContext context) => context.Database?.IsRelational() == true;

    /// <summary>
    /// The changes of the given Pending Exports, and the attributes they name with the id of the Object Type holding each.
    /// The changes' <see cref="PendingExportAttributeValueChange.Attribute"/> is set from those attributes, one instance
    /// per attribute; the changes are not yet on their exports (<see cref="AttachPendingExportChanges"/>). None on a context
    /// that runs no SQL, whose exports were handed theirs (<see cref="WithChangesForTestDoubles"/>).
    /// </summary>
    /// <param name="context">The context to read on.</param>
    /// <param name="pendingExportIds">The Pending Exports whose changes to read.</param>
    /// <param name="tracking">How the changes and attributes are tracked: the context's own behaviour when null.</param>
    /// <remarks>
    /// Each export's changes come in id order, so a Connector applying an export's changes in sequence sees them in one
    /// order every time: the order the single-query Includes these loads replace returned them in (they ordered by the
    /// change's key after the export's). The export batch's split Include never fixed the order within an export, and
    /// returned whatever its plan produced. Ordered by Pending Export first, the read stays an index scan on
    /// <c>PendingExportId</c> by value, with only a batch's changes to sort.
    /// </remarks>
    internal static async Task<(List<PendingExportAttributeValueChange> Changes, Dictionary<int, (ConnectedSystemObjectTypeAttribute Attribute, int ObjectTypeId)> Attributes)>
        GetPendingExportChangesAsync(JimDbContext context, IEnumerable<Guid> pendingExportIds, QueryTrackingBehavior? tracking)
    {
        var ids = pendingExportIds.Select(id => (Guid?)id).Distinct().ToArray();
        if (ids.Length == 0 || !RunsSql(context))
            return ([], []);

        var behaviour = tracking ?? context.ChangeTracker.QueryTrackingBehavior;

        var changes = await context.PendingExportAttributeValueChanges
            .AsTracking(behaviour)
            .Where(change => ids.Contains(change.PendingExportId))
            .OrderBy(change => change.PendingExportId)
            .ThenBy(change => change.Id)
            .ToListAsync();
        if (changes.Count == 0)
            return (changes, []);

        var attributeIds = changes.Select(change => change.AttributeId).Distinct().ToArray();
        var attributes = (await context.ConnectedSystemAttributes
                .AsTracking(behaviour)
                .Where(attribute => attributeIds.Contains(attribute.Id))
                .Select(attribute => new { Attribute = attribute, ObjectTypeId = attribute.ConnectedSystemObjectType.Id })
                .ToListAsync())
            .DistinctBy(row => row.Attribute.Id)
            .ToDictionary(row => row.Attribute.Id, row => (row.Attribute, row.ObjectTypeId));

        // A change's attribute is required (an inner join under the Include), so one the lookup does not hold cannot arise.
        foreach (var change in changes)
            change.Attribute = attributes[change.AttributeId].Attribute;

        return (changes, attributes);
    }

    /// <summary>
    /// Hangs changes on their Pending Exports, in the order given, each once: a change already in its export's collection
    /// (as EF Core's fix-up puts a tracked change on its tracked export) is left where it is.
    /// </summary>
    internal static void AttachPendingExportChanges(IEnumerable<PendingExport> pendingExports, IEnumerable<PendingExportAttributeValueChange> changes)
    {
        var changesByPendingExportId = changes
            .Where(change => change.PendingExportId.HasValue)
            .GroupBy(change => change.PendingExportId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var pendingExport in pendingExports.Where(pe => changesByPendingExportId.ContainsKey(pe.Id)))
        {
            var held = pendingExport.AttributeValueChanges;
            foreach (var change in changesByPendingExportId[pendingExport.Id].Where(change => !held.Contains(change)))
                held.Add(change);
        }
    }
}
