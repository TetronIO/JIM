// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData.Repositories;

/// <summary>
/// Loads Synchronisation Rules' scoping criteria trees at whatever depth they were built, and resolves which rule owns
/// each criterion. The one place that walks the tree from the database, so no reader can quietly assume a depth.
/// </summary>
/// <remarks>
/// The criteria editor lets a group hold child groups at any depth. Include chains cannot express "every level", and
/// the loaders used to spell out two; a group below that was absent at evaluation time, and its parent then evaluated
/// as an empty group, which counts as met, so the rule's scope silently widened. Only top-level groups carry the
/// owning rule's id (the shadow <c>SyncRuleId</c>); every other group carries only its parent (<c>ParentGroupId</c>),
/// so the tree is walked level by level from the top. Trees are a handful of levels deep in practice, so this costs a
/// few small queries per load, independent of how many rules are loaded.
/// </remarks>
internal static class SyncRuleScopingTreeLoader
{
    /// <summary>
    /// Far beyond any tree an administrator would build; reaching it means the group graph holds a cycle, which the
    /// model can never legitimately produce, so the load fails loudly rather than looping.
    /// </summary>
    private const int MaximumDepth = 64;

    /// <summary>
    /// Loads the complete scoping criteria tree of each rule, with every criterion's Metaverse or Connected System
    /// attribute, onto <see cref="SyncRule.ObjectScopingCriteriaGroups"/>.
    /// </summary>
    /// <param name="db">The context the rules were loaded from.</param>
    /// <param name="rules">The rules to complete. Callers must not have included the groups themselves.</param>
    /// <param name="asTrackingRequested">Whether the rules' query asked for tracking explicitly. The groups follow the
    /// rules: tracked when the query asked for it or the context tracks by default (the Worker's does), untracked
    /// otherwise. Tracked groups are what lets the editor's load, mutate, save pattern persist edits anywhere in the
    /// tree; untracked groups hung off a tracked rule would look like new rows to EF, and collide with the tracked
    /// attributes their criteria point at.</param>
    internal static async Task LoadAsync(JimDbContext db, IReadOnlyCollection<SyncRule> rules, bool asTrackingRequested)
    {
        if (rules.Count == 0)
            return;

        var tracked = asTrackingRequested || db.ChangeTracker.QueryTrackingBehavior == QueryTrackingBehavior.TrackAll;

        var placements = await GetGroupPlacementsAsync(db, rules.Select(r => r.Id).ToList());
        if (placements.Count == 0)
            return;

        var groupIds = placements.Keys.ToList();
        var query = db.SyncRuleScopingCriteriaGroups
            .AsSplitQuery()
            .Include(g => g.Criteria.OrderBy(c => c.Id))
                .ThenInclude(c => c.MetaverseAttribute)
            .Include(g => g.Criteria.OrderBy(c => c.Id))
                .ThenInclude(c => c.ConnectedSystemAttribute)
            .Where(g => groupIds.Contains(g.Id));
        query = tracked ? query.AsTracking() : query.AsNoTracking();

        var groupsById = (await query.ToListAsync()).ToDictionary(g => g.Id);
        var rulesById = rules.ToDictionary(r => r.Id);

        // Assemble top-down in a stable order. A tracked load has already been fixed up by EF from the foreign keys,
        // and a tree may have been loaded into this context before, so every attach is guarded to stay idempotent.
        foreach (var (groupId, placement) in placements.OrderBy(p => p.Value.Depth).ThenBy(p => p.Value.Position).ThenBy(p => p.Key))
        {
            var group = groupsById[groupId];
            if (placement.ParentGroupId is { } parentId)
            {
                var parent = groupsById[parentId];
                group.ParentGroup = parent;
                if (!parent.ChildGroups.Contains(group))
                    parent.ChildGroups.Add(group);
            }
            else
            {
                var rule = rulesById[placement.SyncRuleId];
                if (!rule.ObjectScopingCriteriaGroups.Contains(group))
                    rule.ObjectScopingCriteriaGroups.Add(group);
            }
        }
    }

    /// <summary>
    /// Returns every scoping criterion, at any depth, belonging to the given rules, with the rule that owns it.
    /// </summary>
    /// <param name="db">The context to query.</param>
    /// <param name="syncRuleIds">The rules whose criteria to return.</param>
    /// <param name="metaverseAttributeId">When supplied, only criteria evaluating this Metaverse Attribute.</param>
    internal static async Task<List<ScopingCriterionOwnership>> GetCriterionOwnershipAsync(
        JimDbContext db, IReadOnlyCollection<int> syncRuleIds, int? metaverseAttributeId = null)
    {
        if (syncRuleIds.Count == 0)
            return [];

        var placements = await GetGroupPlacementsAsync(db, syncRuleIds);
        if (placements.Count == 0)
            return [];

        var groupIds = placements.Keys.Select(id => (int?)id).ToList();
        var query = db.SyncRuleScopingCriteria
            .AsNoTracking()
            .Where(c => groupIds.Contains(EF.Property<int?>(c, "SyncRuleScopingCriteriaGroupId")));
        if (metaverseAttributeId.HasValue)
            query = query.Where(c => c.MetaverseAttributeId == metaverseAttributeId.Value);

        var criteria = await query
            .Select(c => new
            {
                c.Id,
                c.MetaverseAttributeId,
                GroupId = EF.Property<int?>(c, "SyncRuleScopingCriteriaGroupId")!.Value
            })
            .ToListAsync();

        return criteria
            .Select(c => new ScopingCriterionOwnership(placements[c.GroupId].SyncRuleId, c.Id, c.MetaverseAttributeId))
            .ToList();
    }

    /// <summary>
    /// Walks the group tree of each rule from its top-level groups down, recording every group's owning rule, parent
    /// and depth. Ids and foreign keys only; no group entities are materialised.
    /// </summary>
    private static async Task<Dictionary<int, GroupPlacement>> GetGroupPlacementsAsync(JimDbContext db, IReadOnlyCollection<int> syncRuleIds)
    {
        var ruleIds = syncRuleIds.Select(id => (int?)id).ToList();
        var topLevel = await db.SyncRuleScopingCriteriaGroups
            .AsNoTracking()
            .Where(g => ruleIds.Contains(EF.Property<int?>(g, "SyncRuleId")))
            .Select(g => new { g.Id, g.Position, SyncRuleId = EF.Property<int?>(g, "SyncRuleId")!.Value })
            .ToListAsync();

        var placements = topLevel.ToDictionary(g => g.Id, g => new GroupPlacement(g.SyncRuleId, null, 1, g.Position));
        var frontier = topLevel.Select(g => (int?)g.Id).ToList();
        var depth = 1;

        while (frontier.Count > 0)
        {
            if (++depth > MaximumDepth)
                throw new InvalidOperationException(
                    $"Scoping criteria groups are nested more than {MaximumDepth} levels deep; the group graph must contain a cycle.");

            var parentIds = frontier;
            var children = await db.SyncRuleScopingCriteriaGroups
                .AsNoTracking()
                .Where(g => parentIds.Contains(EF.Property<int?>(g, "ParentGroupId")))
                .Select(g => new { g.Id, g.Position, ParentGroupId = EF.Property<int?>(g, "ParentGroupId")!.Value })
                .ToListAsync();

            frontier = [];
            foreach (var child in children)
            {
                if (placements.ContainsKey(child.Id))
                    throw new InvalidOperationException(
                        $"Scoping criteria group {child.Id} is reachable twice; the group graph must contain a cycle.");

                placements[child.Id] = new GroupPlacement(placements[child.ParentGroupId].SyncRuleId, child.ParentGroupId, depth, child.Position);
                frontier.Add(child.Id);
            }
        }

        return placements;
    }

    private readonly record struct GroupPlacement(int SyncRuleId, int? ParentGroupId, int Depth, int Position);
}

/// <summary>
/// A scoping criterion and the Synchronisation Rule that owns the tree it sits in.
/// </summary>
internal readonly record struct ScopingCriterionOwnership(int SyncRuleId, int CriterionId, int? MetaverseAttributeId);
