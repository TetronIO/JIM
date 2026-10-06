// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Search;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Loads a Predefined Search's criteria tree at whatever depth it was built, and resolves the criteria within the
/// trees of a set of searches. The one place that walks the tree from the database, so no reader can quietly assume a
/// depth.
/// </summary>
/// <remarks>
/// A criteria group can hold child groups at any depth: the REST API and PowerShell add a child under any group of a
/// search, and the portal edits any depth. Include chains cannot express "every level", and the loaders used to spell
/// out two; a group below that was absent when the search ran, and its parent then rendered as an empty group, which
/// matches everything, so the search returned objects its criteria exclude. Only top-level groups carry the owning
/// search's id (<see cref="PredefinedSearchCriteriaGroup.PredefinedSearchId"/>); every other group carries only its
/// parent (<see cref="PredefinedSearchCriteriaGroup.ParentGroupId"/>), so the tree is walked level by level from the
/// top. Trees are a handful of levels deep in practice, so this costs a few small queries per load. The Synchronisation
/// Rule equivalent is <see cref="SyncRuleScopingTreeLoader"/>.
/// </remarks>
internal static class PredefinedSearchCriteriaTreeLoader
{
    /// <summary>
    /// Loads the complete criteria tree of <paramref name="search"/>, with every criterion's Metaverse Attribute, onto
    /// <see cref="PredefinedSearch.CriteriaGroups"/>. Siblings are ordered by position at every level.
    /// </summary>
    /// <param name="db">The context the search was loaded from.</param>
    /// <param name="search">The search to complete. Callers must not have included its groups themselves.</param>
    /// <remarks>
    /// The groups follow the search's own tracking: a search loaded on a context that tracks by default (the Worker's
    /// does) gets tracked groups, because untracked groups hung off a tracked search look like new rows to EF, and the
    /// next save would insert the tree again.
    /// </remarks>
    internal static async Task LoadAsync(JimDbContext db, PredefinedSearch search)
    {
        var groupIds = await GetTreeGroupIdsAsync(db, [search.Id]);
        if (groupIds.Count == 0)
            return;

        var query = db.PredefinedSearchCriteriaGroups
            .AsSplitQuery()
            .Include(g => g.Criteria.OrderBy(c => c.Id))
                .ThenInclude(c => c.MetaverseAttribute)
            .Where(g => groupIds.Contains(g.Id));
        var tracked = db.Entry(search).State != EntityState.Detached;
        query = tracked ? query.AsTracking() : query.AsNoTracking();

        var groupsById = (await query.ToListAsync()).ToDictionary(g => g.Id);

        // A tracked load has already been fixed up by EF from the foreign keys, so every attach is guarded to stay
        // idempotent; ordering is applied afterwards for the same reason.
        foreach (var group in groupsById.Values)
        {
            if (group.ParentGroupId is { } parentId)
            {
                var parent = groupsById[parentId];
                group.ParentGroup = parent;
                if (!parent.ChildGroups.Contains(group))
                    parent.ChildGroups.Add(group);
            }
            else if (!search.CriteriaGroups.Contains(group))
            {
                search.CriteriaGroups.Add(group);
            }
        }

        search.CriteriaGroups.Sort(BySiblingOrder);
        foreach (var group in groupsById.Values)
            group.ChildGroups.Sort(BySiblingOrder);
    }

    /// <summary>
    /// Returns the ids of every criterion, at any depth of the given searches' trees, that filters on
    /// <paramref name="metaverseAttributeId"/>.
    /// </summary>
    internal static async Task<List<int>> GetCriterionIdsAsync(JimDbContext db, IReadOnlyCollection<int> predefinedSearchIds, int metaverseAttributeId)
    {
        var groupIds = await GetTreeGroupIdsAsync(db, predefinedSearchIds);
        if (groupIds.Count == 0)
            return [];

        return await db.PredefinedSearchCriteria
            .AsNoTracking()
            .Where(c => c.MetaverseAttributeId == metaverseAttributeId
                && c.PredefinedSearchCriteriaGroupId != null
                && groupIds.Contains(c.PredefinedSearchCriteriaGroupId.Value))
            .Select(c => c.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Walks the group tree of each search from its top-level groups down, returning the id of every group in it. Ids
    /// and foreign keys only; no group entities are materialised.
    /// </summary>
    private static async Task<List<int>> GetTreeGroupIdsAsync(JimDbContext db, IReadOnlyCollection<int> predefinedSearchIds)
    {
        if (predefinedSearchIds.Count == 0)
            return [];

        var searchIds = predefinedSearchIds.ToList();
        var frontier = await db.PredefinedSearchCriteriaGroups
            .AsNoTracking()
            .Where(g => g.PredefinedSearchId != null && searchIds.Contains(g.PredefinedSearchId.Value))
            .Select(g => g.Id)
            .ToListAsync();
        var groupIds = new HashSet<int>(frontier);

        // Every level adds groups not seen before or fails, so the walk ends however the rows are linked; a cycle
        // (which the model can never legitimately produce) fails loudly rather than looping.
        while (frontier.Count > 0)
        {
            var parentIds = frontier;
            var children = await db.PredefinedSearchCriteriaGroups
                .AsNoTracking()
                .Where(g => g.ParentGroupId != null && parentIds.Contains(g.ParentGroupId.Value))
                .Select(g => g.Id)
                .ToListAsync();

            var revisited = children.Where(groupIds.Contains).ToList();
            if (revisited.Count > 0)
                throw new InvalidOperationException(
                    $"Predefined Search criteria group {revisited[0]} is reachable twice; the group graph must contain a cycle.");

            groupIds.UnionWith(children);
            frontier = children;
        }

        return groupIds.ToList();
    }

    private static int BySiblingOrder(PredefinedSearchCriteriaGroup a, PredefinedSearchCriteriaGroup b) =>
        a.Position != b.Position ? a.Position.CompareTo(b.Position) : a.Id.CompareTo(b.Id);
}
