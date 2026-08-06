// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Connectors.Sql.Providers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Exceptions;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Utilities;
using Serilog;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JIM.Connectors.Sql;

/// <summary>
/// Reads objects out of a database: everything there is for a Full Import, and only what has changed
/// for a Delta Import. Either way a page of rows at a time per configured Object Type, ordered and
/// seeked on the anchor, with each object type's multi-valued attributes gathered from its related
/// tables in one query per page.
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
    /// The bind variable carrying the watermark a Delta Import reads changes beyond.
    /// </summary>
    internal const string WatermarkParameterName = "jimWatermark";

    /// <summary>
    /// The bind variables carrying each related table's own watermark, suffixed by its position in the
    /// Object Type's related tables. Named distinctly from <see cref="WatermarkParameterName"/> rather
    /// than suffixing it, so neither name is a prefix of the other.
    /// </summary>
    internal const string RelatedWatermarkParameterPrefix = "jimRelatedWatermark";

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
    private readonly SqlDeltaImportMode _deltaMode;
    private readonly string? _persistedConnectorData;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;
    private readonly IConnectorProgress _progress;

    internal SqlConnectorImport(
        ISqlProvider provider,
        DbConnection connection,
        SqlSchemaConfiguration configuration,
        TimeZoneInfo databaseTimeZone,
        SqlDeltaImportMode deltaMode,
        ConnectedSystem connectedSystem,
        ConnectedSystemRunProfile runProfile,
        List<ConnectedSystemPaginationToken> paginationTokens,
        string? persistedConnectorData,
        ILogger logger,
        CancellationToken cancellationToken,
        IConnectorProgress progress)
    {
        _provider = provider;
        _connection = connection;
        _configuration = configuration;
        _databaseTimeZone = databaseTimeZone;
        _deltaMode = deltaMode;
        _connectedSystem = connectedSystem;
        _runProfile = runProfile;
        _paginationTokens = paginationTokens;
        _persistedConnectorData = persistedConnectorData;
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

        // Only on the initial call: both of these are the whole run's, JIM keeps them, and asking again
        // on every page would make an expensive query the price of paging.
        if (_paginationTokens.Count == 0)
        {
            // A Full Import is how a Delta Import's baseline is established, so where this Connected
            // System is configured for one, it records where the changes stood before a single row was
            // read. Reading first would leave anything changed during the run behind the watermark and
            // therefore unread for ever.
            if (_deltaMode != SqlDeltaImportMode.NotSet)
                result.PersistedConnectorData = (await CaptureWatermarkAsync(plans)).Serialise();

            await ReportExpectedObjectCountAsync(plans);
        }

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

    #region Delta import

    /// <summary>
    /// Reads what has changed since JIM last looked, in whichever way this Connected System is
    /// configured to find out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The watermark is captured before a single change is read, and returned from the first page
    /// only.</b> JIM replays the run's original watermark to every page and saves the new one after the
    /// last page, so a run that dies half way through re-reads its changes rather than skipping them.
    /// A later page returning a watermark of its own would move the run's own starting point mid-run.
    /// </para>
    /// <para>
    /// <b>Nothing is read up to an upper bound.</b> A change arriving while the run is in flight is read
    /// by this run or the next one, and possibly both; re-importing an object that has not changed since
    /// costs a comparison and produces no change, whereas an upper bound would silently drop anything
    /// committed inside it. The same trade the LDAP Connector makes, for the same reason.
    /// </para>
    /// </remarks>
    /// <exception cref="CannotPerformDeltaImportException">No Delta Import Mode has been chosen, so there is nothing to read changes from.</exception>
    internal async Task<ConnectedSystemImportResult> GetDeltaImportObjectsAsync()
    {
        if (_deltaMode == SqlDeltaImportMode.NotSet)
            throw new CannotPerformDeltaImportException(
                $"No {SqlConnectorConstants.SettingDeltaImportMode} has been chosen for this Connected System, so JIM has no way of finding out what has changed. " +
                "Choose one, and configure it in the Object Types document.");

        var plans = BuildPlans();

        if (plans.Count == 0)
        {
            _logger.Warning("SqlConnectorImport: no configured Object Type has been selected for synchronisation, so there is nothing to import");
            return new ConnectedSystemImportResult();
        }

        var previous = SqlConnectorWatermark.TryRead(_persistedConnectorData);

        if (previous == null)
            return await FallBackToFullImportAsync(string.IsNullOrWhiteSpace(_persistedConnectorData)
                ? "JIM holds no watermark for this Connected System, and a Delta Import reads its changes from one"
                : "the watermark JIM holds for this Connected System cannot be read");

        if (previous.Mode != _deltaMode)
            return await FallBackToFullImportAsync(
                $"the {SqlConnectorConstants.SettingDeltaImportMode} has changed since the watermark JIM holds was written, and a watermark taken from one mechanism says nothing about another");

        var result = new ConnectedSystemImportResult();

        if (_paginationTokens.Count == 0)
        {
            await _progress.EnterPhaseAsync(SqlConnectorPhases.QueryChanges, "Querying changes...");

            result.PersistedConnectorData = (await CaptureWatermarkAsync(plans)).Serialise();
            await ReportExpectedChangeCountAsync(plans, previous);
        }

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

            var page = SqlDeltaPagePosition.FromToken(token, plan);

            await _progress.EnterPhaseAsync(SqlConnectorPhases.Fetch, $"Fetching changed {plan.Name} objects (page {page.PageNumber})...");

            var watermark = previous.ObjectTypes.GetValueOrDefault(plan.Name);
            var deltaPage = _deltaMode == SqlDeltaImportMode.ChangeLogTable
                ? await ReadChangeLogPageAsync(plan, page, watermark)
                : await ReadWatermarkColumnPageAsync(plan, page, watermark, previous.RelatedTablesFor(plan.Name));

            result.ImportObjects.AddRange(deltaPage.ImportObjects);
            objectsRead += deltaPage.ImportObjects.Count;

            // One call drains a page per configured Object Type, so the Activity's counters move while
            // the call is still in flight rather than only when it returns.
            await _progress.ReportObjectsReadAsync(objectsRead);

            if (deltaPage.NextPosition != null)
                result.PaginationTokens.Add(deltaPage.NextPosition.ToToken(plan));
        }

        return result;
    }

    /// <summary>
    /// Runs a Full Import in place of the Delta Import that was asked for, and says so.
    /// </summary>
    /// <remarks>
    /// A missing or unusable watermark is a state problem with exactly one remedy, and it is one JIM can
    /// apply itself: a Full Import delivers the right data and leaves the baseline the next Delta Import
    /// needs. Refusing would fail the run and leave an administrator to do by hand what JIM has just
    /// worked out is needed. A missing Delta Import Mode is refused instead, because that is a question
    /// nobody has answered rather than a state to recover from, and a scheduled Delta Import quietly
    /// costing a Full Import every cycle would hide it for ever.
    /// <para>
    /// The Full Import narrates its counting step, which a Delta Import never declared. The narration
    /// still reaches the Activity; only the stepper stays on the steps this run said it would perform,
    /// which is better than every Delta Import carrying a step it almost never reaches.
    /// </para>
    /// </remarks>
    private async Task<ConnectedSystemImportResult> FallBackToFullImportAsync(string reason)
    {
        _logger.Warning("SqlConnectorImport: a Delta Import was requested, but {Reason}. Falling back to a Full Import to establish a baseline", reason);

        var result = await GetFullImportObjectsAsync();

        result.WarningMessage =
            $"A Delta Import was requested, but {reason}. A Full Import was performed instead, which has established the watermark, so the next Delta Import should run normally.";
        result.WarningErrorType = ActivityRunProfileExecutionItemErrorType.DeltaImportFallbackToFullImport;

        return result;
    }

    #endregion

    #region Delta import: change-log table

    /// <summary>
    /// Reads a page of an object type's change log, and turns it into the objects those changes are
    /// about: a deletion carries the anchor alone, and everything else is read back from the object
    /// type's own source as it now stands.
    /// </summary>
    private async Task<SqlDeltaPage> ReadChangeLogPageAsync(SqlImportPlan plan, SqlDeltaPagePosition page, SqlDeltaValue? watermark)
    {
        var changeLog = RequireChangeLog(plan);
        var keysetColumns = BuildChangeLogKeysetColumns(plan, changeLog);

        var selectColumns = keysetColumns.Select(keysetColumn => keysetColumn.Name)
            .Append(changeLog.ChangeTypeColumn)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var request = new SqlKeysetPageRequest
        {
            SchemaName = changeLog.SchemaName,
            ObjectName = changeLog.TableName,
            SelectColumns = selectColumns,
            AnchorColumns = [.. keysetColumns.Select(keysetColumn => keysetColumn.Name)],
            PageSizeParameterName = PageSizeParameterName,
            LastAnchorParameterNames = page.IsFirstPage ? [] : [.. Enumerable.Range(0, keysetColumns.Count).Select(AnchorParameterName)],
            ChangeColumn = watermark == null ? null : changeLog.SequenceColumn,
            ChangeParameterName = watermark == null ? null : WatermarkParameterName
        };

        var rows = await ReadDeltaPageAsync(plan, request, page, watermark, selectColumns);
        var changes = CollapseChanges(plan, changeLog, rows, selectColumns);

        // A deletion has no row left to read, and a change type the configuration does not account for
        // has nothing to say what to read it as; both are answered by the anchor alone.
        var importObjects = changes
            .Where(change => change.ChangeType == ObjectChangeType.Deleted || change.UnmappedChangeType != null)
            .Select(change => BuildChangedObjectIdentity(plan, changeLog, change))
            .ToList();

        // An unmapped value leaves the change type unset, so this is everything the anchor alone did not
        // already answer.
        var changed = changes.Where(change => change.ChangeType is ObjectChangeType.Added or ObjectChangeType.Updated).ToList();
        importObjects.AddRange(await ReadChangedObjectsAsync(plan, changed));

        return new SqlDeltaPage(
            importObjects,
            rows.Count == _runProfile.PageSize ? page.Advance(DescribeKeyset(plan, keysetColumns, rows[^1], selectColumns)) : null);
    }

    /// <summary>
    /// The columns a change-log page is ordered and seeked on: the sequence first, because that is the
    /// order changes happened in, then the anchor so that two changes sharing a sequence value still
    /// have a total order and neither is read twice nor skipped.
    /// </summary>
    private static List<SqlDeltaKeysetColumn> BuildChangeLogKeysetColumns(SqlImportPlan plan, SqlChangeLogConfiguration changeLog)
    {
        // The sequence column belongs to the change log, which is not part of the Connected System's
        // schema, so its type is read from the value the database returns rather than declared.
        var keysetColumns = new List<SqlDeltaKeysetColumn> { new(changeLog.SequenceColumn, null) };

        keysetColumns.AddRange(changeLog.AnchorColumns.Select((anchorColumn, index) =>
            new SqlDeltaKeysetColumn(anchorColumn, plan.AnchorColumns[index].Type)));

        return keysetColumns;
    }

    /// <summary>
    /// Reduces a page of change-log rows to one entry per object: the last change a page records for an
    /// object is what that object's fate now is, and importing the earlier ones as well would be work
    /// that the later one immediately undoes.
    /// </summary>
    private List<SqlChangeLogEntry> CollapseChanges(
        SqlImportPlan plan,
        SqlChangeLogConfiguration changeLog,
        IReadOnlyList<object?[]> rows,
        IReadOnlyList<string> selectColumns)
    {
        var changeTypeOrdinal = IndexOfColumn(selectColumns, changeLog.ChangeTypeColumn);
        var anchorOrdinals = changeLog.AnchorColumns.Select(anchorColumn => IndexOfColumn(selectColumns, anchorColumn)).ToArray();

        var entries = new List<SqlChangeLogEntry>(rows.Count);
        var latestByAnchor = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var anchorValues = anchorOrdinals.Select(ordinal => row[ordinal]).ToArray();
            var anchorKey = ComposeChangeAnchorKey(plan, changeLog, anchorValues);
            var rawChangeType = row[changeTypeOrdinal];

            // A change type is what a row is for, so a NULL one is as unusable as an unrecognised value
            // and is reported the same way.
            var changeTypeValue = rawChangeType == null ? null : Convert.ToString(rawChangeType, CultureInfo.InvariantCulture);
            var mapped = changeTypeValue != null && changeLog.ChangeTypes.TryGetValue(changeTypeValue, out var changeType);

            latestByAnchor[anchorKey] = entries.Count;
            entries.Add(new SqlChangeLogEntry(
                anchorKey,
                anchorValues,
                mapped ? changeLog.ChangeTypes[changeTypeValue!] : ObjectChangeType.NotSet,
                mapped ? null : changeTypeValue ?? "NULL"));
        }

        return [.. entries.Where((entry, index) => latestByAnchor[entry.AnchorKey] == index)];
    }

    /// <summary>
    /// The changed object's anchor as one string, rendered exactly as the object type's own anchor is so
    /// that the two identify the same object.
    /// </summary>
    /// <exception cref="InvalidDataException">A change-log row does not say which object it is about, which no amount of reading further can recover from.</exception>
    private static string ComposeChangeAnchorKey(SqlImportPlan plan, SqlChangeLogConfiguration changeLog, IReadOnlyList<object?> anchorValues)
    {
        var parts = new string[anchorValues.Count];

        for (var index = 0; index < anchorValues.Count; index++)
        {
            var value = anchorValues[index] ?? throw new InvalidDataException(
                $"Object Type '{plan.Name}' has a change-log row with a NULL value in anchor column '{changeLog.AnchorColumns[index]}', so there is no way to tell which object it is about.");

            parts[index] = SqlAnchorValue.ToTokenString(value, plan.AnchorColumns[index].Type);
        }

        return string.Join(SqlConnectorSchema.ComposedAnchorSeparator, parts);
    }

    /// <summary>
    /// Builds what JIM needs to act on a change with no row behind it: the object type and the anchor,
    /// which together are enough to find the Connected System Object the change is about.
    /// </summary>
    private ConnectedSystemImportObject BuildChangedObjectIdentity(SqlImportPlan plan, SqlChangeLogConfiguration changeLog, SqlChangeLogEntry entry)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = plan.Name,
            ChangeType = entry.ChangeType
        };

        if (plan.ComposedAnchorName != null)
        {
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute
            {
                Name = plan.ComposedAnchorName,
                Type = AttributeDataType.Text,
                StringValues = [entry.AnchorKey]
            });
        }
        else
        {
            var anchorColumn = plan.AnchorColumns[0];
            var attribute = new ConnectedSystemImportObjectAttribute { Name = anchorColumn.Name, Type = anchorColumn.Type };
            ApplyTypedValue(attribute, anchorColumn.Type, entry.AnchorValues[0]!);
            importObject.Attributes.Add(attribute);
        }

        if (entry.UnmappedChangeType != null)
        {
            importObject.ErrorType = ConnectedSystemImportObjectError.AttributeValueError;
            importObject.ErrorMessage =
                $"Column '{changeLog.ChangeTypeColumn}' holds '{entry.UnmappedChangeType}', which the change-log configuration for Object Type '{plan.Name}' does not account for. " +
                "Add it to 'createValues', 'updateValues' or 'deleteValues'.";

            _logger.Warning("SqlConnectorImport: Object Type {ObjectType} has a change-log row whose {Column} value is not configured", plan.Name, changeLog.ChangeTypeColumn);
        }

        return importObject;
    }

    /// <summary>
    /// Reads the objects a page's creates and updates are about, as they now stand, in one query per
    /// batch of anchors rather than one per object.
    /// </summary>
    /// <remarks>
    /// A change whose row is no longer there produces nothing. The row was deleted after its change was
    /// logged, so the change log holds a deletion for it further on; emitting a half-built object now
    /// would be worse than waiting one page for the truth.
    /// </remarks>
    private async Task<List<ConnectedSystemImportObject>> ReadChangedObjectsAsync(SqlImportPlan plan, IReadOnlyList<SqlChangeLogEntry> changes)
    {
        if (changes.Count == 0)
            return [];

        var changeTypesByAnchor = changes.ToDictionary(change => change.AnchorKey, change => change.ChangeType, StringComparer.Ordinal);
        var rows = await ReadRowsByAnchorAsync(plan, [.. changes.Select(change => change.AnchorValues)]);

        if (rows.Count < changes.Count)
            _logger.Debug("SqlConnectorImport: Object Type {ObjectType} has {MissingCount} change(s) whose row is no longer in the source; their deletions will arrive from the change log",
                plan.Name, changes.Count - rows.Count);

        var importObjects = BuildImportObjects(plan, rows, out var anchorKeys);
        await GatherRelatedAttributesAsync(plan, rows, importObjects, anchorKeys);

        for (var index = 0; index < importObjects.Count; index++)
            importObjects[index].ChangeType = changeTypesByAnchor[anchorKeys[index]];

        return importObjects;
    }

    /// <summary>
    /// Reads the rows a set of anchors identifies, in batches small enough that no dialect refuses the
    /// statement for the number of bind variables it carries.
    /// </summary>
    private async Task<List<object?[]>> ReadRowsByAnchorAsync(SqlImportPlan plan, IReadOnlyList<object?[]> anchors)
    {
        var rows = new List<object?[]>(anchors.Count);
        var anchorsPerQuery = Math.Max(1, MaxJoinParametersPerQuery / plan.AnchorColumns.Count);

        for (var offset = 0; offset < anchors.Count; offset += anchorsPerQuery)
        {
            var batch = anchors.Skip(offset).Take(anchorsPerQuery).ToList();

            using var command = _provider.CreateCommand(_connection, BuildAnchorLookupCommandText(plan, batch.Count));

            for (var anchorIndex = 0; anchorIndex < batch.Count; anchorIndex++)
            {
                for (var columnIndex = 0; columnIndex < plan.AnchorColumns.Count; columnIndex++)
                    command.Parameters.Add(_provider.CreateParameter(JoinParameterName(anchorIndex, columnIndex), batch[anchorIndex][columnIndex]));
            }

            rows.AddRange(await ReadRowsAsync(command, plan.SelectColumns));
        }

        return rows;
    }

    /// <summary>
    /// Selects the rows a set of anchors identifies. Standard SQL in both dialects, built from the
    /// quoting and parameter rendering the provider seam supplies; values are never interpolated.
    /// </summary>
    private string BuildAnchorLookupCommandText(SqlImportPlan plan, int anchorCount)
    {
        var columns = plan.SelectColumns.Select(_provider.QuoteIdentifier);

        var predicates = Enumerable.Range(0, anchorCount).Select(anchorIndex =>
        {
            var terms = plan.AnchorColumns.Select((anchorColumn, columnIndex) =>
                $"{_provider.QuoteIdentifier(anchorColumn.Name)} = {_provider.GetParameterPlaceholder(JoinParameterName(anchorIndex, columnIndex))}");

            return $"({string.Join(" AND ", terms)})";
        });

        return $"SELECT {string.Join(", ", columns)} FROM {BuildFromClause(plan)} WHERE {string.Join(" OR ", predicates)}";
    }

    #endregion

    #region Delta import: watermark column

    /// <summary>
    /// Reads a page of the rows that have changed: those whose own last-modified or version column has
    /// moved beyond the watermark, and those any of whose related tables holds a row for that has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A related table is a source of changes to the parent object.</b> A group membership added or
    /// revoked, or a phone number replaced, never touches the object's own row, so its watermark does not
    /// move and reading the primary source alone would miss the change entirely. Each related table
    /// carries a watermark column of its own, and a parent is selected on either its own evidence or any
    /// of theirs; the object is then imported whole, exactly as a Full Import would deliver it.
    /// </para>
    /// <para>
    /// <b>This mode cannot observe a deletion, and nothing here is going to change that.</b> A row that
    /// has been deleted has no column left to move, so its absence never reaches the predicate; the same
    /// goes for a row that has fallen out of a view. Deletions need the change-log table, or a periodic
    /// Full Import. Every change is reported as an update, because a last-modified column cannot tell a
    /// create from one; JIM creates the Connected System Object where it holds none.
    /// </para>
    /// <para>
    /// <b>The same limitation applies one level down, and the documentation must say so.</b> A row
    /// removed from a related table is a change to the parent object and is detected wherever the
    /// customer's table records the removal: a soft-delete flag, a tombstone row, or an update to the
    /// row's own watermark. Where a related table hard-deletes its rows instead, there is nothing left
    /// for any watermark to compare, so a revoked membership is invisible until the next Full Import.
    /// </para>
    /// </remarks>
    private async Task<SqlDeltaPage> ReadWatermarkColumnPageAsync(
        SqlImportPlan plan,
        SqlDeltaPagePosition page,
        SqlDeltaValue? watermark,
        IReadOnlyDictionary<string, SqlDeltaValue> relatedWatermarks)
    {
        var watermarkColumn = RequireWatermarkColumn(plan);
        var keysetColumns = plan.AnchorColumns.Select(anchorColumn => new SqlDeltaKeysetColumn(anchorColumn.Name, anchorColumn.Type)).ToList();

        // With no watermark at all this object type is read from the beginning, which already includes
        // everything a related table could have to say about it.
        var relatedChanges = watermark == null ? [] : BuildRelatedChanges(plan, relatedWatermarks);

        var request = new SqlKeysetPageRequest
        {
            SchemaName = plan.Configuration.SchemaName,
            ObjectName = plan.Configuration.TableName,
            SelectStatement = plan.Configuration.SelectStatement,
            SelectColumns = plan.SelectColumns,
            AnchorColumns = [.. keysetColumns.Select(keysetColumn => keysetColumn.Name)],
            PageSizeParameterName = PageSizeParameterName,
            LastAnchorParameterNames = page.IsFirstPage ? [] : [.. Enumerable.Range(0, keysetColumns.Count).Select(AnchorParameterName)],
            ChangeColumn = watermark == null ? null : watermarkColumn,
            ChangeParameterName = watermark == null ? null : WatermarkParameterName,
            RelatedChangeSources = [.. relatedChanges.Select(relatedChange => relatedChange.Source)]
        };

        var rows = await ReadDeltaPageAsync(plan, request, page, watermark, plan.SelectColumns, relatedChanges);

        var importObjects = BuildImportObjects(plan, rows, out var anchorKeys);
        await GatherRelatedAttributesAsync(plan, rows, importObjects, anchorKeys);

        foreach (var importObject in importObjects)
            importObject.ChangeType = ObjectChangeType.Updated;

        return new SqlDeltaPage(
            importObjects,
            rows.Count == _runProfile.PageSize ? page.Advance(DescribeKeyset(plan, keysetColumns, rows[^1], plan.SelectColumns)) : null);
    }

    #endregion

    #region Delta import: watermarks, counting and paging mechanics

    /// <summary>
    /// Asks each object type's change source, and in Watermark Column mode each of its related tables,
    /// where it currently stands, which is what the next Delta Import will read from.
    /// </summary>
    /// <remarks>
    /// One watermark per source, never one across them: see <see cref="SqlConnectorWatermark"/> for why
    /// a single maximum would permanently skip whatever the slower-moving sources had recorded below it.
    /// </remarks>
    private async Task<SqlConnectorWatermark> CaptureWatermarkAsync(IReadOnlyList<SqlImportPlan> plans)
    {
        var watermark = new SqlConnectorWatermark { Mode = _deltaMode };

        foreach (var plan in plans)
        {
            var (source, changeColumn) = ResolveChangeSource(plan);

            // No highest value at all is what an empty change log, or a source nothing has been written
            // to, looks like. Recording nothing for it means the next run reads from the beginning,
            // which is the only answer that cannot miss a change.
            var described = SqlConnectorWatermark.Describe(await ReadHighestValueAsync(source, changeColumn));
            if (described != null)
                watermark.ObjectTypes[plan.Name] = described;

            // Only Watermark Column mode reads a related table for changes; a change log states what
            // happened to the object however it happened.
            if (_deltaMode != SqlDeltaImportMode.WatermarkColumn)
                continue;

            var relatedWatermarks = await CaptureRelatedWatermarksAsync(plan);
            if (relatedWatermarks.Count > 0)
                watermark.RelatedTables[plan.Name] = relatedWatermarks;
        }

        return watermark;
    }

    /// <summary>
    /// Asks each of an object type's related tables where it currently stands.
    /// </summary>
    /// <remarks>
    /// A related table with no watermark column records nothing and is not refused here: this also runs
    /// for a Full Import, which is both how a baseline is established and how JIM recovers from an
    /// unusable watermark, and refusing it would take away the remedy. The next Delta Import refuses the
    /// same configuration loudly, so nothing is hidden by leaving it alone now.
    /// </remarks>
    private async Task<Dictionary<string, SqlDeltaValue>> CaptureRelatedWatermarksAsync(SqlImportPlan plan)
    {
        var relatedWatermarks = new Dictionary<string, SqlDeltaValue>(StringComparer.OrdinalIgnoreCase);

        // Only the related tables declaring a watermark column have one to capture. A Full Import reaches
        // here with tables that do not, which is deliberate: see the remarks above.
        var watermarked = plan.RelatedTables
            .Select(relatedTable => relatedTable.Configuration)
            .Where(configuration => configuration.WatermarkColumn != null);

        foreach (var configuration in watermarked)
        {
            var source = _provider.QualifyObjectName(configuration.SchemaName, configuration.TableName);
            var described = SqlConnectorWatermark.Describe(await ReadHighestValueAsync(source, configuration.WatermarkColumn!));

            if (described != null)
                relatedWatermarks[configuration.AttributeName] = described;
        }

        return relatedWatermarks;
    }

    /// <summary>
    /// The highest value a column currently holds, which is where a watermark comes from. No dialect
    /// divergence to hide behind the provider seam: an aggregate over a source is the same statement in
    /// both, built from the same quoting the seam already provides.
    /// </summary>
    private async Task<object?> ReadHighestValueAsync(string source, string column)
    {
        using var command = _provider.CreateCommand(_connection, $"SELECT MAX({_provider.QuoteIdentifier(column)}) FROM {source}");
        return await command.ExecuteScalarAsync(_cancellationToken);
    }

    /// <summary>
    /// Asks the database how much this Delta Import is about to read, which is what turns the fetch into
    /// a percentage and a time remaining rather than a number counting up.
    /// </summary>
    /// <remarks>
    /// In change-log mode this counts change rows rather than objects, so it is an upper bound: an
    /// object changed three times is three rows and one object. Counting objects instead would need a
    /// DISTINCT over a composite anchor, which the two dialects do not express the same way, for a
    /// number that is already right in the overwhelmingly common case.
    /// </remarks>
    private async Task ReportExpectedChangeCountAsync(IReadOnlyList<SqlImportPlan> plans, SqlConnectorWatermark previous)
    {
        long expected = 0;
        foreach (var plan in plans)
            expected += await CountChangesAsync(plan, previous.ObjectTypes.GetValueOrDefault(plan.Name), previous.RelatedTablesFor(plan.Name));

        _logger.Debug("SqlConnectorImport: expecting up to {ExpectedObjectCount} changed object(s) across {ObjectTypeCount} Object Type(s)", expected, plans.Count);

        await _progress.ReportExpectedObjectCountAsync(expected > int.MaxValue ? int.MaxValue : (int)expected);
    }

    /// <summary>
    /// How many rows this object type's pages are about to return, counted against exactly the predicate
    /// those pages read with, so that the count and the read can never disagree about what a change is.
    /// </summary>
    private async Task<long> CountChangesAsync(SqlImportPlan plan, SqlDeltaValue? watermark, IReadOnlyDictionary<string, SqlDeltaValue> relatedWatermarks)
    {
        var relatedChanges = watermark == null || _deltaMode != SqlDeltaImportMode.WatermarkColumn
            ? []
            : BuildRelatedChanges(plan, relatedWatermarks);

        var (source, changeColumn) = ResolveChangeSource(plan, relatedChanges.Count > 0);

        var commandText = watermark == null
            ? $"SELECT COUNT(*) FROM {source}"
            : $"SELECT COUNT(*) FROM {source} WHERE {BuildChangedRowsPredicate(plan, changeColumn, relatedChanges)}";

        using var command = _provider.CreateCommand(_connection, commandText);

        if (watermark != null)
        {
            command.Parameters.Add(_provider.CreateParameter(WatermarkParameterName, BindDeltaValue(plan, watermark, "watermark")));
            BindRelatedWatermarks(command, plan, relatedChanges);
        }

        var count = await command.ExecuteScalarAsync(_cancellationToken);

        return count == null || count == DBNull.Value ? 0 : Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What this object type's changes are read from, and the column that orders them, for whichever
    /// mode is configured.
    /// </summary>
    /// <param name="plan">The object type being read.</param>
    /// <param name="aliased">Whether the source has to be named, which it does exactly when a correlated subquery refers back to its columns.</param>
    private (string Source, string ChangeColumn) ResolveChangeSource(SqlImportPlan plan, bool aliased = false)
    {
        if (_deltaMode != SqlDeltaImportMode.ChangeLogTable)
            return (BuildFromClause(plan, aliased), RequireWatermarkColumn(plan));

        var changeLog = RequireChangeLog(plan);
        return (_provider.QualifyObjectName(changeLog.SchemaName, changeLog.TableName), changeLog.SequenceColumn);
    }

    /// <summary>
    /// The related tables this object type's changes may also come from, each paired with the watermark
    /// JIM holds for it.
    /// </summary>
    /// <exception cref="SqlSchemaConfigurationException">A related table has no watermark column for this mode to read. Refused when the Connected System is saved; this is the backstop for configuration changed since.</exception>
    private static List<SqlRelatedChange> BuildRelatedChanges(SqlImportPlan plan, IReadOnlyDictionary<string, SqlDeltaValue> relatedWatermarks)
    {
        return [.. plan.RelatedTables.Select((relatedTable, index) =>
        {
            var configuration = relatedTable.Configuration;
            var watermark = relatedWatermarks.GetValueOrDefault(configuration.AttributeName);

            var source = new SqlRelatedChangeSource
            {
                SchemaName = configuration.SchemaName,
                TableName = configuration.TableName,
                JoinColumns = configuration.JoinColumns,
                WatermarkColumn = RequireRelatedWatermarkColumn(plan, configuration),

                // No watermark for this related table yet (it was added to the document after the last
                // run, or JIM last ran before it watched related tables at all) means every parent it
                // holds a row for is read once. One expensive run beats a missed change.
                WatermarkParameterName = watermark == null ? null : RelatedWatermarkParameterName(index)
            };

            return new SqlRelatedChange(source, watermark);
        })];
    }

    /// <summary>
    /// The predicate selecting the rows this object type considers changed, rendered by the dialect so
    /// that no SQL of any dialect's is written here.
    /// </summary>
    private string BuildChangedRowsPredicate(SqlImportPlan plan, string changeColumn, IReadOnlyList<SqlRelatedChange> relatedChanges) =>
        _provider.BuildChangedRowsPredicate(new SqlChangeFilter
        {
            ChangeColumn = changeColumn,
            ChangeParameterName = WatermarkParameterName,
            AnchorColumns = [.. plan.AnchorColumns.Select(anchorColumn => anchorColumn.Name)],
            RelatedSources = [.. relatedChanges.Select(relatedChange => relatedChange.Source)]
        });

    /// <summary>
    /// Binds each related table's own watermark, skipping the ones JIM holds none for: those have no
    /// parameter in the statement because their predicate is existence alone.
    /// </summary>
    private void BindRelatedWatermarks(DbCommand command, SqlImportPlan plan, IReadOnlyList<SqlRelatedChange> relatedChanges)
    {
        foreach (var relatedChange in relatedChanges.Where(relatedChange => relatedChange.Source.WatermarkParameterName != null))
            command.Parameters.Add(_provider.CreateParameter(
                relatedChange.Source.WatermarkParameterName!,
                BindDeltaValue(plan, relatedChange.Watermark!, $"watermark for related table '{relatedChange.Source.TableName}'")));
    }

    private static string RelatedWatermarkParameterName(int index) => $"{RelatedWatermarkParameterPrefix}{index}";

    /// <exception cref="SqlSchemaConfigurationException">The Object Types document has nothing for the configured mode to read on this related table.</exception>
    private static string RequireRelatedWatermarkColumn(SqlImportPlan plan, SqlRelatedTableConfiguration relatedTable) =>
        relatedTable.WatermarkColumn ?? throw new SqlSchemaConfigurationException(
            $"{SqlConnectorConstants.SettingDeltaImportMode} is '{SqlConnectorConstants.DeltaImportModeWatermarkColumn}', but Object Type '{plan.Name}' has related table attribute '{relatedTable.AttributeName}' with no 'watermarkColumn' in {SqlConnectorConstants.SettingObjectTypes}. " +
            $"A row added to or removed from '{relatedTable.TableName}' changes the object without touching its own row, so without one that change could never be detected.");

    /// <exception cref="SqlSchemaConfigurationException">The Object Types document has nothing for the configured mode to read. Refused when the Connected System is saved; this is the backstop for configuration changed since.</exception>
    private static SqlChangeLogConfiguration RequireChangeLog(SqlImportPlan plan) =>
        plan.Configuration.ChangeLog ?? throw new SqlSchemaConfigurationException(
            $"{SqlConnectorConstants.SettingDeltaImportMode} is '{SqlConnectorConstants.DeltaImportModeChangeLogTable}', but Object Type '{plan.Name}' has no 'changeLog' in {SqlConnectorConstants.SettingObjectTypes}.");

    /// <exception cref="SqlSchemaConfigurationException">The Object Types document has nothing for the configured mode to read.</exception>
    private static string RequireWatermarkColumn(SqlImportPlan plan) =>
        plan.Configuration.WatermarkColumn ?? throw new SqlSchemaConfigurationException(
            $"{SqlConnectorConstants.SettingDeltaImportMode} is '{SqlConnectorConstants.DeltaImportModeWatermarkColumn}', but Object Type '{plan.Name}' has no 'watermarkColumn' in {SqlConnectorConstants.SettingObjectTypes}.");

    /// <summary>
    /// Runs one page of a Delta Import's keyset read, binding the run's watermark and the position the
    /// previous page ended at.
    /// </summary>
    private async Task<List<object?[]>> ReadDeltaPageAsync(
        SqlImportPlan plan,
        SqlKeysetPageRequest request,
        SqlDeltaPagePosition page,
        SqlDeltaValue? watermark,
        IReadOnlyList<string> columns,
        IReadOnlyList<SqlRelatedChange>? relatedChanges = null)
    {
        using var command = _provider.CreateCommand(_connection, _provider.BuildKeysetPageCommandText(request));
        command.Parameters.Add(_provider.CreateParameter(PageSizeParameterName, _runProfile.PageSize));

        if (request.HasChangeFilter)
        {
            command.Parameters.Add(_provider.CreateParameter(WatermarkParameterName, BindDeltaValue(plan, watermark!, "watermark")));
            BindRelatedWatermarks(command, plan, relatedChanges ?? []);
        }

        for (var index = 0; index < page.Position.Count; index++)
            command.Parameters.Add(_provider.CreateParameter(AnchorParameterName(index), BindDeltaValue(plan, page.Position[index], "pagination token")));

        return await ReadRowsAsync(command, columns);
    }

    /// <summary>
    /// Turns a value carried between pages, or between runs, back into what a predicate compares against.
    /// </summary>
    /// <exception cref="InvalidDataException">The value cannot be read for the type it was written with, which would otherwise resume from the wrong place.</exception>
    private object BindDeltaValue(SqlImportPlan plan, SqlDeltaValue deltaValue, string description)
    {
        if (!SqlAnchorValue.TryFromTokenString(deltaValue.Value, deltaValue.Type, out var value) || value == null)
            throw new InvalidDataException(
                $"Object Type '{plan.Name}' was replayed a {description} whose value '{deltaValue.Value}' cannot be read as a {deltaValue.Type}. Run a Full Import to re-establish the baseline.");

        // The byte order a GUID is bound in is dialect-specific, so it goes back through the provider
        // rather than being handed to the driver as it came out of the token.
        return deltaValue.Type == AttributeDataType.Guid ? _provider.ConvertFromGuid((Guid)value) : value;
    }

    /// <summary>
    /// Describes where a page ended, so the next one resumes immediately after it.
    /// </summary>
    /// <exception cref="InvalidDataException">A page ended on a row with nothing to resume from, which would restart the read from the beginning for ever.</exception>
    private static List<SqlDeltaValue> DescribeKeyset(
        SqlImportPlan plan,
        IReadOnlyList<SqlDeltaKeysetColumn> keysetColumns,
        object?[] lastRow,
        IReadOnlyList<string> columns)
    {
        return [.. keysetColumns.Select(keysetColumn =>
        {
            var value = lastRow[IndexOfColumn(columns, keysetColumn.Name)] ?? throw new InvalidDataException(
                $"Object Type '{plan.Name}' read a page ending on a row with a NULL value in '{keysetColumn.Name}', which orders the page, so there is nothing to resume the next one from.");

            return keysetColumn.Type == null
                ? SqlConnectorWatermark.Describe(value)!
                : new SqlDeltaValue(SqlAnchorValue.ToTokenString(value, keysetColumn.Type.Value), keysetColumn.Type.Value);
        })];
    }

    private static int IndexOfColumn(IReadOnlyList<string> columns, string columnName)
    {
        for (var index = 0; index < columns.Count; index++)
            if (string.Equals(columns[index], columnName, StringComparison.OrdinalIgnoreCase))
                return index;

        throw new SqlSchemaConfigurationException($"Column '{columnName}' was not returned by the query that was supposed to read it.");
    }

    #endregion

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

    /// <summary>
    /// What an object type's rows are read from. A statement standing in for a table is always named,
    /// because a derived table has to be; a table or view is named only where a correlated subquery
    /// refers back to its columns, so every statement that needed no alias before still generates none.
    /// </summary>
    private string BuildFromClause(SqlImportPlan plan, bool aliased = false)
    {
        if (plan.Configuration.IsCustomSelect)
            return $"({plan.Configuration.SelectStatement}) {_provider.QuoteIdentifier(SqlKeysetPageRequest.SourceAlias)}";

        var qualifiedObjectName = _provider.QualifyObjectName(plan.Configuration.SchemaName, plan.Configuration.TableName!);

        return aliased ? $"{qualifiedObjectName} {_provider.QuoteIdentifier(SqlKeysetPageRequest.SourceAlias)}" : qualifiedObjectName;
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

        return await ReadRowsAsync(command, plan.SelectColumns);
    }

    /// <summary>
    /// Materialises a result set as rows in the caller's column order, so that everything downstream
    /// addresses a row by the position it asked for rather than by whatever ordinal the driver used.
    /// </summary>
    private async Task<List<object?[]>> ReadRowsAsync(DbCommand command, IReadOnlyList<string> columns)
    {
        var rows = new List<object?[]>();

        using var reader = await command.ExecuteReaderAsync(_cancellationToken);
        var ordinals = columns.Select(reader.GetOrdinal).ToArray();

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
                $"Object Type '{plan.Name}' was replayed a pagination token holding {parsed?.Anchor?.Count ?? 0} anchor value(s), but its anchor has {plan.AnchorColumns.Count} column(s). The configuration changed mid-run; run a Full Import again.");

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

/// <summary>
/// A column a Delta Import's page is ordered and seeked on, and the type its values are carried as. A
/// null type means the column is not part of the Connected System's schema (a change log's own columns
/// are not), so its type is read from the value the database returns.
/// </summary>
internal sealed record SqlDeltaKeysetColumn(string Name, AttributeDataType? Type);

/// <summary>
/// One change a change log records, reduced to what JIM acts on.
/// </summary>
internal sealed record SqlChangeLogEntry(string AnchorKey, object?[] AnchorValues, ObjectChangeType ChangeType, string? UnmappedChangeType);

/// <summary>
/// One related table a Watermark Column page watches for changes to its parent object, and the
/// watermark it is compared against (null where JIM holds none for it yet).
/// </summary>
internal sealed record SqlRelatedChange(SqlRelatedChangeSource Source, SqlDeltaValue? Watermark);

/// <summary>
/// What one page of a Delta Import produced, and where the next one resumes from (null where this page
/// was the last).
/// </summary>
internal sealed record SqlDeltaPage(List<ConnectedSystemImportObject> ImportObjects, SqlDeltaPagePosition? NextPosition);

/// <summary>
/// Where one Object Type's Delta Import has got to: the values the previous page ended on, and which
/// page is being read, so the narration can say so.
/// </summary>
/// <remarks>
/// Each value carries its own type, which a Full Import's token does not have to: a Full Import seeks on
/// the anchor alone, whose type the Connected System's schema states, while a Delta Import also seeks on
/// a change log's sequence column, which belongs to no object type and is therefore described by what
/// the database returned rather than by anything declared.
/// </remarks>
internal sealed record SqlDeltaPagePosition
{
    private static readonly JsonSerializerOptions TokenOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal IReadOnlyList<SqlDeltaValue> Position { get; init; } = [];

    internal int PageNumber { get; init; } = 1;

    internal bool IsFirstPage => Position.Count == 0;

    /// <exception cref="InvalidDataException">The token cannot be read for this configuration, which would otherwise resume the page from the wrong row.</exception>
    internal static SqlDeltaPagePosition FromToken(ConnectedSystemPaginationToken? token, SqlImportPlan plan)
    {
        if (string.IsNullOrEmpty(token?.StringValue))
            return new SqlDeltaPagePosition();

        SqlDeltaPageToken? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SqlDeltaPageToken>(token.StringValue, TokenOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Object Type '{plan.Name}' was replayed a pagination token JIM cannot read. Run the Run Profile again to start from the beginning.", ex);
        }

        if (parsed?.Position == null || parsed.Position.Count == 0)
            throw new InvalidDataException(
                $"Object Type '{plan.Name}' was replayed a pagination token holding nothing to resume from. The configuration changed mid-run; run the Run Profile again.");

        return new SqlDeltaPagePosition { Position = parsed.Position, PageNumber = parsed.Page };
    }

    internal SqlDeltaPagePosition Advance(List<SqlDeltaValue> position) => new() { Position = position, PageNumber = PageNumber + 1 };

    internal ConnectedSystemPaginationToken ToToken(SqlImportPlan plan) =>
        new(plan.TokenName, JsonSerializer.Serialize(new SqlDeltaPageToken([.. Position], PageNumber), TokenOptions));
}

/// <summary>
/// A Delta Import pagination token's contents as they are written and read back.
/// </summary>
internal sealed record SqlDeltaPageToken(List<SqlDeltaValue> Position, int Page);
