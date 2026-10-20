// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// The SCIM requests behind one imported change to a Connected System Object (D17): the resource's journal entries
/// between the watermark its import started from and the one it reached.
/// </summary>
public sealed class ScimRequestsBehindChange
{
    /// <summary>
    /// The Connected System whose store the requests wrote to.
    /// </summary>
    public required int ConnectedSystemId { get; init; }

    /// <summary>
    /// The SCIM resource, which is the Connected System Object's external id.
    /// </summary>
    public required Guid ResourceId { get; init; }

    /// <summary>
    /// The change sequence the import started after; null for an import that read from the start.
    /// </summary>
    public long? AfterSequence { get; init; }

    /// <summary>
    /// The change sequence the import read up to, inclusive.
    /// </summary>
    public required long UpToSequence { get; init; }

    /// <summary>
    /// The resource's accepted writes in that range, oldest first.
    /// </summary>
    public required List<ScimStoreJournalEntry> Entries { get; init; }
}
