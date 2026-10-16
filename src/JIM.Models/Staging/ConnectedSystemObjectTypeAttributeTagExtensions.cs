// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// Reads JIM's own classifications off a Connected System Object Type attribute's tags.
/// </summary>
/// <remarks>
/// The portal's schema screen and the REST API ask the same question, so it is answered in one place rather than by
/// each of them matching tag strings for itself.
/// </remarks>
public static class ConnectedSystemObjectTypeAttributeTagExtensions
{
    /// <summary>
    /// Whether the Connector classified this attribute as operational: one the Connected System maintains for its own
    /// operation. See <see cref="AttributeTags.Operational"/>.
    /// </summary>
    public static bool IsOperational(this ConnectedSystemObjectTypeAttribute attribute) =>
        attribute.Tags.Contains(AttributeTags.Operational);
}
