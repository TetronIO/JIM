// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Models.Staging;
using Serilog;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace JIM.Connectors.Sql;

/// <summary>
/// Turns a database's catalogue and the administrator's Object Types document into a Connector Schema:
/// one Connected System Object Type per configured object type, its columns typed by the dialect's own
/// mapping, its related tables as multi-valued attributes, and its anchor as the recommended external ID.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue says what columns exist and what type they are; only the administrator can say which
/// of them identify an object, and which carry another object type's anchor. So this class never
/// guesses: what the catalogue states, it reads; what it cannot state, it takes from configuration; and
/// a declared foreign key that lines up with another object type's anchor becomes a suggestion an
/// administrator confirms, never configuration applied on their behalf.
/// </para>
/// <para>
/// Failure is deliberately asymmetric. A column JIM has no type for is skipped with a warning, because
/// refusing an object type over a spatial or XML column nobody wants to synchronise would make the
/// Connector unusable against ordinary line-of-business tables. The same column in a load-bearing
/// position (an anchor, a configured reference, a related table's value or join column) fails the whole
/// discovery, because there is nothing to work around: every object of the type would be unidentifiable
/// or its attribute unreadable.
/// </para>
/// </remarks>
internal sealed class SqlConnectorSchema
{
    /// <summary>
    /// What a composed anchor's parts are joined with. JIM identifies a Connected System Object by one
    /// attribute value, so a composite key is projected as one attribute; the separator is a character
    /// no sane column name contains, which keeps the projection recognisable in the portal.
    /// </summary>
    internal const string ComposedAnchorSeparator = "+";

    /// <summary>
    /// What a composite anchor's single projected attribute is called. Shared with import, which has to
    /// compose exactly the attribute discovery declared, or the object has no external ID value.
    /// </summary>
    internal static string ComposedAnchorAttributeName(IReadOnlyList<string> anchorColumns) =>
        string.Join(ComposedAnchorSeparator, anchorColumns);

    private readonly ISqlProvider _provider;
    private readonly DbConnection _connection;
    private readonly SqlSchemaConfiguration _configuration;
    private readonly SqlTypeMappingOptions _typeMappingOptions;
    private readonly ILogger _logger;
    private readonly ConnectorSchema _schema = new();

    internal SqlConnectorSchema(
        ISqlProvider provider,
        DbConnection connection,
        SqlSchemaConfiguration configuration,
        SqlTypeMappingOptions typeMappingOptions,
        ILogger logger)
    {
        _provider = provider;
        _connection = connection;
        _configuration = configuration;
        _typeMappingOptions = typeMappingOptions;
        _logger = logger;
    }

    /// <exception cref="SqlSchemaConfigurationException">The configuration names something the database does not have, or a load-bearing column has no JIM attribute type.</exception>
    internal async Task<ConnectorSchema> GetSchemaAsync()
    {
        // Enumerated once for the whole discovery: it is what resolves an unqualified table name, tells
        // a table from a view, and turns a name the account cannot see into a message that says so.
        var tables = await ReadCatalogueObjectsAsync(_provider.TablesCommandText);
        var views = await ReadCatalogueObjectsAsync(_provider.ViewsCommandText);

        _logger.Debug("SqlConnectorSchema: the database account can see {TableCount} table(s) and {ViewCount} view(s)", tables.Count, views.Count);

        var sources = _configuration.ObjectTypes.ToDictionary(
            objectType => objectType.Name,
            objectType => ResolveSource(objectType, tables, views),
            StringComparer.OrdinalIgnoreCase);

        var anchorIndex = BuildAnchorIndex(sources);

        foreach (var objectType in _configuration.ObjectTypes)
            _schema.ObjectTypes.Add(await BuildObjectTypeAsync(objectType, sources[objectType.Name], anchorIndex));

        return _schema;
    }

    #region Object types

    private async Task<ConnectorSchemaObjectType> BuildObjectTypeAsync(
        SqlObjectTypeConfiguration configuration,
        SqlSchemaSource source,
        IReadOnlyDictionary<string, string> anchorIndex)
    {
        var columns = source.IsStatement
            ? await ReadStatementColumnsAsync(configuration)
            : await ReadColumnsAsync(source.SchemaName, source.ObjectName);

        var columnsByName = columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var objectType = new ConnectorSchemaObjectType(configuration.Name);

        // Everything load-bearing is checked before a single attribute is emitted, so a configuration
        // that cannot work never produces a half-built object type.
        var anchorColumns = RequireColumns(configuration, columnsByName, configuration.AnchorColumns, "anchor column");
        var referenceColumns = configuration.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        RequireColumns(configuration, columnsByName, [.. referenceColumns.Keys], "reference column");

        var anchorColumnNames = new HashSet<string>(configuration.AnchorColumns, StringComparer.OrdinalIgnoreCase);
        var writability = source.IsTable ? AttributeWritability.Writable : AttributeWritability.ReadOnly;

        foreach (var column in columns)
        {
            var isAnchor = anchorColumnNames.Contains(column.Name);
            var isReference = referenceColumns.ContainsKey(column.Name);

            // A column in a load-bearing position must map: an unreadable anchor makes every object of
            // the type unidentifiable, and an unreadable reference cannot resolve to anything.
            var mustMap = isAnchor || isReference;
            var attributeType = MapColumnType(configuration, column, mustMap, $"column '{column.Name}'");
            if (attributeType == null)
                continue;

            objectType.Attributes.Add(new ConnectorSchemaAttribute(
                column.Name,
                isReference ? AttributeDataType.Reference : attributeType.Value,
                AttributePlurality.SingleValued,
                required: !column.IsNullable,
                className: null,
                writability: isAnchor ? AttributeWritability.ReadOnly : writability));
        }

        await AddRelatedTableAttributesAsync(configuration, objectType, writability);

        objectType.RecommendedExternalIdAttribute = ResolveExternalIdAttribute(configuration, objectType, anchorColumns);

        if (source.IsTable)
            await AddForeignKeySuggestionsAsync(configuration, objectType, source, referenceColumns, anchorIndex);

        return objectType;
    }

    /// <summary>
    /// Decides which attribute identifies an object of this type.
    /// </summary>
    /// <remarks>
    /// JIM identifies a Connected System Object by one attribute value, and a composite key is more than
    /// one. It is therefore projected as a single synthesised Text attribute composed of its parts, with
    /// the parts still available individually; import composes the value, which is why nothing can be
    /// written to it.
    /// </remarks>
    private static ConnectorSchemaAttribute ResolveExternalIdAttribute(
        SqlObjectTypeConfiguration configuration,
        ConnectorSchemaObjectType objectType,
        IReadOnlyList<SqlDiscoveredColumn> anchorColumns)
    {
        if (anchorColumns.Count == 1)
            return objectType.Attributes.Single(attribute => string.Equals(attribute.Name, anchorColumns[0].Name, StringComparison.OrdinalIgnoreCase));

        var composedName = ComposedAnchorAttributeName(configuration.AnchorColumns);

        if (objectType.Attributes.Any(attribute => string.Equals(attribute.Name, composedName, StringComparison.OrdinalIgnoreCase)))
            throw new SqlSchemaConfigurationException(
                $"Object Type '{configuration.Name}' has a column called '{composedName}', which is the name JIM composes for its multi-column anchor. Rename the column, or expose it through a view under another name.");

        var composedAnchor = new ConnectorSchemaAttribute(
            composedName,
            AttributeDataType.Text,
            AttributePlurality.SingleValued,
            required: true,
            className: null,
            writability: AttributeWritability.ReadOnly)
        {
            Description = $"Composed by JIM from the anchor columns {string.Join(", ", configuration.AnchorColumns)}, because a Connected System Object is identified by one value."
        };

        objectType.Attributes.Add(composedAnchor);
        return composedAnchor;
    }

    #endregion

    #region Related tables

    private async Task AddRelatedTableAttributesAsync(
        SqlObjectTypeConfiguration configuration,
        ConnectorSchemaObjectType objectType,
        AttributeWritability writability)
    {
        foreach (var relatedTable in configuration.RelatedTables)
        {
            if (objectType.Attributes.Any(attribute => string.Equals(attribute.Name, relatedTable.AttributeName, StringComparison.OrdinalIgnoreCase)))
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{configuration.Name}' has both a column and a related table attribute called '{relatedTable.AttributeName}'. Give the related table attribute another name, so neither one shadows the other.");

            var columns = await ReadColumnsAsync(relatedTable.SchemaName, relatedTable.TableName);
            if (columns.Count == 0)
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{configuration.Name}' reads attribute '{relatedTable.AttributeName}' from {Describe(relatedTable.SchemaName, relatedTable.TableName)}, which this database account cannot see. Check the name, the schema, and the permissions granted to the account JIM connects as.");

            var columnsByName = columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var joinColumn in relatedTable.JoinColumns.Where(joinColumn => !columnsByName.ContainsKey(joinColumn)))
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{configuration.Name}' joins attribute '{relatedTable.AttributeName}' on column '{joinColumn}', which {Describe(relatedTable.SchemaName, relatedTable.TableName)} does not have.");

            if (!columnsByName.TryGetValue(relatedTable.ValueColumn, out var valueColumn))
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{configuration.Name}' takes attribute '{relatedTable.AttributeName}' from column '{relatedTable.ValueColumn}', which {Describe(relatedTable.SchemaName, relatedTable.TableName)} does not have.");

            // A multi-valued attribute's whole point is its values, so an unreadable value column is not
            // something to work around.
            var attributeType = MapColumnType(configuration, valueColumn, mustMap: true, $"attribute '{relatedTable.AttributeName}'");

            objectType.Attributes.Add(new ConnectorSchemaAttribute(
                relatedTable.AttributeName,
                relatedTable.ReferencesObjectType != null ? AttributeDataType.Reference : attributeType!.Value,
                AttributePlurality.MultiValued,
                required: false,
                className: Describe(relatedTable.SchemaName, relatedTable.TableName),
                writability: writability));
        }
    }

    #endregion

    #region Foreign key suggestions

    /// <summary>
    /// Indexes each object type's anchor by the table and column it lives in, so a declared foreign key
    /// can be recognised as pointing at one. Only single-column anchors on table-backed object types
    /// take part: a composite anchor is not what a single foreign key column carries, and a view or a
    /// statement has no table for a constraint to reference.
    /// </summary>
    private IReadOnlyDictionary<string, string> BuildAnchorIndex(IReadOnlyDictionary<string, SqlSchemaSource> sources)
    {
        var anchorIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var objectType in _configuration.ObjectTypes.Where(objectType => objectType.AnchorColumns.Count == 1))
        {
            var source = sources[objectType.Name];
            if (!source.IsTable)
                continue;

            anchorIndex[AnchorKey(source.SchemaName, source.ObjectName, objectType.AnchorColumns[0])] = objectType.Name;
        }

        return anchorIndex;
    }

    /// <summary>
    /// Surfaces a declared foreign key that lines up with another object type's anchor as advice on the
    /// attribute it belongs to.
    /// </summary>
    /// <remarks>
    /// The Connector Schema has no field for "a suggestion", and inventing one would mean changing a
    /// model every Connector shares for something only this one has. An attribute's Description is the
    /// per-column, administrator-facing text the model does carry: it survives the schema import, and
    /// the portal renders it in a Description column beside the attribute. So the suggestion is written
    /// there, and it stays advice: the attribute keeps the type its own SQL type maps to, and becomes a
    /// Reference only when the administrator configures the column as one.
    /// </remarks>
    private async Task AddForeignKeySuggestionsAsync(
        SqlObjectTypeConfiguration configuration,
        ConnectorSchemaObjectType objectType,
        SqlSchemaSource source,
        IReadOnlyDictionary<string, SqlColumnConfiguration> referenceColumns,
        IReadOnlyDictionary<string, string> anchorIndex)
    {
        if (anchorIndex.Count == 0)
            return;

        foreach (var foreignKey in await ReadForeignKeysAsync(source.SchemaName, source.ObjectName))
        {
            // Already configured: there is nothing left to suggest.
            if (referenceColumns.ContainsKey(foreignKey.ColumnName))
                continue;

            if (!anchorIndex.TryGetValue(AnchorKey(foreignKey.ReferencedSchema, foreignKey.ReferencedTable, foreignKey.ReferencedColumn), out var referencedObjectTypeName))
                continue;

            var attribute = objectType.Attributes.FirstOrDefault(a => string.Equals(a.Name, foreignKey.ColumnName, StringComparison.OrdinalIgnoreCase));
            if (attribute == null)
                continue;

            attribute.Description =
                $"Foreign key {foreignKey.ConstraintName} points at Object Type '{referencedObjectTypeName}'. To have JIM resolve it as a Reference, add this column to Object Type '{configuration.Name}' in {SqlConnectorConstants.SettingObjectTypes} with \"referencesObjectType\": \"{referencedObjectTypeName}\".";
        }
    }

    private static string AnchorKey(string? schemaName, string objectName, string columnName) => $"{schemaName}.{objectName}.{columnName}";

    #endregion

    #region Sources

    /// <summary>
    /// Works out which table or view an object type's source names, and refuses anything ambiguous.
    /// </summary>
    /// <remarks>
    /// Schema qualification is optional because a least-privilege account usually sees exactly one
    /// object of a given name, and asking for a schema an administrator does not know is friction for
    /// nothing. Where the name is not unique, the answer names the schemas it found, which is what the
    /// administrator needs to qualify it.
    /// </remarks>
    private static SqlSchemaSource ResolveSource(
        SqlObjectTypeConfiguration configuration,
        IReadOnlyList<SqlCatalogueObject> tables,
        IReadOnlyList<SqlCatalogueObject> views)
    {
        if (configuration.IsCustomSelect)
            return new SqlSchemaSource(null, string.Empty, SqlSchemaSourceKind.Statement);

        var candidates = tables.Select(table => (Object: table, Kind: SqlSchemaSourceKind.Table))
            .Concat(views.Select(view => (Object: view, Kind: SqlSchemaSourceKind.View)))
            .Where(candidate => string.Equals(candidate.Object.ObjectName, configuration.TableName, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => configuration.SchemaName == null || string.Equals(candidate.Object.SchemaName, configuration.SchemaName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
            throw new SqlSchemaConfigurationException(
                $"Object Type '{configuration.Name}' reads from {Describe(configuration.SchemaName, configuration.TableName!)}, which this database account cannot see. Check the name, the schema, and the permissions granted to the account JIM connects as.");

        if (candidates.Count > 1)
            throw new SqlSchemaConfigurationException(
                $"Object Type '{configuration.Name}' names '{configuration.TableName}', which exists in more than one schema ({string.Join(", ", candidates.Select(candidate => candidate.Object.SchemaName))}). Add a 'schema' to say which one.");

        var resolved = candidates[0];
        return new SqlSchemaSource(resolved.Object.SchemaName, resolved.Object.ObjectName, resolved.Kind);
    }

    /// <summary>
    /// Refuses configuration naming columns the source does not have, before anything is built from it.
    /// </summary>
    private static List<SqlDiscoveredColumn> RequireColumns(
        SqlObjectTypeConfiguration configuration,
        IReadOnlyDictionary<string, SqlDiscoveredColumn> columnsByName,
        IReadOnlyList<string> columnNames,
        string description)
    {
        var columns = new List<SqlDiscoveredColumn>(columnNames.Count);

        foreach (var columnName in columnNames)
        {
            if (!columnsByName.TryGetValue(columnName, out var column))
                throw new SqlSchemaConfigurationException(
                    $"Object Type '{configuration.Name}' names '{columnName}' as a {description}, but its source does not have a column called that. The columns it does have are: {string.Join(", ", columnsByName.Keys)}.");

            columns.Add(column);
        }

        return columns;
    }

    /// <summary>
    /// Maps a column's SQL type, deciding between a warning and a failure by whether anything depends
    /// on the column.
    /// </summary>
    /// <returns>The attribute type, or null where an unmappable column was skipped.</returns>
    private AttributeDataType? MapColumnType(SqlObjectTypeConfiguration configuration, SqlDiscoveredColumn column, bool mustMap, string description)
    {
        try
        {
            return _provider.MapColumnType(column.ColumnType, _typeMappingOptions);
        }
        catch (SqlTypeMappingException ex)
        {
            if (mustMap)
                throw new SqlSchemaConfigurationException($"Object Type '{configuration.Name}' cannot use {description}. {ex.Message}", ex);

            _schema.Warnings.Add(
                $"Object Type '{configuration.Name}': column '{column.Name}' was skipped, because its type '{column.ColumnType.TypeName}' has no equivalent JIM attribute type. Expose it through a view that casts it to a supported type if it needs synchronising.");

            return null;
        }
    }

    private static string Describe(string? schemaName, string objectName) =>
        string.IsNullOrWhiteSpace(schemaName) ? objectName : $"{schemaName}.{objectName}";

    #endregion

    #region Catalogue reads

    private async Task<List<SqlCatalogueObject>> ReadCatalogueObjectsAsync(string commandText)
    {
        var catalogueObjects = new List<SqlCatalogueObject>();

        using var command = _provider.CreateCommand(_connection, commandText);
        using var reader = await command.ExecuteReaderAsync();

        var schemaNameOrdinal = reader.GetOrdinal(SqlCatalogueColumns.SchemaName);
        var objectNameOrdinal = reader.GetOrdinal(SqlCatalogueColumns.ObjectName);

        while (await reader.ReadAsync())
            catalogueObjects.Add(new SqlCatalogueObject(GetNullableString(reader, schemaNameOrdinal), reader.GetString(objectNameOrdinal)));

        return catalogueObjects;
    }

    private async Task<List<SqlDiscoveredColumn>> ReadColumnsAsync(string? schemaName, string objectName)
    {
        var columns = new List<SqlDiscoveredColumn>();

        using var command = _provider.CreateCommand(_connection, _provider.ColumnsCommandText);

        // Schema and object names are values here, not identifiers, so they are bound rather than
        // interpolated even though the identifiers themselves have already been validated.
        command.Parameters.Add(_provider.CreateParameter(SqlCatalogueParameters.SchemaName, schemaName));
        command.Parameters.Add(_provider.CreateParameter(SqlCatalogueParameters.ObjectName, objectName));

        using var reader = await command.ExecuteReaderAsync();

        var columnNameOrdinal = reader.GetOrdinal(SqlCatalogueColumns.ColumnName);
        var dataTypeNameOrdinal = reader.GetOrdinal(SqlCatalogueColumns.DataTypeName);
        var maxLengthOrdinal = reader.GetOrdinal(SqlCatalogueColumns.MaxLength);
        var precisionOrdinal = reader.GetOrdinal(SqlCatalogueColumns.NumericPrecision);
        var scaleOrdinal = reader.GetOrdinal(SqlCatalogueColumns.NumericScale);
        var isNullableOrdinal = reader.GetOrdinal(SqlCatalogueColumns.IsNullable);

        while (await reader.ReadAsync())
        {
            var columnType = new SqlColumnType(
                reader.GetString(dataTypeNameOrdinal),
                GetNullableInt(reader, precisionOrdinal),
                GetNullableInt(reader, scaleOrdinal),
                GetNullableInt(reader, maxLengthOrdinal));

            columns.Add(new SqlDiscoveredColumn(
                reader.GetString(columnNameOrdinal),
                columnType,
                string.Equals(GetNullableString(reader, isNullableOrdinal), "YES", StringComparison.OrdinalIgnoreCase)));
        }

        return columns;
    }

    /// <summary>
    /// Learns the shape of an administrator-supplied SELECT statement, which no catalogue describes.
    /// </summary>
    /// <remarks>
    /// The statement is executed schema-only, so the database plans it and reports its result columns
    /// without reading a single row; on a large table that difference is the whole cost of discovery.
    /// The column metadata it hands back carries the same type name, precision and scale a catalogue
    /// would, so the type mapper sees no difference between a statement's column and a table's.
    /// </remarks>
    private async Task<List<SqlDiscoveredColumn>> ReadStatementColumnsAsync(SqlObjectTypeConfiguration configuration)
    {
        using var command = _provider.CreateCommand(_connection, configuration.SelectStatement!);

        DbDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(CommandBehavior.SchemaOnly);
        }
        catch (DbException ex)
        {
            throw new SqlSchemaConfigurationException(
                $"Object Type '{configuration.Name}' has a 'select' the database would not accept: {ex.Message}", ex);
        }

        using (reader)
        {
            return reader.GetColumnSchema()
                .Select(column => new SqlDiscoveredColumn(
                    column.ColumnName,
                    new SqlColumnType(column.DataTypeName ?? string.Empty, column.NumericPrecision, column.NumericScale, column.ColumnSize),
                    column.AllowDBNull ?? true))
                .ToList();
        }
    }

    private async Task<List<SqlDiscoveredForeignKey>> ReadForeignKeysAsync(string? schemaName, string objectName)
    {
        var foreignKeys = new List<SqlDiscoveredForeignKey>();

        using var command = _provider.CreateCommand(_connection, _provider.ForeignKeyColumnsCommandText);
        command.Parameters.Add(_provider.CreateParameter(SqlCatalogueParameters.SchemaName, schemaName));
        command.Parameters.Add(_provider.CreateParameter(SqlCatalogueParameters.ObjectName, objectName));

        using var reader = await command.ExecuteReaderAsync();

        var constraintNameOrdinal = reader.GetOrdinal(SqlCatalogueColumns.ConstraintName);
        var columnNameOrdinal = reader.GetOrdinal(SqlCatalogueColumns.ColumnName);
        var referencedSchemaOrdinal = reader.GetOrdinal(SqlCatalogueColumns.ReferencedSchema);
        var referencedTableOrdinal = reader.GetOrdinal(SqlCatalogueColumns.ReferencedTable);
        var referencedColumnOrdinal = reader.GetOrdinal(SqlCatalogueColumns.ReferencedColumn);

        while (await reader.ReadAsync())
        {
            foreignKeys.Add(new SqlDiscoveredForeignKey(
                reader.GetString(constraintNameOrdinal),
                reader.GetString(columnNameOrdinal),
                GetNullableString(reader, referencedSchemaOrdinal),
                reader.GetString(referencedTableOrdinal),
                reader.GetString(referencedColumnOrdinal)));
        }

        return foreignKeys;
    }

    private static string? GetNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>
    /// Reads a catalogue's numeric column without assuming its CLR type: SQL Server reports precision
    /// and scale as small integers, Oracle as NUMBER, which a driver may hand back as a decimal.
    /// </summary>
    private static int? GetNullableInt(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    #endregion
}

/// <summary>
/// A table or view as the catalogue reports it.
/// </summary>
internal sealed record SqlCatalogueObject(string? SchemaName, string ObjectName);

/// <summary>
/// A column as discovery found it, whether from a catalogue or from a statement's result metadata.
/// </summary>
internal sealed record SqlDiscoveredColumn(string Name, SqlColumnType ColumnType, bool IsNullable);

/// <summary>
/// One column of a declared foreign key, with both sides of the constraint.
/// </summary>
internal sealed record SqlDiscoveredForeignKey(string ConstraintName, string ColumnName, string? ReferencedSchema, string ReferencedTable, string ReferencedColumn);

/// <summary>
/// What an object type's objects are read from, which decides how its shape is learned, whether it can
/// be exported to, and whether constraint metadata exists for it at all.
/// </summary>
internal enum SqlSchemaSourceKind
{
    Table = 0,
    View = 1,
    Statement = 2
}

/// <summary>
/// An object type's source, resolved against what the database account can actually see.
/// </summary>
internal sealed record SqlSchemaSource(string? SchemaName, string ObjectName, SqlSchemaSourceKind Kind)
{
    internal bool IsTable => Kind == SqlSchemaSourceKind.Table;

    internal bool IsStatement => Kind == SqlSchemaSourceKind.Statement;
}
