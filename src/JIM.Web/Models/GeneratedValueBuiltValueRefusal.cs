// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// What happens when a target refuses a value built from a generated value as already in use (Generated Value Forms,
/// #2014): the "If a target refuses it" column of the "Values built from ..." table.
/// </summary>
public enum GeneratedValueBuiltValueRefusal
{
    /// <summary>JIM corrects the generated value, and the value is worked out again from the new one.</summary>
    GeneratedValueCorrected = 0,

    /// <summary>An ordinary export error: the value's own Keep unique switch is off.</summary>
    KeepUniqueOff = 1,

    /// <summary>An ordinary export error: the generated Attribute Flow's own Collision Remediation is off.</summary>
    CollisionRemediationOff = 2,

    /// <summary>An ordinary export error: the value is built from more than one generated value, so JIM cannot tell which to correct.</summary>
    BuiltFromSeveral = 3
}
