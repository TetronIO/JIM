// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;

namespace JIM.Models.History;

/// <summary>
/// An attribute condition checked against the attribute's definition: its data type is known and its value parsed
/// to that type. This is what the repository queries with; <see cref="MetaverseObjectChangeAttributeCondition"/> is
/// what an administrator supplies.
/// </summary>
public class ResolvedAttributeChangeCondition
{
    public int AttributeId { get; set; }

    public AttributeDataType AttributeType { get; set; }

    public AttributeChangeOperator Operator { get; set; }

    /// <summary>
    /// The value to match, or null for any value.
    /// </summary>
    public AttributeChangeValueMatch? Match { get; set; }
}
