// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using Serilog;

namespace JIM.Application.Services;

/// <summary>
/// Computes a structured diff tree between two configuration snapshots. Scalars are compared by value; child
/// collections are matched by stable database id (so a diff is stable across reordering), and matched objects are
/// recursed into. Secret scalars are compared by their keyed hash and reported only as a change type, never by value.
/// </summary>
public class ConfigurationDiffService
{
    internal ConfigurationDiffService()
    {
    }

    /// <summary>
    /// Diffs <paramref name="newSnapshot"/> against <paramref name="oldSnapshot"/>. When <paramref name="oldSnapshot"/>
    /// is null (the first version of an object) every node is reported as added. The version arguments are carried onto
    /// the result for display and are otherwise unused by the comparison.
    /// </summary>
    public ConfigurationDiff Diff(ConfigurationSnapshot? oldSnapshot, ConfigurationSnapshot newSnapshot, int? oldVersion = null, int? newVersion = null)
    {
        ArgumentNullException.ThrowIfNull(newSnapshot);

        var root = DiffNode(oldSnapshot?.Root, newSnapshot.Root);
        var counts = new DiffCounts();
        Tally(root, counts);

        return new ConfigurationDiff
        {
            ObjectType = newSnapshot.ObjectType,
            ObjectId = newSnapshot.ObjectId,
            ObjectGuidId = newSnapshot.ObjectGuidId,
            ObjectName = newSnapshot.ObjectName,
            OldVersion = oldVersion,
            NewVersion = newVersion,
            Root = root,
            AddedCount = counts.Added,
            RemovedCount = counts.Removed,
            ModifiedCount = counts.Modified
        };
    }

    /// <summary>
    /// Produces a whole-object "removed" diff for a deletion tombstone: the object's final captured state, rendered as
    /// deleted (every field present and marked removed), with no successor to compare against. A delete records an
    /// unversioned tombstone snapshot on its Activity rather than a versioned entry against the object, so this lets the
    /// Activity detail page surface what the deleted object looked like. The public <see cref="Diff"/> requires a
    /// non-null new snapshot (it models creation, old-null, but not deletion), so this is a dedicated entry point.
    /// </summary>
    public ConfigurationDiff DiffDeletion(ConfigurationSnapshot deletedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(deletedSnapshot);

        // Old side present, new side absent => the whole subtree is Removed.
        var root = DiffNode(deletedSnapshot.Root, null);
        var counts = new DiffCounts();
        Tally(root, counts);

        return new ConfigurationDiff
        {
            ObjectType = deletedSnapshot.ObjectType,
            ObjectId = deletedSnapshot.ObjectId,
            ObjectGuidId = deletedSnapshot.ObjectGuidId,
            ObjectName = deletedSnapshot.ObjectName,
            OldVersion = null,
            NewVersion = null,
            Root = root,
            AddedCount = counts.Added,
            RemovedCount = counts.Removed,
            ModifiedCount = counts.Modified
        };
    }

    /// <summary>
    /// Produces a short, human-readable one-line summary of a diff (for change-history list rows). Reports "Created" or
    /// "Deleted" for whole-object additions/removals, otherwise the changed top-level sections, or "No changes".
    /// </summary>
    public static string Summarise(ConfigurationDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (diff.Root.ChangeType == ConfigurationDiffChangeType.Added)
            return "Created";
        if (diff.Root.ChangeType == ConfigurationDiffChangeType.Removed)
            return "Deleted";

        var changedSections = (diff.Root.Children ?? [])
            .Where(c => c.ChangeType != ConfigurationDiffChangeType.Unchanged)
            .Select(c => c.Label ?? c.Key)
            .ToList();

        return changedSections.Count == 0 ? "No changes" : string.Join(", ", changedSections);
    }

    private ConfigurationDiffNode DiffNode(ConfigurationSnapshotNode? oldNode, ConfigurationSnapshotNode? newNode)
    {
        // Whole-subtree add/remove: one side is absent.
        if (oldNode == null)
            return MapSubtree(newNode!, ConfigurationDiffChangeType.Added);
        if (newNode == null)
            return MapSubtree(oldNode, ConfigurationDiffChangeType.Removed);

        var node = new ConfigurationDiffNode
        {
            Key = newNode.Key,
            Label = newNode.Label,
            NodeType = newNode.NodeType,
            IsSecret = newNode.IsSecret,
            ItemId = newNode.ItemId,
            ItemGuidId = newNode.ItemGuidId
        };

        if (newNode.NodeType == ConfigurationSnapshotNodeType.Scalar)
        {
            var changed = !string.Equals(oldNode.Value, newNode.Value, StringComparison.Ordinal);
            node.ChangeType = changed ? ConfigurationDiffChangeType.Modified : ConfigurationDiffChangeType.Unchanged;
            // Never expose secret material: a secret change is reported by ChangeType alone.
            if (!newNode.IsSecret)
            {
                node.OldValue = oldNode.Value;
                node.NewValue = newNode.Value;
                node.OldDisplayValue = oldNode.DisplayValue;
                node.NewDisplayValue = newNode.DisplayValue;
            }
            return node;
        }

        node.Children = DiffChildren(oldNode, newNode);
        node.ChangeType = node.Children.Any(c => c.ChangeType != ConfigurationDiffChangeType.Unchanged)
            ? ConfigurationDiffChangeType.Modified
            : ConfigurationDiffChangeType.Unchanged;
        return node;
    }

    private List<ConfigurationDiffNode> DiffChildren(ConfigurationSnapshotNode oldNode, ConfigurationSnapshotNode newNode)
    {
        var oldChildren = oldNode.Children ?? [];
        var newChildren = newNode.Children ?? [];
        var result = new List<ConfigurationDiffNode>();

        if (newNode.NodeType == ConfigurationSnapshotNodeType.Collection)
        {
            // Match items by stable database id (integer or Guid) so the diff is stable across reordering. A Guid item
            // id is used where no unique integer key exists (e.g. a Schedule Step, whose StepIndex is not unique).
            var oldGroups = oldChildren
                .Where(c => ItemKey(c) != null)
                .GroupBy(c => ItemKey(c)!)
                .ToList();

            // Item keys are expected to be unique within a collection (they are database ids). If a snapshot ever
            // carries duplicates, matching degrades to first-wins and can pair the wrong items; surface that rather
            // than failing silently.
            foreach (var duplicate in oldGroups.Where(g => g.Count() > 1))
                Log.Warning("ConfigurationDiffService: duplicate item key {ItemKey} in collection {CollectionKey}; diff matching may pair the wrong items.", duplicate.Key, oldNode.Key);

            var oldById = oldGroups.ToDictionary(g => g.Key, g => g.First());
            var matchedIds = new HashSet<object>();

            foreach (var newChild in newChildren)
            {
                var key = ItemKey(newChild);
                if (key != null && oldById.TryGetValue(key, out var oldMatch))
                {
                    matchedIds.Add(key);
                    result.Add(DiffNode(oldMatch, newChild));
                }
                else
                {
                    result.Add(DiffNode(null, newChild));
                }
            }

            foreach (var oldChild in oldChildren.Where(c => ItemKey(c) is not { } k || !matchedIds.Contains(k)))
                result.Add(DiffNode(oldChild, null));
        }
        else
        {
            // Match object members by key.
            var oldByKey = oldChildren.GroupBy(c => c.Key).ToDictionary(g => g.Key, g => g.First());
            var newKeys = new HashSet<string>(newChildren.Select(c => c.Key));

            foreach (var newChild in newChildren)
            {
                oldByKey.TryGetValue(newChild.Key, out var oldMatch);
                result.Add(DiffNode(oldMatch, newChild));
            }

            foreach (var oldChild in oldChildren.Where(c => !newKeys.Contains(c.Key)))
                result.Add(DiffNode(oldChild, null));
        }

        return result;
    }

    // The stable identity of a collection item: its Guid id where the collection is Guid-keyed, otherwise its integer
    // id. Null when the node is not a collection item. Boxed so a single dictionary can key either kind (a given
    // collection is uniformly one or the other, so there is no cross-type collision).
    private static object? ItemKey(ConfigurationDiffNode node) => (object?)node.ItemGuidId ?? node.ItemId;

    private static object? ItemKey(ConfigurationSnapshotNode node) => (object?)node.ItemGuidId ?? node.ItemId;

    private ConfigurationDiffNode MapSubtree(ConfigurationSnapshotNode source, ConfigurationDiffChangeType changeType)
    {
        var node = new ConfigurationDiffNode
        {
            Key = source.Key,
            Label = source.Label,
            NodeType = source.NodeType,
            IsSecret = source.IsSecret,
            ItemId = source.ItemId,
            ItemGuidId = source.ItemGuidId,
            ChangeType = changeType
        };

        if (source.NodeType == ConfigurationSnapshotNodeType.Scalar && !source.IsSecret)
        {
            if (changeType == ConfigurationDiffChangeType.Added)
            {
                node.NewValue = source.Value;
                node.NewDisplayValue = source.DisplayValue;
            }
            else
            {
                node.OldValue = source.Value;
                node.OldDisplayValue = source.DisplayValue;
            }
        }

        if (source.Children is { Count: > 0 })
            node.Children = source.Children.Select(c => MapSubtree(c, changeType)).ToList();

        return node;
    }

    private static void Tally(ConfigurationDiffNode node, DiffCounts counts)
    {
        switch (node.ChangeType)
        {
            case ConfigurationDiffChangeType.Modified:
                if (node.NodeType == ConfigurationSnapshotNodeType.Scalar)
                    counts.Modified++;
                TallyChildren(node, counts);
                break;

            case ConfigurationDiffChangeType.Added:
            case ConfigurationDiffChangeType.Removed:
                // Count a collection item or a scalar once; do not recurse into the rest of the added/removed subtree.
                // A structural container (no item id) that appeared/vanished is counted by its contained items instead.
                if (node.ItemId.HasValue || node.ItemGuidId.HasValue || node.NodeType == ConfigurationSnapshotNodeType.Scalar)
                {
                    if (node.ChangeType == ConfigurationDiffChangeType.Added) counts.Added++; else counts.Removed++;
                }
                else
                {
                    TallyChildren(node, counts);
                }
                break;

            case ConfigurationDiffChangeType.Unchanged:
                TallyChildren(node, counts);
                break;
        }
    }

    private static void TallyChildren(ConfigurationDiffNode node, DiffCounts counts)
    {
        if (node.Children == null)
            return;
        foreach (var child in node.Children)
            Tally(child, counts);
    }

    private sealed class DiffCounts
    {
        public int Added;
        public int Removed;
        public int Modified;
    }
}
