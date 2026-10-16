// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// A Connected System Object joined to a Metaverse Object that an ambiguous match found (#2052).
/// </summary>
public class MatchCandidateJoinedObject
{
    /// <summary>The Connected System Object's id.</summary>
    public Guid ConnectedSystemObjectId { get; init; }

    /// <summary>The Connected System it belongs to.</summary>
    public int ConnectedSystemId { get; init; }

    /// <summary>The Connected System's name.</summary>
    public string? ConnectedSystemName { get; init; }

    /// <summary>The Connected System Object Type's name.</summary>
    public string? TypeName { get; init; }

    /// <summary>The object's display name, where it has one.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The object's external id, where it has one.</summary>
    public string? ExternalId { get; init; }
}
