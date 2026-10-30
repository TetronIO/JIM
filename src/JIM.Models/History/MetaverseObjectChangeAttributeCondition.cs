// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.History;

/// <summary>
/// One attribute condition in a Metaverse Object change search, as an administrator states it: which Metaverse
/// Attribute, how it changed, and optionally the value it changed to or from.
/// </summary>
public class MetaverseObjectChangeAttributeCondition
{
    public int AttributeId { get; set; }

    public AttributeChangeOperator Operator { get; set; } = AttributeChangeOperator.AnyChange;

    /// <summary>
    /// The value to match, as typed. Blank matches any value. Text and Reference values match case-insensitively, and
    /// <c>*</c> matches any run of characters.
    /// </summary>
    public string? Value { get; set; }
}
