// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// What an Object Matching Rule computed for one object (#2052).
/// </summary>
public enum MatchValueResolutionKind
{
    /// <summary>One or more values to look up.</summary>
    Values,

    /// <summary>Nothing to look up, so the next rule is tried. Not an error.</summary>
    NoValue,

    /// <summary>The rule could not be worked out for this object, so the object is stopped with an error.</summary>
    Failed
}
