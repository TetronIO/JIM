// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace JIM.Connectors.Sql;

/// <summary>
/// Reads objects out of a database for a Full Import: a page of rows at a time per configured Object
/// Type, ordered and seeked on the anchor, with each object type's multi-valued attributes gathered from
/// its related tables in one query per page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Paging.</b> Keyset pagination, never OFFSET, so the cost of reading page N does not grow with N.
/// Each configured Object Type carries its own Connected System Pagination Token holding the anchor the
/// last page ended on; JIM replays it on the next call. A page shorter than the Run Profile's page size
/// means the object type is drained and no token is returned for it, and an import result carrying no
/// tokens at all is how JIM is told the run is over. Returning one forever is an infinite import.
/// </para>
/// <para>
/// <b>Failure is deliberately asymmetric</b>, matching schema discovery. A value that cannot be
/// converted errors that one object and leaves the rest of the page alone; configuration that cannot
/// work (an anchor the schema does not have, a reference to an object type with no single anchor) fails
/// the run, because every object of the type would be affected. A NULL anchor also fails the run: it
/// makes an object unidentifiable and, because the keyset seeks past the last anchor read, it would
/// otherwise be re-read on every page for ever.
/// </para>
/// </remarks>
internal sealed class SqlConnectorImport
{
    /// <summary>
    /// The bind variable carrying the Run Profile's page size.
    /// </summary>
    internal const string PageSizeParameterName = "jimPageSize";

    /// <summary>
    /// The bind variables carrying the previous page's last anchor, suffixed by anchor column index.
    /// </summary>
    internal const string AnchorParameterPrefix = "jimAnchor";

    /// <summary>
    /// The bind variables carrying a page's anchors into a related-table gather, suffixed by the page
    /// row and the anchor column each one belongs to.
    /// </summary>
    internal const string JoinParameterPrefix = "jimJoin";

    /// <summary>
    /// How many bind variables one related-table gather may carry. Microsoft SQL Server caps a statement
    /// at 2,100 parameters, so a large page size against a composite anchor would otherwise fail at the
    /// server; a page beyond this is gathered in more than one query rather than one per row.
    /// </summary>
    private const int MaxJoinParametersPerQuery = 900;

    private readonly ISqlProvider _provider;
    private readonly DbConnection _connection;
    private readonly SqlSchemaConfiguration _configuration;
    private readonly TimeZoneInfo _databaseTimeZone;
    private readonly ConnectedSystem _connectedSystem;
    private readonly ConnectedSystemRunProfile _runProfile;
    private readonly List<ConnectedSystemPaginationToken> _paginationTokens;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly IConnectorProgress _progress;

    internal SqlConnectorImport(
        ISqlProvider provider,
        DbConnection connection,
        SqlSchemaConfiguration configuration,
        TimeZoneInfo databaseTimeZone,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        _provider = provider;
        _connection = connection;
        _configuration = configuration;
        _databaseTimeZone = databaseTimeZone;
        _connectedSystem = connectedSystem;
        _runProfile = runProfile;
        _paginationTokens = paginationTokens;
        _logger = logger;
        _cancellationToken = cancellationToken;
        _progress = progress;
    }

    /// <exception cref="SqlSchemaConfigurationException">The configuration cannot be acted on: an anchor the schema does not have, or a reference JIM could never resolve.</exception>
    /// <exception cref="InvalidDataException">A row could not be paged past: a NULL or unreadable anchor, or a pagination token JIM replayed that this configuration cannot parse.</exception>
    internal async Task<ConnectedSystemImportResult> GetFullImportObjectsAsync()
    {
        var result = new ConnectedSystemImportResult();
        var plans = BuildPlans();

        if (plans.Count == 0)
        {
            _logger.Warning("SqlConnectorImport: no configured Object Type has been selected for synchronisation, so there is nothing to import");
            return result;
        }

        // Only on the initial call: the count is the whole run's, JIM keeps it, and asking again on
        // every page would make an expensive query the price of paging.
        if (_paginationTokens.Count == 0)
            await ReportExpectedObjectCountAsync(plans);

        var objectsRead = 0;

        foreach (var plan in plans)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("SqlConnectorImport: cancellation requested. Stopping between pages");
                return result;
            }

            var token = _paginationTokens.SingleOrDefault(paginationToken => paginationToken.Name == plan.TokenName);

            // No token on a subsequent call means this object type was drained by an earlier one.
            if (_paginationTokens.Count > 0 && token == null)
                continue;

            var page = SqlImportPagePosition.FromToken(token, plan);

            await _progress.EnterPhaseAsync(SqlConnectorPhases.Fetch, $"Fetching {plan.Name} objects (page {page.PageNumber})...");

            var rows = await ReadPageAsync(plan, page);
            var importObjects = BuildImportObjects(plan, rows, out var anchorKeys);

            await GatherRelatedAttributesAsync(plan, rows, importObjects, anchorKeys);

            result.ImportObjects.AddRange(importObjects);
            objectsRead += importObjects.Count;

            // One call drains a page per configured Object Type, so the Activity's counters move while
            // the call is still in flight rather than only when it returns.
            await _progress.ReportObjectsReadAsync(objectsRead);

            // A short page is the end of this object type; a full one may or may not be, and there is no
            // way to tell without asking, so one empty read at the end is unavoidable.
            if (rows.Count == _runProfile.PageSize)
                result.PaginationTokens.Add(page.ToToken(plan, rows[^1]));
        }

        return result;
    }

    #region Planning

    /// <summary>
    /// Works out what each configured Object Type's page has to read, and refuses configuration that
    /// could not produce identifiable objects, before a single row is read.
    /// </summary>
    private List<SqlImportPlan> BuildPlans()
    {
        if (_connectedSystem.ObjectTypes == null)
            throw new SqlSchemaConfigurationException($"Connected System '{_connectedSystem.Name}' has no schema. Import the schema before running an import.");

        var plans = new List<SqlImportPlan>();

        foreach (var configuration in _configuration.ObjectTypes)
        {
            var objectType = _connectedSystem.ObjectTypes.FirstOrDefault(candidate => string.Equals(candidate.Name, configuration.Name, StringComparison.OrdinalIgnoreCase));

            // An object type the administrator has not selected is not part of this run, and one absent
            // from the schema altogether is a schema that predates the configuration; both are answered
            // by importing the schema again, neither is a reason to fail a run of the others.
            if (objectType is not { Selected: true })
            {
                _logger.Debug("SqlConnectorImport: Object Type '{ObjectType}' is not selected for synchronisation, so it is not being imported", configuration.Name);
                continue;
            }

            plans.Add(BuildPlan(configuration, objectType));
        }

        return plans;
    }

    private SqlImportPlan BuildPlan(SqlObjectTypeConfiguration configuration, ConnectedSystemObjectType objectType)
    {
        var attributesByName = objectType.Attributes.ToDictionary(attribute => attribute.Name, StringComparer.OrdinalIgnoreCase);

        var anchorColumns = configuration.AnchorColumns
            .Select(anchorColumn => new SqlImportColumn(anchorColumn, RequireAttributeType(configuration, attributesByName, anchorColumn, "anchor column")))
            .ToList();

        var relatedTableNames = configuration.RelatedTables
            .Select(relatedTable => relatedTable.AttributeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var composedAnchorName = anchorColumns.Count > 1 ? SqlConnectorSchema.ComposedAnchorAttributeName(configuration.AnchorColumns) : null;
        var referenceColumns = configuration.Columns.ToDictionary(
            column => column.Name,
            column => ResolveReferencedAnchorType(configuration, column.ReferencesObjectType, $"column '{column.Name}'"),
            StringComparer.OrdinalIgnoreCase);

        // Everything the administrator selected that is genuinely a column of the source: not the
        // attribute JIM composes for a multi-column anchor, and not one that lives in a related table.
        var attributes = objectType.Attributes
            .Where(attribute => attribute.Selected || attribute.IsExternalId)
            .Where(attribute => !relatedTableNames.Contains(attribute.Name))
            .Where(attribute => !string.Equals(attribute.Name, composedAnchorName, StringComparison.OrdinalIgnoreCase))
            .Select(attribute => new SqlImportColumn(attribute.Name, attribute.Type))
            .ToList();

        // The anchor is always read, whether or not it is selected: it orders the page and positions the
        // next one.
        var selectColumns = anchorColumns.Select(column => column.Name)
            .Concat(attributes.Select(attribute => attribute.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var relatedTables = configuration.RelatedTables
            .Where(relatedTable => attributesByName.TryGetValue(relatedTable.AttributeName, out var attribute) && (attribute.Selected || attribute.IsExternalId))
            .Select(relatedTable => new SqlImportRelatedTable(
                relatedTable,
                RequireAttributeType(configuration, attributesByName, relatedTable.AttributeName, "related table attribute"),
                relatedTable.ReferencesObjectType == null
                    ? null
                    : ResolveReferencedAnchorType(configuration, relatedTable.ReferencesObjectType, $"related table attribute '{relatedTable.AttributeName}'")))
            .ToList();

        return new SqlImportPlan(configuration, anchorColumns, attributes, selectColumns, referenceColumns, relatedTables, composedAnchorName);
    }

    private static AttributeDataType RequireAttributeType(
        SqlObjectTypeConfiguration configuration,
        IReadOnlyDictionary<string, ConnectedSystemObjectTypeAttribute> attributesByName,
        string attributeName,
        string description)
    {
        if (!attributesByName.TryGetValue(attributeName, out var attribute))
            throw new SqlSchemaConfigurationException(
                $"Object Type '{configuration.Name}' names '{attributeName}' as a {description}, but the Connected System's schema has no attribute called that. Import the schema again so it matches the {SqlConnectorConstants.SettingObjectTypes} document.");

        return attribute.Type;
    }

    /// <summary>
    /// The type of the anchor a reference carries, which is what its string form has to be rendered
    /// from so that JIM can resolve it against the referenced object.
    /// </summary>
    private AttributeDataType ResolveReferencedAnchorType(SqlObjectTypeConfiguration configuration, string referencedObjectTypeName, string description)
    {
        var referenced = _configuration.ObjectTypes.First(objectType => string.Equals(objectType.Name, referencedObjectTypeName, StringComparison.OrdinalIgnoreCase));

        // One column carries one value, and a composite anchor is more than one, so there is nothing a
        // reference could be resolved from.
        if (referenced.AnchorColumns.Count != 1)
            throw new SqlSchemaConfigurationException(
                $"Object Type '{configuration.Name}' has {description} referencing Object Type '{referenced.Name}', whose anchor spans {referenced.AnchorColumns.Count} columns. A reference carries one anchor value, so it can only point at an object type identified by a single column.");

        var referencedObjectType = _connectedSystem.ObjectTypes!.FirstOrDefault(objectType => string.Equals(objectType.Name, referenced.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new SqlSchemaConfigurationException(
                $"Object Type '{configuration.Name}' has {description} referencing Object Type '{referenced.Name}', which the Connected System's schema does not have. Import the schema again so it matches the {SqlConnectorConstants.SettingObjectTypes} document.");

        var anchorAttribute = referencedObjectType.Attributes.FirstOrDefault(attribute => string.Equals(attribute.Name, referenced.AnchorColumns[0], StringComparison.OrdinalIgnoreCase))
            ?? throw new SqlSchemaConfigurationException(
                $"Object Type '{configuration.Name}' has {description} referencing Object Type '{referenced.Name}', whose anchor column '{referenced.AnchorColumns[0]}' is not in the Connected System's schema.");

        return anchorAttribute.Type;
    }

    #endregion

    #region Counting

    /// <summary>
    /// Asks the database how many objects this run will produce, which is what turns the fetch into a
    /// percentage and a time remaining rather than a number counting up.
    /// </summary>
    private async Task ReportExpectedObjectCountAsync(IReadOnlyList<SqlImportPlan> plans)
    {
        await _progress.EnterPhaseAsync(SqlConnectorPhases.Count, "Counting rows...");

        long expected = 0;
        foreach (var plan in plans)
            expected += await CountAsync(plan);

        _logger.Debug("SqlConnectorImport: expecting {ExpectedObjectCount} object(s) across {ObjectTypeCount} Object Type(s)", expected, plans.Count);

        await _progress.ReportExpectedObjectCountAsync(expected > int.MaxValue ? int.MaxValue : (int)expected);
    }

    private async Task<long> CountAsync(SqlImportPlan plan)
    {
        // No dialect divergence to hide behind the provider seam here: a count over a source is the same
        // statement in both, built from the same quoting the seam already provides.
        using var command = _provider.CreateCommand(_connection, $"SELECT COUNT(*) FROM {BuildFromClause(plan)}");
        var count = await command.ExecuteScalarAsync(_cancellationToken);

        return count == null || count == DBNull.Value ? 0 : Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    private string BuildFromClause(SqlImportPlan plan)
    {
        return plan.Configuration.IsCustomSelect
            ? $"({plan.Configuration.SelectStatement}) {_provider.QuoteIdentifier(SqlKeysetPageRequest.SourceAlias)}"
            : _provider.QualifyObjectName(plan.Configuration.SchemaName, plan.Configuration.TableName!);
    }

    #endregion

    #region Reading pages

    private async Task<List<object?[]>> ReadPageAsync(SqlImportPlan plan, SqlImportPagePosition page)
    {
        var request = new SqlKeysetPageRequest
        {
            SchemaName = plan.Configuration.SchemaName,
            ObjectName = plan.Configuration.TableName,
            SelectStatement = plan.Configuration.SelectStatement,
            SelectColumns = plan.SelectColumns,
            AnchorColumns = [.. plan.AnchorColumns.Select(anchorColumn => anchorColumn.Name)],
            PageSizeParameterName = PageSizeParameterName,
            LastAnchorParameterNames = page.IsFirstPage ? [] : [.. Enumerable.Range(0, plan.AnchorColumns.Count).Select(AnchorParameterName)]
        };

        using var command = _provider.CreateCommand(_connection, _provider.BuildKeysetPageCommandText(request));
        command.Parameters.Add(_provider.CreateParameter(PageSizeParameterName, _runProfile.PageSize));

        for (var index = 0; index < page.LastAnchor.Count; index++)
            command.Parameters.Add(_provider.CreateParameter(AnchorParameterName(index), BindAnchorValue(plan, index, page.LastAnchor[index])));

        var rows = new List<object?[]>();

        using var reader = await command.ExecuteReaderAsync(_cancellationToken);
        var ordinals = plan.SelectColumns.Select(reader.GetOrdinal).ToArray();

        while (await reader.ReadAsync(_cancellationToken))
        {
            var row = new object?[ordinals.Length];
            for (var index = 0; index < ordinals.Length; index++)
                row[index] = reader.IsDBNull(ordinals[index]) ? null : reader.GetValue(ordinals[index]);

            rows.Add(row);
        }

        return rows;
    }

    private static string AnchorParameterName(int index) => $"{AnchorParameterPrefix}{index}";

    /// <summary>
    /// Turns a pagination token's anchor back into the value a page boundary is compared against.
    /// </summary>
    /// <exception cref="InvalidDataException">The token cannot be read for this configuration, which would otherwise resume the page from the wrong row.</exception>
    private object BindAnchorValue(SqlImportPlan plan, int index, string tokenValue)
    {
        var anchorColumn = plan.AnchorColumns[index];

        if (!SqlAnchorValue.TryFromTokenString(tokenValue, anchorColumn.Type, out var value) || value == null)
            throw new InvalidDataException(
                $"Object Type '{plan.Name}' was replayed a pagination token whose anchor value for column '{anchorColumn.Name}' cannot be read as a {anchorColumn.Type}. Run a Full Import again to start from the beginning.");

        // The byte order a GUID is bound in is dialect-specific, so it goes back through the provider
        // rather than being handed to the driver as it came out of the token.
        return anchorColumn.Type == AttributeDataType.Guid ? _provider.ConvertFromGuid((Guid)value) : value;
    }

    #endregion

    #region Shaping objects

    private List<ConnectedSystemImportObject> BuildImportObjects(SqlImportPlan plan, IReadOnlyList<object?[]> rows, out List<string> anchorKeys)
    {
        var importObjects = new List<ConnectedSystemImportObject>(rows.Count);
        anchorKeys = new List<string>(rows.Count);

        foreach (var row in rows)
        {
            var importObject = new ConnectedSystemImportObject
            {
                // A Full Import states what is there; whether that is a create or an update is JIM's to
                // work out from what it already holds.
                ObjectType = plan.Name
            };

            anchorKeys.Add(ComposeAnchorKey(plan, row));

            if (plan.ComposedAnchorName != null)
                importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
                {
                    Name = plan.ComposedAnchorName,
                    Type = AttributeDataType.Text,
                    StringValues = [anchorKeys[^1]]
                });

            foreach (var column in plan.Attributes)
            {
                var value = row[plan.ColumnIndex(column.Name)];
                if (value == null)
                    continue;

                try
                {
                    AddValue(importObject, plan, column, value);
                }
                catch (Exception ex) when (IsValueConversionFailure(ex))
                {
                    SetValueError(importObject, plan, column.Name, ex);
                    break;
                }
            }

            importObjects.Add(importObject);
        }

        return importObjects;
    }

    /// <summary>
    /// The anchor as one string: what identifies the object to JIM where the anchor is composite, and
    /// what a related table's rows are matched back to their parent by in every case.
    /// </summary>
    /// <exception cref="InvalidDataException">A row's anchor is NULL or unreadable, which makes it both unidentifiable and impossible to page past.</exception>
    private string ComposeAnchorKey(SqlImportPlan plan, object?[] row)
    {
        var parts = new string[plan.AnchorColumns.Count];

        for (var index = 0; index < plan.AnchorColumns.Count; index++)
        {
            var anchorColumn = plan.AnchorColumns[index];
            var value = row[plan.ColumnIndex(anchorColumn.Name)];

            if (value == null)
                throw new InvalidDataException(
                    $"Object Type '{plan.Name}' returned a row with a NULL value in anchor column '{anchorColumn.Name}'. An anchor identifies an object and orders the page it arrived in, so a NULL makes both impossible; exclude such rows through a view, or choose another anchor.");

            try
            {
                parts[index] = SqlAnchorValue.ToTokenString(value, anchorColumn.Type);
            }
            catch (Exception ex) when (IsValueConversionFailure(ex))
            {
                throw new InvalidDataException(
                    $"Object Type '{plan.Name}' returned a row whose anchor column '{anchorColumn.Name}' could not be read as a {anchorColumn.Type}: {ex.Message}", ex);
            }
        }

        return string.Join(SqlConnectorSchema.ComposedAnchorSeparator, parts);
    }

    private void AddValue(ConnectedSystemImportObject importObject, SqlImportPlan plan, SqlImportColumn column, object value)
    {
        var attribute = importObject.Attributes.FirstOrDefault(candidate => candidate.Name == column.Name);
        if (attribute == null)
        {
            attribute = new ConnectedSystemImportObjectAttribute { Name = column.Name, Type = column.Type };
            importObject.Attributes.Add(attribute);
        }

        // A column configured as a reference carries the referenced row's anchor, and JIM resolves it
        // into a hard reference during the import, so the value is rendered exactly as that object's own
        // anchor attribute will be.
        if (plan.ReferenceColumns.TryGetValue(column.Name, out var referencedAnchorType))
        {
            attribute.ReferenceValues.Add(SqlAnchorValue.ToTokenString(value, referencedAnchorType));
            return;
        }

        ApplyTypedValue(attribute, column.Type, value);
    }

    private void ApplyTypedValue(ConnectedSystemImportObjectAttribute attribute, AttributeDataType type, object value)
    {
        switch (type)
        {
            case AttributeDataType.Text:
                attribute.StringValues.Add(ToText(value));
                break;
            case AttributeDataType.Number:
                attribute.IntValues.Add(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                break;
            case AttributeDataType.LongNumber:
                attribute.LongValues.Add(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case AttributeDataType.Decimal:
                attribute.DecimalValues.Add(ToDecimal(value));
                break;
            case AttributeDataType.Boolean:
                attribute.BoolValue = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                break;
            case AttributeDataType.DateTime:
                attribute.DateTimeValue = ToUtc(value);
                break;
            case AttributeDataType.Guid:
                attribute.GuidValues.Add(_provider.ConvertToGuid(value));
                break;
            case AttributeDataType.Binary:
                attribute.ByteValues.Add(value as byte[] ?? throw new InvalidCastException($"A Binary attribute cannot be built from a {value.GetType().Name} value."));
                break;
            case AttributeDataType.Reference:
                // Only reached where a column is typed Reference in the schema but is not configured as
                // one, which leaves nothing to say what its values point at.
                throw new NotSupportedException($"The attribute is a Reference, but no 'referencesObjectType' is configured for it in {SqlConnectorConstants.SettingObjectTypes}, so JIM has nothing to resolve its values against.");
            default:
                throw new NotSupportedException($"A {type} attribute cannot be imported from a database column.");
        }
    }

    /// <summary>
    /// Renders a value that is not already text. Never a culture-sensitive ToString, and never a plain
    /// one for a decimal: 5.00 and 5.0 have to produce the same string, or they read as two values.
    /// </summary>
    private static string ToText(object value)
    {
        return value switch
        {
            string text => text,
            decimal number => DecimalAttributeValue.ToCanonicalString(number),
            DateTimeOffset dateTimeOffset => dateTimeOffset.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            byte[] bytes => Convert.ToHexString(bytes),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    /// <summary>
    /// Converts to decimal without routing through double, which would drop digits. FLOAT and REAL
    /// columns are the exception the PRD documents: they are approximate binary types, so the conversion
    /// from what the driver hands back is not bit-exact, and mapping them to Text instead would
    /// reintroduce lexicographic comparison of numbers.
    /// </summary>
    private static decimal ToDecimal(object value) =>
        value as decimal? ?? Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// Normalises a date and time to UTC, which is the only way JIM stores one.
    /// </summary>
    /// <remarks>
    /// A value carrying its own offset needs no configuration to interpret it. A value carrying none is
    /// ambiguous at the wire level, so it is interpreted in the time zone the administrator declared for
    /// this Connected System (PRD requirement 9). The kind is stated explicitly at every exit, because
    /// an unspecified kind downstream would be taken for UTC by whoever reads it next.
    /// </remarks>
    private DateTime ToUtc(object value)
    {
        switch (value)
        {
            case DateTimeOffset dateTimeOffset:
                return dateTimeOffset.UtcDateTime;

            case DateTime dateTime:
                return dateTime.Kind switch
                {
                    DateTimeKind.Utc => dateTime,
                    DateTimeKind.Local => dateTime.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(dateTime, _databaseTimeZone), DateTimeKind.Utc)
                };

            default:
                // A driver that hands a date back as text has told JIM nothing about its offset, so it is
                // interpreted exactly as a zoneless column is.
                return ToUtc(DateTime.SpecifyKind(Convert.ToDateTime(value, CultureInfo.InvariantCulture), DateTimeKind.Unspecified));
        }
    }

    /// <summary>
    /// Whether a failure is one row's value being unreadable rather than something wrong with the run.
    /// </summary>
    private static bool IsValueConversionFailure(Exception exception) =>
        exception is FormatException or InvalidCastException or OverflowException or ArgumentException or NotSupportedException;

    private void SetValueError(ConnectedSystemImportObject importObject, SqlImportPlan plan, string columnName, Exception exception)
    {
        importObject.ErrorType = ConnectedSystemImportObjectError.AttributeValueError;
        importObject.ErrorMessage = $"Column '{columnName}' could not be read: {exception.Message}";

        _logger.Warning(exception, "SqlConnectorImport: Object Type {ObjectType} has a row whose column {Column} could not be read", plan.Name, columnName);
    }

    #endregion

    #region Related tables

    /// <summary>
    /// Gathers a page's multi-valued attributes, one query per related table per page rather than one
    /// per object. At 500,000 rows that is the difference between a working Connector and an unusable
    /// one: a query per row is 500,000 round trips.
    /// </summary>
    private async Task GatherRelatedAttributesAsync(
        SqlImportPlan plan,
        IReadOnlyList<object?[]> rows,
        IReadOnlyList<ConnectedSystemImportObject> importObjects,
        IReadOnlyList<string> anchorKeys)
    {
        if (plan.RelatedTables.Count == 0 || rows.Count == 0)
            return;

        var importObjectsByAnchor = new Dictionary<string, ConnectedSystemImportObject>(StringComparer.Ordinal);
        for (var index = 0; index < importObjects.Count; index++)
            importObjectsByAnchor[anchorKeys[index]] = importObjects[index];

        await _progress.ReportAsync($"Gathering multi-valued attributes for {plan.Name}...");

        foreach (var relatedTable in plan.RelatedTables)
        {
            var rowsPerQuery = Math.Max(1, MaxJoinParametersPerQuery / plan.AnchorColumns.Count);

            for (var offset = 0; offset < rows.Count; offset += rowsPerQuery)
            {
                var batch = rows.Skip(offset).Take(rowsPerQuery).ToList();
                await GatherRelatedAttributeBatchAsync(plan, relatedTable, batch, importObjectsByAnchor);
            }
        }
    }

    private async Task GatherRelatedAttributeBatchAsync(
        SqlImportPlan plan,
        SqlImportRelatedTable relatedTable,
        IReadOnlyList<object?[]> rows,
        IReadOnlyDictionary<string, ConnectedSystemImportObject> importObjectsByAnchor)
    {
        var configuration = relatedTable.Configuration;

        using var command = _provider.CreateCommand(_connection, BuildRelatedTableCommandText(plan, configuration, rows.Count));

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < plan.AnchorColumns.Count; columnIndex++)
            {
                var anchorValue = rows[rowIndex][plan.ColumnIndex(plan.AnchorColumns[columnIndex].Name)];
                command.Parameters.Add(_provider.CreateParameter(JoinParameterName(rowIndex, columnIndex), anchorValue));
            }
        }

        using var reader = await command.ExecuteReaderAsync(_cancellationToken);

        var joinOrdinals = configuration.JoinColumns.Select(reader.GetOrdinal).ToArray();
        var valueOrdinal = reader.GetOrdinal(configuration.ValueColumn);

        while (await reader.ReadAsync(_cancellationToken))
        {
            if (reader.IsDBNull(valueOrdinal))
                continue;

            var anchorKey = ComposeRelatedAnchorKey(plan, reader, joinOrdinals);
            if (anchorKey == null || !importObjectsByAnchor.TryGetValue(anchorKey, out var importObject))
                continue;

            var column = new SqlImportColumn(configuration.AttributeName, relatedTable.AttributeType);

            try
            {
                AddRelatedValue(importObject, relatedTable, column, reader.GetValue(valueOrdinal));
            }
            catch (Exception ex) when (IsValueConversionFailure(ex))
            {
                SetValueError(importObject, plan, configuration.ValueColumn, ex);
            }
        }
    }

    private void AddRelatedValue(ConnectedSystemImportObject importObject, SqlImportRelatedTable relatedTable, SqlImportColumn column, object value)
    {
        var attribute = importObject.Attributes.FirstOrDefault(candidate => candidate.Name == column.Name);
        if (attribute == null)
        {
            attribute = new ConnectedSystemImportObjectAttribute { Name = column.Name, Type = column.Type };
            importObject.Attributes.Add(attribute);
        }

        if (relatedTable.ReferencedAnchorType != null)
        {
            attribute.ReferenceValues.Add(SqlAnchorValue.ToTokenString(value, relatedTable.ReferencedAnchorType.Value));
            return;
        }

        ApplyTypedValue(attribute, column.Type, value);
    }

    /// <summary>
    /// The parent this related row belongs to, rendered exactly as the parent's own anchor was so the
    /// two match. Null where a join column is NULL, which can never identify a parent.
    /// </summary>
    private static string? ComposeRelatedAnchorKey(SqlImportPlan plan, DbDataReader reader, int[] joinOrdinals)
    {
        var parts = new string[joinOrdinals.Length];

        for (var index = 0; index < joinOrdinals.Length; index++)
        {
            if (reader.IsDBNull(joinOrdinals[index]))
                return null;

            parts[index] = SqlAnchorValue.ToTokenString(reader.GetValue(joinOrdinals[index]), plan.AnchorColumns[index].Type);
        }

        return string.Join(SqlConnectorSchema.ComposedAnchorSeparator, parts);
    }

    /// <summary>
    /// Selects a related table's values for a page of parents. Standard SQL in both dialects, built from
    /// the quoting and parameter rendering the provider seam supplies; values are never interpolated.
    /// </summary>
    private string BuildRelatedTableCommandText(SqlImportPlan plan, SqlRelatedTableConfiguration configuration, int rowCount)
    {
        var columns = configuration.JoinColumns.Append(configuration.ValueColumn).Select(_provider.QuoteIdentifier);

        var predicates = Enumerable.Range(0, rowCount).Select(rowIndex =>
        {
            var terms = configuration.JoinColumns.Select((joinColumn, columnIndex) =>
                $"{_provider.QuoteIdentifier(joinColumn)} = {_provider.GetParameterPlaceholder(JoinParameterName(rowIndex, columnIndex))}");

            return $"({string.Join(" AND ", terms)})";
        });

        return $"SELECT {string.Join(", ", columns)} " +
               $"FROM {_provider.QualifyObjectName(configuration.SchemaName, configuration.TableName)} " +
               $"WHERE {string.Join(" OR ", predicates)}";
    }

    private static string JoinParameterName(int rowIndex, int columnIndex) => $"{JoinParameterPrefix}{rowIndex}_{columnIndex}";

    #endregion
}

/// <summary>
/// A column an import reads, and the JIM attribute type it arrives as.
/// </summary>
internal sealed record SqlImportColumn(string Name, AttributeDataType Type);

/// <summary>
/// A related table an import gathers, with the types its values arrive as.
/// </summary>
internal sealed record SqlImportRelatedTable(SqlRelatedTableConfiguration Configuration, AttributeDataType AttributeType, AttributeDataType? ReferencedAnchorType);

/// <summary>
/// What one configured Object Type's pages read, resolved against the Connected System's schema once
/// rather than per page.
/// </summary>
internal sealed class SqlImportPlan
{
    private readonly Dictionary<string, int> _columnIndexes;

    internal SqlImportPlan(
        SqlObjectTypeConfiguration configuration,
        IReadOnlyList<SqlImportColumn> anchorColumns,
        IReadOnlyList<SqlImportColumn> attributes,
        IReadOnlyList<string> selectColumns,
        IReadOnlyDictionary<string, AttributeDataType> referenceColumns,
        IReadOnlyList<SqlImportRelatedTable> relatedTables,
        string? composedAnchorName)
    {
        Configuration = configuration;
        AnchorColumns = anchorColumns;
        Attributes = attributes;
        SelectColumns = selectColumns;
        ReferenceColumns = referenceColumns;
        RelatedTables = relatedTables;
        ComposedAnchorName = composedAnchorName;

        _columnIndexes = selectColumns
            .Select((columnName, index) => (columnName, index))
            .ToDictionary(column => column.columnName, column => column.index, StringComparer.OrdinalIgnoreCase);
    }

    internal SqlObjectTypeConfiguration Configuration { get; }

    internal string Name => Configuration.Name;

    /// <summary>
    /// The name of this object type's Connected System Pagination Token. One per object type, which is
    /// what lets each of them be drained independently.
    /// </summary>
    internal string TokenName => Configuration.Name;

    internal IReadOnlyList<SqlImportColumn> AnchorColumns { get; }

    internal IReadOnlyList<SqlImportColumn> Attributes { get; }

    internal IReadOnlyList<string> SelectColumns { get; }

    internal IReadOnlyDictionary<string, AttributeDataType> ReferenceColumns { get; }

    internal IReadOnlyList<SqlImportRelatedTable> RelatedTables { get; }

    /// <summary>
    /// The attribute JIM composes from a multi-column anchor, or null where the anchor is one column and
    /// identifies the object on its own.
    /// </summary>
    internal string? ComposedAnchorName { get; }

    internal int ColumnIndex(string columnName) => _columnIndexes[columnName];
}

/// <summary>
/// Where one Object Type's reading has got to: the anchor the previous page ended on, and which page is
/// being read, so the narration can say so.
/// </summary>
/// <remarks>
/// Carried in the Connected System Pagination Token as JSON rather than as a delimited string, because
/// an anchor value can contain any character a column can, and a delimiter one of them happened to
/// contain would resume the next page from the wrong row without any error.
/// </remarks>
internal sealed record SqlImportPagePosition
{
    internal IReadOnlyList<string> LastAnchor { get; init; } = [];

    internal int PageNumber { get; init; } = 1;

    internal bool IsFirstPage => LastAnchor.Count == 0;

    internal static SqlImportPagePosition FromToken(ConnectedSystemPaginationToken? token, SqlImportPlan plan)
    {
        if (string.IsNullOrEmpty(token?.StringValue))
            return new SqlImportPagePosition();

        SqlImportPageToken? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SqlImportPageToken>(token.StringValue);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Object Type '{plan.Name}' was replayed a pagination token JIM cannot read. Run a Full Import again to start from the beginning.", ex);
        }

        if (parsed?.Anchor == null || parsed.Anchor.Count != plan.AnchorColumns.Count)
            throw new InvalidDataException(
                $"Object Type '{plan.Name}' was replayed a pagination token holding {parsed?.Anchor.Count ?? 0} anchor value(s), but its anchor has {plan.AnchorColumns.Count} column(s). The configuration changed mid-run; run a Full Import again.");

        return new SqlImportPagePosition { LastAnchor = parsed.Anchor, PageNumber = parsed.Page };
    }

    internal ConnectedSystemPaginationToken ToToken(SqlImportPlan plan, object?[] lastRow)
    {
        var anchor = plan.AnchorColumns
            .Select(anchorColumn => SqlAnchorValue.ToTokenString(lastRow[plan.ColumnIndex(anchorColumn.Name)]!, anchorColumn.Type))
            .ToList();

        return new ConnectedSystemPaginationToken(plan.TokenName, JsonSerializer.Serialize(new SqlImportPageToken(anchor, PageNumber + 1)));
    }
}

/// <summary>
/// A pagination token's contents as they are written and read back.
/// </summary>
internal sealed record SqlImportPageToken(List<string> Anchor, int Page);
