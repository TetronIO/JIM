// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Utilities;
using Serilog;
using System.Data.Common;
using System.Globalization;

namespace JIM.Connectors.Sql;

/// <summary>
/// Applies Pending Exports to a database: a row created, a row's columns changed, a row removed, each
/// with whatever related-table rows its multi-valued attributes carry.
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction per object.</b> Everything an object needs (its parent row and every related-table
/// row belonging to it) is written inside a transaction of its own, committed together or rolled back
/// together. A half-written object is worse than an unwritten one: JIM would confirm attributes that
/// were never applied, and the administrator would have nothing saying which.
/// </para>
/// <para>
/// <b>One result per Pending Export, in order.</b> JIM matches results to Pending Exports by position,
/// so this never filters, reorders or coalesces them. A failure is caught per object and returned as a
/// failed result, feeding the existing retry and backoff machinery; it never aborts the batch.
/// </para>
/// <para>
/// <b>Values are always bound.</b> Only identifiers reach a statement's text, quoted by the provider.
/// Connector configuration is privileged administrator input, but the injection surface it defends is
/// still exactly value parameterisation and identifier quoting.
/// </para>
/// <para>
/// <b>A statement that matched nothing is not a statement that succeeded.</b> This Connector declares
/// automatic export confirmation, so JIM records an export's attribute values against the Connected
/// System Object on a successful result, without a confirming import to check them against. A driver
/// raises nothing when a statement matches no row, so every write reads its affected-row count back and
/// answers for it: a write that had to change something and did not fails the object, while a removal
/// that found nothing to remove has already reached the end state it asked for.
/// </para>
/// <para>
/// <b>A value is bound as the column's type, not as JIM's.</b> The type an attribute has in JIM does not
/// say what the column holding it is: a Reference and a composite anchor's parts are both text in JIM
/// and may be a <c>uniqueidentifier</c> or an exact numeric in the table, and a date and time column may
/// or may not carry its own offset. So the plan for an Object Type reads the database's own column
/// catalogue once, and every value is converted to what its column expects before it is bound. The
/// catalogue is asked rather than the Object Types document because a column retyped in the table would
/// leave a recorded type stale and silently wrong.
/// </para>
/// </remarks>
internal sealed class SqlConnectorExport
{
    /// <summary>
    /// Names the parameters carrying the anchor: the row a statement acts on, and the parent a related
    /// row belongs to. One per anchor column, in anchor order.
    /// </summary>
    internal const string AnchorParameterPrefix = "exAnchor";

    /// <summary>
    /// Names the parameters carrying the values being written.
    /// </summary>
    internal const string ValueParameterPrefix = "exValue";

    /// <summary>
    /// Names the parameter a database-generated key is returned through, for the dialects that bind one.
    /// </summary>
    internal const string GeneratedKeyParameterName = "exKey";

    private readonly ISqlProvider _provider;
    private readonly DbConnection _connection;
    private readonly SqlSchemaConfiguration _configuration;
    private readonly TimeZoneInfo _databaseTimeZone;
    private readonly SqlTypeMappingOptions _typeMappingOptions;
    private readonly ILogger _logger;
    private readonly Dictionary<string, SqlExportPlan> _plans = new(StringComparer.OrdinalIgnoreCase);

    internal SqlConnectorExport(
        ISqlProvider provider,
        DbConnection connection,
        SqlSchemaConfiguration configuration,
        TimeZoneInfo databaseTimeZone,
        SqlTypeMappingOptions typeMappingOptions,
        ILogger logger)
    {
        _provider = provider;
        _connection = connection;
        _configuration = configuration;
        _databaseTimeZone = databaseTimeZone;
        _typeMappingOptions = typeMappingOptions;
        _logger = logger;
    }

    /// <summary>
    /// Applies every Pending Export in the batch, returning one result per Pending Export in the order
    /// they arrived.
    /// </summary>
    internal async Task<List<ConnectedSystemExportResult>> ExecuteAsync(IList<PendingExport> pendingExports, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pendingExports);

        if (pendingExports.Count == 0)
        {
            _logger.Information("SqlConnectorExport: there are no Pending Exports to apply");
            return [];
        }

        _logger.Debug("SqlConnectorExport: applying {PendingExportCount} Pending Export(s)", pendingExports.Count);

        var results = new ConnectedSystemExportResult[pendingExports.Count];
        var failed = 0;

        for (var index = 0; index < pendingExports.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pendingExport = pendingExports[index];

            try
            {
                results[index] = await ExportObjectAsync(pendingExport, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Every failure this object could have is one object's failure: a value that cannot be
                // written, configuration that does not describe it, or the database refusing the write.
                // It is reported against the object and the batch carries on; an aborting run is a
                // cancellation, and that is the one thing this does not swallow.
                failed++;
                _logger.Error(ex, "SqlConnectorExport: Pending Export {PendingExportId} ({ChangeType}) could not be applied", pendingExport.Id, pendingExport.ChangeType);
                results[index] = ConnectedSystemExportResult.Failed(ex.Message);
            }
        }

        _logger.Information("SqlConnectorExport: applied {PendingExportCount} Pending Export(s), {FailedCount} of which failed",
            pendingExports.Count, failed);

        return [.. results];
    }

    /// <summary>
    /// Applies one Pending Export inside a transaction of its own.
    /// </summary>
    private async Task<ConnectedSystemExportResult> ExportObjectAsync(PendingExport pendingExport, CancellationToken cancellationToken)
    {
        // Deliberately before the transaction opens: the plan's catalogue read is a read of the
        // database's own metadata, it is shared by every object of the type, and holding a transaction
        // open across it would lengthen every object's lock window for nothing.
        var plan = await ResolvePlanAsync(pendingExport, cancellationToken);

        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var result = pendingExport.ChangeType switch
            {
                PendingExportChangeType.Create => await CreateAsync(plan, pendingExport, transaction, cancellationToken),
                PendingExportChangeType.Update => await UpdateAsync(plan, pendingExport, transaction, cancellationToken),
                PendingExportChangeType.Delete => await DeleteAsync(plan, pendingExport, transaction, cancellationToken),
                _ => throw new NotSupportedException($"A Pending Export change type of {pendingExport.ChangeType} is not one this Connector knows how to apply.")
            };

            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing this object wrote is left behind. A cancelled run needs no branch of its own:
            // disposing an uncommitted transaction rolls it back.
            await RollbackAsync(transaction, pendingExport);
            throw;
        }
    }

    /// <summary>
    /// Undoes everything an object wrote. A failure to roll back is reported rather than thrown: the
    /// failure that led here is the one worth telling the administrator about, and a transaction the
    /// server has already ended has nothing left to undo.
    /// </summary>
    private async Task RollbackAsync(DbTransaction transaction, PendingExport pendingExport)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            _logger.Warning(ex, "SqlConnectorExport: the transaction for Pending Export {PendingExportId} could not be rolled back", pendingExport.Id);
        }
    }

    #region Create, update and delete

    /// <summary>
    /// Inserts the parent row, then the related-table rows belonging to it.
    /// </summary>
    /// <remarks>
    /// Related rows can only follow the parent, because where the database generates the anchor they are
    /// joined by a value that does not exist until the parent row does.
    /// </remarks>
    private async Task<ConnectedSystemExportResult> CreateAsync(
        SqlExportPlan plan,
        PendingExport pendingExport,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var columnValues = BuildParentColumnValues(plan, pendingExport, forCreate: true);
        var suppliedAnchor = ResolveSuppliedAnchor(plan, columnValues);

        // Resolved before a row is written: the external ID is what JIM will identify the new object by
        // for the rest of its life, so an anchor column this export could not compose one from fails the
        // object while there is still nothing to undo.
        var anchorTypes = ResolveAnchorTypes(plan);

        var (externalId, anchor) = suppliedAnchor == null
            ? await InsertReturningGeneratedKeyAsync(plan, anchorTypes, columnValues, transaction, cancellationToken)
            : await InsertWithSuppliedAnchorAsync(plan, anchorTypes, columnValues, suppliedAnchor, transaction, cancellationToken);

        await ApplyRelatedChangesAsync(plan, anchor, pendingExport, transaction, cancellationToken);

        return ConnectedSystemExportResult.Succeeded(externalId);
    }

    /// <summary>
    /// Inserts a row whose anchor JIM supplied, which is the case where the administrator selected an
    /// external ID the Synchronisation Rules author a value for.
    /// </summary>
    private async Task<(string ExternalId, IReadOnlyList<SqlExportColumnValue> Anchor)> InsertWithSuppliedAnchorAsync(
        SqlExportPlan plan,
        IReadOnlyDictionary<string, AttributeDataType> anchorTypes,
        IReadOnlyList<SqlExportColumnValue> columnValues,
        IReadOnlyList<SqlExportColumnValue> suppliedAnchor,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var insert = new SqlInsertCommand
        {
            SchemaName = plan.SchemaName,
            ObjectName = plan.TableName,
            Columns = ToColumnParameters(columnValues)
        };

        var rowsAffected = await ExecuteAsync(_provider.BuildInsertCommandText(insert), columnValues, transaction, cancellationToken);

        if (rowsAffected == 0)
            throw new InvalidOperationException(InsertWroteNothingMessage(plan.QualifiedTableName));

        return (ComposeExternalId(anchorTypes, suppliedAnchor), suppliedAnchor);
    }

    /// <summary>
    /// Inserts a row whose anchor the database generates, and reads the generated value back as the new
    /// object's external ID.
    /// </summary>
    private async Task<(string ExternalId, IReadOnlyList<SqlExportColumnValue> Anchor)> InsertReturningGeneratedKeyAsync(
        SqlExportPlan plan,
        IReadOnlyDictionary<string, AttributeDataType> anchorTypes,
        IReadOnlyList<SqlExportColumnValue> columnValues,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (plan.AnchorColumns.Count != 1)
            throw new SqlSchemaConfigurationException(
                $"Object Type '{plan.Name}' has a {plan.AnchorColumns.Count}-column anchor, and this export supplied a value for none of them. " +
                "A database generates one key for a new row, never a composite one, so an object type identified by several columns has to have every one of them flowed to it by a Synchronisation Rule.");

        var anchorColumn = plan.AnchorColumns[0];

        var insert = new SqlInsertCommand
        {
            SchemaName = plan.SchemaName,
            ObjectName = plan.TableName,
            Columns = ToColumnParameters(columnValues),
            GeneratedKeyColumn = anchorColumn,
            GeneratedKeyParameterName = GeneratedKeyParameterName
        };

        using var command = CreateCommand(_provider.BuildInsertReturningGeneratedKeyCommandText(insert), columnValues, transaction);

        object? generatedKey;

        if (_provider.GeneratedKeyRetrieval == SqlGeneratedKeyRetrieval.ResultSet)
        {
            // No affected-row count to read: an insert that returns its key as a result set answers with
            // the key or with nothing at all, and nothing at all is the check below.
            generatedKey = await command.ExecuteScalarAsync(cancellationToken);
        }
        else
        {
            var keyParameter = _provider.CreateGeneratedKeyParameter(GeneratedKeyParameterName, anchorTypes[anchorColumn])
                ?? throw new NotSupportedException($"The {_provider.DisplayName} provider returns a generated key through a bound parameter but supplied none for it.");

            command.Parameters.Add(keyParameter);

            // A bound output parameter can come back holding a value from a statement that wrote no row,
            // so the key arriving is not on its own evidence that the row did.
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
                throw new InvalidOperationException(InsertWroteNothingMessage(plan.QualifiedTableName));

            // Through the seam, never read raw: a driver is free to answer a bound parameter with a
            // value type of its own, and ODP.NET always does. The provider is also what decides whether
            // the driver said "no value", because its way of saying so need not be DBNull.
            generatedKey = _provider.ConvertFromDriverValue(keyParameter.Value);
        }

        if (generatedKey == null || generatedKey == DBNull.Value)
            throw new InvalidOperationException(
                $"Object Type '{plan.Name}' inserted a row, but the database returned no value for its anchor column '{anchorColumn}'. " +
                "Nothing would identify the new object, so the write is being rolled back; check that the column is backed by an identity, a sequence or a default.");

        var anchor = new[] { new SqlExportColumnValue(anchorColumn, AnchorParameterName(0), generatedKey) };
        return (ComposeExternalId(anchorTypes, anchor), anchor);
    }

    /// <summary>
    /// Writes the changed columns of the parent row, then the related-table rows its multi-valued
    /// attributes changed.
    /// </summary>
    private async Task<ConnectedSystemExportResult> UpdateAsync(
        SqlExportPlan plan,
        PendingExport pendingExport,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var anchor = ResolveAnchor(plan, pendingExport);
        var columnValues = BuildParentColumnValues(plan, pendingExport, forCreate: false);

        // An export that only changes a multi-valued attribute touches no column of the parent row, and
        // an UPDATE with nothing to set is not a statement any dialect accepts.
        if (columnValues.Count > 0)
        {
            var update = new SqlUpdateCommand
            {
                SchemaName = plan.SchemaName,
                ObjectName = plan.TableName,
                Columns = ToColumnParameters(columnValues),
                KeyColumns = ToColumnParameters(anchor)
            };

            var rowsAffected = await ExecuteAsync(_provider.BuildUpdateCommandText(update), [.. columnValues, .. anchor], transaction, cancellationToken);

            // The row this object is meant to be is not there. JIM confirms an export's values without
            // reading them back, so carrying on would record attribute values against a row that does
            // not exist, and nothing downstream would ever notice.
            if (rowsAffected == 0)
                throw new InvalidOperationException(
                    $"No row of '{plan.QualifiedTableName}' is identified by this Connected System Object's external ID, so the update changed nothing and is being rolled back. " +
                    $"Either the row was deleted outside JIM, or its anchor column(s) ({string.Join(", ", plan.AnchorColumns)}) no longer hold the values JIM recorded for it. " +
                    "The Connected System Object is stale either way: run a Full Import against this Connected System to reconcile it with the table.");
        }

        await ApplyRelatedChangesAsync(plan, anchor, pendingExport, transaction, cancellationToken);

        return ConnectedSystemExportResult.Succeeded();
    }

    /// <summary>
    /// Removes the object's related-table rows, then the parent row itself.
    /// </summary>
    /// <remarks>
    /// The children go first because a related row holding its parent's anchor cannot outlive it, and
    /// JIM never relies on the schema declaring a cascade: a related table joined by an unconstrained
    /// column (the ordinary identity case) declares none, and would be left holding orphaned rows.
    /// </remarks>
    private async Task<ConnectedSystemExportResult> DeleteAsync(
        SqlExportPlan plan,
        PendingExport pendingExport,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var anchor = ResolveAnchor(plan, pendingExport);

        foreach (var relatedTable in plan.Configuration.RelatedTables)
        {
            var joinValues = BuildJoinValues(relatedTable, anchor);
            var delete = new SqlDeleteCommand
            {
                SchemaName = relatedTable.SchemaName,
                ObjectName = relatedTable.TableName,
                KeyColumns = ToColumnParameters(joinValues)
            };

            // The affected-row count is deliberately not read: an object with no values in this related
            // table has no rows here to remove, which is the ordinary case rather than a fault.
            await ExecuteAsync(_provider.BuildDeleteCommandText(delete), joinValues, transaction, cancellationToken);
        }

        var parentDelete = new SqlDeleteCommand
        {
            SchemaName = plan.SchemaName,
            ObjectName = plan.TableName,
            KeyColumns = ToColumnParameters(anchor)
        };

        var rowsAffected = await ExecuteAsync(_provider.BuildDeleteCommandText(parentDelete), anchor, transaction, cancellationToken);

        // Deliberately a success, and deliberately not a failure: the end state this export asked for is
        // a row that is not there, and that is already the case. Failing it would retry a Pending Export
        // that can never succeed, for as long as the object exists. Do not "fix" this into a throw. The
        // warning is here because a row that went missing before JIM removed it still says something
        // about the Connected System the administrator should see.
        if (rowsAffected == 0)
            _logger.Warning(
                "SqlConnectorExport: Pending Export {PendingExportId} deleted no row of '{TableName}'; the row this Connected System Object identifies had already gone, so the delete is being treated as done",
                pendingExport.Id, plan.QualifiedTableName);

        return ConnectedSystemExportResult.Succeeded();
    }

    #endregion

    #region Related tables

    /// <summary>
    /// Applies every multi-valued attribute change this Pending Export carries, inside the parent's own
    /// transaction.
    /// </summary>
    private async Task ApplyRelatedChangesAsync(
        SqlExportPlan plan,
        IReadOnlyList<SqlExportColumnValue> anchor,
        PendingExport pendingExport,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var change in pendingExport.AttributeValueChanges.Where(change => plan.IsRelatedTableAttribute(change.Attribute.Name)))
        {
            var relatedTable = plan.RequireRelatedTable(change.Attribute.Name);
            var relatedColumns = plan.RequireRelatedTableColumns(change.Attribute.Name);
            var statement = BuildRelatedTableStatement(relatedTable, relatedColumns, BuildJoinValues(relatedTable, anchor), change);
            var rowsAffected = await ExecuteAsync(statement.CommandText, statement.BoundValues, transaction, cancellationToken);

            // A removal that matched nothing has already reached the end state it asked for, so it is
            // left alone. An add that matched nothing wrote nothing while raising nothing, which is a
            // trigger or a rule discarding the write; confirming that value would be a lie.
            if (statement.AddsARow && rowsAffected == 0)
                throw new InvalidOperationException(
                    $"Attribute '{change.Attribute.Name}' added no row to '{QualifiedName(relatedTable.SchemaName, relatedTable.TableName)}', though the database raised no error. " +
                    "A trigger or a rule is discarding the write, so JIM would be confirming a value the table does not hold; the export is being rolled back. " +
                    "Check what the table's triggers and rules do with an inserted row, and that the Connected System's credentials may write to it.");
        }
    }

    /// <summary>
    /// The statement one multi-valued attribute change becomes: a row removed, every row removed, or a
    /// row added.
    /// </summary>
    /// <remarks>
    /// Anything that is not a removal adds a row. A multi-valued attribute has no single value for an
    /// update to replace, so a change type of Update against one is one more value rather than a
    /// different one.
    /// </remarks>
    private SqlExportStatement BuildRelatedTableStatement(
        SqlRelatedTableConfiguration relatedTable,
        SqlExportColumnTypes relatedColumns,
        IReadOnlyList<SqlExportColumnValue> joinValues,
        PendingExportAttributeValueChange change)
    {
        // Refused before anything is generated, so a related table that no longer has the columns the
        // Object Types document names never produces a statement the database has to reject.
        foreach (var joinColumnName in joinValues.Select(joinValue => joinValue.ColumnName))
            relatedColumns.Require(joinColumnName);

        if (change.ChangeType == PendingExportAttributeChangeType.RemoveAll)
        {
            var clear = new SqlDeleteCommand
            {
                SchemaName = relatedTable.SchemaName,
                ObjectName = relatedTable.TableName,
                KeyColumns = ToColumnParameters(joinValues)
            };

            return new SqlExportStatement(_provider.BuildDeleteCommandText(clear), joinValues, AddsARow: false);
        }

        var value = new SqlExportColumnValue(relatedTable.ValueColumn, ValueParameterName(0),
            ToDatabaseValue(change, relatedTable.ValueColumn, relatedColumns.Require(relatedTable.ValueColumn)));
        List<SqlExportColumnValue> boundValues = [.. joinValues, value];

        if (change.ChangeType == PendingExportAttributeChangeType.Remove)
        {
            var remove = new SqlDeleteCommand
            {
                SchemaName = relatedTable.SchemaName,
                ObjectName = relatedTable.TableName,
                KeyColumns = ToColumnParameters(boundValues)
            };

            return new SqlExportStatement(_provider.BuildDeleteCommandText(remove), boundValues, AddsARow: false);
        }

        var add = new SqlInsertCommand
        {
            SchemaName = relatedTable.SchemaName,
            ObjectName = relatedTable.TableName,
            Columns = ToColumnParameters(boundValues)
        };

        return new SqlExportStatement(_provider.BuildInsertCommandText(add), boundValues, AddsARow: true);
    }

    /// <summary>
    /// The parent's anchor, expressed as the related table's own join columns. Every anchor column is
    /// joined: correlating on fewer would write, or remove, another object's values without any error.
    /// </summary>
    private static List<SqlExportColumnValue> BuildJoinValues(SqlRelatedTableConfiguration relatedTable, IReadOnlyList<SqlExportColumnValue> anchor)
    {
        if (relatedTable.JoinColumns.Count != anchor.Count)
            throw new SqlSchemaConfigurationException(
                $"Attribute '{relatedTable.AttributeName}' joins its related table on {relatedTable.JoinColumns.Count} column(s), but the Object Type's anchor has {anchor.Count}. " +
                "Joining on part of an anchor would act on another object's values, so the join must name one column per anchor column, in the same order.");

        return [.. relatedTable.JoinColumns.Select((joinColumn, index) => anchor[index] with { ColumnName = joinColumn })];
    }

    #endregion

    #region Anchors

    /// <summary>
    /// The anchor identifying the row an update or a delete acts on, taken from the Connected System
    /// Object's external ID.
    /// </summary>
    private List<SqlExportColumnValue> ResolveAnchor(SqlExportPlan plan, PendingExport pendingExport)
    {
        var connectedSystemObject = pendingExport.ConnectedSystemObject
            ?? throw new InvalidOperationException(
                $"This Pending Export carries no Connected System Object, so JIM cannot tell which row of Object Type '{plan.Name}' it applies to.");

        var externalId = connectedSystemObject.AttributeValues.FirstOrDefault(value => value.AttributeId == connectedSystemObject.ExternalIdAttributeId)
            ?? throw new InvalidOperationException(
                $"The Connected System Object has no external ID value, so JIM cannot tell which row of Object Type '{plan.Name}' to change.");

        if (plan.AnchorColumns.Count == 1)
            return
            [
                new SqlExportColumnValue(plan.AnchorColumns[0], AnchorParameterName(0),
                    ToDatabaseValue(externalId, plan.AnchorColumns[0], plan.ParentColumns.Require(plan.AnchorColumns[0])))
            ];

        // A composite anchor reaches JIM as the single Text attribute discovery composes for it, because
        // a Connected System Object is identified by one value. Its parts are separated exactly as the
        // import that wrote them composed them.
        var parts = (externalId.StringValue ?? string.Empty).Split(SqlConnectorSchema.ComposedAnchorSeparator);

        if (parts.Length != plan.AnchorColumns.Count)
            throw new InvalidOperationException(
                $"The Connected System Object's external ID has {parts.Length} part(s), but Object Type '{plan.Name}' has a {plan.AnchorColumns.Count}-column anchor. " +
                "Import the schema and run a Full Import so that external IDs are composed from the anchor the Object Types document now declares.");

        return [.. plan.AnchorColumns.Select((anchorColumn, index) =>
            new SqlExportColumnValue(anchorColumn, AnchorParameterName(index),
                ResolveAnchorPart(connectedSystemObject, anchorColumn, plan.ParentColumns.Require(anchorColumn), parts[index])))];
    }

    /// <summary>
    /// One column of a composite anchor.
    /// </summary>
    /// <remarks>
    /// The part columns carry typed values of their own wherever the administrator selected them for
    /// synchronisation, and those are used in preference to anything derived from a string. Where they
    /// were not selected, the composed external ID is all JIM holds, so the part is converted to the
    /// type its column declares: a part left as text is refused outright by a <c>uniqueidentifier</c> or
    /// a <c>RAW(16)</c> anchor column, and every object of the type would fail against it.
    /// </remarks>
    private object? ResolveAnchorPart(ConnectedSystemObject connectedSystemObject, string anchorColumn, SqlColumnType columnType, string part)
    {
        var value = connectedSystemObject.AttributeValues
            .FirstOrDefault(candidate => string.Equals(candidate.Attribute.Name, anchorColumn, StringComparison.OrdinalIgnoreCase));

        return value == null
            ? ToColumnValue(anchorColumn, anchorColumn, columnType, part)
            : ToDatabaseValue(value, anchorColumn, columnType);
    }

    /// <summary>
    /// The anchor a create supplied among its own attribute changes, or null where it supplied none and
    /// the database is expected to generate one.
    /// </summary>
    /// <remarks>
    /// All or nothing: a partly supplied composite anchor is neither JIM's to compose nor the database's
    /// to generate, so it is treated as absent and reported as such rather than half-written.
    /// </remarks>
    private static List<SqlExportColumnValue>? ResolveSuppliedAnchor(SqlExportPlan plan, IReadOnlyList<SqlExportColumnValue> columnValues)
    {
        var supplied = new List<SqlExportColumnValue>(plan.AnchorColumns.Count);

        for (var index = 0; index < plan.AnchorColumns.Count; index++)
        {
            var anchorColumn = plan.AnchorColumns[index];
            var columnValue = columnValues.FirstOrDefault(candidate => string.Equals(candidate.ColumnName, anchorColumn, StringComparison.OrdinalIgnoreCase));

            if (columnValue == null)
                return null;

            // An anchor flowed with nothing in it is not the database's to generate either: the column
            // is named in the INSERT, and what it would compose to is an empty external ID that JIM
            // could never find the row by again.
            if (columnValue.Value == null)
                throw new InvalidOperationException(
                    $"Object Type '{plan.Name}' has a create whose anchor column '{anchorColumn}' was flowed with no value. " +
                    "The anchor is what identifies the new object, so JIM would record a Connected System Object it could never find the row for; the create is being rolled back. " +
                    "Check the Synchronisation Rule flowing this attribute, or leave the column unmapped so that the database generates the key.");

            // Renamed onto the anchor parameters, because these values key the related-table rows that
            // follow the insert rather than the insert's own column list.
            supplied.Add(columnValue with { ParameterName = AnchorParameterName(index) });
        }

        return supplied;
    }

    /// <summary>
    /// The new object's external ID, composed from its anchor exactly as an import of the same row would
    /// compose it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rendered by <see cref="SqlAnchorValue"/>, the same routine and the same anchor types an import
    /// uses, because an external ID composed any other way is one the confirming import never matches:
    /// JIM would see a create it had already made, for ever. An Oracle table keyed on
    /// <c>RAW(16) DEFAULT SYS_GUID()</c> is the case that proves it, where the driver hands back bytes on
    /// both sides and only the attribute type says they are a GUID rather than a digest.
    /// </para>
    /// <para>
    /// The values are the ones bound to the statement, so a GUID has already been through
    /// <see cref="ISqlProvider.ConvertFromGuid"/>; rendering takes it back through
    /// <see cref="ISqlProvider.ConvertToGuid"/>, which is the same round trip the row itself makes.
    /// </para>
    /// </remarks>
    private string ComposeExternalId(IReadOnlyDictionary<string, AttributeDataType> anchorTypes, IReadOnlyList<SqlExportColumnValue> anchor)
    {
        return string.Join(SqlConnectorSchema.ComposedAnchorSeparator, anchor.Select(columnValue => columnValue.Value == null
            ? string.Empty
            : SqlAnchorValue.ToTokenString(_provider, columnValue.Value, anchorTypes[columnValue.ColumnName])));
    }

    /// <summary>
    /// The JIM attribute type of each anchor column, which decides both how a database-generated key is
    /// returned and how the external ID is composed from it.
    /// </summary>
    /// <remarks>
    /// Taken from the database's own catalogue, as every other type this export binds by is (see the
    /// remarks on this class). That is also where the recorded schema an import composes by came from, so
    /// the two agree by construction rather than by coincidence. An anchor column JIM has no attribute
    /// type for fails the object here, naming it: assuming an exact numeric because identities and
    /// sequences usually generate one is how a GUID key came to be recorded as hex.
    /// </remarks>
    private Dictionary<string, AttributeDataType> ResolveAnchorTypes(SqlExportPlan plan)
    {
        return plan.AnchorColumns.ToDictionary(
            anchorColumn => anchorColumn,
            anchorColumn => ResolveAnchorType(plan, anchorColumn),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The JIM attribute type of one anchor column.
    /// </summary>
    /// <exception cref="InvalidOperationException">The column's SQL type maps onto no JIM attribute type, so no external ID could be composed from it.</exception>
    private AttributeDataType ResolveAnchorType(SqlExportPlan plan, string anchorColumn)
    {
        var columnType = plan.ParentColumns.Require(anchorColumn);

        try
        {
            return _provider.MapColumnType(columnType, _typeMappingOptions);
        }
        catch (SqlTypeMappingException ex)
        {
            throw new InvalidOperationException(
                $"Object Type '{plan.Name}' is identified by column '{anchorColumn}' of type '{columnType.TypeName}', which JIM has no attribute type for, so there is no external ID it could compose that the confirming import would recognise. {ex.Message}", ex);
        }
    }

    #endregion

    #region Values

    /// <summary>
    /// The columns of the parent row this Pending Export writes, with the values to write into them.
    /// </summary>
    /// <remarks>
    /// A removal against a single-valued column writes NULL, which is what "this attribute no longer has
    /// a value" means in a table. On a create there is nothing to remove, so those changes are dropped:
    /// a column left out of an INSERT already holds whatever the schema says it should.
    /// </remarks>
    private List<SqlExportColumnValue> BuildParentColumnValues(SqlExportPlan plan, PendingExport pendingExport, bool forCreate)
    {
        var changes = pendingExport.AttributeValueChanges
            .Where(change => !plan.IsRelatedTableAttribute(change.Attribute.Name))
            .Where(change => !plan.IsComposedAnchorAttribute(change.Attribute.Name))
            .Where(change => !forCreate || !IsRemoval(change))
            .ToList();

        if (!forCreate)
            RefuseAnchorColumns(plan, changes);

        // The column's own type is required whether or not there is a value to write: a column the
        // catalogue does not describe is one this Object Type can no longer be written to at all.
        return [.. changes.Select((change, index) =>
        {
            var columnType = plan.ParentColumns.Require(change.Attribute.Name);
            return new SqlExportColumnValue(
                change.Attribute.Name,
                ValueParameterName(index),
                IsRemoval(change) ? null : ToDatabaseValue(change, change.Attribute.Name, columnType));
        })];
    }

    /// <summary>
    /// Refuses an update that would write one of the Object Type's anchor columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Defence in depth. Do not remove this as redundant.</b> Discovery marks a table-backed Object
    /// Type's anchor columns <see cref="AttributeWritability.WritableOnCreate"/>, and the engine keeps
    /// such an attribute out of every Update Pending Export
    /// (<c>SyncRuleMapping.FlowsOnUpdateExport</c>). This guard is what stands behind that, for a
    /// Pending Export staged before a schema import recorded the writability, or reaching the Connector
    /// by any other route.
    /// </para>
    /// <para>
    /// Synchronisation integrity is the reason it is a hard failure rather than a dropped column. An
    /// UPDATE rewriting a primary key succeeds: it finds the row, changes it, and raises nothing. What
    /// it leaves behind is a Connected System Object anchored to a key no row has any more, and this
    /// Connector confirms an export's values without reading them back, so nothing downstream would
    /// ever notice.
    /// </para>
    /// </remarks>
    private static void RefuseAnchorColumns(SqlExportPlan plan, IReadOnlyList<PendingExportAttributeValueChange> changes)
    {
        var anchorChange = changes.FirstOrDefault(change =>
            plan.AnchorColumns.Any(anchorColumn => string.Equals(anchorColumn, change.Attribute.Name, StringComparison.OrdinalIgnoreCase)));

        if (anchorChange == null)
            return;

        throw new InvalidOperationException(
            $"Object Type '{plan.Name}' has an update that would write anchor column '{anchorChange.Attribute.Name}', which is the primary key of the row this Connected System Object is anchored to. " +
            "A primary key cannot be rewritten: the row would keep a key JIM no longer holds for it, and the object would be orphaned without any error, so the update is being rolled back. " +
            "The attribute is set on creation only, and JIM does not flow one on an update; import the schema for this Connected System so that its writability is current, and check the Synchronisation Rule targeting it. " +
            "A business identifier that has genuinely been reissued is rejoined by an Object Matching Rule, not by rewriting the anchor.");
    }

    private static bool IsRemoval(PendingExportAttributeValueChange change) =>
        change.ChangeType is PendingExportAttributeChangeType.Remove or PendingExportAttributeChangeType.RemoveAll;

    /// <summary>
    /// Turns a Pending Export's attribute value into what the driver binds, inverting exactly what an
    /// import of the same column does to the value coming the other way.
    /// </summary>
    /// <param name="change">The attribute value change to write.</param>
    /// <param name="columnName">The column it is going into, which is the attribute's own name except in a related table.</param>
    /// <param name="columnType">
    /// The type of that column, as the database's own catalogue reports it. It decides what a date and
    /// time means and what a Reference's anchor has to become; the rest of JIM's types already know
    /// their own CLR shape.
    /// </param>
    private object? ToDatabaseValue(PendingExportAttributeValueChange change, string columnName, SqlColumnType columnType)
    {
        return change.Attribute.Type switch
        {
            AttributeDataType.Text => change.StringValue,
            AttributeDataType.Number => change.IntValue,
            AttributeDataType.LongNumber => change.LongValue,
            AttributeDataType.Decimal => ToDecimal(change),
            AttributeDataType.Boolean => change.BoolValue,
            AttributeDataType.DateTime => change.DateTimeValue == null ? null : ToDatabaseDateTime(change.DateTimeValue.Value, columnType),
            AttributeDataType.Guid => change.GuidValue == null ? null : _provider.ConvertFromGuid(change.GuidValue.Value),
            AttributeDataType.Binary => change.ByteValue,
            AttributeDataType.Reference => ToColumnValue(change.Attribute.Name, columnName, columnType, ToReferenceAnchor(change)),
            _ => throw new NotSupportedException($"Attribute '{change.Attribute.Name}' is a {change.Attribute.Type}, which cannot be written to a database column.")
        };
    }

    /// <summary>
    /// Turns a Connected System Object's stored value into what the driver binds. Exactly one of a
    /// stored value's fields is populated, decided by the attribute's type when it was imported, so
    /// reading them in turn recovers the value without needing the schema again.
    /// </summary>
    /// <remarks>
    /// The one caller is an anchor, and an anchor's text form is the one thing JIM holds for a column
    /// whose type it never learned, so a stored string is converted to the column's own type rather than
    /// left for the database to interpret.
    /// </remarks>
    private object? ToDatabaseValue(ConnectedSystemObjectAttributeValue value, string columnName, SqlColumnType columnType)
    {
        return value switch
        {
            { StringValue: { } text } => ToColumnValue(value.Attribute.Name, columnName, columnType, text),
            { IntValue: { } number } => number,
            { LongValue: { } number } => number,
            { DecimalValue: { } number } => number,
            { GuidValue: { } identifier } => _provider.ConvertFromGuid(identifier),
            { BoolValue: { } flag } => flag,
            { DateTimeValue: { } dateTime } => ToDatabaseDateTime(dateTime, columnType),
            _ => value.ByteValue
        };
    }

    /// <summary>
    /// Converts a value JIM holds as text into whatever the column it is going into expects.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things reach an export as text with no type of their own: a Reference, which carries the
    /// referenced object's anchor, and one part of a composed external ID. A database implicitly
    /// converts a string to a numeric or a character column, so binding text there works by accident;
    /// against a SQL Server <c>uniqueidentifier</c> or an Oracle <c>RAW(16)</c> it does not, and the
    /// statement is refused with a message about types rather than about the object.
    /// </para>
    /// <para>
    /// A value that will not convert fails its object here, naming the attribute, the column and the
    /// column's type. Binding it anyway would leave the database to decide what it meant, which is the
    /// whole thing reading the catalogue exists to stop.
    /// </para>
    /// </remarks>
    private object ToColumnValue(string attributeName, string columnName, SqlColumnType columnType, string text)
    {
        var attributeType = MapColumnType(attributeName, columnName, columnType);

        try
        {
            return attributeType switch
            {
                AttributeDataType.Text => text,
                AttributeDataType.Number => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                AttributeDataType.LongNumber => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                AttributeDataType.Decimal => DecimalAttributeValue.TryParse(text, out var number)
                    ? number
                    : throw new FormatException($"'{text}' is not a decimal number."),
                AttributeDataType.Guid => IdentifierParser.TryFromString(text, out var identifier)
                    ? _provider.ConvertFromGuid(identifier)
                    : throw new FormatException($"'{text}' is not a GUID."),
                AttributeDataType.Boolean => bool.Parse(text),
                AttributeDataType.Binary => Convert.FromHexString(text),
                AttributeDataType.DateTime => ToDatabaseDateTime(
                    DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), columnType),
                _ => throw new FormatException($"a {attributeType} column cannot be written from a text value.")
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Attribute '{attributeName}' holds '{text}', which JIM cannot write to column '{columnName}' of type '{columnType.TypeName}': {ex.Message} " +
                "The value is left unwritten rather than bound as text for the database to interpret; check the Synchronisation Rule flowing this attribute, and that the column is the one the Object Type means.", ex);
        }
    }

    /// <summary>
    /// The JIM attribute type a column's SQL type maps onto, for the dialect this export is speaking.
    /// </summary>
    private AttributeDataType MapColumnType(string attributeName, string columnName, SqlColumnType columnType)
    {
        try
        {
            return _provider.MapColumnType(columnType, _typeMappingOptions);
        }
        catch (SqlTypeMappingException ex)
        {
            throw new InvalidOperationException(
                $"Attribute '{attributeName}' is written to column '{columnName}', which JIM has no attribute type for. {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The exact decimal to bind. Never routed through a floating point type, which drops digits, and
    /// never parsed with the running culture, which would read "1,5" as fifteen.
    /// </summary>
    private static decimal? ToDecimal(PendingExportAttributeValueChange change)
    {
        if (change.DecimalValue is { } value)
            return value;

        if (string.IsNullOrEmpty(change.StringValue))
            return null;

        // A Decimal held in its canonical string form, which is the one form JIM renders one in.
        if (DecimalAttributeValue.TryParse(change.StringValue, out var parsed))
            return parsed;

        throw new FormatException($"Attribute '{change.Attribute.Name}' holds '{change.StringValue}', which is not a decimal number this Connector can write to a numeric column.");
    }

    /// <summary>
    /// Inverts exactly what an import applies to a date and time coming the other way.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An import can tell the two kinds of column apart because the driver hands back a
    /// <see cref="DateTimeOffset"/> for one and a <see cref="DateTime"/> for the other. An export has no
    /// such signal in the value, so it takes the distinction from the column's own type, which is why
    /// the catalogue is read at all.
    /// </para>
    /// <para>
    /// A column carrying its own offset takes the instant JIM holds, stated as UTC: the Database Time
    /// Zone setting exists to interpret columns that state nothing, and applying it here would move the
    /// instant by the zone's offset without any error (PRD requirement 9). A zoneless column holds
    /// wall-clock time in the zone the administrator declared, so that is what is written into it, with
    /// an unspecified kind to match: a kind of UTC would have some drivers convert it a second time.
    /// Where the Connected System is configured for UTC (the default), both are the identity conversion.
    /// </para>
    /// </remarks>
    private object ToDatabaseDateTime(DateTime value, SqlColumnType columnType)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        if (_provider.ColumnCarriesAnOffset(columnType))
            return new DateTimeOffset(utc, TimeSpan.Zero);

        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, _databaseTimeZone), DateTimeKind.Unspecified);
    }

    /// <summary>
    /// The referenced object's anchor, which is what a reference column holds.
    /// </summary>
    /// <remarks>
    /// JIM resolves a reference before an export runs, replacing the Metaverse Object it was staged
    /// against with the referenced Connected System Object's own external ID and stamping
    /// <see cref="PendingExportAttributeValueChange.ResolvedReferenceCsoId"/> with the object it
    /// resolved to. A reference still carrying an unresolved value is one whose target does not exist in
    /// this Connected System yet; that object fails and is retried, because writing anything else would
    /// point the row at the wrong object and nothing downstream would ever notice.
    /// </remarks>
    private static string ToReferenceAnchor(PendingExportAttributeValueChange change)
    {
        if (!string.IsNullOrEmpty(change.UnresolvedReferenceValue))
            throw new InvalidOperationException(
                $"Attribute '{change.Attribute.Name}' references an object that does not exist in this Connected System yet, so there is no anchor to write into it. " +
                "The reference is resolved and this export retried once that object has been created.");

        return change.StringValue
            ?? throw new InvalidOperationException($"Attribute '{change.Attribute.Name}' is a Reference carrying no anchor value, so there is nothing to write into it.");
    }

    #endregion

    #region Statements

    /// <summary>
    /// Runs one statement in the object's transaction, with every value bound, and answers with how many
    /// rows it affected. Every caller has to decide what none means for the statement it ran, because a
    /// driver reports it as a success either way.
    /// </summary>
    private async Task<int> ExecuteAsync(
        string commandText,
        IReadOnlyList<SqlExportColumnValue> boundValues,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = CreateCommand(commandText, boundValues, transaction);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// What an administrator is told when an INSERT wrote no row without raising. The database took the
    /// statement and discarded it, which only a trigger, a rule or an INSTEAD OF view does.
    /// </summary>
    private static string InsertWroteNothingMessage(string qualifiedTableName) =>
        $"No row was written to '{qualifiedTableName}', though the database raised no error. " +
        "A trigger or a rule is discarding the write, so JIM would be confirming an object the table does not hold; the export is being rolled back. " +
        "Check what the table's triggers and rules do with an inserted row, and that the Connected System's credentials may write to it.";

    /// <summary>
    /// A table as an administrator names it, for a message rather than for a statement: what a statement
    /// is given is quoted by the provider instead.
    /// </summary>
    internal static string QualifiedName(string? schemaName, string tableName) =>
        string.IsNullOrEmpty(schemaName) ? tableName : $"{schemaName}.{tableName}";

    private DbCommand CreateCommand(string commandText, IReadOnlyList<SqlExportColumnValue> boundValues, DbTransaction transaction)
    {
        var command = _provider.CreateCommand(_connection, commandText);
        command.Transaction = transaction;

        foreach (var boundValue in boundValues)
            command.Parameters.Add(_provider.CreateParameter(boundValue.ParameterName, boundValue.Value));

        return command;
    }

    private static IReadOnlyList<SqlColumnParameter> ToColumnParameters(IReadOnlyList<SqlExportColumnValue> boundValues) =>
        [.. boundValues.Select(boundValue => new SqlColumnParameter(boundValue.ColumnName, boundValue.ParameterName))];

    private static string AnchorParameterName(int index) => AnchorParameterPrefix + index.ToString(CultureInfo.InvariantCulture);

    private static string ValueParameterName(int index) => ValueParameterPrefix + index.ToString(CultureInfo.InvariantCulture);

    #endregion

    #region Planning

    /// <summary>
    /// Works out where an object of this Pending Export's type is written, refusing configuration that
    /// could not be written to before anything is attempted.
    /// </summary>
    /// <remarks>
    /// Resolved lazily and kept for the batch, because every Pending Export of the same type answers to
    /// the same plan and the catalogue read behind it is a round trip. An Object Type nobody exports in
    /// this batch therefore costs nothing at all.
    /// </remarks>
    private async Task<SqlExportPlan> ResolvePlanAsync(PendingExport pendingExport, CancellationToken cancellationToken)
    {
        var objectTypeName = ResolveObjectTypeName(pendingExport)
            ?? throw new SqlSchemaConfigurationException(
                "This Pending Export names no Connected System Object Type, so JIM cannot tell which table to write it to.");

        if (_plans.TryGetValue(objectTypeName, out var plan))
            return plan;

        var configuration = _configuration.ObjectTypes.FirstOrDefault(objectType => string.Equals(objectType.Name, objectTypeName, StringComparison.OrdinalIgnoreCase))
            ?? throw new SqlSchemaConfigurationException(
                $"Object Type '{objectTypeName}' is not one the {SqlConnectorConstants.SettingObjectTypes} document declares, so JIM has nowhere to write it. " +
                "Add it to the document, or stop the Synchronisation Rules provisioning objects of that type into this Connected System.");

        if (configuration.IsCustomSelect)
            throw new SqlSchemaConfigurationException(
                $"Object Type '{configuration.Name}' reads its objects from a SELECT statement, which is not something JIM can write to. " +
                "Point the Object Type at a table to export to it.");

        var parentColumns = await ReadColumnTypesAsync(configuration.SchemaName, configuration.TableName!, cancellationToken);
        var relatedTableColumns = new Dictionary<string, SqlExportColumnTypes>(StringComparer.OrdinalIgnoreCase);

        foreach (var relatedTable in configuration.RelatedTables)
            relatedTableColumns[relatedTable.AttributeName] = await ReadColumnTypesAsync(relatedTable.SchemaName, relatedTable.TableName, cancellationToken);

        plan = new SqlExportPlan(configuration, parentColumns, relatedTableColumns);
        _plans[objectTypeName] = plan;
        return plan;
    }

    /// <summary>
    /// Asks the database what one table's columns are typed as.
    /// </summary>
    private async Task<SqlExportColumnTypes> ReadColumnTypesAsync(string? schemaName, string tableName, CancellationToken cancellationToken)
    {
        var columns = await SqlCatalogueReader.ReadColumnsAsync(_provider, _connection, schemaName, tableName, cancellationToken);

        _logger.Debug("SqlConnectorExport: '{TableName}' has {ColumnCount} column(s) this export can write to",
            QualifiedName(schemaName, tableName), columns.Count);

        return new SqlExportColumnTypes(QualifiedName(schemaName, tableName), columns);
    }

    /// <summary>
    /// What kind of object this Pending Export is for. The Connected System Object states it, including
    /// for a create, where JIM stages a provisioning object before the row exists; the attribute changes
    /// are the fallback for a Pending Export loaded without it.
    /// </summary>
    private static string? ResolveObjectTypeName(PendingExport pendingExport)
    {
        return pendingExport.ConnectedSystemObject?.Type?.Name
            ?? pendingExport.AttributeValueChanges.FirstOrDefault()?.Attribute.ConnectedSystemObjectType?.Name;
    }

    #endregion
}

/// <summary>
/// A column an export writes or keys on, the parameter carrying its value, and the value itself. The
/// three travel together so a generated statement can never drift out of step with what is bound to it.
/// </summary>
internal sealed record SqlExportColumnValue(string ColumnName, string ParameterName, object? Value);

/// <summary>
/// One statement an export runs, with everything bound to it.
/// </summary>
/// <param name="CommandText">The statement, as the provider generated it.</param>
/// <param name="BoundValues">The values bound to it.</param>
/// <param name="AddsARow">
/// Whether this statement has to change something to have done its job. An add that affected no row
/// wrote nothing and must fail the object; a removal that affected no row has reached the end state it
/// asked for.
/// </param>
internal sealed record SqlExportStatement(string CommandText, IReadOnlyList<SqlExportColumnValue> BoundValues, bool AddsARow);

/// <summary>
/// Where one Connected System Object Type's objects are written, and what identifies them. Resolved once
/// per object type per batch, because every Pending Export of the same type answers to the same one.
/// </summary>
internal sealed class SqlExportPlan
{
    private readonly Dictionary<string, SqlRelatedTableConfiguration> _relatedTables;
    private readonly IReadOnlyDictionary<string, SqlExportColumnTypes> _relatedTableColumns;

    internal SqlExportPlan(
        SqlObjectTypeConfiguration configuration,
        SqlExportColumnTypes parentColumns,
        IReadOnlyDictionary<string, SqlExportColumnTypes> relatedTableColumns)
    {
        Configuration = configuration;
        ParentColumns = parentColumns;
        _relatedTableColumns = relatedTableColumns;
        _relatedTables = configuration.RelatedTables.ToDictionary(relatedTable => relatedTable.AttributeName, StringComparer.OrdinalIgnoreCase);

        ComposedAnchorName = configuration.AnchorColumns.Count > 1
            ? SqlConnectorSchema.ComposedAnchorAttributeName(configuration.AnchorColumns)
            : null;
    }

    internal SqlObjectTypeConfiguration Configuration { get; }

    /// <summary>
    /// What the database says the parent table's columns are typed as, read once for the batch.
    /// </summary>
    internal SqlExportColumnTypes ParentColumns { get; }

    internal string Name => Configuration.Name;

    internal string? SchemaName => Configuration.SchemaName;

    /// <summary>
    /// The table objects of this type are written to. Never null: an object type reading from a
    /// statement is refused before a plan is built for it.
    /// </summary>
    internal string TableName => Configuration.TableName!;

    /// <summary>
    /// The table as an administrator names it, for the messages an export reports failures through.
    /// </summary>
    internal string QualifiedTableName => SqlConnectorExport.QualifiedName(SchemaName, TableName);

    internal IReadOnlyList<string> AnchorColumns => Configuration.AnchorColumns;

    /// <summary>
    /// The attribute JIM composes for a multi-column anchor, or null where the anchor is one column.
    /// It is not a column of the table, so nothing is ever written to it.
    /// </summary>
    internal string? ComposedAnchorName { get; }

    internal bool IsRelatedTableAttribute(string attributeName) => _relatedTables.ContainsKey(attributeName);

    internal SqlRelatedTableConfiguration RequireRelatedTable(string attributeName) => _relatedTables[attributeName];

    /// <summary>
    /// What the database says one related table's columns are typed as.
    /// </summary>
    internal SqlExportColumnTypes RequireRelatedTableColumns(string attributeName) => _relatedTableColumns[attributeName];

    internal bool IsComposedAnchorAttribute(string attributeName) =>
        ComposedAnchorName != null && string.Equals(attributeName, ComposedAnchorName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What one table's columns are typed as, as the database's own catalogue reports them.
/// </summary>
/// <remarks>
/// Read from the catalogue rather than recorded in the Object Types document, because the document is
/// administrator-authored configuration that no database update touches: a column retyped in the table
/// would leave a recorded type stale, and a value bound as the wrong type is exactly the failure this
/// exists to prevent.
/// </remarks>
internal sealed class SqlExportColumnTypes
{
    private readonly Dictionary<string, SqlColumnType> _columnTypes;

    internal SqlExportColumnTypes(string qualifiedTableName, IEnumerable<SqlDiscoveredColumn> columns)
    {
        QualifiedTableName = qualifiedTableName;
        _columnTypes = columns.ToDictionary(column => column.Name, column => column.ColumnType, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The table as an administrator names it, for the messages a failure is reported through.
    /// </summary>
    internal string QualifiedTableName { get; }

    /// <summary>
    /// The type of one column.
    /// </summary>
    /// <remarks>
    /// A column the catalogue does not describe fails the object rather than falling back to binding a
    /// string: the table has changed under the Object Types document, and letting the database interpret
    /// an untyped value is how a write ends up meaning something nobody asked for.
    /// </remarks>
    /// <exception cref="SqlSchemaConfigurationException">The catalogue describes no such column.</exception>
    internal SqlColumnType Require(string columnName)
    {
        if (_columnTypes.TryGetValue(columnName, out var columnType))
            return columnType;

        throw new SqlSchemaConfigurationException(_columnTypes.Count == 0
            ? $"The account JIM connects as can see no columns of '{QualifiedTableName}', so it cannot tell what type to write '{columnName}' as. " +
              "Either the table has gone, or the account's permissions on it have. Import the schema for this Connected System once the table is visible again, so that JIM writes the columns it actually has."
            : $"'{QualifiedTableName}' has no column called '{columnName}', so JIM cannot tell what type to write it as, and will not bind it as text for the database to interpret. " +
              $"The table has changed since this configuration was written. Import the schema for this Connected System, and correct the {SqlConnectorConstants.SettingObjectTypes} document and the Synchronisation Rules that flow to it. " +
              $"The columns '{QualifiedTableName}' does have are: {string.Join(", ", _columnTypes.Keys)}.");
    }
}
