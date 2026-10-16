// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// How a Run Profile Execution Item's Unresolved References section presents itself (#959).
/// </summary>
public enum UnresolvedReferenceTone
{
    /// <summary>The run left no references unresolved; there is no section.</summary>
    None = 0,

    /// <summary>The run counted the unresolved references as the item's error.</summary>
    Error = 1,

    /// <summary>
    /// The run left references unresolved without counting them as the item's error: Unresolved Reference Handling was
    /// Warn or Ignore, or another error held the item's one error slot.
    /// </summary>
    Warning = 2
}
