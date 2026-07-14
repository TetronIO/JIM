// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// Response for the real-time attribute name-availability check. Backs the UI's live validator on the create and
/// rename fields. The comparison is case-insensitive; the queried name is echoed back exactly as supplied.
/// </summary>
public class MetaverseAttributeNameAvailabilityDto
{
    /// <summary>
    /// The name that was checked, exactly as supplied by the caller.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// True when no other Metaverse Attribute already uses this name (compared case-insensitively); false when taken.
    /// </summary>
    public bool Available { get; set; }
}
