// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Scim;

/// <summary>
/// Size limits on the SCIM store's indexed columns. The SCIM server refuses a request exceeding one with a SCIM error
/// before anything is written; the column limits are the backstop.
/// </summary>
public static class ScimStoreLimits
{
    /// <summary>
    /// The longest <c>userName</c>, <c>externalId</c> or <c>displayName</c> the store accepts, in characters. Ample
    /// for any real identifier, and short enough that the unique indexes over these columns can never be refused for
    /// an oversized entry.
    /// </summary>
    public const int IdentifierMaxLength = 1024;
}
