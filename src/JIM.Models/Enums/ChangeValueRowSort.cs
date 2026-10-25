// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Enums;

/// <summary>
/// The order a window of a change's values is read in.
/// </summary>
public enum ChangeValueRowSort
{
    /// <summary>The order the change recorded them in: by change, then attribute name.</summary>
    SourceOrder,

    /// <summary>By attribute name.</summary>
    Attribute,

    /// <summary>By operation (Set, Add, Remove), then attribute name.</summary>
    Operation,

    /// <summary>By the value as it reads, including a referenced object's name.</summary>
    Value
}
