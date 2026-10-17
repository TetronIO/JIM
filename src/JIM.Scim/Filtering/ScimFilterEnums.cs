// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Scim.Filtering;

/// <summary>
/// A comparison operator in a SCIM filter (RFC 7644 section 3.4.2.2). Written lower case; read in any case.
/// </summary>
public enum ScimComparisonOperator
{
    /// <summary>Equal.</summary>
    Eq,

    /// <summary>Not equal.</summary>
    Ne,

    /// <summary>Contains.</summary>
    Co,

    /// <summary>Starts with.</summary>
    Sw,

    /// <summary>Ends with.</summary>
    Ew,

    /// <summary>Present: the attribute has a non-empty value. Takes no comparison value.</summary>
    Pr,

    /// <summary>Greater than.</summary>
    Gt,

    /// <summary>Greater than or equal to.</summary>
    Ge,

    /// <summary>Less than.</summary>
    Lt,

    /// <summary>Less than or equal to.</summary>
    Le
}

/// <summary>
/// A logical operator joining two SCIM filters. <see cref="And"/> binds more tightly than <see cref="Or"/>.
/// </summary>
public enum ScimLogicalOperator
{
    And,
    Or
}

/// <summary>
/// The JSON kind of a comparison value in a SCIM filter.
/// </summary>
public enum ScimFilterValueKind
{
    String,
    Number,
    Boolean,
    Null
}
