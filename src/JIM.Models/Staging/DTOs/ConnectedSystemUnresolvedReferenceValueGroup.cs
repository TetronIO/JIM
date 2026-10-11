// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// The unresolved reference values across a whole Connected System that share a parent in its hierarchy, represented by
/// one of them (#959): the Connected System-wide counterpart of <see cref="ConnectedSystemObjectUnresolvedReferenceValueGroup"/>,
/// which also says how many objects the values sit on, so "Where they point" can show both.
/// </summary>
public class ConnectedSystemUnresolvedReferenceValueGroup
{
    /// <summary>
    /// The parent the group's values share, as the database compares it: what follows the first unescaped comma,
    /// lower-cased. Passed back to ask how many objects several groups' values sit on between them.
    /// </summary>
    public string ParentKey { get; set; } = null!;

    /// <summary>One of the group's values, standing for all of them.</summary>
    public string SampleValue { get; set; } = null!;

    /// <summary>How many values the group holds.</summary>
    public int Count { get; set; }

    /// <summary>How many Connected System Objects hold the group's values.</summary>
    public int ObjectCount { get; set; }
}
