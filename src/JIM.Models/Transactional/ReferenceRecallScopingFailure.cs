// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Localisation;
using JIM.Models.Resources;

namespace JIM.Models.Transactional;

/// <summary>
/// A referencing Metaverse Object that reference recall left untouched because an export rule's scoping criterion could
/// not be evaluated for it (a pattern ran past its time limit). Whether it is in scope is unknown, so nothing is staged
/// for it; the caller records it as that object's error.
/// </summary>
/// <param name="MetaverseObjectId">The referencing Metaverse Object.</param>
/// <param name="Message">Why the scope could not be decided, naming the Synchronisation Rule and the attribute.</param>
/// <param name="Cause"><paramref name="Message"/> as a message, where the failure carried one that says exactly the same.</param>
public sealed record ReferenceRecallScopingFailure(Guid MetaverseObjectId, string Message, JimMessage? Cause = null)
{
    /// <summary>
    /// The error message for the object's execution item: why, and what was left undone.
    /// </summary>
    public string DescribeForAdministrator() => Describe().ToEnglish();

    /// <summary><see cref="DescribeForAdministrator"/> as a message.</summary>
    public JimMessage Describe() =>
        JimMessage.Create(MessageCatalogues.Sync, nameof(SyncMessages.ReferenceRecallScopingFailure),
            Cause is { } cause && cause.ToEnglish() == Message ? cause : (MessageArgument)Message, MetaverseObjectId.ToString());
}
