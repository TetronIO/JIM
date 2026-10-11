// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;

namespace JIM.Web.Models.Api;

/// <summary>
/// How many members led nowhere further, and why.
/// </summary>
public class CausalConsequenceEndingCountDto
{
    /// <summary>
    /// Why they lead nowhere further: WaitingForSynchronisation, NothingFollowed or NoFurtherConsequences.
    /// </summary>
    public CausalConsequenceEnding Ending { get; set; }

    /// <summary>
    /// How many members end this way.
    /// </summary>
    public int MemberCount { get; set; }

    /// <summary>
    /// The Connected System an import is waiting on, where the ending names one.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// That Connected System's name.
    /// </summary>
    public string? ConnectedSystemName { get; set; }

    /// <summary>
    /// Maps one ending count.
    /// </summary>
    public static CausalConsequenceEndingCountDto FromModel(CausalConsequenceEndingCount ending)
    {
        ArgumentNullException.ThrowIfNull(ending);
        return new CausalConsequenceEndingCountDto
        {
            Ending = ending.Ending,
            MemberCount = ending.MemberCount,
            ConnectedSystemId = ending.ConnectedSystemId,
            ConnectedSystemName = ending.ConnectedSystemName
        };
    }
}
