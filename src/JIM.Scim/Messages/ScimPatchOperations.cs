// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Messages;

/// <summary>
/// The PATCH operation names from RFC 7644 section 3.5.2. Protocol values, deliberately lower case.
/// </summary>
public static class ScimPatchOperations
{
    /// <summary>Adds a value, or appends one to a multi-valued attribute.</summary>
    public const string Add = "add";

    /// <summary>Replaces the value at the path.</summary>
    public const string Replace = "replace";

    /// <summary>Removes the value at the path.</summary>
    public const string Remove = "remove";
}
