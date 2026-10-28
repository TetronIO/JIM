// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using Serilog;
namespace JIM.Models.Interfaces;

/// <summary>
/// Enables a Connector to describe what its source contains (schemas, tables and views, their columns and keys),
/// so an administrator can choose from it rather than having to know and type each name. Purely informational:
/// reading the catalogue never changes a setting; the setting named by <see cref="Catalogue"/> is changed only by
/// the administrator's own edit.
/// </summary>
/// <remarks>
/// Database-shaped on purpose. The realistic second implementer is another database engine, and a vocabulary
/// stretched to also describe a directory or a file would describe each of them badly.
/// </remarks>
public interface IConnectorCatalogue
{
    /// <summary>
    /// Which setting the catalogue helps the administrator write. A property of the Connector, not of any Connected
    /// System's settings, so it can be read before anything has been configured.
    /// </summary>
    public ConnectorCatalogueDeclaration Catalogue { get; }

    /// <summary>
    /// The tables and views the configured account can see, in the order the source reports them.
    /// </summary>
    public Task<List<ConnectorCatalogueObject>> GetCatalogueObjectsAsync(List<ConnectedSystemSettingValue> settings, ILogger logger, CancellationToken cancellationToken = default);

    /// <summary>
    /// One table or view's columns and keys. A null schema name finds the object by name alone, which fails where
    /// the name is in more than one schema.
    /// </summary>
    /// <returns>Null where the configured account cannot see an object of that name.</returns>
    /// <exception cref="ArgumentException">No schema was given and the name is in more than one.</exception>
    public Task<ConnectorCatalogueObjectDetail?> GetCatalogueObjectAsync(List<ConnectedSystemSettingValue> settings, string? schemaName, string objectName, ILogger logger, CancellationToken cancellationToken = default);

    /// <summary>
    /// The columns a query would return, learnt without reading a row.
    /// </summary>
    /// <exception cref="ArgumentException">The statement is not a single query, or the source would not accept it.</exception>
    public Task<ConnectorCatalogueObjectDetail> DescribeStatementAsync(List<ConnectedSystemSettingValue> settings, string statement, ILogger logger, CancellationToken cancellationToken = default);
}
