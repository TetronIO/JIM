// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging.DTOs;

/// <summary>
/// An Object Type or attribute that a schema refresh found under a name differing only in case from the one JIM held.
/// Configuration names ignore case, so it is the same Object Type or attribute: it keeps its id, and every
/// Synchronisation Rule mapping and Object Matching Rule referring to it, and takes the Connector's casing.
/// </summary>
public class SchemaNameChange
{
    /// <summary>
    /// The name JIM held before the refresh.
    /// </summary>
    public string OldName { get; set; } = null!;

    /// <summary>
    /// The name the Connector now reports, which the refresh stores.
    /// </summary>
    public string NewName { get; set; } = null!;
}
