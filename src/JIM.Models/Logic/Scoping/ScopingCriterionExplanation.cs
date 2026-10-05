// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Search;

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// How one scoping criterion evaluated (#348): what it compares, against what, and what the object held. Values are
/// rendered culture-invariantly so every surface shows the same text, and are withheld for credential attributes.
/// </summary>
public sealed class ScopingCriterionExplanation
{
    public int CriterionId { get; set; }

    /// <summary>Where the criterion sits; see <see cref="ScopingGroupExplanation.Path"/>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The Metaverse Attribute (export rules) or Connected System attribute (import rules) compared.</summary>
    public int? AttributeId { get; set; }

    public string? AttributeName { get; set; }

    public AttributeDataType? AttributeType { get; set; }

    public SearchComparisonType ComparisonType { get; set; }

    /// <summary>Whether a text comparison is case-sensitive; meaningless for other types.</summary>
    public bool CaseSensitive { get; set; }

    /// <summary>
    /// The value the criterion compares against, rendered; null when the criterion's expected value is empty. For a
    /// relative date criterion this is the boundary it resolved to.
    /// </summary>
    public string? ExpectedDisplay { get; set; }

    public DateCriteriaValueMode ValueMode { get; set; }

    /// <summary>A relative date criterion's offset in words, for example "30 days ago"; null when absolute.</summary>
    public string? RelativeDisplay { get; set; }

    /// <summary>The boundary a relative date criterion resolved to at evaluation, in UTC; null when absolute.</summary>
    public DateTime? ResolvedDate { get; set; }

    /// <summary>The value the object held and the criterion compared, rendered; null when it held none.</summary>
    public string? ActualDisplay { get; set; }

    /// <summary>True when the expected and actual values are withheld because the attribute may hold a credential.</summary>
    public bool Masked { get; set; }

    /// <summary>
    /// How many further values the object holds for the attribute that were not compared: scoping compares only an
    /// attribute's first value (#1923).
    /// </summary>
    public int AdditionalValuesNotEvaluated { get; set; }

    public ScopingCriterionOutcome Outcome { get; set; }
}
