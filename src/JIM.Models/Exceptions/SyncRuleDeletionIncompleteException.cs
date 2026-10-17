// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Exceptions;

/// <summary>
/// A Synchronisation Rule deletion recalled every value it could but left some Metaverse Objects exactly as they were,
/// because a surviving contributor's scope could not be decided for them (#2336). The rule is therefore not deleted, and
/// stays disabled: deleting it would sever the provenance the recall selects on, stranding the values it could not
/// recall. Deleting the rule again, once the cause is corrected, recalls what remains and deletes it.
/// </summary>
public class SyncRuleDeletionIncompleteException : OperationalException
{
    /// <param name="syncRuleName">The Synchronisation Rule being deleted.</param>
    /// <param name="heldBackCount">How many Metaverse Objects were left as they were.</param>
    public SyncRuleDeletionIncompleteException(string syncRuleName, int heldBackCount)
        : base($"Synchronisation Rule '{syncRuleName}' was not deleted: {heldBackCount:N0} Metaverse Object(s) were left as they " +
               "were, with the values the rule contributed, because a scoping criterion could not be evaluated for a surviving " +
               "contributor. The errors on this Activity name each object and criterion. Correct the cause, then delete the " +
               "Synchronisation Rule again to finish.")
    {
        HeldBackCount = heldBackCount;
    }

    /// <summary>How many Metaverse Objects were left as they were.</summary>
    public int HeldBackCount { get; }
}
