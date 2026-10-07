// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Data.Common;
using System.Globalization;
using JIM.Connectors.Sql.Providers;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;

namespace JIM.Connectors.Sql;

/// <summary>
/// Searches a database for generated values already in use (#1941; Unique Value Generation, #242, release 3). One
/// statement per object, against the Object Type's own table, view or SELECT statement (or, for a multi-valued
/// attribute, its related table), asking for the distinct values of the probed column that equal any of the object's
/// candidates or the control value, each bound as a parameter.
/// <para>
/// Case is ignored, as it is at every uniqueness check (PRD FR 31): <c>JBloggs</c> is in use when <c>jbloggs</c> is,
/// whatever the column's collation. A column whose collation already ignores case (Microsoft SQL Server's default)
/// is compared as it stands, so an index on it serves the search; any other (Oracle Database's default, a
/// case-sensitive collation, a SELECT statement, or a column whose collation the dialect cannot report) is compared
/// lower-cased on both sides. The collation is read once per column for the life of the probe connection.
/// </para>
/// </summary>
internal sealed class SqlConnectorUniquenessProbe
{
    /// <summary>
    /// The prefix of each bound value's parameter name (<c>probe0</c>, <c>probe1</c>, ...).
    /// </summary>
    internal const string ParameterPrefix = "probe";

    private readonly ISqlProvider _provider;
    private readonly DbConnection _connection;
    private readonly SqlSchemaConfiguration _configuration;
    private readonly ILogger _logger;
    private readonly Dictionary<(string Source, string Column), bool> _columnIgnoresCase = [];

    internal SqlConnectorUniquenessProbe(ISqlProvider provider, DbConnection connection, SqlSchemaConfiguration configuration, ILogger logger)
    {
        _provider = provider;
        _connection = connection;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Searches the database for the batch and reports an outcome per candidate. Never throws for a database fault: a
    /// refused, failed or timed-out statement is a <see cref="UniquenessProbeResult.Failed"/> result naming why, and
    /// an attribute that cannot be searched is <see cref="UniquenessProbeResult.Undetermined"/>. Cancellation of the
    /// run propagates.
    /// </summary>
    internal async Task<UniquenessProbeResult> ProbeAsync(UniquenessProbeRequest request, CancellationToken cancellationToken)
    {
        request.Validate();

        var objectType = _configuration.ObjectTypes.FirstOrDefault(o => string.Equals(o.Name, request.ObjectTypeName, StringComparison.OrdinalIgnoreCase));
        if (objectType == null)
            return UniquenessProbeResult.Undetermined(request.Candidates.Count,
                $"The {SqlConnectorConstants.SettingObjectTypes} document has no {request.ObjectTypeName} Object Type, so there is no table to search");

        var values = request.ControlValue == null ? request.Candidates.ToList() : request.Candidates.Append(request.ControlValue).ToList();

        ProbeSource source;
        try
        {
            source = ResolveSource(objectType, request.AttributeName);
        }
        catch (ArgumentException)
        {
            // An identifier the dialect refuses to quote is not a column any statement could name.
            return UniquenessProbeResult.Undetermined(request.Candidates.Count,
                $"\"{request.AttributeName}\" is not a column name JIM can search");
        }

        // Bounded by the request's own timeout as well as the run's cancellation, both in the driver and here, so the
        // statement never carries on after the session has stopped waiting for it.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        var valuesFound = new List<string>();
        try
        {
            var ignoresCase = await ColumnIgnoresCaseAsync(source, timeout.Token);

            await using var command = _provider.CreateCommand(_connection, BuildCommandText(source, values.Count, ignoresCase));
            command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds));
            for (var index = 0; index < values.Count; index++)
                command.Parameters.Add(_provider.CreateParameter(ParameterPrefix + index.ToString(CultureInfo.InvariantCulture), values[index]));

            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            while (await reader.ReadAsync(timeout.Token))
            {
                if (await reader.IsDBNullAsync(0, timeout.Token))
                    continue;

                var value = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
                if (value == null)
                    continue;

                valuesFound.Add(value);

                // Microsoft SQL Server compares text ignoring trailing spaces, so a fixed-width CHAR column it
                // matched hands the value back padded; the unpadded form is the one that was asked for.
                var unpadded = value.TrimEnd(' ');
                if (unpadded.Length != value.Length)
                    valuesFound.Add(unpadded);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TimedOut(request);
        }
        catch (DbException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Some drivers report a cancelled statement as a database error of their own. The run was cancelled, and
            // that is what the caller must see, never a probe failure that would carry on the run.
            throw new OperationCanceledException("The synchronisation was cancelled while probing the database.", ex, cancellationToken);
        }
        catch (DbException) when (timeout.IsCancellationRequested)
        {
            return TimedOut(request);
        }
        catch (DbException ex)
        {
            _logger.Warning(ex, "SqlConnectorUniquenessProbe: The search of Object Type {ObjectType} for {Attribute} values failed.",
                LogSanitiser.Sanitise(objectType.Name), LogSanitiser.Sanitise(request.AttributeName));
            return UniquenessProbeResult.Failed(request.Candidates.Count, $"The database refused the probe: {ex.Message.TrimEnd('.')}");
        }
        catch (InvalidOperationException ex)
        {
            // The driver's report of a connection that is no longer usable.
            _logger.Warning(ex, "SqlConnectorUniquenessProbe: The probe connection could not run the search of Object Type {ObjectType}.",
                LogSanitiser.Sanitise(objectType.Name));
            return UniquenessProbeResult.Failed(request.Candidates.Count, $"The probe failed: {ex.Message.TrimEnd('.')}");
        }

        _logger.Debug("SqlConnectorUniquenessProbe: Searched Object Type {ObjectType} for {CandidateCount} {Attribute} candidate(s); {ValueCount} value(s) returned.",
            LogSanitiser.Sanitise(objectType.Name), request.Candidates.Count, LogSanitiser.Sanitise(request.AttributeName), valuesFound.Count);

        return UniquenessProbeResult.FromValuesFound(request, valuesFound);
    }

    /// <summary>
    /// Where an attribute's values are searched: an attribute held in a related table in that table's value column;
    /// any other in the column of the Object Type's own table, view or SELECT statement named exactly as import names
    /// it. Identifiers are quoted by the dialect.
    /// </summary>
    /// <exception cref="ArgumentException">A name the dialect will not quote as an identifier.</exception>
    private ProbeSource ResolveSource(SqlObjectTypeConfiguration objectType, string attributeName)
    {
        var relatedTable = objectType.RelatedTables.FirstOrDefault(r => string.Equals(r.AttributeName, attributeName, StringComparison.OrdinalIgnoreCase));
        if (relatedTable != null)
        {
            var qualifiedTable = _provider.QualifyObjectName(relatedTable.SchemaName, relatedTable.TableName);
            return new ProbeSource(qualifiedTable, qualifiedTable, relatedTable.ValueColumn, _provider.QuoteIdentifier(relatedTable.ValueColumn));
        }

        var column = _provider.QuoteIdentifier(attributeName);
        if (objectType.IsCustomSelect)
            return new ProbeSource($"({objectType.SelectStatement}) {_provider.QuoteIdentifier(SqlKeysetPageRequest.SourceAlias)}", null, attributeName, column);

        var qualifiedObject = _provider.QualifyObjectName(objectType.SchemaName, objectType.TableName!);
        return new ProbeSource(qualifiedObject, qualifiedObject, attributeName, column);
    }

    /// <summary>
    /// The statement for one batch: the distinct values of the source's column that equal any bound value, compared
    /// as the column stands where it already ignores case and lower-cased on both sides otherwise. Values are never
    /// interpolated.
    /// </summary>
    private string BuildCommandText(ProbeSource source, int valueCount, bool columnIgnoresCase)
    {
        var placeholders = Enumerable.Range(0, valueCount)
            .Select(index => _provider.GetParameterPlaceholder(ParameterPrefix + index.ToString(CultureInfo.InvariantCulture)))
            .Select(placeholder => columnIgnoresCase ? placeholder : $"LOWER({placeholder})");

        var compared = columnIgnoresCase ? source.QuotedColumn : $"LOWER({source.QuotedColumn})";

        return $"SELECT DISTINCT {source.QuotedColumn} FROM {source.From} WHERE {compared} IN ({string.Join(", ", placeholders)})";
    }

    /// <summary>
    /// Whether the source's column already compares text without regard to case, read from the dialect's catalogue
    /// once per column for the life of the connection. A statement has no catalogue entry, a dialect may be unable to
    /// say, and a read may fail; each answers no, which is never wrong, only unable to use a plain index on the column.
    /// </summary>
    private async Task<bool> ColumnIgnoresCaseAsync(ProbeSource source, CancellationToken cancellationToken)
    {
        if (source.CatalogueName == null || _provider.ColumnIgnoresCaseCommandText is not { } commandText)
            return false;

        var key = (source.CatalogueName, source.ColumnName);
        if (_columnIgnoresCase.TryGetValue(key, out var known))
            return known;

        var ignoresCase = false;
        try
        {
            await using var command = _provider.CreateCommand(_connection, commandText);
            command.Parameters.Add(_provider.CreateParameter(SqlCatalogueParameters.ObjectName, source.CatalogueName));
            command.Parameters.Add(_provider.CreateParameter(SqlCatalogueParameters.ColumnName, source.ColumnName));

            var answer = await command.ExecuteScalarAsync(cancellationToken);
            ignoresCase = answer is not null and not DBNull && Convert.ToInt32(answer, CultureInfo.InvariantCulture) == 1;
        }
        catch (DbException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.Debug(ex, "SqlConnectorUniquenessProbe: Could not read how column {Column} of {Source} compares text; comparing lower-cased values.",
                LogSanitiser.Sanitise(source.ColumnName), LogSanitiser.Sanitise(source.CatalogueName));
        }

        _columnIgnoresCase[key] = ignoresCase;
        return ignoresCase;
    }

    private UniquenessProbeResult TimedOut(UniquenessProbeRequest request)
    {
        _logger.Warning("SqlConnectorUniquenessProbe: The search for {Attribute} values did not complete within {TimeoutSeconds} seconds.",
            LogSanitiser.Sanitise(request.AttributeName), request.Timeout.TotalSeconds);
        return UniquenessProbeResult.Failed(request.Candidates.Count, $"The database did not answer within {request.Timeout.TotalSeconds:0} seconds");
    }

    /// <summary>
    /// Where one attribute is searched.
    /// </summary>
    /// <param name="From">What the statement reads from, ready for its FROM clause.</param>
    /// <param name="CatalogueName">The table or view as the catalogue knows it, or null for a SELECT statement.</param>
    /// <param name="ColumnName">The column, unquoted, as the catalogue knows it.</param>
    /// <param name="QuotedColumn">The column, quoted for the statement.</param>
    private sealed record ProbeSource(string From, string? CatalogueName, string ColumnName, string QuotedColumn);
}
