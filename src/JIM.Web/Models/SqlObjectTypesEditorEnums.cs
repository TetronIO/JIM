// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// What a summary chip on a collapsed SQL Object Type card describes.
/// </summary>
public enum SqlObjectTypeSummaryChipKind
{
    /// <summary>The column or columns identifying a row.</summary>
    Anchor = 0,
    /// <summary>A column holding another object's anchor.</summary>
    Reference = 1,
    /// <summary>A related table read as a multi-valued attribute.</summary>
    MultiValued = 2
}

/// <summary>
/// What the SQL Object Types editor offers to do about a foreign key it found on an Object Type's source.
/// </summary>
public enum SqlObjectTypeSuggestionKind
{
    /// <summary>The key points at another Object Type's anchor: configure the column as a reference to it.</summary>
    Reference = 0,
    /// <summary>The key points at a table no Object Type reads from yet: add an Object Type for it.</summary>
    AddObjectType = 1
}
