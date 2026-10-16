// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// How many of a Connected System Object's unresolved reference values share one likely cause (#959).
/// </summary>
public class UnresolvedReferenceCauseCount
{
    /// <summary>The likely cause.</summary>
    public UnresolvedReferenceCause Cause { get; set; }

    /// <summary>Where the fix for this cause lies.</summary>
    public UnresolvedReferenceFixLocation FixLocation { get; set; }

    /// <summary>How many values it accounts for.</summary>
    public int Count { get; set; }
}
