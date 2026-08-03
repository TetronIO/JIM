// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Messages;

/// <summary>
/// The canonical <c>scimType</c> error keywords from RFC 7644 section 3.12, table 9.
/// These are protocol values and are deliberately not localised or reworded.
/// </summary>
public static class ScimErrorTypes
{
    /// <summary>The specified filter syntax was invalid, or the attribute and filter comparison combination is not supported.</summary>
    public const string InvalidFilter = "invalidFilter";

    /// <summary>The specified filter yields many more results than the server is willing to process.</summary>
    public const string TooMany = "tooMany";

    /// <summary>One or more of the attribute values are already in use or are reserved.</summary>
    public const string Uniqueness = "uniqueness";

    /// <summary>The attempted modification is not compatible with the target attribute's mutability or current state.</summary>
    public const string Mutability = "mutability";

    /// <summary>The request body message structure was invalid or did not conform to the request schema.</summary>
    public const string InvalidSyntax = "invalidSyntax";

    /// <summary>The path attribute was invalid or malformed.</summary>
    public const string InvalidPath = "invalidPath";

    /// <summary>The specified path did not yield an attribute that could be operated on.</summary>
    public const string NoTarget = "noTarget";

    /// <summary>A required value was missing, or the value specified was not compatible with the attribute's type or the operation.</summary>
    public const string InvalidValue = "invalidValue";

    /// <summary>The specified SCIM protocol version is not supported.</summary>
    public const string InvalidVersion = "invalidVers";

    /// <summary>The specified request cannot be completed, due to the passing of sensitive information in a request URI.</summary>
    public const string Sensitive = "sensitive";
}
