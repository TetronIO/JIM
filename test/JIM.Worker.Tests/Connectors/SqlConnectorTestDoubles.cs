// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Utilities;
using NUnit.Framework;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace JIM.Worker.Tests.Connectors;

/// <summary>
/// A stand-in for a database server behind <see cref="ISqlProvider"/>, so the JIM SQL Connector's
/// connection handling can be exercised without one. The Connector never touches a driver type
/// directly, which is what makes this substitution possible at all.
/// </summary>
internal sealed class FakeSqlProvider : SqlProviderBase
{
    /// <summary>
    /// Thrown by <see cref="DbConnection.Open"/> instead of connecting, which is how an unreachable
    /// server or a refused login is expressed here.
    /// </summary>
    internal Exception? OpenFailure { get; init; }

    /// <summary>
    /// When set, a connection succeeds only once it has been built with a pinned server certificate,
    /// which is how a driver that refuses a certificate the operating system's bundle does not vouch
    /// for, and accepts it once told to, is expressed here.
    /// </summary>
    internal bool SucceedsOnlyWithAPinnedCertificate { get; init; }

    /// <summary>
    /// Whether this dialect's driver can be told to accept one specific server certificate. Settable so
    /// a test can stand in for Oracle Database, whose driver cannot.
    /// </summary>
    internal bool CanPinServerCertificate { get; init; } = true;

    /// <summary>
    /// What the connectivity query returns when the connection opens.
    /// </summary>
    internal object? ConnectivityTestResult { get; init; } = 1;

    /// <summary>
    /// The command text every command created through this provider was given, in order, so a test can
    /// assert that the Connector ran the dialect's own connectivity query rather than one of its own.
    /// </summary>
    internal List<string> ExecutedCommandTexts { get; } = [];

    /// <summary>
    /// Every connection string built through this provider, so a test can assert what the Connector
    /// asked for without a driver parsing it first.
    /// </summary>
    internal List<SqlConnectionSettings> BuiltConnectionSettings { get; } = [];

    /// <summary>
    /// Every connection this provider handed out, so a test can assert that one was released rather
    /// than left open on the stand-in database.
    /// </summary>
    internal List<FakeDbConnection> OpenConnections { get; } = [];

    /// <summary>
    /// The tables, views, columns and foreign keys this stand-in database declares, which is what
    /// schema discovery reads. Empty unless a test populates it.
    /// </summary>
    internal FakeSqlCatalogue Catalogue { get; } = new();

    /// <summary>
    /// The settings every connection was configured with before being opened, so a test can assert that
    /// the Connector gives the dialect its chance to configure the connection object itself.
    /// </summary>
    internal List<SqlConnectionSettings> ConfiguredConnectionSettings { get; } = [];

    /// <summary>
    /// Which dialect this stand-in speaks. Settable so a test can exercise the Oracle type-mapping
    /// opt-ins, which are the one place schema discovery's answer depends on the database server.
    /// </summary>
    internal SqlDatabaseType DialectUnderTest { get; init; } = SqlDatabaseType.SqlServer;

    public override SqlDatabaseType DatabaseType => DialectUnderTest;

    public override string DisplayName => "Fake Database";

    public override string ParameterPrefix => "@";

    public override string ConnectivityTestCommandText => "SELECT 1";

    public override SqlGeneratedKeyRetrieval GeneratedKeyRetrieval => SqlGeneratedKeyRetrieval.ResultSet;

    public override bool SupportsPinnedServerCertificate => CanPinServerCertificate;

    public override int GetDefaultPort(SqlConnectionEncryption encryption) => 1433;

    protected override char OpenQuote => '[';

    protected override char CloseQuote => ']';

    public override DbParameter CreateParameter(string parameterName, object? value)
    {
        SqlIdentifier.ValidateParameterName(parameterName, nameof(parameterName));
        return new FakeDbParameter { ParameterName = parameterName, Value = value ?? DBNull.Value };
    }

    public override DbParameter? CreateGeneratedKeyParameter(string parameterName, AttributeDataType keyType) => throw new NotSupportedException();

    public override string BuildConnectionString(SqlConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        BuiltConnectionSettings.Add(settings);
        return $"Fake;Host={settings.Host}";
    }

    public override DbConnection CreateConnection(string connectionString)
    {
        var connection = new FakeDbConnection(this, connectionString);
        OpenConnections.Add(connection);
        return connection;
    }

    public override void ConfigureConnection(DbConnection connection, SqlConnectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ConfiguredConnectionSettings.Add(settings);
    }

    /// <summary>
    /// The Microsoft SQL Server page shape, because this stand-in quotes and prefixes the way that
    /// dialect does. The real dialects' generation is asserted in their own tests; what matters here is
    /// that the Connector asks the provider for a page rather than writing SQL of its own.
    /// </summary>
    public override string BuildKeysetPageCommandText(SqlKeysetPageRequest request)
    {
        ValidateKeysetPageRequest(request);

        var select = $"SELECT TOP ({GetParameterPlaceholder(request.PageSizeParameterName)}) {BuildColumnList(request.SelectColumns)}";
        var from = $"FROM {BuildFromClause(request)}";
        var orderBy = BuildAnchorOrderByClause(request.AnchorColumns);

        return request.IsFirstPage
            ? $"{select} {from} {orderBy}"
            : $"{select} {from} WHERE {BuildKeysetPredicate(request.AnchorColumns, request.LastAnchorParameterNames)} {orderBy}";
    }

    public override string BuildInsertReturningGeneratedKeyCommandText(SqlInsertCommand command) => throw new NotSupportedException();

    public override Guid ConvertToGuid(object value)
    {
        return value switch
        {
            Guid guid => guid,
            byte[] bytes => IdentifierParser.FromMicrosoftBytes(bytes),
            string text => IdentifierParser.FromString(text),
            _ => throw new ArgumentException($"Unexpected GUID value of type {value.GetType().Name}.", nameof(value))
        };
    }

    public override object ConvertFromGuid(Guid value) => value;

    // Catalogue queries are the real providers' own SQL, which no stand-in database could answer. These
    // stand in for them as recognisable tokens, so a test can still assert that discovery asked the
    // dialect for its catalogue rather than inventing a query of its own.
    public override string TablesCommandText => "FAKE CATALOGUE: TABLES";

    public override string ViewsCommandText => "FAKE CATALOGUE: VIEWS";

    public override string ColumnsCommandText => "FAKE CATALOGUE: COLUMNS";

    public override string PrimaryKeyColumnsCommandText => "FAKE CATALOGUE: PRIMARY KEY COLUMNS";

    public override string ForeignKeyColumnsCommandText => "FAKE CATALOGUE: FOREIGN KEY COLUMNS";
}

/// <summary>
/// Materialises the JIM SQL Connector's declared settings the way JIM does when a Connected System is
/// created, so a test sees the same defaults an administrator would, and supplies values for them.
/// </summary>
internal static class SqlConnectorSettingValues
{
    internal static List<ConnectedSystemSettingValue> Create(SqlConnector connector)
    {
        return connector.GetSettings().Select(setting =>
        {
            var definitionSetting = new ConnectorDefinitionSetting
            {
                Name = setting.Name,
                Description = setting.Description,
                Category = setting.Category,
                Type = setting.Type,
                DefaultCheckboxValue = setting.DefaultCheckboxValue,
                DefaultStringValue = setting.DefaultStringValue,
                DefaultIntValue = setting.DefaultIntValue,
                DropDownValues = setting.DropDownValues,
                Required = setting.Required,
                RequiredGroup = setting.RequiredGroup,
                RequiredGroupCardinality = setting.RequiredGroupCardinality,
                RequiredWhenSetting = setting.RequiredWhenSetting,
                RequiredWhenValue = setting.RequiredWhenValue
            };

            var settingValue = new ConnectedSystemSettingValue { Setting = definitionSetting };

            if (definitionSetting is { Type: ConnectedSystemSettingType.CheckBox, DefaultCheckboxValue: { } defaultCheckboxValue })
                settingValue.CheckboxValue = defaultCheckboxValue;

            if (definitionSetting.Type is ConnectedSystemSettingType.String or ConnectedSystemSettingType.DropDown or ConnectedSystemSettingType.File &&
                !string.IsNullOrEmpty(definitionSetting.DefaultStringValue))
                settingValue.StringValue = definitionSetting.DefaultStringValue;

            if (definitionSetting is { Type: ConnectedSystemSettingType.Integer, DefaultIntValue: { } defaultIntValue })
                settingValue.IntValue = defaultIntValue;

            return settingValue;
        }).ToList();
    }

    /// <summary>
    /// A complete, valid Microsoft SQL Server configuration.
    /// </summary>
    internal static List<ConnectedSystemSettingValue> CreateSqlServer(SqlConnector connector, bool encrypt = true)
    {
        var settingValues = Create(connector);
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseType, SqlConnectorConstants.DatabaseTypeSqlServer);
        SetString(settingValues, SqlConnectorConstants.SettingHost, "db.example.com");
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseName, "HR");
        SetString(settingValues, SqlConnectorConstants.SettingUsername, "jim_sync");
        SetEncrypted(settingValues, SqlConnectorConstants.SettingPassword, "sup3rs3cret");
        SetCheckbox(settingValues, SqlConnectorConstants.SettingSqlServerEncryptConnection, encrypt);
        SetString(settingValues, SqlConnectorConstants.SettingObjectTypes, SqlConnectorConstants.ObjectTypesExample);
        return settingValues;
    }

    /// <summary>
    /// A complete, valid Oracle Database configuration. A null encryption mode stands for an
    /// administrator who never answered the question at all.
    /// </summary>
    internal static List<ConnectedSystemSettingValue> CreateOracle(SqlConnector connector, string? encryptionMode)
    {
        var settingValues = Create(connector);
        SetString(settingValues, SqlConnectorConstants.SettingDatabaseType, SqlConnectorConstants.DatabaseTypeOracle);
        SetString(settingValues, SqlConnectorConstants.SettingHost, "hr.example.com");
        SetString(settingValues, SqlConnectorConstants.SettingOracleDatabaseIdentifiedBy, SqlConnectorConstants.OracleIdentifiedByServiceName);
        SetString(settingValues, SqlConnectorConstants.SettingOracleServiceName, "HRPDB");
        SetString(settingValues, SqlConnectorConstants.SettingUsername, "jim_sync");
        SetEncrypted(settingValues, SqlConnectorConstants.SettingPassword, "sup3rs3cret");
        SetString(settingValues, SqlConnectorConstants.SettingOracleEncryption, encryptionMode);
        SetString(settingValues, SqlConnectorConstants.SettingObjectTypes, SqlConnectorConstants.ObjectTypesExample);
        return settingValues;
    }

    internal static void SetString(List<ConnectedSystemSettingValue> settingValues, string name, string? value) =>
        Find(settingValues, name).StringValue = value;

    internal static void SetEncrypted(List<ConnectedSystemSettingValue> settingValues, string name, string? value) =>
        Find(settingValues, name).StringEncryptedValue = value;

    internal static void SetInt(List<ConnectedSystemSettingValue> settingValues, string name, int? value) =>
        Find(settingValues, name).IntValue = value;

    internal static void SetCheckbox(List<ConnectedSystemSettingValue> settingValues, string name, bool value) =>
        Find(settingValues, name).CheckboxValue = value;

    internal static ConnectedSystemSettingValue Find(List<ConnectedSystemSettingValue> settingValues, string name) =>
        settingValues.Single(sv => sv.Setting.Name == name);
}

/// <summary>
/// A column as a schema catalogue would report it.
/// </summary>
internal sealed record FakeCatalogueColumn(
    string Name,
    string DataTypeName,
    int? Precision = null,
    int? Scale = null,
    int? MaxLength = null,
    bool IsNullable = true);

/// <summary>
/// A foreign key column as a schema catalogue would report it.
/// </summary>
internal sealed record FakeCatalogueForeignKey(
    string ConstraintName,
    string ColumnName,
    string? ReferencedSchema,
    string ReferencedTable,
    string ReferencedColumn);

/// <summary>
/// The tables, views, columns and foreign keys a <see cref="FakeSqlProvider"/> answers catalogue
/// queries from, plus the columns any administrator-supplied SELECT statement returns.
/// </summary>
internal sealed class FakeSqlCatalogue
{
    private readonly Dictionary<string, List<FakeCatalogueColumn>> _columns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<FakeCatalogueForeignKey>> _foreignKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<FakeCatalogueColumn>> _selectStatementColumns = new(StringComparer.Ordinal);

    internal List<(string? SchemaName, string ObjectName)> Tables { get; } = [];

    internal List<(string? SchemaName, string ObjectName)> Views { get; } = [];

    internal void AddTable(string? schemaName, string objectName, params FakeCatalogueColumn[] columns)
    {
        Tables.Add((schemaName, objectName));
        _columns[Key(schemaName, objectName)] = [.. columns];
    }

    internal void AddView(string? schemaName, string objectName, params FakeCatalogueColumn[] columns)
    {
        Views.Add((schemaName, objectName));
        _columns[Key(schemaName, objectName)] = [.. columns];
    }

    internal void AddForeignKey(string? schemaName, string objectName, FakeCatalogueForeignKey foreignKey)
    {
        var key = Key(schemaName, objectName);
        if (!_foreignKeys.TryGetValue(key, out var foreignKeys))
            _foreignKeys[key] = foreignKeys = [];

        foreignKeys.Add(foreignKey);
    }

    /// <summary>
    /// Declares what an administrator-supplied SELECT statement returns. Keyed on the statement itself,
    /// because that is all discovery has to go on for a query with no catalogue entry.
    /// </summary>
    internal void AddSelectStatement(string statement, params FakeCatalogueColumn[] columns)
    {
        _selectStatementColumns[statement] = [.. columns];
    }

    internal IReadOnlyList<FakeCatalogueColumn> GetColumns(string? schemaName, string objectName) =>
        _columns.TryGetValue(Key(schemaName, objectName), out var columns) ? columns : [];

    internal IReadOnlyList<FakeCatalogueForeignKey> GetForeignKeys(string? schemaName, string objectName) =>
        _foreignKeys.TryGetValue(Key(schemaName, objectName), out var foreignKeys) ? foreignKeys : [];

    internal IReadOnlyList<FakeCatalogueColumn>? GetSelectStatementColumns(string statement) =>
        _selectStatementColumns.TryGetValue(statement, out var columns) ? columns : null;

    /// <summary>
    /// The rows a table or view holds, which is what an import reads. Kept separate from the catalogue
    /// entries above, because schema discovery and import ask this stand-in different questions.
    /// </summary>
    internal List<FakeSqlDataTable> DataTables { get; } = [];

    internal void AddRows(string? schemaName, string objectName, string[] columns, params object?[][] rows)
    {
        DataTables.Add(new FakeSqlDataTable(schemaName, objectName, columns, [.. rows]));
    }

    private static string Key(string? schemaName, string objectName) => $"{schemaName}.{objectName}";
}

/// <summary>
/// A table or view holding rows, as an import reads it.
/// </summary>
internal sealed record FakeSqlDataTable(string? SchemaName, string ObjectName, string[] Columns, List<object?[]> Rows)
{
    internal int IndexOf(string columnName)
    {
        var ordinal = Array.FindIndex(Columns, column => string.Equals(column, columnName, StringComparison.OrdinalIgnoreCase));
        return ordinal >= 0 ? ordinal : throw new IndexOutOfRangeException(columnName);
    }
}

/// <summary>
/// The connection a <see cref="FakeSqlProvider"/> hands out. It opens, or fails the way a driver does.
/// </summary>
internal sealed class FakeDbConnection : DbConnection
{
    private readonly FakeSqlProvider _provider;
    private ConnectionState _state = ConnectionState.Closed;

    internal FakeDbConnection(FakeSqlProvider provider, string connectionString)
    {
        _provider = provider;
        ConnectionString = connectionString;
    }

    [AllowNull]
    public override string ConnectionString { get; set; }

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "1.0";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open()
    {
        // The settings this connection was built from are the ones most recently handed to the provider,
        // which is what lets a pinned certificate change the outcome.
        var pinned = _provider.BuiltConnectionSettings.LastOrDefault()?.PinnedServerCertificatePath != null;

        if (_provider.SucceedsOnlyWithAPinnedCertificate && !pinned)
            throw _provider.OpenFailure ?? new FakeDbException("The server's certificate is not trusted.");

        if (!_provider.SucceedsOnlyWithAPinnedCertificate && _provider.OpenFailure != null)
            throw _provider.OpenFailure;

        _state = ConnectionState.Open;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() => new FakeDbCommand(_provider);

    /// <summary>
    /// Stated explicitly, so that "was this connection released?" is answered by this double rather than
    /// by whatever the base class happens to do with Close on dispose.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();

        base.Dispose(disposing);
    }
}

/// <summary>
/// The command a <see cref="FakeSqlProvider"/> hands out. It records what it was asked to run and
/// answers with the provider's canned result.
/// </summary>
internal sealed class FakeDbCommand : DbCommand
{
    private readonly FakeSqlProvider _provider;

    internal FakeDbCommand(FakeSqlProvider provider)
    {
        _provider = provider;
    }

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; }

    public override CommandType CommandType { get; set; }

    public override bool DesignTimeVisible { get; set; }

    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection { get; set; }

    protected override DbParameterCollection DbParameterCollection { get; } = new FakeDbParameterCollection();

    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery()
    {
        _provider.ExecutedCommandTexts.Add(CommandText);
        return 0;
    }

    public override object? ExecuteScalar()
    {
        _provider.ExecutedCommandTexts.Add(CommandText);

        // A count over a source this stand-in holds rows for; anything else is the connectivity query.
        if (CommandText.StartsWith("SELECT COUNT", StringComparison.OrdinalIgnoreCase))
            return ResolveDataTable()?.Rows.Count
                ?? throw new FakeDbException($"This stand-in database has nothing to count for: {CommandText}");

        return _provider.ConnectivityTestResult;
    }

    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new FakeDbParameter();

    /// <summary>
    /// Answers a schema-catalogue query, or an administrator-supplied SELECT statement, from the
    /// provider's stand-in catalogue. Which query it is, is decided by matching the command text against
    /// the dialect's own catalogue command texts, which is exactly the coupling under test: discovery
    /// must ask the provider rather than write SQL of its own.
    /// </summary>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        _provider.ExecutedCommandTexts.Add(CommandText);
        var catalogue = _provider.Catalogue;

        if (CommandText == _provider.TablesCommandText)
            return FakeDbDataReader.ForObjects(catalogue.Tables);

        if (CommandText == _provider.ViewsCommandText)
            return FakeDbDataReader.ForObjects(catalogue.Views);

        if (CommandText == _provider.ColumnsCommandText)
            return FakeDbDataReader.ForColumns(catalogue.GetColumns(GetBoundValue(SqlCatalogueParameters.SchemaName), GetBoundValue(SqlCatalogueParameters.ObjectName)!));

        if (CommandText == _provider.ForeignKeyColumnsCommandText)
            return FakeDbDataReader.ForForeignKeys(catalogue.GetForeignKeys(GetBoundValue(SqlCatalogueParameters.SchemaName), GetBoundValue(SqlCatalogueParameters.ObjectName)!));

        if (CommandText == _provider.PrimaryKeyColumnsCommandText)
            return FakeDbDataReader.Empty();

        var selectStatementColumns = catalogue.GetSelectStatementColumns(CommandText);
        if (selectStatementColumns != null)
        {
            // A statement with no catalogue entry is read for its shape alone, so it must never be asked
            // for rows.
            Assert.That(behavior.HasFlag(CommandBehavior.SchemaOnly), Is.True,
                "An administrator-supplied SELECT statement is executed only to learn its columns, so it must be run schema-only.");

            return FakeDbDataReader.ForStatementShape(selectStatementColumns);
        }

        var dataTable = ResolveDataTable();
        if (dataTable != null)
            return ReadRows(dataTable);

        throw new FakeDbException($"This stand-in database has nothing to answer with for: {CommandText}");
    }

    private string? GetBoundValue(string parameterName)
    {
        var index = DbParameterCollection.IndexOf(parameterName);
        return index < 0 ? null : DbParameterCollection[index].Value as string;
    }

    #region Reading rows

    /// <summary>
    /// Which source this command reads, found by matching the dialect's own qualified name against the
    /// command text. The longest match wins, so EMPLOYEE_PHONES is never mistaken for EMPLOYEES.
    /// </summary>
    private FakeSqlDataTable? ResolveDataTable()
    {
        FakeSqlDataTable? match = null;
        var matchedLength = 0;

        foreach (var dataTable in _provider.Catalogue.DataTables)
        {
            var qualifiedName = _provider.QualifyObjectName(dataTable.SchemaName, dataTable.ObjectName);
            if (CommandText.Contains(qualifiedName, StringComparison.Ordinal) && qualifiedName.Length > matchedLength)
            {
                match = dataTable;
                matchedLength = qualifiedName.Length;
            }
        }

        return match;
    }

    /// <summary>
    /// Answers a keyset page or a related-table gather from the rows this stand-in holds.
    /// </summary>
    /// <remarks>
    /// Deliberately the smallest interpreter that can answer both: it reads the ordering and the join
    /// columns out of the generated command text, and takes every value from a bound parameter. What it
    /// therefore proves is what these tests are about, that the Connector ordered by the anchor, bound
    /// the previous page's boundary, and keyed the gather on the page's anchors. Whether the predicate
    /// SQL itself is correct is a question for the providers' own tests, against a real dialect.
    /// </remarks>
    private DbDataReader ReadRows(FakeSqlDataTable dataTable)
    {
        var orderByColumns = ParseOrderByColumns();
        return orderByColumns.Count > 0 ? ReadPage(dataTable, orderByColumns) : ReadRelatedRows(dataTable);
    }

    private DbDataReader ReadPage(FakeSqlDataTable dataTable, IReadOnlyList<string> anchorColumns)
    {
        var anchorOrdinals = anchorColumns.Select(dataTable.IndexOf).ToArray();
        var lastAnchor = BoundValuesWithPrefix(SqlConnectorImport.AnchorParameterPrefix);

        var rows = dataTable.Rows
            .Where(row => lastAnchor.Count == 0 || CompareAnchors(row, anchorOrdinals, lastAnchor) > 0)
            .ToList();

        rows.Sort((left, right) => CompareRows(left, right, anchorOrdinals));

        var pageSizeIndex = DbParameterCollection.IndexOf(SqlConnectorImport.PageSizeParameterName);
        Assert.That(pageSizeIndex, Is.GreaterThanOrEqualTo(0), "A page must be limited by a bound page size, never by reading everything and discarding.");
        var pageSize = Convert.ToInt32(DbParameterCollection[pageSizeIndex].Value, CultureInfo.InvariantCulture);

        return FakeDbDataReader.ForRows(dataTable.Columns, rows.Take(pageSize));
    }

    private DbDataReader ReadRelatedRows(FakeSqlDataTable dataTable)
    {
        // Each bound join parameter is named for the page row and the anchor column it carries, so the
        // tuples the Connector asked for can be reconstructed exactly.
        var joinColumns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in JoinPredicatePattern.Matches(CommandText))
            joinColumns[match.Groups["parameter"].Value] = match.Groups["column"].Value;

        Assert.That(joinColumns, Is.Not.Empty, "A related table must be gathered against the page's anchors, never read whole.");

        var wanted = new List<object?[]>();
        foreach (var group in joinColumns.GroupBy(join => join.Key[..join.Key.LastIndexOf('_')]))
        {
            var tuple = group
                .OrderBy(join => join.Key, StringComparer.Ordinal)
                .Select(join => (Ordinal: dataTable.IndexOf(join.Value), Value: BoundValue(join.Key)))
                .ToList();

            wanted.AddRange(dataTable.Rows.Where(row => tuple.All(part => Equals(row[part.Ordinal], part.Value))));
        }

        return FakeDbDataReader.ForRows(dataTable.Columns, wanted);
    }

    /// <summary>
    /// The columns the command orders by, which for a keyset page are exactly its anchor columns.
    /// </summary>
    private List<string> ParseOrderByColumns()
    {
        var index = CommandText.IndexOf("ORDER BY ", StringComparison.Ordinal);
        if (index < 0)
            return [];

        return [.. CommandText[(index + "ORDER BY ".Length)..]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(column => column.Trim('[', ']'))];
    }

    private List<object?> BoundValuesWithPrefix(string prefix)
    {
        return [.. Enumerable.Range(0, DbParameterCollection.Count)
            .Select(index => DbParameterCollection[index])
            .Where(parameter => parameter.ParameterName.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(parameter => parameter.ParameterName, StringComparer.Ordinal)
            .Select(parameter => parameter.Value == DBNull.Value ? null : parameter.Value)];
    }

    private object? BoundValue(string parameterName)
    {
        var index = DbParameterCollection.IndexOf(parameterName);
        var value = index < 0 ? null : DbParameterCollection[index].Value;
        return value == DBNull.Value ? null : value;
    }

    private static int CompareAnchors(object?[] row, int[] anchorOrdinals, IReadOnlyList<object?> lastAnchor)
    {
        for (var index = 0; index < anchorOrdinals.Length; index++)
        {
            var comparison = CompareValues(row[anchorOrdinals[index]], lastAnchor[index]);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static int CompareRows(object?[] left, object?[] right, int[] anchorOrdinals)
    {
        foreach (var ordinal in anchorOrdinals)
        {
            var comparison = CompareValues(left[ordinal], right[ordinal]);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static int CompareValues(object? left, object? right)
    {
        if (left == null || right == null)
            return left == null && right == null ? 0 : left == null ? -1 : 1;

        // A numeric anchor read back out of a pagination token need not return as the CLR type the row
        // holds (an int column's boundary parses back as an int, a decimal one as a decimal), so numbers
        // are compared numerically rather than by type.
        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));

        return Comparer<object>.Default.Compare(left, right);
    }

    private static bool IsNumeric(object value)
    {
        return Type.GetTypeCode(value.GetType()) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16
            or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
            or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;
    }

    private static readonly Regex JoinPredicatePattern = new(
        @"\[(?<column>[^\]]+)\]\s*=\s*@(?<parameter>" + SqlConnectorImport.JoinParameterPrefix + @"\d+_\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    #endregion
}

/// <summary>
/// The parameter collection a <see cref="FakeDbCommand"/> carries, which supports lookup by name so a
/// catalogue query's bound schema and object names can be read back.
/// </summary>
internal sealed class FakeDbParameterCollection : DbParameterCollection
{
    private readonly List<DbParameter> _parameters = [];

    public override int Count => _parameters.Count;

    public override object SyncRoot { get; } = new();

    public override int Add(object value)
    {
        _parameters.Add((DbParameter)value);
        return _parameters.Count - 1;
    }

    public override void AddRange(Array values) => throw new NotSupportedException();

    public override void Clear() => _parameters.Clear();

    public override bool Contains(object value) => _parameters.Contains(value);

    public override bool Contains(string value) => IndexOf(value) >= 0;

    public override void CopyTo(Array array, int index) => throw new NotSupportedException();

    public override System.Collections.IEnumerator GetEnumerator() => _parameters.GetEnumerator();

    public override int IndexOf(object value) => _parameters.IndexOf((DbParameter)value);

    public override int IndexOf(string parameterName) =>
        _parameters.FindIndex(parameter => string.Equals(parameter.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase));

    public override void Insert(int index, object value) => _parameters.Insert(index, (DbParameter)value);

    public override void Remove(object value) => _parameters.Remove((DbParameter)value);

    public override void RemoveAt(int index) => _parameters.RemoveAt(index);

    public override void RemoveAt(string parameterName) => _parameters.RemoveAt(IndexOf(parameterName));

    protected override DbParameter GetParameter(int index) => _parameters[index];

    protected override DbParameter GetParameter(string parameterName) => _parameters[IndexOf(parameterName)];

    protected override void SetParameter(int index, DbParameter value) => _parameters[index] = value;

    protected override void SetParameter(string parameterName, DbParameter value) => _parameters[IndexOf(parameterName)] = value;
}

/// <summary>
/// A bound parameter. Values only ever arrive here, never in command text, which is the contract the
/// catalogue queries are asserted against.
/// </summary>
internal sealed class FakeDbParameter : DbParameter
{
    public override DbType DbType { get; set; }

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; } = true;

    [AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    public override bool SourceColumnNullMapping { get; set; }

    public override object? Value { get; set; }

    public override void ResetDbType()
    {
    }
}

/// <summary>
/// A result set of rows in memory, shaped like the one a schema catalogue would return. It also reports
/// its column schema, which is how the shape of an administrator-supplied SELECT statement is read.
/// </summary>
internal sealed class FakeDbDataReader : DbDataReader, IDbColumnSchemaGenerator
{
    private readonly string[] _columnNames;
    private readonly List<object?[]> _rows;
    private readonly List<DbColumn> _columnSchema;
    private int _rowIndex = -1;

    private FakeDbDataReader(string[] columnNames, List<object?[]> rows, List<DbColumn>? columnSchema = null)
    {
        _columnNames = columnNames;
        _rows = rows;
        _columnSchema = columnSchema ?? [];
    }

    internal static FakeDbDataReader Empty() => new([], []);

    internal static FakeDbDataReader ForObjects(IEnumerable<(string? SchemaName, string ObjectName)> objects)
    {
        return new FakeDbDataReader(
            [SqlCatalogueColumns.SchemaName, SqlCatalogueColumns.ObjectName],
            objects.Select(o => new object?[] { o.SchemaName, o.ObjectName }).ToList());
    }

    internal static FakeDbDataReader ForColumns(IEnumerable<FakeCatalogueColumn> columns)
    {
        var ordinal = 0;
        return new FakeDbDataReader(
        [
            SqlCatalogueColumns.ColumnName,
            SqlCatalogueColumns.DataTypeName,
            SqlCatalogueColumns.MaxLength,
            SqlCatalogueColumns.NumericPrecision,
            SqlCatalogueColumns.NumericScale,
            SqlCatalogueColumns.IsNullable,
            SqlCatalogueColumns.OrdinalPosition
        ],
            columns.Select(column => new object?[]
            {
                column.Name,
                column.DataTypeName,
                column.MaxLength,
                column.Precision,
                column.Scale,
                column.IsNullable ? "YES" : "NO",
                ++ordinal
            }).ToList());
    }

    internal static FakeDbDataReader ForForeignKeys(IEnumerable<FakeCatalogueForeignKey> foreignKeys)
    {
        var ordinal = 0;
        return new FakeDbDataReader(
        [
            SqlCatalogueColumns.ConstraintName,
            SqlCatalogueColumns.ColumnName,
            SqlCatalogueColumns.ReferencedSchema,
            SqlCatalogueColumns.ReferencedTable,
            SqlCatalogueColumns.ReferencedColumn,
            SqlCatalogueColumns.OrdinalPosition
        ],
            foreignKeys.Select(foreignKey => new object?[]
            {
                foreignKey.ConstraintName,
                foreignKey.ColumnName,
                foreignKey.ReferencedSchema,
                foreignKey.ReferencedTable,
                foreignKey.ReferencedColumn,
                ++ordinal
            }).ToList());
    }

    /// <summary>
    /// A result set of table rows, as an import reads one.
    /// </summary>
    internal static FakeDbDataReader ForRows(string[] columnNames, IEnumerable<object?[]> rows) => new(columnNames, [.. rows]);

    internal static FakeDbDataReader ForStatementShape(IEnumerable<FakeCatalogueColumn> columns)
    {
        var columnSchema = columns.Select(column => (DbColumn)new FakeDbColumn(column)).ToList();
        return new FakeDbDataReader(columnSchema.Select(column => column.ColumnName).ToArray(), [], columnSchema);
    }

    public ReadOnlyCollection<DbColumn> GetColumnSchema() => new(_columnSchema);

    public override int FieldCount => _columnNames.Length;

    public override bool HasRows => _rows.Count > 0;

    public override bool IsClosed => false;

    public override int Depth => 0;

    public override int RecordsAffected => -1;

    public override object this[int ordinal] => GetValue(ordinal);

    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ++_rowIndex < _rows.Count;

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());

    public override bool NextResult() => false;

    public override int GetOrdinal(string name)
    {
        var ordinal = Array.FindIndex(_columnNames, columnName => string.Equals(columnName, name, StringComparison.OrdinalIgnoreCase));
        return ordinal >= 0 ? ordinal : throw new IndexOutOfRangeException(name);
    }

    public override string GetName(int ordinal) => _columnNames[ordinal];

    public override object GetValue(int ordinal) => _rows[_rowIndex][ordinal] ?? DBNull.Value;

    public override bool IsDBNull(int ordinal) => _rows[_rowIndex][ordinal] == null;

    public override string GetString(int ordinal) => (string)GetValue(ordinal);

    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();

    public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;

    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override Type GetFieldType(int ordinal) => GetValue(ordinal).GetType();

    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);

    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    public override int GetValues(object[] values) => throw new NotSupportedException();

    public override System.Collections.IEnumerator GetEnumerator() => _rows.GetEnumerator();
}

/// <summary>
/// A column of a result set, as a driver reports one for a statement that has no catalogue entry.
/// </summary>
internal sealed class FakeDbColumn : DbColumn
{
    internal FakeDbColumn(FakeCatalogueColumn column)
    {
        ColumnName = column.Name;
        DataTypeName = column.DataTypeName;
        NumericPrecision = column.Precision;
        NumericScale = column.Scale;
        ColumnSize = column.MaxLength;
        AllowDBNull = column.IsNullable;
    }
}

/// <summary>
/// A driver-shaped failure. Both priority 1 drivers report connection failures as a
/// <see cref="DbException"/>, so this is what an unreachable server or a refused login looks like to
/// the Connector.
/// </summary>
internal sealed class FakeDbException : DbException
{
    internal FakeDbException(string message) : base(message)
    {
    }
}
