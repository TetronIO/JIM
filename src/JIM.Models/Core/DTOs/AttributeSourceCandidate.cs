// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core.DTOs;

/// <summary>
/// One import mapping that can contribute a Metaverse Object attribute, with the value it would supply for this
/// Metaverse Object and its standing against the value in use.
/// </summary>
public class AttributeSourceCandidate
{
    /// <summary>1-based position in the attribute's priority order (1 wins).</summary>
    public int Rank { get; set; }

    public int MappingId { get; set; }

    public int SyncRuleId { get; set; }

    public string SyncRuleName { get; set; } = null!;

    public int ConnectedSystemId { get; set; }

    public string ConnectedSystemName { get; set; } = null!;

    public bool IsExpression { get; set; }

    public string? Expression { get; set; }

    public AttributeSourceState State { get; set; }

    /// <summary>The value(s) this source would supply, as display text, capped for multi-valued attributes.</summary>
    public List<string> CandidateValues { get; set; } = new();

    /// <summary>Why the candidate could not be evaluated, or other context worth showing; null otherwise.</summary>
    public string? Note { get; set; }
}
