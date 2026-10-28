// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Connectors.Sql;

/// <summary>
/// An Object Type's change-log table in the Object Types document, as written.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SqlChangeLogDocument
{
    /// <summary>The schema the change-log table is in, or null to find it by name alone.</summary>
    public string? Schema { get; set; }

    /// <summary>The change-log table or view.</summary>
    public string? Table { get; set; }

    /// <summary>The columns carrying the changed object's anchor, one per anchor column, in the same order.</summary>
    public List<string?>? AnchorColumns { get; set; }

    /// <summary>The monotonic sequence or timestamp column that orders changes.</summary>
    public string? SequenceColumn { get; set; }

    /// <summary>The column saying what kind of change a row records.</summary>
    public string? ChangeTypeColumn { get; set; }

    /// <summary>The change-type values that mean an object was created.</summary>
    public List<string?>? CreateValues { get; set; }

    /// <summary>The change-type values that mean an object was updated.</summary>
    public List<string?>? UpdateValues { get; set; }

    /// <summary>The change-type values that mean an object was deleted.</summary>
    public List<string?>? DeleteValues { get; set; }
}
