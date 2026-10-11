// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities;

/// <summary>
/// One of the objects an ambiguous match found (#2052): a Metaverse Object an import could have joined, or (on export)
/// a Connected System Object provisioning could have claimed. JIM never chooses between them, so the Activity item
/// lists every candidate, up to <see cref="MaxRecorded"/>, for the administrator to resolve.
/// </summary>
/// <remarks>
/// Stored as <see cref="CausalEdge"/> stores its cause side: a cascading foreign key to the Activity item, and
/// snapshot scalars for the candidate with no foreign key, so a candidate deleted later (often the very fix for the
/// ambiguity) still reads correctly, unlinked and marked as no longer existing.
/// </remarks>
public class ActivityRunProfileExecutionItemMatchCandidate
{
    /// <summary>The most candidates recorded on one item; <see cref="CandidateTotal"/> carries the full count.</summary>
    public const int MaxRecorded = 10;

    /// <summary>
    /// Whether an Activity item with this error records candidates, so a reader asks for them only where there can be
    /// some.
    /// </summary>
    public static bool AreRecordedFor(ActivityRunProfileExecutionItemErrorType? errorType) =>
        errorType == ActivityRunProfileExecutionItemErrorType.AmbiguousMatch;

    /// <summary>Assigned in code so the raw-SQL insert does not round-trip database-generated keys.</summary>
    public Guid Id { get; set; }

    /// <summary>The Activity item reporting the ambiguity. Cascades on delete.</summary>
    public Guid ActivityRunProfileExecutionItemId { get; set; }

    /// <summary>Foreign key navigation for <see cref="ActivityRunProfileExecutionItemId"/>.</summary>
    public ActivityRunProfileExecutionItem ActivityRunProfileExecutionItem { get; set; } = null!;

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

    /// <summary>How many candidates the match found in all, which can exceed the <see cref="MaxRecorded"/> listed.</summary>
    public int CandidateTotal { get; set; }
}
