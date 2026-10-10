// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exceptions;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    #region Generated Values (#242)

    /// <inheritdoc />
    public async Task<HashSet<string>> GetMetaverseAttributeValuesInUseAsync(int metaverseAttributeId, IReadOnlyCollection<string> normalisedValues, Guid? excludingMetaverseObjectId)
    {
        if (normalisedValues.Count == 0)
            return [];

        // LOWER("StringValue") on the left matches the expression index (IX_MetaverseObjectAttributeValues_
        // AttributeId_LowerStringValue) exactly; normalisedValues arrives already lower-cased by the caller, so
        // no LOWER() is needed on the right-hand array.
        var sql = @"SELECT DISTINCT LOWER(""StringValue"") AS ""Value""
                    FROM ""MetaverseObjectAttributeValues""
                    WHERE ""AttributeId"" = {0} AND ""StringValue"" IS NOT NULL AND LOWER(""StringValue"") = ANY({1})";
        var parameters = new List<object> { metaverseAttributeId, normalisedValues.ToArray() };

        if (excludingMetaverseObjectId.HasValue)
        {
            sql += @" AND ""MetaverseObjectId"" <> {2}";
            parameters.Add(excludingMetaverseObjectId.Value);
        }

        var rows = await _context.Database.SqlQueryRaw<string>(sql, parameters.ToArray()).ToListAsync();
        return rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<HashSet<string>> GetConnectedSystemAttributeValuesInUseAsync(int connectedSystemObjectTypeAttributeId, IReadOnlyCollection<string> normalisedValues, Guid? excludingConnectedSystemObjectId)
    {
        if (normalisedValues.Count == 0)
            return [];

        var sql = @"SELECT DISTINCT LOWER(""StringValue"") AS ""Value""
                    FROM ""ConnectedSystemObjectAttributeValues""
                    WHERE ""AttributeId"" = {0} AND ""StringValue"" IS NOT NULL AND LOWER(""StringValue"") = ANY({1})";
        var parameters = new List<object> { connectedSystemObjectTypeAttributeId, normalisedValues.ToArray() };

        if (excludingConnectedSystemObjectId.HasValue)
        {
            sql += @" AND ""ConnectedSystemObjectId"" <> {2}";
            parameters.Add(excludingConnectedSystemObjectId.Value);
        }

        var rows = await _context.Database.SqlQueryRaw<string>(sql, parameters.ToArray()).ToListAsync();
        return rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorSpaceValueHolder>> GetConnectedSystemAttributeValueHoldersAsync(int connectedSystemObjectTypeAttributeId, IReadOnlyCollection<string> normalisedValues)
    {
        if (normalisedValues.Count == 0)
            return [];

        // Holders, not a taken set: the gate filters out the requesting object's own accounts per request, so one
        // query per attribute serves a whole batch. Only candidate matches come back, so the row count is tiny.
        var rows = await _context.Database.SqlQueryRaw<ConnectorSpaceValueHolderRow>(
            @"SELECT LOWER(av.""StringValue"") AS ""NormalisedValue"", NULL::bigint AS ""NumberValue"",
                     av.""ConnectedSystemObjectId"" AS ""ConnectedSystemObjectId"", cso.""MetaverseObjectId"" AS ""MetaverseObjectId""
              FROM ""ConnectedSystemObjectAttributeValues"" av
              JOIN ""ConnectedSystemObjects"" cso ON cso.""Id"" = av.""ConnectedSystemObjectId""
              WHERE av.""AttributeId"" = {0} AND av.""StringValue"" IS NOT NULL AND LOWER(av.""StringValue"") = ANY({1})",
            connectedSystemObjectTypeAttributeId, normalisedValues.ToArray()).ToListAsync();

        return rows.Select(r => r.ToHolder()).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetConnectedSystemAttributeSampleValuesAsync(int connectedSystemObjectTypeAttributeId, int maximumCount)
    {
        if (maximumCount <= 0)
            return [];

        // The inner LIMIT bounds the rows read before DISTINCT, so a sample costs a handful of rows rather than a
        // de-duplication of every value the attribute holds (it runs once per probed attribute per run, but an
        // attribute can hold a million values). A probed attribute is meant to be unique, so a few times the wanted
        // count is ample to find that many distinct values; finding fewer only means fewer controls to choose from.
        var rows = await _context.Database.SqlQueryRaw<string>(
            @"SELECT DISTINCT s.""Value""
              FROM (SELECT av.""StringValue"" AS ""Value""
                    FROM ""ConnectedSystemObjectAttributeValues"" av
                    JOIN ""ConnectedSystemObjects"" cso ON cso.""Id"" = av.""ConnectedSystemObjectId""
                    WHERE av.""AttributeId"" = {0} AND cso.""Status"" = {1}
                      AND av.""StringValue"" IS NOT NULL AND av.""StringValue"" <> ''
                    LIMIT {2}) s
              LIMIT {3}",
            connectedSystemObjectTypeAttributeId, (int)ConnectedSystemObjectStatus.Normal, maximumCount * 4, maximumCount).ToListAsync();

        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ConnectorSpaceValueHolder>> GetConnectedSystemAttributeNumberHoldersAsync(int connectedSystemObjectTypeAttributeId, IReadOnlyCollection<long> values)
    {
        if (values.Count == 0)
            return [];

        var rows = await _context.Database.SqlQueryRaw<ConnectorSpaceValueHolderRow>(
            @"SELECT NULL::text AS ""NormalisedValue"", COALESCE(av.""IntValue""::bigint, av.""LongValue"") AS ""NumberValue"",
                     av.""ConnectedSystemObjectId"" AS ""ConnectedSystemObjectId"", cso.""MetaverseObjectId"" AS ""MetaverseObjectId""
              FROM ""ConnectedSystemObjectAttributeValues"" av
              JOIN ""ConnectedSystemObjects"" cso ON cso.""Id"" = av.""ConnectedSystemObjectId""
              WHERE av.""AttributeId"" = {0} AND (av.""IntValue"" = ANY({1}) OR av.""LongValue"" = ANY({1}))",
            connectedSystemObjectTypeAttributeId, values.ToArray()).ToListAsync();

        return rows.Select(r => r.ToHolder()).ToList();
    }

    private sealed record ConnectorSpaceValueHolderRow(string? NormalisedValue, long? NumberValue, Guid ConnectedSystemObjectId, Guid? MetaverseObjectId)
    {
        public ConnectorSpaceValueHolder ToHolder() => new(NormalisedValue, NumberValue, ConnectedSystemObjectId, MetaverseObjectId);
    }

    /// <inheritdoc />
    public async Task<HashSet<long>> GetMetaverseAttributeNumbersInUseAsync(int metaverseAttributeId, IReadOnlyCollection<long> values, Guid? excludingMetaverseObjectId)
    {
        if (values.Count == 0)
            return [];

        // A value already held as IntValue is just as taken as one held as LongValue: the two columns are the
        // same attribute type distinction (Number vs Long Number), not two independent value spaces.
        var exclude = excludingMetaverseObjectId.HasValue ? @" AND ""MetaverseObjectId"" <> {2}" : string.Empty;
        var sql = $@"SELECT DISTINCT ""IntValue""::bigint AS ""Value""
                    FROM ""MetaverseObjectAttributeValues""
                    WHERE ""AttributeId"" = {{0}} AND ""IntValue"" = ANY({{1}}){exclude}
                    UNION
                    SELECT DISTINCT ""LongValue"" AS ""Value""
                    FROM ""MetaverseObjectAttributeValues""
                    WHERE ""AttributeId"" = {{0}} AND ""LongValue"" = ANY({{1}}){exclude}";

        var parameters = new List<object> { metaverseAttributeId, values.ToArray() };
        if (excludingMetaverseObjectId.HasValue)
            parameters.Add(excludingMetaverseObjectId.Value);

        var rows = await _context.Database.SqlQueryRaw<long>(sql, parameters.ToArray()).ToListAsync();
        return rows.ToHashSet();
    }

    /// <inheritdoc />
    public async Task<HashSet<long>> GetConnectedSystemAttributeNumbersInUseAsync(int connectedSystemObjectTypeAttributeId, IReadOnlyCollection<long> values, Guid? excludingConnectedSystemObjectId)
    {
        if (values.Count == 0)
            return [];

        var exclude = excludingConnectedSystemObjectId.HasValue ? @" AND ""ConnectedSystemObjectId"" <> {2}" : string.Empty;
        var sql = $@"SELECT DISTINCT ""IntValue""::bigint AS ""Value""
                    FROM ""ConnectedSystemObjectAttributeValues""
                    WHERE ""AttributeId"" = {{0}} AND ""IntValue"" = ANY({{1}}){exclude}
                    UNION
                    SELECT DISTINCT ""LongValue"" AS ""Value""
                    FROM ""ConnectedSystemObjectAttributeValues""
                    WHERE ""AttributeId"" = {{0}} AND ""LongValue"" = ANY({{1}}){exclude}";

        var parameters = new List<object> { connectedSystemObjectTypeAttributeId, values.ToArray() };
        if (excludingConnectedSystemObjectId.HasValue)
            parameters.Add(excludingConnectedSystemObjectId.Value);

        var rows = await _context.Database.SqlQueryRaw<long>(sql, parameters.ToArray()).ToListAsync();
        return rows.ToHashSet();
    }

    /// <inheritdoc />
    public async Task<HashSet<string>> GetGeneratedValueAssignmentValuesInUseAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, IReadOnlyCollection<string> normalisedValues, Guid? excludingObjectId)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);

        if (normalisedValues.Count == 0)
            return [];

        var attributeColumn = metaverseAttributeId.HasValue ? "MetaverseAttributeId" : "ConnectedSystemObjectTypeAttributeId";
        var objectColumn = metaverseAttributeId.HasValue ? "MetaverseObjectId" : "ConnectedSystemObjectId";
        var attributeId = metaverseAttributeId ?? connectedSystemObjectTypeAttributeId!.Value;

        // IS DISTINCT FROM is what lets one static query handle both cases: a null excludingObjectId (no
        // exclusion; the object column, always populated on a live assignment, is distinct from NULL on every
        // row) and a real one (excluded only from the one row that matches it).
        var sql = $@"SELECT ""NormalisedValue"" AS ""Value""
                    FROM ""GeneratedValueAssignments""
                    WHERE ""{attributeColumn}"" = {{0}} AND ""NormalisedValue"" = ANY({{1}}) AND (""{objectColumn}"" IS DISTINCT FROM {{2}})";

        var rows = await _context.Database.SqlQueryRaw<string>(
            sql,
            attributeId,
            normalisedValues.ToArray(),
            BulkSqlHelpers.NullableParam(excludingObjectId, NpgsqlTypes.NpgsqlDbType.Uuid)).ToListAsync();

        return rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task CreateGeneratedValueAssignmentsAsync(IReadOnlyCollection<GeneratedValueAssignment> assignments)
    {
        if (assignments.Count == 0)
            return;

        _context.GeneratedValueAssignments.AddRange(assignments);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" } pgEx)
        {
            // Detach every entity this call added: a failed SaveChangesAsync does not untrack them (EF leaves a
            // failed insert's entries in the Added state), so without this, the next unrelated SaveChangesAsync
            // on the same context (the worker's page-flush context lives for the whole run) would re-attempt
            // exactly the same failed INSERT and throw again, and a caller's one-at-a-time retry of this same
            // batch would resend every entity, not just the one(s) that actually lost the race.
            foreach (var assignment in assignments)
                _context.Entry(assignment).State = EntityState.Detached;

            // Decision 13's losing-run rule: the cross-assignment unique index on (attribute, NormalisedValue)
            // is what makes uniqueness hold across concurrent runs and pages, and this is what its loser sees.
            // The caller is expected to recognise this exception and draw the next candidate, not treat it as an
            // unclassified database failure.
            throw new GeneratedValueConflictException(
                $"A generated value assignment could not be created because the value is already held by another live assignment for the same attribute: {pgEx.MessageText}", ex);
        }
    }

    /// <inheritdoc />
    public async Task UpdateGeneratedValueAssignmentAsync(GeneratedValueAssignment assignment)
    {
        assignment.LastUpdated = DateTime.UtcNow;

        // UpdateDetachedSafe marks only this entity Modified, without walking its navigation properties (which
        // would otherwise risk identity conflicts with instances the sync page's other queries already track).
        _repo.UpdateDetachedSafe(assignment);
        await _context.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task DeleteGeneratedValueAssignmentsAsync(IReadOnlyCollection<Guid> assignmentIds)
    {
        if (assignmentIds.Count == 0)
            return;

        // Fix up the tracker before the raw delete: the worker's per-run context tracks by default, and a
        // deleted-but-still-tracked assignment would poison the next SaveChangesAsync exactly as an untracked
        // raw DELETE does for any other entity (src/CLAUDE.md, "Raw SQL Writes Must Fix Up or Detach Tracked
        // Instances").
        var idSet = assignmentIds.ToHashSet();
        DetachTrackedEntities<GeneratedValueAssignment>(a => idSet.Contains(a.Id));

        await _context.Database.ExecuteSqlRawAsync(
            @"DELETE FROM ""GeneratedValueAssignments"" WHERE ""Id"" = ANY({0})",
            assignmentIds.ToArray());
    }

    /// <inheritdoc />
    public Task<GeneratedValueAssignment?> GetGeneratedValueAssignmentAsync(Guid metaverseObjectId, int metaverseAttributeId)
    {
        return _context.GeneratedValueAssignments
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.MetaverseObjectId == metaverseObjectId && a.MetaverseAttributeId == metaverseAttributeId);
    }

    /// <inheritdoc />
    public Task<GeneratedValueAssignment?> GetGeneratedValueAssignmentForConnectedSystemObjectAsync(Guid connectedSystemObjectId, int connectedSystemObjectTypeAttributeId)
    {
        return _context.GeneratedValueAssignments
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.ConnectedSystemObjectId == connectedSystemObjectId && a.ConnectedSystemObjectTypeAttributeId == connectedSystemObjectTypeAttributeId);
    }

    /// <inheritdoc />
    public async Task<List<GeneratedValueAssignment>> GetGeneratedValueAssignmentsForMetaverseObjectsAsync(IReadOnlyCollection<Guid> metaverseObjectIds)
    {
        if (metaverseObjectIds.Count == 0)
            return [];

        return await _context.GeneratedValueAssignments
            .AsNoTracking()
            .Where(a => a.MetaverseObjectId != null && metaverseObjectIds.Contains(a.MetaverseObjectId!.Value))
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<GeneratedValueAssignment>> GetGeneratedValueAssignmentsForConnectedSystemObjectsAsync(IReadOnlyCollection<Guid> connectedSystemObjectIds)
    {
        if (connectedSystemObjectIds.Count == 0)
            return [];

        return await _context.GeneratedValueAssignments
            .AsNoTracking()
            .Where(a => a.ConnectedSystemObjectId != null && connectedSystemObjectIds.Contains(a.ConnectedSystemObjectId!.Value))
            .ToListAsync();
    }

    /// <inheritdoc />
    public Task<List<GeneratedValueAssignment>> GetGeneratedValueAssignmentsForGenerationAsync(int syncRuleMappingGenerationId)
    {
        return _context.GeneratedValueAssignments
            .AsNoTracking()
            .Where(a => a.SyncRuleMappingGenerationId == syncRuleMappingGenerationId)
            .ToListAsync();
    }

    /// <inheritdoc />
    public Task<GeneratedValueSequence?> GetGeneratedValueSequenceAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);

        return _context.GeneratedValueSequences
            .AsNoTracking()
            .SingleOrDefaultAsync(s =>
                s.MetaverseAttributeId == metaverseAttributeId &&
                s.ConnectedSystemObjectTypeAttributeId == connectedSystemObjectTypeAttributeId);
    }

    /// <inheritdoc />
    public async Task<long?> GetHighestNumericValueForAttributeAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);

        var table = metaverseAttributeId.HasValue ? "MetaverseObjectAttributeValues" : "ConnectedSystemObjectAttributeValues";
        var attributeId = metaverseAttributeId ?? connectedSystemObjectTypeAttributeId!.Value;

        // The character class deliberately avoids a {m,n} quantifier: EF's own raw-SQL parameter parser reads
        // "{n}" in the SQL text as a placeholder reference, so a literal regex quantifier there would be
        // misread as an out-of-range parameter index. The digit-only check plus a length bound achieves the
        // same "1 to 18 digits" constraint without one.
        var sql = $@"SELECT MAX(v) AS ""Value"" FROM (
                        SELECT ""IntValue""::bigint AS v FROM ""{table}"" WHERE ""AttributeId"" = {{0}} AND ""IntValue"" IS NOT NULL
                        UNION ALL
                        SELECT ""LongValue"" FROM ""{table}"" WHERE ""AttributeId"" = {{0}} AND ""LongValue"" IS NOT NULL
                        UNION ALL
                        SELECT ""StringValue""::bigint FROM ""{table}""
                        WHERE ""AttributeId"" = {{0}} AND ""StringValue"" ~ '^[0-9]+$' AND char_length(""StringValue"") BETWEEN 1 AND 18
                    ) numeric_values";

        return await _context.Database.SqlQueryRaw<long?>(sql, attributeId).SingleAsync();
    }

    /// <summary>
    /// How many numbers <see cref="GetSequenceHeldRunAsync"/> first checks through the stores' value indexes before it
    /// reads every value the attribute holds: enough that an isolated collision, or a short run, never costs a scan of
    /// a large attribute, while a run longer than this costs one window and one scan, not a window per thousand.
    /// </summary>
    internal const int SequenceHeldRunWindow = 1000;

    /// <inheritdoc />
    public async Task<SequenceHeldRun> GetSequenceHeldRunAsync(SequenceSkipQuery query)
    {
        ValidateExactlyOneAttributeReference(query.MetaverseAttributeId, query.ConnectedSystemObjectTypeAttributeId);

        // Most runs are short (a collision or two), so look the next window of numbers up by value first: an index
        // probe per store, whatever the attribute's size. Only a window held from end to end, which is what a long
        // run looks like, needs every value the attribute holds read to find where the run ends.
        var windowNumbers = Enumerable.Range(0, SequenceHeldRunWindow).Select(k => query.From + (long)k * query.Increment).ToArray();
        var windowed = await ReadSequenceHeldRunAsync(query, windowNumbers);
        if (windowed.FirstFreeNumber < query.From + (long)SequenceHeldRunWindow * query.Increment)
            return windowed;

        return await ReadSequenceHeldRunAsync(query, window: null);
    }

    /// <summary>
    /// The run of held numbers from <see cref="SequenceSkipQuery.From"/> and its holders, reading only holdings of the
    /// numbers in <paramref name="window"/> (through the value indexes) when one is given, or every holding of the
    /// attribute when not. With a window, a run reaching the window's end is reported as ending there, which the caller
    /// reads as "longer than the window".
    /// </summary>
    private async Task<SequenceHeldRun> ReadSequenceHeldRunAsync(SequenceSkipQuery query, long[]? window)
    {
        var importMode = query.MetaverseAttributeId.HasValue;
        var valueTable = importMode ? "MetaverseObjectAttributeValues" : "ConnectedSystemObjectAttributeValues";
        var valueObjectColumn = importMode ? "MetaverseObjectId" : "ConnectedSystemObjectId";
        var attributeColumn = importMode ? "MetaverseAttributeId" : "ConnectedSystemObjectTypeAttributeId";

        // Window filters, written to match the expression indexes the gates already use (LOWER("StringValue"),
        // "NormalisedValue", LOWER("NormalisedValue")); empty when reading everything.
        var hasWindow = window != null;
        var textFilter = hasWindow ? @" AND LOWER(v.""StringValue"") = ANY(@windowValues)" : string.Empty;
        var numberFilter = hasWindow ? @" AND (v.""IntValue"" = ANY(@windowNumbers) OR v.""LongValue"" = ANY(@windowNumbers))" : string.Empty;
        var assignmentFilter = hasWindow ? @" AND a.""NormalisedValue"" = ANY(@windowValues)" : string.Empty;
        var retiredFilter = hasWindow ? @" AND LOWER(r.""NormalisedValue"") = ANY(@windowValues)" : string.Empty;

        // Every store the uniqueness gates read, each row a holding: (text or number, kind, holder, joined Metaverse
        // Object). Text is parsed below exactly as SequenceSkipQuery.TryParseHeldNumber parses it; a numeric target's
        // own values are read as numbers, as the numeric gates read them. The statement is assembled from fixed
        // fragments only; every value is a parameter.
        var textSources = new List<string>
        {
            $@"SELECT a.""NormalisedValue"" AS t, {(int)SequenceNumberHolderKind.Assignment} AS kind, a.""{valueObjectColumn}"" AS object_id, NULL::uuid AS mvo_id
               FROM ""GeneratedValueAssignments"" a WHERE a.""{attributeColumn}"" = @attributeId{assignmentFilter}",
            $@"SELECT LOWER(r.""NormalisedValue""), {(int)SequenceNumberHolderKind.Retired}, NULL::uuid, NULL::uuid
               FROM ""RetiredGeneratedValues"" r WHERE r.""{attributeColumn}"" = @attributeId{retiredFilter}"
        };
        var numberSources = new List<string>();

        var sources = new List<(SequenceNumberHolderKind Kind, string Holder, string From)>
        {
            (SequenceNumberHolderKind.AttributeValue, $@"v.""{valueObjectColumn}"", NULL::uuid",
                $@"FROM ""{valueTable}"" v WHERE v.""AttributeId"" = @attributeId")
        };
        if (importMode && query.ConnectorSpaceAttributeIds.Count > 0)
        {
            sources.Add((SequenceNumberHolderKind.ConnectorSpace, @"v.""ConnectedSystemObjectId"", cso.""MetaverseObjectId""",
                @"FROM ""ConnectedSystemObjectAttributeValues"" v
                  JOIN ""ConnectedSystemObjects"" cso ON cso.""Id"" = v.""ConnectedSystemObjectId""
                  WHERE v.""AttributeId"" = ANY(@connectorSpaceAttributeIds)"));
        }

        foreach (var (kind, holder, from) in sources)
        {
            if (query.NumericTarget)
                numberSources.Add($@"SELECT COALESCE(v.""IntValue""::bigint, v.""LongValue"") AS n, {(int)kind} AS kind, {holder} {from} AND (v.""IntValue"" IS NOT NULL OR v.""LongValue"" IS NOT NULL){numberFilter}");
            else
                textSources.Add($@"SELECT LOWER(v.""StringValue""), {(int)kind}, {holder} {from} AND v.""StringValue"" IS NOT NULL{textFilter}");
        }

        // A held value counts only when it carries the prefix and suffix around a run of digits written exactly as
        // the sequence writes that number; the CASE guards the cast, since PostgreSQL may evaluate a cast before an
        // unrelated filter in the same WHERE. The first free number is From itself when nothing holds it, otherwise
        // the number after the end of the run starting at From: a run's members are exactly Increment apart, so the
        // first held number whose successor is not the next step is where it ends. "candidates" is referenced twice
        // and so materialised once.
        var sql = $@"
            WITH texts AS ({string.Join(" UNION ALL ", textSources)}),
            tokens AS (
                SELECT kind, object_id, mvo_id, substr(t, @prefixLength + 1, char_length(t) - @prefixLength - @suffixLength) AS token
                FROM texts
                WHERE char_length(t) > @prefixLength + @suffixLength AND left(t, @prefixLength) = @prefix AND right(t, @suffixLength) = @suffix
            ),
            parsed AS (
                SELECT kind, object_id, mvo_id, token,
                       CASE WHEN token ~ '^[0-9]+$' AND char_length(token) <= 18 THEN token::bigint END AS n
                FROM tokens
            ),
            holdings AS (
                SELECT n, kind, object_id, mvo_id FROM parsed
                WHERE n IS NOT NULL
                  AND token = CASE WHEN @fixedWidth IS NOT NULL AND char_length(n::text) <= @fixedWidth THEN lpad(n::text, @fixedWidth, '0') ELSE n::text END
                {string.Concat(numberSources.Select(source => " UNION ALL " + source))}
            ),
            candidates AS (SELECT n, kind, object_id, mvo_id FROM holdings WHERE n >= @from AND (n - @from) % @increment = 0),
            held AS (SELECT DISTINCT n FROM candidates),
            run_end AS (
                SELECT COALESCE(
                    (SELECT @from WHERE NOT EXISTS (SELECT 1 FROM held WHERE n = @from)),
                    (SELECT w.n + @increment FROM (SELECT n, lead(n) OVER (ORDER BY n) AS next FROM held) w
                     WHERE w.next IS NULL OR w.next <> w.n + @increment ORDER BY w.n LIMIT 1)) AS first_free
            )
            SELECT r.first_free, c.n, c.kind, c.object_id, c.mvo_id
            FROM run_end r LEFT JOIN candidates c ON c.n < r.first_free
            ORDER BY c.n";

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        var fixedWidthParam = BulkSqlHelpers.NullableParam(query.FixedWidth, NpgsqlTypes.NpgsqlDbType.Integer);
        fixedWidthParam.ParameterName = "fixedWidth";

        await using var command = new NpgsqlCommand(sql, npgsqlConn, npgsqlTx);
        command.Parameters.Add(new NpgsqlParameter("attributeId", NpgsqlTypes.NpgsqlDbType.Integer) { Value = query.MetaverseAttributeId ?? query.ConnectedSystemObjectTypeAttributeId!.Value });
        command.Parameters.Add(new NpgsqlParameter("connectorSpaceAttributeIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer) { Value = query.ConnectorSpaceAttributeIds.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("prefix", NpgsqlTypes.NpgsqlDbType.Text) { Value = query.Prefix });
        command.Parameters.Add(new NpgsqlParameter("suffix", NpgsqlTypes.NpgsqlDbType.Text) { Value = query.Suffix });
        command.Parameters.Add(new NpgsqlParameter("prefixLength", NpgsqlTypes.NpgsqlDbType.Integer) { Value = query.Prefix.Length });
        command.Parameters.Add(new NpgsqlParameter("suffixLength", NpgsqlTypes.NpgsqlDbType.Integer) { Value = query.Suffix.Length });
        command.Parameters.Add(fixedWidthParam);
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = query.From });
        command.Parameters.Add(new NpgsqlParameter("increment", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (long)query.Increment });
        if (window != null)
        {
            command.Parameters.Add(new NpgsqlParameter("windowValues", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = window.Select(query.WriteNumber).ToArray() });
            command.Parameters.Add(new NpgsqlParameter("windowNumbers", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint) { Value = window });
        }

        long? firstFree = null;
        var holders = new List<SequenceNumberHolder>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            firstFree ??= reader.GetInt64(0);
            if (reader.IsDBNull(1))
                continue;

            holders.Add(new SequenceNumberHolder(
                reader.GetInt64(1),
                (SequenceNumberHolderKind)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetGuid(4)));
        }

        return new SequenceHeldRun(firstFree ?? query.From, holders);
    }

    /// <inheritdoc />
    public async Task<GeneratedValueSequenceBlock> ReserveGeneratedValueSequenceBlockAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, long floor, int count, int increment)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);

        var column = metaverseAttributeId.HasValue ? "MetaverseAttributeId" : "ConnectedSystemObjectTypeAttributeId";
        var attributeId = metaverseAttributeId ?? connectedSystemObjectTypeAttributeId!.Value;

        // Creates the counter row, seeded at the floor, the first time this attribute is reserved against. A
        // concurrent creation that loses the race on the filtered unique index is a harmless no-op (DO NOTHING
        // leaves the winner's row alone): this INSERT never advances an existing counter, only the UPDATE below
        // does, which is what makes two concurrent reservations against the same attribute never overlap.
        var insertSql = $@"INSERT INTO ""GeneratedValueSequences""
                            (""MetaverseAttributeId"", ""ConnectedSystemObjectTypeAttributeId"", ""NextValue"", ""AssignedCount"", ""Created"")
                            VALUES ({{0}}, {{1}}, {{2}}, 0, now())
                            ON CONFLICT (""{column}"") WHERE ""{column}"" IS NOT NULL DO NOTHING";

        await _context.Database.ExecuteSqlRawAsync(
            insertSql,
            BulkSqlHelpers.NullableParam(metaverseAttributeId, NpgsqlTypes.NpgsqlDbType.Integer),
            BulkSqlHelpers.NullableParam(connectedSystemObjectTypeAttributeId, NpgsqlTypes.NpgsqlDbType.Integer),
            floor);

        // The atomic advance: the counter only ever moves forward (GREATEST against both the flow's floor and
        // the counter's own position), and RETURNING hands back the first number of the block just reserved
        // ("NextValue" after this UPDATE, minus the block just added, is exactly where the block started), the
        // counter itself, and its last administrator move as whole microseconds (exact, unlike a timestamp
        // round-tripped through a parameter), which a hand-back of the unused tail compares (#2044). Two
        // concurrent reservations against the same attribute serialise on this row's lock, so the second one's
        // GREATEST always sees the first one's advance rather than a stale value.
        //
        // Run through raw Npgsql rather than EF's Database.SqlQueryRaw: EF composes a SqlQuery as a subquery
        // (SELECT ... FROM (<sql>) AS x), and PostgreSQL refuses a data-modifying statement inside a subquery
        // (an UPDATE ... RETURNING is only allowed at the statement's top level), so this fails at runtime
        // against real PostgreSQL even though it type-checks (mirrors SyncRepository.PasswordOperations.cs'
        // StageProvisionedPasswordChangesAsync, which hit the same restriction first).
        const string advanceSql = """
            UPDATE "GeneratedValueSequences"
            SET "NextValue" = GREATEST("NextValue", @floor) + @count * @increment, "LastUpdated" = now()
            WHERE "MetaverseAttributeId" IS NOT DISTINCT FROM @metaverseAttributeId
              AND "ConnectedSystemObjectTypeAttributeId" IS NOT DISTINCT FROM @connectedSystemObjectTypeAttributeId
            RETURNING "NextValue" - @count * @increment, "NextValue", (extract(epoch FROM "LastMovedAt") * 1000000)::bigint
            """;

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        var metaverseAttributeIdParam = BulkSqlHelpers.NullableParam(metaverseAttributeId, NpgsqlTypes.NpgsqlDbType.Integer);
        metaverseAttributeIdParam.ParameterName = "metaverseAttributeId";
        var connectedSystemObjectTypeAttributeIdParam = BulkSqlHelpers.NullableParam(connectedSystemObjectTypeAttributeId, NpgsqlTypes.NpgsqlDbType.Integer);
        connectedSystemObjectTypeAttributeIdParam.ParameterName = "connectedSystemObjectTypeAttributeId";

        await using var command = new NpgsqlCommand(advanceSql, npgsqlConn, npgsqlTx);
        command.Parameters.Add(new NpgsqlParameter("floor", floor));
        command.Parameters.Add(new NpgsqlParameter("count", count));
        command.Parameters.Add(new NpgsqlParameter("increment", increment));
        command.Parameters.Add(metaverseAttributeIdParam);
        command.Parameters.Add(connectedSystemObjectTypeAttributeIdParam);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            throw new InvalidOperationException("Reserving a sequence block updated no counter row, although the insert before it guarantees one exists.");

        return new GeneratedValueSequenceBlock(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetInt64(2));
    }

    /// <inheritdoc />
    public async Task<bool> ReturnUnusedGeneratedValueSequenceNumbersAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, GeneratedValueSequenceBlock block, long firstUnused)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);

        if (firstUnused >= block.CounterAfter)
            return false;

        // One compare-and-swap: the counter moves back only while it stands exactly where the reservation left it and
        // its last administrator move is the one the reservation saw, so a later reservation by another run, or a
        // raised start or Start again in the meantime, leaves it alone. "LastMovedAt" is deliberately not stamped.
        const string handBackSql = """
            UPDATE "GeneratedValueSequences"
            SET "NextValue" = @firstUnused, "LastUpdated" = now()
            WHERE "MetaverseAttributeId" IS NOT DISTINCT FROM @metaverseAttributeId
              AND "ConnectedSystemObjectTypeAttributeId" IS NOT DISTINCT FROM @connectedSystemObjectTypeAttributeId
              AND "NextValue" = @counterAfter
              AND (extract(epoch FROM "LastMovedAt") * 1000000)::bigint IS NOT DISTINCT FROM @counterMovedStamp
            """;

        var npgsqlConn = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(npgsqlConn);
        var npgsqlTx = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction();

        var metaverseAttributeIdParam = BulkSqlHelpers.NullableParam(metaverseAttributeId, NpgsqlTypes.NpgsqlDbType.Integer);
        metaverseAttributeIdParam.ParameterName = "metaverseAttributeId";
        var connectedSystemObjectTypeAttributeIdParam = BulkSqlHelpers.NullableParam(connectedSystemObjectTypeAttributeId, NpgsqlTypes.NpgsqlDbType.Integer);
        connectedSystemObjectTypeAttributeIdParam.ParameterName = "connectedSystemObjectTypeAttributeId";
        var counterMovedStampParam = BulkSqlHelpers.NullableParam(block.CounterMovedStamp, NpgsqlTypes.NpgsqlDbType.Bigint);
        counterMovedStampParam.ParameterName = "counterMovedStamp";

        await using var command = new NpgsqlCommand(handBackSql, npgsqlConn, npgsqlTx);
        command.Parameters.Add(new NpgsqlParameter("firstUnused", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = firstUnused });
        command.Parameters.Add(new NpgsqlParameter("counterAfter", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = block.CounterAfter });
        command.Parameters.Add(metaverseAttributeIdParam);
        command.Parameters.Add(connectedSystemObjectTypeAttributeIdParam);
        command.Parameters.Add(counterMovedStampParam);

        return await command.ExecuteNonQueryAsync() == 1;
    }

    /// <inheritdoc />
    public async Task IncrementGeneratedValueSequenceAssignedCountAsync(int sequenceId, long by)
    {
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""GeneratedValueSequences"" SET ""AssignedCount"" = ""AssignedCount"" + {0}, ""LastUpdated"" = now() WHERE ""Id"" = {1}",
            by, sequenceId);
    }

    /// <inheritdoc />
    public async Task<long?> RaiseGeneratedValueSequenceIfHigherAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, long newStart, int syncRuleMappingId)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);
        return await MoveGeneratedValueSequenceAsync(metaverseAttributeId, connectedSystemObjectTypeAttributeId, newStart, syncRuleMappingId, onlyIfHigher: true);
    }

    /// <inheritdoc />
    public async Task<long?> ResetGeneratedValueSequenceAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, long newValue, int syncRuleMappingId)
    {
        ValidateExactlyOneAttributeReference(metaverseAttributeId, connectedSystemObjectTypeAttributeId);
        return await MoveGeneratedValueSequenceAsync(metaverseAttributeId, connectedSystemObjectTypeAttributeId, newValue, syncRuleMappingId, onlyIfHigher: false);
    }

    /// <summary>
    /// The shared read-then-write behind <see cref="RaiseGeneratedValueSequenceIfHigherAsync"/> and
    /// <see cref="ResetGeneratedValueSequenceAsync"/>. This is an administrator save-time action, not the
    /// worker's per-page hot path, so a plain SELECT-then-UPDATE (rather than the atomic
    /// <c>UPDATE ... RETURNING</c> <see cref="ReserveGeneratedValueSequenceBlockAsync"/> needs to serialise
    /// concurrent allocations) is an acceptable, far simpler shape; a save that races another save against the
    /// same counter is administrator error, not a synchronisation integrity concern.
    /// </summary>
    private async Task<long?> MoveGeneratedValueSequenceAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, long newValue, int syncRuleMappingId, bool onlyIfHigher)
    {
        var existing = await _context.GeneratedValueSequences
            .AsNoTracking()
            .SingleOrDefaultAsync(s =>
                s.MetaverseAttributeId == metaverseAttributeId &&
                s.ConnectedSystemObjectTypeAttributeId == connectedSystemObjectTypeAttributeId);

        if (existing == null)
            return null;

        if (onlyIfHigher && newValue <= existing.NextValue)
            return null;

        var column = metaverseAttributeId.HasValue ? "MetaverseAttributeId" : "ConnectedSystemObjectTypeAttributeId";
        var attributeId = metaverseAttributeId ?? connectedSystemObjectTypeAttributeId!.Value;
        var guard = onlyIfHigher ? @" AND ""NextValue"" < {0}" : string.Empty;

        // Built into a local first, rather than passed inline: EF1002 flags an interpolated string literal
        // handed directly to ExecuteSqlRawAsync, even though column/guard here are fixed, non-user-controlled
        // text (mirrors ReserveGeneratedValueSequenceBlockAsync's insertSql above).
        var sql = $@"UPDATE ""GeneratedValueSequences""
               SET ""NextValue"" = {{0}}, ""LastMovedAt"" = now(), ""LastMovedBySyncRuleMappingId"" = {{1}}, ""LastUpdated"" = now()
               WHERE ""{column}"" = {{2}}{guard}";

        await _context.Database.ExecuteSqlRawAsync(sql, newValue, syncRuleMappingId, attributeId);

        return existing.NextValue;
    }

    /// <inheritdoc />
    public async Task<int> CountMetaverseObjectsAwaitingGeneratedValueAsync(int metaverseObjectTypeId, int connectedSystemId, int metaverseAttributeId)
    {
        const string sql = @"SELECT COUNT(DISTINCT mvo.""Id"") AS ""Value""
                    FROM ""MetaverseObjects"" mvo
                    JOIN ""ConnectedSystemObjects"" cso ON cso.""MetaverseObjectId"" = mvo.""Id"" AND cso.""ConnectedSystemId"" = {0}
                    WHERE mvo.""TypeId"" = {1}
                      AND NOT EXISTS (
                          SELECT 1 FROM ""MetaverseObjectAttributeValues"" v
                          WHERE v.""MetaverseObjectId"" = mvo.""Id"" AND v.""AttributeId"" = {2}
                      )";

        return await _context.Database.SqlQueryRaw<int>(sql, connectedSystemId, metaverseObjectTypeId, metaverseAttributeId).SingleAsync();
    }

    /// <inheritdoc />
    public async Task<List<GeneratedValueAssignmentHeader>> GetGeneratedValueAssignmentHeadersForMetaverseObjectAsync(Guid metaverseObjectId)
    {
        return await _context.GeneratedValueAssignments
            .AsNoTracking()
            .Where(a => a.MetaverseObjectId == metaverseObjectId)
            .Select(a => new GeneratedValueAssignmentHeader
            {
                AssignmentId = a.Id,
                MetaverseAttributeId = a.MetaverseAttributeId!.Value,
                AttributeName = a.MetaverseAttribute!.Name,
                Value = a.Value,
                TokenKind = a.SyncRuleMappingGeneration!.TokenKind,
                SyncRuleId = a.SyncRuleMappingGeneration.SyncRuleMapping!.SyncRuleId,
                SyncRuleName = a.SyncRuleMappingGeneration.SyncRuleMapping.SyncRule!.Name,
                SyncRuleMappingId = a.SyncRuleMappingGeneration.SyncRuleMappingId,
                State = a.State,
                AssignedDate = a.CommittedAt ?? a.Created
            })
            .ToListAsync();
    }

    /// <summary>
    /// Guards the shape every generated-value repository member keyed on "one attribute, Metaverse or Connected
    /// System" shares: exactly one of the pair must be given.
    /// </summary>
    private static void ValidateExactlyOneAttributeReference(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId)
    {
        if (metaverseAttributeId.HasValue == connectedSystemObjectTypeAttributeId.HasValue)
            throw new ArgumentException("Exactly one of metaverseAttributeId and connectedSystemObjectTypeAttributeId must be given.");
    }

    #endregion
}
