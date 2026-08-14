// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Utilities;

/// <summary>
/// Turns the direct object counts a Connector reports into the figures a Container row shows (#1276).
/// </summary>
/// <remarks>
/// The Connector counts what its own search returned, bucketed by the Container each object sits directly in, and
/// hands back one number per Container identifier. Everything read off a Container row is derived here: its own
/// bucket becomes <see cref="ConnectedSystemContainer.ObjectCount"/>, and that plus every descendant's becomes
/// <see cref="ConnectedSystemContainer.SubtreeObjectCount"/>, which is what a
/// <see cref="ConnectedSystemContainerScope.Subtree"/> statement over it reaches.
///
/// Deliberately blind to selections and exclusions. The figure states what the Connected System holds, which is the
/// question being asked while the selection is still being decided. What JIM would actually import once exclusions
/// apply is the Configuration Change Preview's answer (#1251); subtracting an exclusion here would put two
/// different numbers for the same thing on one screen.
/// </remarks>
public static class ContainerObjectCounts
{
    /// <summary>
    /// Applies a Connector's direct counts to a partition's Container hierarchy, and rolls up each Container's
    /// subtree total.
    /// </summary>
    /// <param name="connectedSystemPartition">The partition whose hierarchy is being counted.</param>
    /// <param name="directCountsByContainerIdentifier">
    /// One count per Container identifier, in the Connector's own terms. Null where the Connector cannot report
    /// counts, which leaves every Container uncounted rather than reporting zero: a column of zeroes reads as "every
    /// Container is empty", which is a different and wrong statement.
    ///
    /// A count keyed on an identifier that is not in the hierarchy is not discarded. A Connector may return objects
    /// sitting beneath a Container it does not publish as part of the tree (a hidden or system container), and
    /// dropping those would leave an ancestor's subtree total disagreeing with what an import from it brings back;
    /// they are rolled into the nearest ancestor that is in the hierarchy instead.
    /// </param>
    public static void Apply(
        ConnectedSystemPartition connectedSystemPartition,
        IReadOnlyDictionary<string, int>? directCountsByContainerIdentifier)
    {
        ArgumentNullException.ThrowIfNull(connectedSystemPartition);

        var containers = connectedSystemPartition.Containers ?? [];

        if (directCountsByContainerIdentifier == null)
        {
            foreach (var rootContainer in containers)
                ClearCounts(rootContainer);

            return;
        }

        // A directory's identifiers are case-insensitive, and a Connector has no reason to normalise the case it
        // reports against the case JIM stored when it discovered the Container.
        var remaining = new Dictionary<string, int>(directCountsByContainerIdentifier, StringComparer.OrdinalIgnoreCase);

        foreach (var rootContainer in containers)
            ApplyDirectCounts(rootContainer, remaining);

        // Whatever is left is keyed on a Container the hierarchy does not hold. Each one belongs to the deepest
        // Container that is an ancestor of it, and to nothing at all when no Container in this partition is.
        var beneathUnknownContainers = AttributeToNearestAncestors(containers, remaining);

        foreach (var rootContainer in containers)
            RollUp(rootContainer, beneathUnknownContainers);
    }

    private static void ClearCounts(ConnectedSystemContainer container)
    {
        container.ObjectCount = null;
        container.SubtreeObjectCount = null;

        foreach (var childContainer in container.ChildContainers)
            ClearCounts(childContainer);
    }

    /// <summary>
    /// Sets each Container's own bucket, removing it from <paramref name="remaining"/> so that what survives is
    /// exactly the set of counts belonging to Containers the hierarchy does not hold.
    /// </summary>
    private static void ApplyDirectCounts(ConnectedSystemContainer container, Dictionary<string, int> remaining)
    {
        // A Container the Connector searched and reported nothing for holds nothing, which is a determined answer
        // and a different statement from "not counted".
        container.ObjectCount = remaining.Remove(container.ExternalId, out var count) ? count : 0;

        foreach (var childContainer in container.ChildContainers)
            ApplyDirectCounts(childContainer, remaining);
    }

    /// <summary>
    /// Attributes each count whose Container is not in the hierarchy to the deepest Container that is an ancestor
    /// of it, so the objects still reach every subtree total they belong to. A count with no ancestor in this
    /// partition is dropped; it belongs to some other part of the Connected System entirely.
    /// </summary>
    /// <returns>
    /// How much each Container gains from Containers beneath it that the hierarchy does not hold. Kept apart from
    /// <see cref="ConnectedSystemContainer.ObjectCount"/>, which states what sits <i>directly</i> in a Container:
    /// these objects do not, and adding them there would misreport a One Level row.
    /// </returns>
    private static Dictionary<ConnectedSystemContainer, int> AttributeToNearestAncestors(
        IEnumerable<ConnectedSystemContainer> containers,
        Dictionary<string, int> unplaced)
    {
        var attributed = new Dictionary<ConnectedSystemContainer, int>();
        if (unplaced.Count == 0)
            return attributed;

        var everyContainer = Flatten(containers).ToList();

        foreach (var (identifier, count) in unplaced)
        {
            // Among the Containers that contain this identifier, the deepest is the one with the longest
            // identifier: containment is a suffix relationship, so any two ancestors of the same object are
            // themselves nested, and the longer identifier is the lower of the two.
            var nearest = everyContainer
                .Where(container => IsWithin(identifier, container.ExternalId))
                .MaxBy(container => container.ExternalId.Length);

            if (nearest != null)
                attributed[nearest] = attributed.GetValueOrDefault(nearest) + count;
        }

        return attributed;
    }

    private static IEnumerable<ConnectedSystemContainer> Flatten(IEnumerable<ConnectedSystemContainer> containers)
    {
        foreach (var container in containers)
        {
            yield return container;

            foreach (var descendant in Flatten(container.ChildContainers))
                yield return descendant;
        }
    }

    /// <summary>
    /// Whether one identifier sits beneath another, compared on the separator boundary so that
    /// <c>ou=NotCorp,dc=x</c> is not mistaken for a descendant of <c>ou=Corp,dc=x</c>.
    /// </summary>
    /// <remarks>
    /// A deliberately coarse rule, and not a substitute for <c>IConnectorContainment</c>: it decides only which
    /// ancestor an already-counted object contributes its subtree total to, never whether an object is in scope.
    /// Getting it wrong moves a number between two Containers on one screen; getting containment wrong decides what
    /// an import returns.
    /// </remarks>
    private static bool IsWithin(string identifier, string? ancestorIdentifier) =>
        !string.IsNullOrEmpty(ancestorIdentifier) &&
        identifier.Length > ancestorIdentifier.Length &&
        identifier.EndsWith("," + ancestorIdentifier, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Adds every descendant's total into each Container's, depth first so that a child is complete before its
    /// parent reads it.
    /// </summary>
    private static int RollUp(
        ConnectedSystemContainer container,
        Dictionary<ConnectedSystemContainer, int> beneathUnknownContainers)
    {
        var total = (container.ObjectCount ?? 0) + beneathUnknownContainers.GetValueOrDefault(container);

        foreach (var childContainer in container.ChildContainers)
            total += RollUp(childContainer, beneathUnknownContainers);

        container.SubtreeObjectCount = total;
        return total;
    }
}
