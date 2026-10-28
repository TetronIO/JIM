// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;

namespace JIM.Connectors.Sql;

/// <summary>
/// A column an Object Type configures in the Object Types document, as written.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SqlColumnDocument
{
    /// <summary>The column's name.</summary>
    public string? Name { get; set; }

    /// <summary>The Object Type whose anchor the column's values hold.</summary>
    public string? ReferencesObjectType { get; set; }
}
