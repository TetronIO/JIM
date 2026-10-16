// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Transactional;

/// <summary>
/// A referencing Metaverse Object that reference recall left untouched because an export rule's scoping criterion could
/// not be evaluated for it (a pattern ran past its time limit). Whether it is in scope is unknown, so nothing is staged
/// for it; the caller records it as that object's error.
/// </summary>
/// <param name="MetaverseObjectId">The referencing Metaverse Object.</param>
/// <param name="Message">Why the scope could not be decided, naming the Synchronisation Rule and the attribute.</param>
public sealed record ReferenceRecallScopingFailure(Guid MetaverseObjectId, string Message)
{
    /// <summary>
    /// The error message for the object's execution item: why, and what was left undone.
    /// </summary>
    public string DescribeForAdministrator() =>
        $"{Message} The references Metaverse Object {MetaverseObjectId} held to deleted objects were not removed from its accounts.";
}
