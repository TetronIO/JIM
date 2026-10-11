// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Logic;

namespace JIM.Models.Exceptions;

/// <summary>
/// Represents an exception to the Metaverse Object Matching process.
/// </summary>
public class MultipleMatchesException : OperationalException
{
    public MultipleMatchesException(string message, List<Guid> matches) : base(message)
    {
        Matches = matches;
    }

    /// <summary>
    /// Contains the unique identifiers for the matches found.
    /// </summary>
    public List<Guid> Matches { get; set; }

    /// <summary>The Object Matching Rule whose values found more than one match, where one did (#2052).</summary>
    public ObjectMatchingRule? Rule { get; init; }

    /// <summary>The values the rule computed and looked up (#2052).</summary>
    public IReadOnlyList<object> MatchValues { get; init; } = [];

    /// <summary>
    /// The candidates to list on the Activity item (#2052): up to
    /// <see cref="ActivityRunProfileExecutionItemMatchCandidate.MaxRecorded"/>, ordered by display name, each carrying
    /// the total.
    /// </summary>
    public IReadOnlyList<ActivityRunProfileExecutionItemMatchCandidate> Candidates { get; init; } = [];
}