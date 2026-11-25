// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.Transactional;

/// <summary>
/// A Metaverse Object a synchronisation has projected but not yet published (#2848). A page writes its new Metaverse
/// Objects across parallel connections before it opens the transaction that writes everything else it changed, joins
/// included, since parallel connections cannot share one transaction. Each new object is written with one of these, in
/// the same transaction as the object's own row, and the page's transaction deletes it once the object's join is
/// written. So a record that outlives its page means the page never committed: the object was projected and joined to
/// nothing, and the next run would project its source again beside it. A page that fails removes its own; the Worker
/// removes any a stopped Worker left at its next start.
/// </summary>
/// <remarks>
/// A record per object rather than a column on the object: a Projected object with nothing joined to it is an ordinary
/// state (its last connector left under a Manual Deletion Rule, say), so only a record that is gone once the page commits
/// can tell an unpublished object from one of those.
/// </remarks>
public class UnpublishedMetaverseObject
{
    /// <summary>
    /// The projected Metaverse Object. Cascade delete: an object that is gone has nothing left to publish.
    /// </summary>
    public Guid MetaverseObjectId { get; set; }

    public MetaverseObject? MetaverseObject { get; set; }

    /// <summary>
    /// The synchronisation Activity whose page projected the object, for the log line that reports its removal. A plain
    /// id, not a foreign key: Activities are history, kept and purged on their own terms.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// When the object was projected.
    /// </summary>
    public DateTime Created { get; set; }
}
