// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.Web.Models;

namespace JIM.Web.Services;

/// <summary>
/// Reads one Connected System's catalogue (its tables, views, columns and keys) for a portal editor, with the
/// settings as they stand on screen. Reads never throw for a source that cannot be read; they say why instead.
/// </summary>
public interface IConnectedSystemCatalogueSource
{
    /// <summary>The tables and views the configured account can see.</summary>
    Task<ConnectedSystemCatalogueRead<IReadOnlyList<ConnectorCatalogueObject>>> GetObjectsAsync(CancellationToken cancellationToken = default);

    /// <summary>One table or view's columns and keys; a null value where the account cannot see it.</summary>
    Task<ConnectedSystemCatalogueRead<ConnectorCatalogueObjectDetail>> GetObjectAsync(string? schemaName, string objectName, CancellationToken cancellationToken = default);

    /// <summary>The columns a SELECT statement would return, without reading a row.</summary>
    Task<ConnectedSystemCatalogueRead<ConnectorCatalogueObjectDetail>> DescribeStatementAsync(string statement, CancellationToken cancellationToken = default);
}
