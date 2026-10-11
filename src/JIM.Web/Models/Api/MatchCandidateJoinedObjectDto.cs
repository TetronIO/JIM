// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Models.Api;

/// <summary>
/// A Connected System Object joined to a Metaverse Object that an ambiguous match found (#2052).
/// </summary>
public class MatchCandidateJoinedObjectDto
{
    /// <summary>The Connected System Object's id.</summary>
    public Guid ConnectedSystemObjectId { get; set; }

    /// <summary>The Connected System it belongs to.</summary>
    public int ConnectedSystemId { get; set; }

    /// <summary>The Connected System's name.</summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>The Connected System Object Type's name.</summary>
    public string? TypeName { get; set; }

    /// <summary>The object's display name, where it has one.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The object's external id, where it has one.</summary>
    public string? ExternalId { get; set; }

    /// <summary>Creates the DTO from the model.</summary>
    public static MatchCandidateJoinedObjectDto FromModel(MatchCandidateJoinedObject joined) => new()
    {
        ConnectedSystemObjectId = joined.ConnectedSystemObjectId,
        ConnectedSystemId = joined.ConnectedSystemId,
        ConnectedSystemName = joined.ConnectedSystemName,
        TypeName = joined.TypeName,
        DisplayName = joined.DisplayName,
        ExternalId = joined.ExternalId
    };
}
