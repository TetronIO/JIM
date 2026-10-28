// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models.Api;

/// <summary>
/// A query whose result columns to describe, without running it for rows.
/// </summary>
/// <remarks>Issue #2570.</remarks>
public class DescribeCatalogueStatementRequest
{
    /// <summary>
    /// A single SELECT (or WITH) statement, with no terminating semicolon, as an Object Type's 'select' would be written.
    /// </summary>
    public string? Statement { get; set; }
}
