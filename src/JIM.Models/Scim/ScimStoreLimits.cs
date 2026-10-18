// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// Size limits on the SCIM store's indexed names. The SCIM server refuses a definition or request exceeding one before
/// anything is written; the column limits are the backstop.
/// </summary>
public static class ScimStoreLimits
{
    /// <summary>
    /// The longest resource type name or endpoint, in characters.
    /// </summary>
    public const int NameMaxLength = 200;

    /// <summary>
    /// The longest schema URN or attribute name (an extension attribute's name carries its schema's URN), in
    /// characters.
    /// </summary>
    public const int AttributeMaxLength = 400;
}
