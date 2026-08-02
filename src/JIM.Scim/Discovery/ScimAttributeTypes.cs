// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Discovery;

/// <summary>
/// The SCIM 2.0 attribute data type keywords (RFC 7643 section 2.3). Compare case insensitively:
/// providers are not consistent about the capitalisation of <c>dateTime</c>.
/// </summary>
public static class ScimAttributeTypes
{
    public const string String = "string";
    public const string Boolean = "boolean";
    public const string Decimal = "decimal";
    public const string Integer = "integer";
    public const string DateTime = "dateTime";
    public const string Binary = "binary";
    public const string Reference = "reference";
    public const string Complex = "complex";
}
