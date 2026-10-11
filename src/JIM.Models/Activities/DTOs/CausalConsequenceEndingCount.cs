// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// How many members of a cohort (or the item the walk started from) led nowhere further, and why (#1811).
/// </summary>
public class CausalConsequenceEndingCount
{
    /// <summary>
    /// Why they lead nowhere further.
    /// </summary>
    public CausalConsequenceEnding Ending { get; init; }

    /// <summary>
    /// How many members end this way.
    /// </summary>
    public int MemberCount { get; init; }

    /// <summary>
    /// The Connected System an import is waiting on, where the ending names one.
    /// </summary>
    public int? ConnectedSystemId { get; init; }

    /// <summary>
    /// That Connected System's name.
    /// </summary>
    public string? ConnectedSystemName { get; init; }
}
