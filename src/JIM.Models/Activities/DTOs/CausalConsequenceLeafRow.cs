// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// Members of a cohort that led nowhere further (#1811), counted per reason, so the walk can end each branch with a
/// named state rather than going quiet.
/// </summary>
public class CausalConsequenceLeafRow
{
    /// <summary>
    /// The cohort these members belong to.
    /// </summary>
    public int ParentCohortOrdinal { get; init; }

    /// <summary>
    /// Why they lead nowhere: <see cref="CausalConsequenceEnding.WaitingForSynchronisation"/> or
    /// <see cref="CausalConsequenceEnding.NothingFollowed"/> for an import, otherwise
    /// <see cref="CausalConsequenceEnding.NoFurtherConsequences"/>.
    /// </summary>
    public CausalConsequenceEnding Ending { get; init; }

    /// <summary>
    /// The Connected System an import is waiting on, so the ending can name it.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    /// <summary>
    /// That Connected System's name, as it is now.
    /// </summary>
    public string? ConnectedSystemName { get; init; }

    /// <summary>
    /// How many members end this way.
    /// </summary>
    public int MemberCount { get; init; }
}
