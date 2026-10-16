// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Search.DTOs;

public class PredefinedSearchHeader
{
    /// <summary>
    /// The Predefined Search's identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The name of the Metaverse Object Type the search finds.
    /// </summary>
    public string MetaverseObjectTypeName { get; set; } = null!;

    /// <summary>
    /// Whether this is the search the portal uses for its Metaverse Object Type's list.
    /// </summary>
    public bool IsDefaultForMetaverseObjectType { get; set; }

    /// <summary>
    /// The search's display name.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The stable, human-readable slug that identifies the search in URLs and the API, such as people.
    /// </summary>
    public string Uri { get; set; } = null!;

    /// <summary>
    /// Whether the search ships with JIM, rather than being created by an administrator.
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// Whether end users can see and run the search. A disabled search stays visible to administrators.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// How many attributes the search shows as columns in its results.
    /// </summary>
    public int MetaverseAttributeCount { get; set; }

    /// <summary>
    /// When the search was created (UTC).
    /// </summary>
    public DateTime Created { get; set; } = DateTime.UtcNow;
}