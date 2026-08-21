// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// One Object Type as JIM held it before a schema refresh's merge ran. The merge rebuilds the in-memory
/// schema graph from what the Connector reported, so removed Object Types and attributes are no longer on the
/// merged graph; dependent detection (#1485) needs their ids to resolve which Synchronisation Rules and
/// mappings a removal invalidates, and this snapshot is where those ids survive.
/// </summary>
public class SchemaRefreshPreRefreshType
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public List<SchemaRefreshPreRefreshAttribute> Attributes { get; set; } = new();
}

/// <summary>
/// One attribute of a pre-refresh Object Type: just enough identity for dependent detection to resolve names
/// to the ids configuration references.
/// </summary>
public class SchemaRefreshPreRefreshAttribute
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}
