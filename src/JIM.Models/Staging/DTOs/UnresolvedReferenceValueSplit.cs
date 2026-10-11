// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A Connected System's unresolved reference values split in two (#959): those with one of a given set of values on one
/// of a given set of attributes, and the rest, each with how many objects hold them. "Where they point" uses it to count
/// the values found ambiguous across Object Types apart from those simply not found, for Connectors without Containers.
/// </summary>
public class UnresolvedReferenceValueSplit
{
    /// <summary>How many unresolved values are among those singled out.</summary>
    public int MatchingReferenceCount { get; set; }

    /// <summary>How many Connected System Objects hold at least one of the values singled out.</summary>
    public int MatchingObjectCount { get; set; }

    /// <summary>How many unresolved values are not among those singled out.</summary>
    public int OtherReferenceCount { get; set; }

    /// <summary>How many Connected System Objects hold at least one of the other values.</summary>
    public int OtherObjectCount { get; set; }
}
