// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Servers.Preview;

/// <summary>
/// The remaining-connector arithmetic shared by every caller that asks "if this Connected System's join(s)
/// left, what would still be connected?" (#288 Phase 1 of the Sync Preview Surface plan): a system holding
/// two joined objects where one leaves is still a connector, so only the disconnecting occurrences are
/// removed, not every entry for that system.
/// </summary>
/// <remarks>
/// Hoisted out of <see cref="PreviewDeletionEligibilityEvaluator"/> so the destructive-cascade preview
/// (<c>SyncPreviewServer</c>) and the configuration-change adapters call the identical arithmetic rather than
/// each carrying their own copy. The semantics match
/// <c>SyncTaskProcessorBase.HandleCsoOutOfScopeAsync</c>'s <c>List&lt;int&gt;.Remove(systemId)</c> exactly for
/// the single-occurrence case (<paramref name="disconnectingCount"/> of 1, which every current cascade caller
/// passes): <c>List.Remove</c> deletes the first matching element and leaves every other element, in place, in
/// its original order; this method does the same by walking the list once and skipping the first
/// <paramref name="disconnectingCount"/> occurrences of <paramref name="disconnectingSystemId"/> it meets.
/// </remarks>
internal static class RemainingConnectorsCalculator
{
    /// <summary>
    /// The Connected Systems that would still hold a joined object, one entry per joined object because that
    /// is what the deletion-rule engine counts.
    /// </summary>
    /// <param name="joinedConnectedSystemIds">The Connected System id of each object currently joined to the
    /// Metaverse Object, one entry per joined object (duplicates expected when a system holds more than one).</param>
    /// <param name="disconnectingSystemId">The Connected System whose join(s) are leaving.</param>
    /// <param name="disconnectingCount">How many of the disconnecting system's joined objects are leaving.
    /// Defaults to 1, the ordinary case of a single Connected System Object falling out of scope or being
    /// obsoleted.</param>
    internal static List<int> RemainingConnectorsAfterDisconnection(
        IReadOnlyCollection<int> joinedConnectedSystemIds,
        int disconnectingSystemId,
        int disconnectingCount = 1)
    {
        var remaining = new List<int>(joinedConnectedSystemIds.Count);
        var stillToRemove = disconnectingCount;

        foreach (var systemId in joinedConnectedSystemIds)
        {
            // Only the disconnecting system's entries are removed, and only as many as actually leave scope: a
            // system holding two joined objects where one stays is still a connector.
            if (systemId == disconnectingSystemId && stillToRemove > 0)
                stillToRemove--;
            else
                remaining.Add(systemId);
        }

        return remaining;
    }
}
