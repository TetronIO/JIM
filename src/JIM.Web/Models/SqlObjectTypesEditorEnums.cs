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

/// <summary>
/// The steps of the SQL Connector's Add Object Type dialog, in the order they are taken.
/// </summary>
public enum SqlAddObjectTypeStep
{
    /// <summary>The name, and the table, view or statement the objects come from.</summary>
    Source = 0,
    /// <summary>The column or columns identifying a row.</summary>
    Anchor = 1,
    /// <summary>Columns holding another Object Type's anchor. Optional.</summary>
    References = 2,
    /// <summary>Child tables read as multi-valued attributes. Optional.</summary>
    MultiValuedAttributes = 3
}

/// <summary>
/// Where an anchor in the Add Object Type dialog came from, or why JIM could not suggest one, so the dialog can say.
/// </summary>
public enum SqlAnchorOrigin
{
    /// <summary>Nothing has been read or chosen yet.</summary>
    None = 0,
    /// <summary>Preselected from the table's primary key, in key order.</summary>
    PrimaryKey = 1,
    /// <summary>Chosen by the administrator.</summary>
    Chosen = 2,
    /// <summary>The source is a view, and views declare no keys.</summary>
    ViewDeclaresNoKeys = 3,
    /// <summary>The source is a table that declares no primary key.</summary>
    TableHasNoPrimaryKey = 4,
    /// <summary>The source is a SELECT statement, which declares no keys.</summary>
    StatementDeclaresNoKeys = 5,
    /// <summary>The source's columns could not be read.</summary>
    ColumnsUnreadable = 6
}
