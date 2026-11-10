// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;
using JIM.Models.Core;
using JIM.Models.Staging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace JIM.PostgresData.Repositories;

public partial class SyncRepository
{
    /// <inheritdoc />
    /// <remarks>
    /// One statement per question (<see cref="DriftHeldReferenceAnswerSql"/>), sent as one batch: one round trip for a page.
    /// The batch runs in one implicit transaction, so every answer reads one snapshot, and with JIT compilation off for
    /// that transaction (<see cref="TransactionJit"/>): it costs more than the statement it would speed up (measured at
    /// 200,000 members, #2818).
    /// </remarks>
    public async Task<List<DriftHeldReferenceAnswer>> GetDriftHeldReferenceAnswersAsync(IReadOnlyList<DriftHeldReferenceQuestion> questions)
    {
        var answers = questions.Select(question => new DriftHeldReferenceAnswer(question)).ToList();
        if (questions.Count == 0)
            return answers;

        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        await using var connectionLease = await RawSqlConnectionLease.AcquireAsync(connection);
        await using var batch = new NpgsqlBatch(connection)
        {
            Transaction = (NpgsqlTransaction?)_context.Database.CurrentTransaction?.GetDbTransaction()
        };
        batch.BatchCommands.Add(new NpgsqlBatchCommand(TransactionJit.DisableSql));
        foreach (var question in questions)
        {
            var command = new NpgsqlBatchCommand(DriftHeldReferenceAnswerSql);
            command.Parameters.AddRange(DriftHeldReferenceAnswerParameters(question));
            batch.BatchCommands.Add(command);
        }

        // JIT being turned off first, then one result per question, in order.
        await using var reader = await batch.ExecuteReaderAsync();
        var answered = 0;
        if (await TransactionJit.SkipDisableResultAsync(reader))
        {
            do
            {
                if (answered == answers.Count)
                    throw new InvalidOperationException("The database returned more results than Drift Detection asked questions.");

                var answer = answers[answered++];
                while (await reader.ReadAsync())
                    ReadDriftHeldReferenceAnswerRow(reader, answer);
            }
            while (await reader.NextResultAsync());
        }

        if (answered != answers.Count)
            throw new InvalidOperationException($"The database answered {answered} of Drift Detection's {answers.Count} question(s).");

        return answers;
    }

    /// <summary>A Metaverse Object the held values resolve to that is not expected.</summary>
    private const int DriftAnswerHeldNotExpected = 0;

    /// <summary>An expected Metaverse Object no held value resolves to.</summary>
    private const int DriftAnswerExpectedNotHeld = 1;

    /// <summary>The held values' summary: how many, whether any holds a reference, and the type of the first that holds
    /// something else.</summary>
    private const int DriftAnswerSummary = 2;

    /// <summary>
    /// The statement behind <see cref="GetDriftHeldReferenceAnswersAsync"/>, for one question: one row per Metaverse Object
    /// the two sides disagree on, and one summary row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A held value resolves to the Metaverse Object its referenced Connected System Object is joined to, read as the
    /// synchronisation page load reads it (<c>PopulateReferenceValuesAsync</c>); one that resolves to none is not a held
    /// Metaverse Object, as Drift Detection reads the attribute loaded whole. The values resolving to an expected Metaverse
    /// Object are the agreement, and never leave the database.
    /// </para>
    /// <para>
    /// Set-based whatever the planner estimates, as <c>HeldReferenceValueAnswersSql</c> is (#2817): a held value is tested
    /// against <c>@expected</c> with <c>= ANY</c>, which PostgreSQL answers from a hash of the array, because JIM sends the
    /// statement unprepared and so it is planned with the array as a constant (<c>DriftHeldReferenceAnswerDatabaseTests</c>
    /// checks the plan shows the array, not a parameter). The test sits in a <c>CASE</c>, so the planner does not cost the
    /// array element by element. The expected values no held value resolves to are an <c>EXCEPT</c>, hashed or sorted,
    /// never looped. Each held value's Metaverse Object is first, by value id, of those resolving to it, so the removals
    /// keep the order the whole load would give them.
    /// </para>
    /// <para>
    /// Whether the attribute is held only under another data type (#2319) is answered from the same rows: the first value,
    /// by value id, holding something other than a reference, with its type as <c>SyncEngine.HeldOnlyUnderAnotherDataType</c>
    /// reads it (the first of the stored types it holds).
    /// </para>
    /// </remarks>
    internal static readonly string DriftHeldReferenceAnswerSql = string.Format(CultureInfo.InvariantCulture, """
        WITH held AS MATERIALIZED (
            SELECT av."Id", ref."MetaverseObjectId" AS mvo,
                   (av."ReferenceValueId" IS NOT NULL OR av."UnresolvedReferenceValue" IS NOT NULL) AS holds_reference,
                   CASE WHEN av."StringValue" IS NOT NULL THEN {0}
                        WHEN av."IntValue" IS NOT NULL THEN {1}
                        WHEN av."LongValue" IS NOT NULL THEN {2}
                        WHEN av."DecimalValue" IS NOT NULL THEN {3}
                        WHEN av."DateTimeValue" IS NOT NULL THEN {4}
                        WHEN av."ByteValue" IS NOT NULL THEN {5}
                        WHEN av."GuidValue" IS NOT NULL THEN {6}
                        WHEN av."BoolValue" IS NOT NULL THEN {7}
                   END AS held_type
            FROM "ConnectedSystemObjectAttributeValues" av
            LEFT JOIN "ConnectedSystemObjects" ref ON ref."Id" = av."ReferenceValueId"
            WHERE av."ConnectedSystemObjectId" = @objectId AND av."AttributeId" = @attributeId
        )
        SELECT {8} AS kind, n.mvo, n."Id", NULL::integer AS held_count, NULL::boolean AS holds_reference, NULL::integer AS other_type
        FROM (
            SELECT DISTINCT ON (h.mvo) h.mvo, h."Id"
            FROM held h
            WHERE CASE WHEN h.mvo IS NULL THEN false ELSE NOT (h.mvo = ANY(@expected)) END
            ORDER BY h.mvo, h."Id"
        ) AS n
        UNION ALL
        SELECT {9}, e.mvo, NULL, NULL, NULL, NULL
        FROM (
            SELECT unnest(@expected) AS mvo
            EXCEPT
            SELECT h.mvo FROM held h WHERE h.mvo IS NOT NULL
        ) AS e
        UNION ALL
        SELECT {10}, NULL, NULL, count(*)::integer, coalesce(bool_or(h.holds_reference), false),
               (array_agg(h.held_type ORDER BY h."Id") FILTER (WHERE NOT h.holds_reference AND h.held_type IS NOT NULL))[1]
        FROM held h
        ORDER BY 1, 3
        """,
        (int)AttributeDataType.Text, (int)AttributeDataType.Number, (int)AttributeDataType.LongNumber, (int)AttributeDataType.Decimal,
        (int)AttributeDataType.DateTime, (int)AttributeDataType.Binary, (int)AttributeDataType.Guid, (int)AttributeDataType.Boolean,
        DriftAnswerHeldNotExpected, DriftAnswerExpectedNotHeld, DriftAnswerSummary);

    /// <summary>
    /// The parameters of <see cref="DriftHeldReferenceAnswerSql"/> for one question. Exposed so the plan can be checked
    /// (<c>DriftHeldReferenceAnswerDatabaseTests</c>).
    /// </summary>
    internal static NpgsqlParameter[] DriftHeldReferenceAnswerParameters(DriftHeldReferenceQuestion question) =>
    [
        new NpgsqlParameter("objectId", NpgsqlDbType.Uuid) { Value = question.ConnectedSystemObjectId },
        new NpgsqlParameter("attributeId", NpgsqlDbType.Integer) { Value = question.AttributeId },
        new NpgsqlParameter("expected", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = question.ExpectedValues.ToArray() }
    ];

    /// <summary>
    /// Reads one row of <see cref="DriftHeldReferenceAnswerSql"/> into its answer. Column order: kind, Metaverse Object,
    /// value id, held count, holds a reference, the other type held.
    /// </summary>
    private static void ReadDriftHeldReferenceAnswerRow(NpgsqlDataReader reader, DriftHeldReferenceAnswer answer)
    {
        switch (reader.GetInt32(0))
        {
            case DriftAnswerHeldNotExpected:
                answer.HeldNotExpected.Add(reader.GetGuid(1));
                break;
            case DriftAnswerExpectedNotHeld:
                answer.ExpectedNotHeld.Add(reader.GetGuid(1));
                break;
            case DriftAnswerSummary:
                answer.HeldValueCount = reader.GetInt32(3);
                answer.HoldsReference = reader.GetBoolean(4);
                answer.FirstOtherHeldDataType = reader.IsDBNull(5) ? null : (AttributeDataType)reader.GetInt32(5);
                break;
        }
    }
}
