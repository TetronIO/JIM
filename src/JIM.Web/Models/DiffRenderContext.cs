// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The context a configuration diff node is being rendered in by the field-history view. Within a wholly added or
/// removed subtree every descendant is an addition/removal, so its scalars are shown as a plain "label: value" rather
/// than a "before → after" row.
/// </summary>
public enum DiffRenderContext
{
    /// <summary>An ordinary change against an existing object: modifications show "before → after".</summary>
    Normal,

    /// <summary>Inside a newly added item/subtree: every value is new, shown as "label: value".</summary>
    Added,

    /// <summary>Inside a removed item/subtree: every value is gone, shown as "label: value".</summary>
    Removed
}
