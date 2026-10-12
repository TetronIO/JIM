// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// What is recorded about one of the objects an ambiguous match found (#2052), wherever it is recorded: on the Activity
/// item a run reports it on (<see cref="ActivityRunProfileExecutionItemMatchCandidate"/>), and on a Configuration Change
/// Preview's row predicting it (<c>ConfigurationChangePreviewDeltaMatchCandidate</c>). One shape, so the portal, the REST
/// API and PowerShell read a candidate the same way wherever they meet one.
/// </summary>
/// <remarks>
/// Every field is a snapshot taken when the ambiguity was found, with no foreign key to the candidate: a candidate
/// deleted later (often the very fix for the ambiguity) still reads correctly, unlinked and marked as no longer
/// existing.
/// </remarks>
public abstract class MatchCandidateSnapshot
{
    /// <summary>Whether the candidate is a Metaverse Object (import) or a Connected System Object (export).</summary>
    public MatchCandidateKind CandidateKind { get; set; }

    /// <summary>The candidate's id. Not a foreign key: the candidate may since have been deleted.</summary>
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

    /// <summary>The Object Matching Rule whose values found the candidates. Not a foreign key.</summary>
    public int? ObjectMatchingRuleId { get; set; }

    /// <summary>What the rule compared, when the ambiguity was recorded, for example "Employee ID equals employeeNumber".</summary>
    public string? ObjectMatchingRuleDescriptionSnapshot { get; set; }

    /// <summary>This candidate's position in the list: candidates are ordered by display name, so every run lists them alike.</summary>
    public int Ordinal { get; set; }

    /// <summary>How many candidates the match found in all, which can exceed the <see cref="ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded"/> listed.</summary>
    public int CandidateTotal { get; set; }

    /// <summary>
    /// Copies this snapshot onto <paramref name="target"/>, so a candidate found by one record can be kept by another.
    /// </summary>
    public void CopySnapshotTo(MatchCandidateSnapshot target)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.CandidateKind = CandidateKind;
        target.CandidateId = CandidateId;
        target.DisplayNameSnapshot = DisplayNameSnapshot;
        target.TypeNameSnapshot = TypeNameSnapshot;
        target.ConnectedSystemId = ConnectedSystemId;
        target.ConnectedSystemNameSnapshot = ConnectedSystemNameSnapshot;
        target.MatchedValue = MatchedValue;
        target.ObjectMatchingRuleId = ObjectMatchingRuleId;
        target.ObjectMatchingRuleDescriptionSnapshot = ObjectMatchingRuleDescriptionSnapshot;
        target.Ordinal = Ordinal;
        target.CandidateTotal = CandidateTotal;
    }
}
