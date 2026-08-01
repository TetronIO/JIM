// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// Whether the target a Standard Mapping points at can actually receive the chosen source attribute, and if
/// not, why. Every correspondence the standard describes is either offered or explained (#1122); one that is
/// silently dropped leaves the administrator wondering why the obvious attribute is missing.
/// </summary>
public enum StandardMappingSuggestionStatus
{
    /// <summary>
    /// The target can be selected, so the editor offers it.
    /// </summary>
    Available,

    /// <summary>
    /// The two attributes hold different data types, so a direct flow is not permitted. An Expression source
    /// can still bridge them, which is what the standard's note usually describes.
    /// </summary>
    TypeMismatch,

    /// <summary>
    /// Another Attribute Flow on this Synchronisation Rule already targets the attribute, so it is absent from
    /// the picker; without saying so, it simply looks missing.
    /// </summary>
    AlreadyTargeted,

    /// <summary>
    /// The Connected System reports the attribute as read-only, so no export can write it whatever the
    /// standard says.
    /// </summary>
    ReadOnlyTarget
}
