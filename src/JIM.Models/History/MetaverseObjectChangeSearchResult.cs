// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core.DTOs;

namespace JIM.Models.History;

/// <summary>
/// One row of a Metaverse Object change search: a change to one Metaverse Object, with the object it was made to and
/// the Activity it was made in.
/// </summary>
public class MetaverseObjectChangeSearchResult
{
    /// <summary>
    /// The change itself, in the same shape as a Metaverse Object's own change history.
    /// </summary>
    public MvoChangeHistoryDto Change { get; set; } = new();

    /// <summary>
    /// The changed Metaverse Object's id. For an object since deleted, the id it had.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// Whether the Metaverse Object has since been deleted.
    /// </summary>
    public bool MetaverseObjectDeleted { get; set; }

    public string? MetaverseObjectDisplayName { get; set; }

    public int? MetaverseObjectTypeId { get; set; }

    public string? MetaverseObjectTypeName { get; set; }

    /// <summary>
    /// The Activity the change was made in, when it was made by a synchronisation.
    /// </summary>
    public Guid? ActivityId { get; set; }

    /// <summary>
    /// The ids of the attributes the search's conditions named, so a reader can be shown those changes first.
    /// </summary>
    public List<int> MatchedAttributeIds { get; set; } = [];
}
