// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Exceptions;
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

    /// <inheritdoc />
    public async Task<long> ReserveGeneratedValueSequenceBlockAsync(int? metaverseAttributeId, int? connectedSystemObjectTypeAttributeId, long floor, int count, int increment)
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
        // ("NextValue" after this UPDATE, minus the block just added, is exactly where the block started). Two
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
            RETURNING "NextValue" - @count * @increment
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

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <inheritdoc />
    public async Task IncrementGeneratedValueSequenceAssignedCountAsync(int sequenceId, long by)
    {
        await _context.Database.ExecuteSqlRawAsync(
            @"UPDATE ""GeneratedValueSequences"" SET ""AssignedCount"" = ""AssignedCount"" + {0}, ""LastUpdated"" = now() WHERE ""Id"" = {1}",
            by, sequenceId);
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
