// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using System.Data.Common;
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

    public abstract int GetDefaultPort(bool useTls);

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

        if (!request.IsFirstPage && request.LastAnchorParameterNames.Count != request.AnchorColumns.Count)
            throw new ArgumentException(
                $"A keyset page must supply one last-anchor parameter per anchor column: {request.AnchorColumns.Count} anchor column(s) but {request.LastAnchorParameterNames.Count} parameter(s).",
                nameof(request));
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
