// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.History;

/// <summary>
/// How an attribute condition in a Metaverse Object change search matches an attribute's value changes.
/// </summary>
public enum AttributeChangeOperator
{
    /// <summary>
    /// The attribute changed in any way: a value was added or removed. With a value, that value was added or removed.
    /// </summary>
    AnyChange = 0,

    /// <summary>
    /// A value was added: for a single-valued attribute, the attribute changed to it.
    /// </summary>
    ChangedTo = 1,

    /// <summary>
    /// A value was removed: for a single-valued attribute, the attribute changed from it.
    /// </summary>
    ChangedFrom = 2
}

/// <summary>
/// How several attribute conditions in a Metaverse Object change search combine.
/// </summary>
public enum AttributeConditionMatchMode
{
    /// <summary>
    /// Every condition must be met by the same change: one Metaverse Object, in one Activity.
    /// </summary>
    All = 0,

    /// <summary>
    /// Any one condition is enough.
    /// </summary>
    Any = 1
}
