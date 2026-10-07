// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>What a generated value's history event was, where it was one of the two worth naming.</summary>
public enum GeneratedValueHistoryEvent
{
    /// <summary>The Attribute Flow generated the value and it flowed onto the object.</summary>
    Generated,

    /// <summary>Collision Remediation replaced a value a target rejected as already in use.</summary>
    Corrected
}
