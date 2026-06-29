// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

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
            ItemId = newNode.ItemId
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
            // Match items by stable database id so the diff is stable across reordering.
            var oldById = oldChildren
                .Where(c => c.ItemId.HasValue)
                .GroupBy(c => c.ItemId!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            var matchedIds = new HashSet<int>();

            foreach (var newChild in newChildren)
            {
                if (newChild.ItemId.HasValue && oldById.TryGetValue(newChild.ItemId.Value, out var oldMatch))
                {
                    matchedIds.Add(newChild.ItemId.Value);
                    result.Add(DiffNode(oldMatch, newChild));
                }
                else
                {
                    result.Add(DiffNode(null, newChild));
                }
            }

            foreach (var oldChild in oldChildren.Where(c => !c.ItemId.HasValue || !matchedIds.Contains(c.ItemId.Value)))
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

    private ConfigurationDiffNode MapSubtree(ConfigurationSnapshotNode source, ConfigurationDiffChangeType changeType)
    {
        var node = new ConfigurationDiffNode
        {
            Key = source.Key,
            Label = source.Label,
            NodeType = source.NodeType,
            IsSecret = source.IsSecret,
            ItemId = source.ItemId,
            ChangeType = changeType
        };

        if (source.NodeType == ConfigurationSnapshotNodeType.Scalar && !source.IsSecret)
        {
            if (changeType == ConfigurationDiffChangeType.Added)
                node.NewValue = source.Value;
            else
                node.OldValue = source.Value;
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
                if (node.ItemId.HasValue || node.NodeType == ConfigurationSnapshotNodeType.Scalar)
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
