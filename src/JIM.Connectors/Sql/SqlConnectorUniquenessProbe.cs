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
/// The database's own comparison rules decide what counts as the same value, exactly as they decide what its unique
/// constraints refuse: under Microsoft SQL Server's default, case-insensitive collation a differently cased value is
/// in use; under Oracle Database's default, case-sensitive comparison it is not. The column is compared as it stands,
/// with no function wrapped around it, so an index on it serves the search.
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

        string commandText;
        try
        {
            commandText = BuildCommandText(objectType, request.AttributeName, values.Count);
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
            await using var command = _provider.CreateCommand(_connection, commandText);
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

                // A fixed-width CHAR column hands its values back blank-padded, while both dialects compare it
                // blank-padded, so the database matched the unpadded value too.
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
    /// The statement for one batch: the distinct values of the attribute's column that equal any bound value. An
    /// attribute held in a related table is searched there; any other is a column of the Object Type's own source,
    /// named exactly as import names it. Identifiers are quoted by the dialect, and values are never interpolated.
    /// </summary>
    /// <exception cref="ArgumentException">A name the dialect will not quote as an identifier.</exception>
    private string BuildCommandText(SqlObjectTypeConfiguration objectType, string attributeName, int valueCount)
    {
        var relatedTable = objectType.RelatedTables.FirstOrDefault(r => string.Equals(r.AttributeName, attributeName, StringComparison.OrdinalIgnoreCase));

        string column;
        string from;
        if (relatedTable != null)
        {
            column = _provider.QuoteIdentifier(relatedTable.ValueColumn);
            from = _provider.QualifyObjectName(relatedTable.SchemaName, relatedTable.TableName);
        }
        else
        {
            column = _provider.QuoteIdentifier(attributeName);
            from = objectType.IsCustomSelect
                ? $"({objectType.SelectStatement}) {_provider.QuoteIdentifier(SqlKeysetPageRequest.SourceAlias)}"
                : _provider.QualifyObjectName(objectType.SchemaName, objectType.TableName!);
        }

        var placeholders = Enumerable.Range(0, valueCount)
            .Select(index => _provider.GetParameterPlaceholder(ParameterPrefix + index.ToString(CultureInfo.InvariantCulture)));

        return $"SELECT DISTINCT {column} FROM {from} WHERE {column} IN ({string.Join(", ", placeholders)})";
    }

    private UniquenessProbeResult TimedOut(UniquenessProbeRequest request)
    {
        _logger.Warning("SqlConnectorUniquenessProbe: The search for {Attribute} values did not complete within {TimeoutSeconds} seconds.",
            LogSanitiser.Sanitise(request.AttributeName), request.Timeout.TotalSeconds);
        return UniquenessProbeResult.Failed(request.Candidates.Count, $"The database did not answer within {request.Timeout.TotalSeconds:0} seconds");
    }
}
