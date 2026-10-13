// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;

namespace JIM.Web.Models.Api;

/// <summary>
/// One object an ambiguous match found (#2052), as recorded when the ambiguity arose. An object can join only one
/// Metaverse Object, so a match that finds several stops the object and lists them here.
/// </summary>
public class MatchCandidateDto
{
    /// <summary>Whether the candidate is a Metaverse Object or a Connected System Object. Serialised as the enum member name.</summary>
    public MatchCandidateKind CandidateKind { get; set; }

    /// <summary>The candidate's id. It may since have been deleted.</summary>
    public Guid CandidateId { get; set; }

    /// <summary>The candidate's display name when the ambiguity was recorded.</summary>
    public string? DisplayNameSnapshot { get; set; }

    /// <summary>The candidate's object type name when the ambiguity was recorded.</summary>
    public string? TypeNameSnapshot { get; set; }

    /// <summary>For a Connected System Object candidate, its Connected System.</summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>For a Connected System Object candidate, its Connected System's name when the ambiguity was recorded.</summary>
    public string? ConnectedSystemNameSnapshot { get; set; }

    /// <summary>The value or values this candidate matched on, separated by commas.</summary>
    public string? MatchedValue { get; set; }

    /// <summary>The Object Matching Rule whose values found the candidates.</summary>
    public int? ObjectMatchingRuleId { get; set; }

    /// <summary>What the rule compared when the ambiguity was recorded, for example "Employee ID equals employeeNumber".</summary>
    public string? ObjectMatchingRuleDescriptionSnapshot { get; set; }

    /// <summary>The candidate's position in the list, which is ordered by display name.</summary>
    public int Ordinal { get; set; }

    /// <summary>How many candidates the match found in all; at most 10 are listed.</summary>
    public int CandidateTotal { get; set; }

    /// <summary>Creates the DTO from a recorded candidate.</summary>
    public static MatchCandidateDto FromModel(MatchCandidateSnapshot candidate)
    {
        var dto = new MatchCandidateDto();
        dto.CopyFrom(candidate);
        return dto;
    }

    /// <summary>Copies the recorded candidate's fields.</summary>
    protected void CopyFrom(MatchCandidateSnapshot candidate)
    {
        CandidateKind = candidate.CandidateKind;
        CandidateId = candidate.CandidateId;
        DisplayNameSnapshot = candidate.DisplayNameSnapshot;
        TypeNameSnapshot = candidate.TypeNameSnapshot;
        ConnectedSystemId = candidate.ConnectedSystemId;
        ConnectedSystemNameSnapshot = candidate.ConnectedSystemNameSnapshot;
        MatchedValue = candidate.MatchedValue;
        ObjectMatchingRuleId = candidate.ObjectMatchingRuleId;
        ObjectMatchingRuleDescriptionSnapshot = candidate.ObjectMatchingRuleDescriptionSnapshot;
        Ordinal = candidate.Ordinal;
        CandidateTotal = candidate.CandidateTotal;
    }
}
