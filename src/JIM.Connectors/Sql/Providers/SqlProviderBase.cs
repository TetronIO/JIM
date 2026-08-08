// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace JIM.Connectors.Sql.Providers;

/// <summary>
/// The parts of <see cref="ISqlProvider"/> that are the same whichever database server is in play:
/// identifier quoting mechanics, parameter placeholder rendering, and the keyset-pagination predicate.
/// A provider supplies its quote characters and its dialect-specific statement shapes.
/// </summary>
internal abstract class SqlProviderBase : ISqlProvider
{
    /// <summary>
    /// Recorded on every connection so a DBA can attribute sessions and locks to JIM.
    /// </summary>
    protected const string ApplicationName = "JIM";

    /// <summary>
    /// Characters accepted in a host name or address, beyond letters and digits. Colons and brackets
    /// allow an IPv6 literal; a provider may widen this (SQL Server names instances with a backslash).
    /// Anything outside the set is refused rather than escaped, because a host reaches a connection
    /// string or an Oracle Net descriptor, both of which are parsed structurally.
    /// </summary>
    private const string BaseAllowedHostCharacters = ".-_:[]";

    /// <summary>
    /// Characters accepted in a database, service or SID name, beyond letters and digits.
    /// </summary>
    private const string AllowedDatabaseNameCharacters = ".-_$";

    public abstract SqlDatabaseType DatabaseType { get; }

    public abstract string DisplayName { get; }

    public abstract string ParameterPrefix { get; }

    public abstract string ConnectivityTestCommandText { get; }

    public abstract int GetDefaultPort(SqlConnectionEncryption encryption);

    /// <summary>
    /// "TLS" is what most dialects call it; a dialect with its own name for the encrypted transport
    /// overrides this so an administrator reads the term their own documentation uses.
    /// </summary>
    public virtual string SecureTransportName => "TLS";

    /// <summary>
    /// Off unless a dialect's driver genuinely offers the mechanism. Answering true without one would
    /// have JIM prepare a certificate the driver then ignores, and report trust it does not have.
    /// </summary>
    public virtual bool SupportsPinnedServerCertificate => false;

    public abstract SqlGeneratedKeyRetrieval GeneratedKeyRetrieval { get; }

    public abstract string TablesCommandText { get; }

    public abstract string ViewsCommandText { get; }

    public abstract string ColumnsCommandText { get; }

    public abstract string PrimaryKeyColumnsCommandText { get; }

    public abstract string ForeignKeyColumnsCommandText { get; }

    /// <summary>
    /// The dialect's opening identifier quote character.
    /// </summary>
    protected abstract char OpenQuote { get; }

    /// <summary>
    /// The dialect's closing identifier quote character, doubled when it appears inside an identifier.
    /// </summary>
    protected abstract char CloseQuote { get; }

    /// <summary>
    /// Characters this dialect accepts in a host name in addition to <see cref="BaseAllowedHostCharacters"/>.
    /// </summary>
    protected virtual string AdditionalAllowedHostCharacters => string.Empty;

    #region Parameters

    public string GetParameterPlaceholder(string parameterName)
    {
        SqlIdentifier.ValidateParameterName(parameterName, nameof(parameterName));
        return ParameterPrefix + parameterName;
    }

    public abstract DbParameter CreateParameter(string parameterName, object? value);

    public abstract DbParameter? CreateGeneratedKeyParameter(string parameterName, AttributeDataType keyType);

    #endregion

    #region Connections

    public abstract string BuildConnectionString(SqlConnectionSettings settings);

    public abstract DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// Nothing by default: most drivers take everything they need in the connection string, and a
    /// provider only overrides this where its driver genuinely does not.
    /// </summary>
    public virtual void ConfigureConnection(DbConnection connection, SqlConnectionSettings settings)
    {
    }

    public virtual DbCommand CreateCommand(DbConnection connection, string commandText)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);

        var command = connection.CreateCommand();
        command.CommandText = commandText;
        return command;
    }

    #endregion

    #region Identifiers

    public string QuoteIdentifier(string identifier)
    {
        return SqlIdentifier.Quote(identifier, OpenQuote, CloseQuote, nameof(identifier));
    }

    public string QualifyObjectName(string? schemaName, string objectName)
    {
        var quotedObjectName = QuoteIdentifier(objectName);
        return string.IsNullOrWhiteSpace(schemaName)
            ? quotedObjectName
            : $"{QuoteIdentifier(schemaName)}.{quotedObjectName}";
    }

    #endregion

    #region Import and export statement building

    public abstract string BuildKeysetPageCommandText(SqlKeysetPageRequest request);

    public abstract string BuildInsertReturningGeneratedKeyCommandText(SqlInsertCommand command);

    /// <summary>
    /// Refuses a page request that could not produce a correct page boundary. Both failures here are
    /// silent data defects rather than errors if allowed through: no anchor means no stable ordering,
    /// and a partial anchor means comparing against fewer columns than the ordering uses, which
    /// overlaps or skips rows between pages.
    /// </summary>
    protected static void ValidateKeysetPageRequest(SqlKeysetPageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.SelectColumns.Count == 0)
            throw new ArgumentException("A keyset page must select at least one column.", nameof(request));

        if (request.AnchorColumns.Count == 0)
            throw new ArgumentException("A keyset page needs at least one anchor column to order and seek on.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.ObjectName) == string.IsNullOrWhiteSpace(request.SelectStatement))
            throw new ArgumentException("A keyset page must read from exactly one source: a table or view, or a statement standing in for one.", nameof(request));

        if (!request.IsFirstPage && request.LastAnchorParameterNames.Count != request.AnchorColumns.Count)
            throw new ArgumentException(
                $"A keyset page must supply one last-anchor parameter per anchor column: {request.AnchorColumns.Count} anchor column(s) but {request.LastAnchorParameterNames.Count} parameter(s).",
                nameof(request));

        if (string.IsNullOrWhiteSpace(request.ChangeColumn) != string.IsNullOrWhiteSpace(request.ChangeParameterName))
            throw new ArgumentException(
                "A keyset page restricted to changed rows needs both the column to compare and the parameter carrying the watermark; one without the other would read everything, or nothing.",
                nameof(request));

        if (request.RelatedChangeSources.Count > 0 && !request.HasChangeFilter)
            throw new ArgumentException(
                "A keyset page cannot watch related tables for changes without a watermark on the source itself: a page reading every row already includes every changed one.",
                nameof(request));

        ValidateRelatedChangeSources(request.RelatedChangeSources, request.AnchorColumns, nameof(request));
    }

    /// <summary>
    /// Refuses a related change source that could not correlate to exactly one parent. Correlating on
    /// fewer columns than the anchor has matches rows belonging to other objects, which would import a
    /// stranger's changes as this object's without any error.
    /// </summary>
    private static void ValidateRelatedChangeSources(
        IReadOnlyList<SqlRelatedChangeSource> relatedSources,
        IReadOnlyList<string> anchorColumns,
        string argumentName)
    {
        var mismatched = relatedSources.FirstOrDefault(relatedSource => relatedSource.JoinColumns.Count != anchorColumns.Count);

        if (mismatched != null)
            throw new ArgumentException(
                $"Related change source '{mismatched.TableName}' correlates on {mismatched.JoinColumns.Count} column(s), but the anchor has {anchorColumns.Count}: " +
                "correlating on part of an anchor would attribute another object's changes to this one.",
                argumentName);
    }

    /// <summary>
    /// Renders everything a keyset page filters on: the seek past the previous page's last row, and the
    /// restriction to rows beyond a Delta Import's watermark. Identical in both dialects, so it lives
    /// here rather than being written out twice.
    /// </summary>
    /// <returns>The WHERE clause with its leading space, or an empty string where the page filters on nothing.</returns>
    protected string BuildKeysetWhereClause(SqlKeysetPageRequest request)
    {
        var predicates = new List<string>(2);

        // The watermark first: it is the more selective of the two, and on the first page of a run it is
        // the only thing standing between a Delta Import and a full table scan.
        if (request.ChangeFilter is { } changeFilter)
            predicates.Add(BuildChangedRowsPredicate(changeFilter));

        if (!request.IsFirstPage)
            predicates.Add(BuildKeysetPredicate(request.AnchorColumns, request.LastAnchorParameterNames));

        return predicates.Count == 0 ? string.Empty : $" WHERE {string.Join(" AND ", predicates)}";
    }

    public string BuildChangedRowsPredicate(SqlChangeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ValidateRelatedChangeSources(filter.RelatedSources, filter.AnchorColumns, nameof(filter));

        var ownWatermark = $"{QuoteIdentifier(filter.ChangeColumn)} > {GetParameterPlaceholder(filter.ChangeParameterName)}";

        // Nothing else to consider, so the predicate is exactly what it was before related tables could
        // select a row: a Full Import and Change-Log Table mode generate the statement they always did.
        if (!filter.HasRelatedSources)
            return ownWatermark;

        var alternatives = filter.RelatedSources
            .Select((relatedSource, index) => BuildRelatedChangeExistsPredicate(relatedSource, filter.AnchorColumns, index))
            .Prepend(ownWatermark);

        return $"({string.Join(" OR ", alternatives)})";
    }

    /// <summary>
    /// Renders one related table's correlated existence test: is there a row of it belonging to this
    /// parent whose own watermark has moved. A row removed from the related table is a change to the
    /// parent too, and is detected wherever the customer's related table records its removal (a soft
    /// delete, a tombstone row); a related table that hard-deletes its rows leaves nothing for any
    /// watermark to compare, and that limitation is the documentation's to state.
    /// </summary>
    private string BuildRelatedChangeExistsPredicate(SqlRelatedChangeSource relatedSource, IReadOnlyList<string> anchorColumns, int index)
    {
        var alias = QuoteIdentifier(SqlRelatedChangeSource.AliasPrefix + index.ToString(CultureInfo.InvariantCulture));
        var sourceAlias = QuoteIdentifier(SqlKeysetPageRequest.SourceAlias);

        var correlation = relatedSource.JoinColumns.Select((joinColumn, columnIndex) =>
            $"{alias}.{QuoteIdentifier(joinColumn)} = {sourceAlias}.{QuoteIdentifier(anchorColumns[columnIndex])}");

        // No watermark for this related table yet means JIM cannot tell which of its rows are new, so
        // every parent it holds a row for is read. One expensive run beats a missed change.
        var predicates = relatedSource.WatermarkParameterName == null
            ? correlation
            : correlation.Append($"{alias}.{QuoteIdentifier(relatedSource.WatermarkColumn)} > {GetParameterPlaceholder(relatedSource.WatermarkParameterName)}");

        return $"EXISTS (SELECT 1 FROM {QualifyObjectName(relatedSource.SchemaName, relatedSource.TableName)} {alias} WHERE {string.Join(" AND ", predicates)})";
    }

    /// <summary>
    /// Renders what a keyset page reads from: a quoted, schema-qualified object name, or an
    /// administrator-supplied statement wrapped as a named derived table. Identical in both dialects,
    /// so it lives here rather than being written out twice.
    /// </summary>
    /// <remarks>
    /// A table or view is named only where a correlated subquery needs to refer back to it. An alias
    /// nothing refers to changes the statement a database administrator reads in a trace for no gain,
    /// so every page that needed none yesterday still generates none. A statement standing in for a
    /// table is always named, because a derived table has to be.
    /// </remarks>
    protected string BuildFromClause(SqlKeysetPageRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SelectStatement))
            return $"({request.SelectStatement}) {QuoteIdentifier(SqlKeysetPageRequest.SourceAlias)}";

        var qualifiedObjectName = QualifyObjectName(request.SchemaName, request.ObjectName!);

        return request.RelatedChangeSources.Count == 0
            ? qualifiedObjectName
            : $"{qualifiedObjectName} {QuoteIdentifier(SqlKeysetPageRequest.SourceAlias)}";
    }

    /// <summary>
    /// Renders a quoted, comma-separated column list.
    /// </summary>
    protected string BuildColumnList(IReadOnlyList<string> columnNames)
    {
        return string.Join(", ", columnNames.Select(QuoteIdentifier));
    }

    /// <summary>
    /// Renders the ORDER BY that a keyset page's comparison depends on. It must list exactly the
    /// anchor columns in exactly the compared order, or the seek and the ordering disagree.
    /// </summary>
    protected string BuildAnchorOrderByClause(IReadOnlyList<string> anchorColumns)
    {
        return "ORDER BY " + BuildColumnList(anchorColumns);
    }

    /// <summary>
    /// Renders the predicate that seeks past the previous page's last row.
    /// <para>
    /// For a single anchor this is the obvious <c>anchor &gt; :last</c>. For a composite anchor it is
    /// the lexicographic expansion: neither SQL Server nor Oracle supports row-value comparison with
    /// an inequality, so <c>(a, b) &gt; (:a, :b)</c> has to be written out as
    /// <c>a &gt; :a OR (a = :a AND b &gt; :b)</c>.
    /// </para>
    /// </summary>
    protected string BuildKeysetPredicate(IReadOnlyList<string> anchorColumns, IReadOnlyList<string> lastAnchorParameterNames)
    {
        var quotedColumns = anchorColumns.Select(QuoteIdentifier).ToArray();
        var placeholders = lastAnchorParameterNames.Select(GetParameterPlaceholder).ToArray();

        if (quotedColumns.Length == 1)
            return $"{quotedColumns[0]} > {placeholders[0]}";

        var terms = new List<string>(quotedColumns.Length);
        var term = new StringBuilder();
        for (var i = 0; i < quotedColumns.Length; i++)
        {
            term.Clear();
            for (var equalityIndex = 0; equalityIndex < i; equalityIndex++)
                term.Append(quotedColumns[equalityIndex]).Append(" = ").Append(placeholders[equalityIndex]).Append(" AND ");

            term.Append(quotedColumns[i]).Append(" > ").Append(placeholders[i]);
            terms.Add(i == 0 ? term.ToString() : $"({term})");
        }

        return $"({string.Join(" OR ", terms)})";
    }

    /// <summary>
    /// Renders the quoted column list of an INSERT.
    /// </summary>
    protected string BuildInsertColumnList(IReadOnlyList<SqlColumnParameter> columns)
    {
        return string.Join(", ", columns.Select(column => QuoteIdentifier(column.ColumnName)));
    }

    /// <summary>
    /// Renders the bound value list of an INSERT. Values are never interpolated.
    /// </summary>
    protected string BuildInsertValueList(IReadOnlyList<SqlColumnParameter> columns)
    {
        return string.Join(", ", columns.Select(column => GetParameterPlaceholder(column.ParameterName)));
    }

    /// <summary>
    /// Refuses an insert that could not produce a usable external ID.
    /// </summary>
    protected static void ValidateInsertCommand(SqlInsertCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Columns.Count == 0)
            throw new ArgumentException("An insert must write at least one column.", nameof(command));
    }

    /// <summary>
    /// Refuses a generated-key insert that names no column to return, which would leave the new object
    /// with no external ID at all.
    /// </summary>
    protected static void ValidateInsertReturningGeneratedKeyCommand(SqlInsertCommand command)
    {
        ValidateInsertCommand(command);

        if (string.IsNullOrWhiteSpace(command.GeneratedKeyColumn) || string.IsNullOrWhiteSpace(command.GeneratedKeyParameterName))
            throw new ArgumentException(
                "An insert that returns a database-generated key must name both the column holding it and the parameter it comes back through.",
                nameof(command));
    }

    /// <summary>
    /// A plain INSERT. Identical in both dialects, so it is written once here; a dialect that genuinely
    /// differs overrides it.
    /// </summary>
    public virtual string BuildInsertCommandText(SqlInsertCommand command)
    {
        ValidateInsertCommand(command);

        return $"INSERT INTO {QualifyObjectName(command.SchemaName, command.ObjectName)} " +
               $"({BuildInsertColumnList(command.Columns)}) " +
               $"VALUES ({BuildInsertValueList(command.Columns)})";
    }

    /// <summary>
    /// An UPDATE keyed on every one of the key columns. Identical in both dialects, so it is written
    /// once here; a dialect that genuinely differs overrides it.
    /// </summary>
    public virtual string BuildUpdateCommandText(SqlUpdateCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Columns.Count == 0)
            throw new ArgumentException("An update must write at least one column.", nameof(command));

        // An update with no key would rewrite every row of the table, which is the one failure here
        // that no error message downstream would ever attribute to JIM.
        if (command.KeyColumns.Count == 0)
            throw new ArgumentException("An update must be keyed on at least one column, or it would rewrite every row of the table.", nameof(command));

        return $"UPDATE {QualifyObjectName(command.SchemaName, command.ObjectName)} " +
               $"SET {BuildAssignmentList(command.Columns)} " +
               $"WHERE {BuildKeyPredicate(command.KeyColumns)}";
    }

    /// <summary>
    /// A DELETE keyed on every one of the key columns. Identical in both dialects, so it is written
    /// once here; a dialect that genuinely differs overrides it.
    /// </summary>
    public virtual string BuildDeleteCommandText(SqlDeleteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Same reasoning as the update above, with a worse outcome: an unkeyed delete empties the table.
        if (command.KeyColumns.Count == 0)
            throw new ArgumentException("A delete must be keyed on at least one column, or it would empty the table.", nameof(command));

        return $"DELETE FROM {QualifyObjectName(command.SchemaName, command.ObjectName)} " +
               $"WHERE {BuildKeyPredicate(command.KeyColumns)}";
    }

    /// <summary>
    /// Renders an UPDATE's SET list. Values are never interpolated.
    /// </summary>
    private string BuildAssignmentList(IReadOnlyList<SqlColumnParameter> columns)
    {
        return string.Join(", ", columns.Select(column => $"{QuoteIdentifier(column.ColumnName)} = {GetParameterPlaceholder(column.ParameterName)}"));
    }

    /// <summary>
    /// Renders the predicate identifying the rows a statement acts on. Every key column is compared, so
    /// a composite anchor never matches more rows than the one object it names.
    /// </summary>
    private string BuildKeyPredicate(IReadOnlyList<SqlColumnParameter> keyColumns)
    {
        return string.Join(" AND ", keyColumns.Select(column => $"{QuoteIdentifier(column.ColumnName)} = {GetParameterPlaceholder(column.ParameterName)}"));
    }

    #endregion

    #region Values

    public abstract Guid ConvertToGuid(object value);

    public abstract object ConvertFromGuid(Guid value);

    #endregion

    #region Type mapping

    public AttributeDataType MapColumnType(SqlColumnType columnType, SqlTypeMappingOptions options)
    {
        return SqlTypeMapper.Map(DatabaseType, columnType, options);
    }

    public bool ColumnCarriesAnOffset(SqlColumnType columnType)
    {
        return SqlTypeMapper.CarriesAnOffset(columnType);
    }

    #endregion

    #region Connection setting validation

    /// <summary>
    /// Refuses a host that is not a plausible host name or address. The host is placed into a
    /// connection string (SQL Server) or an Oracle Net connect descriptor, and while both are built
    /// through their driver's own builder, the descriptor's contents are parsed structurally by Oracle
    /// Net afterwards; a host carrying a parenthesis could rewrite the address it sits in.
    /// </summary>
    protected void ValidateHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("A database host is required.", nameof(host));

        var allowed = BaseAllowedHostCharacters + AdditionalAllowedHostCharacters;
        if (host.Any(character => !char.IsAsciiLetterOrDigit(character) && !allowed.Contains(character, StringComparison.Ordinal)))
            throw new ArgumentException($"'{host}' is not a valid database host name or address.", nameof(host));
    }

    /// <summary>
    /// Refuses a database, service or SID name that is not a plausible name, for the same reason as
    /// <see cref="ValidateHost"/>.
    /// </summary>
    protected static void ValidateDatabaseName(string? name, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"A value is required for {argumentName}.", argumentName);

        if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && !AllowedDatabaseNameCharacters.Contains(character, StringComparison.Ordinal)))
            throw new ArgumentException($"'{name}' is not a valid database, service or SID name.", argumentName);
    }

    #endregion
}
