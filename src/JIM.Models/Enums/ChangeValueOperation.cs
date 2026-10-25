// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Enums;

/// <summary>
/// What a row of a change's values did to its attribute, as a reader sees it. A single-valued attribute's new value is
/// a Set, carrying the value it replaced where the change recorded one; a multi-valued attribute gains values by Add.
/// Declared in the order the rows sort by it.
/// </summary>
public enum ChangeValueOperation
{
    Set,
    Add,
    Remove
}
