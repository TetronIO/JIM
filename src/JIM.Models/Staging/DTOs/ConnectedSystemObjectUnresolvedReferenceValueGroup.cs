// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// A Connected System Object's unresolved reference values that share a parent in the Connected System's hierarchy,
/// represented by one of them (#959). Every value with the same parent sits in the same Containers and partitions, so
/// diagnosing the sample diagnoses the group: an object with hundreds of unresolved members is summarised by
/// diagnosing a handful of parents rather than every member.
/// </summary>
public class ConnectedSystemObjectUnresolvedReferenceValueGroup
{
    /// <summary>One of the group's values, standing for all of them.</summary>
    public string SampleValue { get; set; } = null!;

    /// <summary>How many values the group holds.</summary>
    public int Count { get; set; }
}
