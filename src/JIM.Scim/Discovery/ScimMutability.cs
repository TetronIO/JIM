// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Discovery;

/// <summary>
/// The SCIM 2.0 attribute mutability keywords (RFC 7643 section 7). These decide whether an attribute
/// can be targeted by an export Attribute Flow.
/// </summary>
public static class ScimMutability
{
    /// <summary>Provider-maintained; importable but never writable.</summary>
    public const string ReadOnly = "readOnly";

    public const string ReadWrite = "readWrite";

    /// <summary>Settable once, on creation, and never changed afterwards.</summary>
    public const string Immutable = "immutable";

    /// <summary>Writable but never returned, for example <c>password</c>.</summary>
    public const string WriteOnly = "writeOnly";
}
