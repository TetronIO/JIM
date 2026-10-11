// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// How a Connected System Object came to be joined to its Metaverse Object (#348): the join type, when, the
/// Synchronisation Rule responsible, and the Activity that made the join while history still holds it.
/// </summary>
public sealed class JoinRecord
{
    public ConnectedSystemObjectJoinType JoinType { get; set; }

    public DateTime? DateJoined { get; set; }

    /// <summary>How the object was joined; null when that was not recorded and history does not show it.</summary>
    public ConnectedSystemObjectJoinMethod? Method { get; set; }

    /// <summary>
    /// How it joined and by which rule, in one sentence generated on the server, for example "Projected by the
    /// Synchronisation Rule "HR Users Import"". The date and Activity are separate, for each surface to present.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The Synchronisation Rule that projected, provisioned or joined the object; null when it is not known or has
    /// since been deleted (the name is kept).
    /// </summary>
    public int? SyncRuleId { get; set; }

    /// <summary>The Synchronisation Rule's name as it was when the join was made; null when not known.</summary>
    public string? SyncRuleName { get; set; }

    /// <summary>Where <see cref="SyncRuleName"/> came from.</summary>
    public JoinRecordSource Source { get; set; }

    /// <summary>
    /// The Object Matching Rule that found the match, for a join made by matching (#2052); null when not recorded or
    /// since deleted (the value is kept in <see cref="MatchValue"/>).
    /// </summary>
    public int? ObjectMatchingRuleId { get; set; }

    /// <summary>What the join matched on, as recorded when it was made (#2052).</summary>
    public string? MatchValue { get; set; }

    /// <summary>The Metaverse attribute the Object Matching Rule compares, while the rule exists.</summary>
    public string? MatchedAttributeName { get; set; }

    /// <summary>
    /// What the Object Matching Rule compares the Metaverse attribute with, while the rule exists: the Connected System
    /// attribute's name, or the expression.
    /// </summary>
    public string? MatchSource { get; set; }

    /// <summary>The Activity that made the join, while Activity history still holds it.</summary>
    public Guid? ActivityId { get; set; }

    /// <summary>The Run Profile Execution Item within <see cref="ActivityId"/> that made the join.</summary>
    public Guid? RunProfileExecutionItemId { get; set; }
}
