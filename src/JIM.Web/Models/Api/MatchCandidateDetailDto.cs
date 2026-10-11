// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;
using JIM.Models.Activities;

namespace JIM.Web.Models.Api;

/// <summary>
/// An ambiguous match's candidate on an Execution Item (#2052): as recorded, plus whether it still exists and, for a
/// Metaverse Object, what is joined to it now. The current state serialises after the recorded fields.
/// </summary>
public class MatchCandidateDetailDto : MatchCandidateDto
{
    /// <summary>Whether the candidate still exists.</summary>
    [JsonPropertyOrder(1)]
    public bool Exists { get; set; }

    /// <summary>For a Metaverse Object candidate that still exists, the Connected System Objects joined to it now.</summary>
    [JsonPropertyOrder(1)]
    public List<MatchCandidateJoinedObjectDto> JoinedObjects { get; set; } = [];

    /// <summary>Creates the DTO from a candidate's detail.</summary>
    public static MatchCandidateDetailDto FromModel(MatchCandidateDetail detail)
    {
        var dto = new MatchCandidateDetailDto
        {
            Exists = detail.Exists,
            JoinedObjects = detail.JoinedObjects.Select(MatchCandidateJoinedObjectDto.FromModel).ToList()
        };
        dto.CopyFrom(detail.Candidate);
        return dto;
    }
}
