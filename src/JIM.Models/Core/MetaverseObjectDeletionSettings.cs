// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// A Metaverse Object Type's deletion settings, expressed as a value so they can be reasoned about apart from the
/// type that currently holds them. That is what a configuration change preview needs: the same objects evaluated
/// twice, once under the settings in force and once under the settings being proposed.
///
/// The rule these settings encode is deliberately a **scalar function of one object's standing state**, not a
/// query. An object awaiting deletion carries a disconnection date and either has connectors or does not; from
/// those two facts and a settings pair, the date JIM would delete it on is fully determined. Keeping it in one
/// place means the preview and the housekeeping sweep cannot drift into disagreeing, and a preview that disagreed
/// with the housekeeper would be a confident wrong answer about deletions.
/// </summary>
/// <param name="Rule">Which objects the type deletes automatically, if any.</param>
/// <param name="GracePeriod">
/// How long after disconnection deletion waits. Null and <see cref="TimeSpan.Zero"/> mean the same thing here (no
/// wait), matching how the housekeeping sweep reads them.
/// </param>
public record MetaverseObjectDeletionSettings(MetaverseObjectDeletionRule Rule, TimeSpan? GracePeriod)
{
    /// <summary>
    /// The settings a Metaverse Object Type currently holds.
    /// </summary>
    public static MetaverseObjectDeletionSettings From(MetaverseObjectType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return new MetaverseObjectDeletionSettings(type.DeletionRule, type.DeletionGracePeriod);
    }

    /// <summary>
    /// When JIM would delete a Metaverse Object in this state, or null if it never would.
    /// </summary>
    /// <param name="lastConnectorDisconnectedDate">
    /// When the object was marked as disconnected, or null if it never has been. An object with no mark is not on
    /// any path to automatic deletion, whatever the rule says: the mark is what the sweep looks for.
    /// </param>
    /// <param name="hasConnectedSystemObjects">
    /// Whether the object still has joined Connected System Objects. Load-bearing for
    /// <see cref="MetaverseObjectDeletionRule.WhenLastConnectorDisconnected"/> only; the authoritative-source rule
    /// deletes an object whose trigger system has gone even while other systems still hold it.
    /// </param>
    public DateTime? DeletionEligibleAt(DateTime? lastConnectorDisconnectedDate, bool hasConnectedSystemObjects)
    {
        if (Rule == MetaverseObjectDeletionRule.Manual || !lastConnectorDisconnectedDate.HasValue)
            return null;

        if (Rule == MetaverseObjectDeletionRule.WhenLastConnectorDisconnected && hasConnectedSystemObjects)
            return null;

        return GracePeriod is { } grace && grace > TimeSpan.Zero
            ? lastConnectorDisconnectedDate.Value.Add(grace)
            : lastConnectorDisconnectedDate.Value;
    }

    /// <summary>
    /// Whether the housekeeping sweep would delete a Metaverse Object in this state as at <paramref name="asAt"/>.
    /// The boundary is inclusive, matching the sweep: an object whose grace period expires this instant is deleted
    /// on this pass, and a preview that called it safe would be wrong by one sweep.
    /// </summary>
    public bool IsEligibleAt(DateTime? lastConnectorDisconnectedDate, bool hasConnectedSystemObjects, DateTime asAt) =>
        DeletionEligibleAt(lastConnectorDisconnectedDate, hasConnectedSystemObjects) is { } eligibleAt && eligibleAt <= asAt;
}
