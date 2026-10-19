// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// One page of an import's read of a Connected System's store (<see cref="ScimStoreImportQuery"/>).
/// </summary>
public sealed class ScimStoreImportPage
{
    /// <summary>
    /// The resources, in id order: live ones, and for a Delta Import tombstones (<see cref="ScimStoreResource.DeletedAt"/>
    /// set, no payload).
    /// </summary>
    public IReadOnlyList<ScimStoreResource> Resources { get; init; } = [];

    /// <summary>
    /// The references the page's live resources hold, keyed by the resource holding them.
    /// </summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<ScimStoreReferenceTarget>> References { get; init; } =
        new Dictionary<Guid, IReadOnlyList<ScimStoreReferenceTarget>>();

    /// <summary>
    /// Whether a later page holds more.
    /// </summary>
    public bool HasMore { get; init; }
}
