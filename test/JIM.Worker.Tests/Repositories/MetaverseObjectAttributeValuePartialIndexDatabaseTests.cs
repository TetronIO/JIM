// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Security.Cryptography;
using System.Text;
using JIM.Models.Core;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the single-column value indexes on <c>MetaverseObjectAttributeValues</c> (#2864): six
/// are partial (<c>value IS NOT NULL</c>), and the text and date ones are gone because an <c>(AttributeId, value)</c>
/// composite serves every reader of theirs. Each test reads the plan of a real reader against a table shaped as
/// production's is: one typed value per row, every other value column NULL. A partial index serves only a predicate the
/// planner can prove implies its own, so a reader whose predicate it could not prove, or whose index was dropped
/// without a composite to take over, would fall back to another index read with a filter, or to a scan.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectAttributeValuePartialIndexDatabaseTests
{
    private const int ObjectCount = 5000;

    private static readonly string[] PartialValueColumns =
    [
        "IntValue", "LongValue", "DecimalValue", "GuidValue", "ReferenceValueId", "UnresolvedReferenceValueId"
    ];

    // Redundant: (AttributeId, StringValue) and (AttributeId, DateTimeValue) serve every reader of these columns.
    private static readonly string[] DroppedValueColumns = ["StringValue", "DateTimeValue"];

    private string _connectionString = null!;

    private JimDbContext NewContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .AddInterceptors(interceptors)
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL partial value index tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task ValueIndexes_SixHoldOnlyTheRowsWithAValueAndTheTextAndDateOnesAreGoneAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT indexname, indexdef FROM pg_indexes
            WHERE tablename = 'MetaverseObjectAttributeValues' AND indexname = ANY(@names)
            """, connection);
        command.Parameters.AddWithValue("names", PartialValueColumns.Concat(DroppedValueColumns).Select(IndexName).ToArray());

        var definitions = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                definitions[reader.GetString(0)] = reader.GetString(1);
        }

        using (Assert.EnterMultipleScope())
        {
            foreach (var column in PartialValueColumns)
            {
                Assert.That(definitions.ContainsKey(IndexName(column)), Is.True, $"{IndexName(column)} is missing");
                Assert.That(definitions.GetValueOrDefault(IndexName(column)), Does.EndWith($"(\"{column}\") WHERE (\"{column}\" IS NOT NULL)"),
                    $"{IndexName(column)} must hold only the rows with a {column}");
            }

            foreach (var column in DroppedValueColumns)
                Assert.That(definitions.ContainsKey(IndexName(column)), Is.False, $"{IndexName(column)} is redundant and must be gone");
        }
    }

    [TestCase(AttributeDataType.Number, "IntValue")]
    [TestCase(AttributeDataType.LongNumber, "LongValue")]
    [TestCase(AttributeDataType.Decimal, "DecimalValue")]
    [TestCase(AttributeDataType.Guid, "GuidValue")]
    public async Task ObjectMatching_NonTextValue_SeeksItsValueIndexAsync(AttributeDataType valueType, string column)
    {
        // Object Matching has no (AttributeId, value) composite for these types on the Metaverse side, so the
        // single-column index is the only index keyed on the value sought; this lookup runs once per object projected.
        var estate = await SeedEstateAsync();
        object sought = valueType switch
        {
            AttributeDataType.Number => 7,
            AttributeDataType.LongNumber => 9_000_000_007L,
            AttributeDataType.Decimal => 7.5m,
            _ => SeededGuid(7)
        };

        await using var connection = await OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + MetaverseRepository.BuildMatchValueLookupSql(valueType, caseSensitive: true), connection);
        MetaverseRepository.AddMatchValueLookupParameters(explain, estate.Person.Id, estate.AttributeFor(valueType).Id, valueType, [sought], take: 2);

        AssertSeeksThrough(await PlanAsync(explain), column);
    }

    [Test]
    public async Task GeneratedNumberGate_SeeksTheIntAndLongValueIndexesAsync()
    {
        var estate = await SeedEstateAsync();
        var recorder = new CommandRecordingInterceptor();
        await using (var ctx = NewContext(recorder))
            await new SyncRepository(new PostgresDataRepository(ctx)).GetMetaverseAttributeNumbersInUseAsync(estate.Number.Id, [7L, 8L], null);

        var plan = await ExplainRecordedAsync(recorder.Commands.Single());
        using (Assert.EnterMultipleScope())
        {
            AssertSeeksThrough(plan, "IntValue");
            AssertSeeksThrough(plan, "LongValue");
        }
    }

    [Test]
    public async Task SignInLookupByGuid_SeeksTheGuidValueIndexAsync()
    {
        // A Guid sign-in identifier is looked up by AuthServer at every sign-in.
        var estate = await SeedEstateAsync();
        var recorder = new CommandRecordingInterceptor();
        await using (var ctx = NewContext(recorder))
        {
            var found = await new PostgresDataRepository(ctx).Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(estate.Person, estate.Guid, SeededGuid(7));
            Assert.That(found, Is.Not.Null, "The seeded object must be found");
        }

        AssertSeeksThrough(await ExplainRecordedAsync(recorder.Commands.First()), "GuidValue");
    }

    [Test]
    public async Task SignInLookupByText_SeeksTheAttributeIdStringValueCompositeAsync()
    {
        // A text sign-in identifier is looked up by AuthServer at every sign-in; with the single-column StringValue index
        // gone, the (AttributeId, StringValue) composite is what keeps it a seek.
        var estate = await SeedEstateAsync();
        var recorder = new CommandRecordingInterceptor();
        await using (var ctx = NewContext(recorder))
        {
            var found = await new PostgresDataRepository(ctx).Metaverse.GetMetaverseObjectByTypeAndAttributeAsync(estate.Person, estate.Text, "E7");
            Assert.That(found, Is.Not.Null, "The seeded object must be found");
        }

        AssertSeeksThrough(await ExplainRecordedAsync(recorder.Commands.First()), "StringValue", "IX_MetaverseObjectAttributeValues_AttributeId_StringValue");
    }

    [Test]
    public async Task AttributeValueObjectCount_SeeksTheAttributeIdStringValueCompositeByTheAttributeAloneAsync()
    {
        // Deleting, unassigning or retyping a Metaverse Attribute first counts the objects holding a value of it, of any
        // type (MetaverseServer's impact checks), and the foreign-key cascade on deleting the attribute finds its values
        // the same way. The (AttributeId, StringValue) composite is the only index led by AttributeId that holds every
        // row, so it stays whole rather than partial on StringValue IS NOT NULL as its Connected System Object twin is
        // (#2868): a partial one cannot serve WHERE AttributeId = @id alone, and each count would read the whole table.
        // Counted here for a number attribute, whose rows carry no text at all.
        var estate = await SeedEstateAsync();
        await using (var connection = await OpenAsync())
        await using (var definition = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'IX_MetaverseObjectAttributeValues_AttributeId_StringValue'", connection))
        {
            Assert.That(await definition.ExecuteScalarAsync() as string, Does.Not.Contain(" WHERE "),
                "IX_MetaverseObjectAttributeValues_AttributeId_StringValue must hold every value row");
        }

        var recorder = new CommandRecordingInterceptor();
        await using (var ctx = NewContext(recorder))
        {
            var count = await new PostgresDataRepository(ctx).Metaverse.GetAttributeValueObjectCountAsync(estate.Number.Id);
            Assert.That(count, Is.EqualTo(ObjectCount), "Every seeded object holds a staff number");
        }

        // The estate spreads its rows over seven attributes, so a seventh of so small a table is cheaper to scan; a
        // production Metaverse spreads them over dozens. With sequential scans priced out, the plan shows whether the
        // index can serve the count at all, which is the property a partial index would lose.
        AssertSeeksThrough(await ExplainRecordedAsync(recorder.Commands.Single(), sequentialScansPricedOut: true),
            "AttributeId", "IX_MetaverseObjectAttributeValues_AttributeId_StringValue");
    }

    [Test]
    public async Task TemporalScopeReconcilerDateRange_SeeksTheAttributeIdDateTimeValueCompositeAsync()
    {
        // The outbound lane of the Temporal Scope Reconciler selects the objects whose date crossed a boundary; with the
        // single-column DateTimeValue index gone, the (AttributeId, DateTimeValue) composite is what keeps it a seek.
        var estate = await SeedEstateAsync();
        var recorder = new CommandRecordingInterceptor();
        var now = DateTime.UtcNow;
        await using (var ctx = NewContext(recorder))
        {
            var ids = await new PostgresDataRepository(ctx).Metaverse.GetMetaverseObjectIdsByDateAttributeRangeAsync(
                estate.Person.Id, estate.DateTime.Id, now.AddDays(-10.5), now.AddDays(-5.5));
            Assert.That(ids, Has.Count.EqualTo(5), "The seeded dates five to ten days ago must be found");
        }

        AssertSeeksThrough(await ExplainRecordedAsync(recorder.Commands.Single()), "DateTimeValue", "IX_MetaverseObjectAttributeValues_AttributeId_DateTimeValue");
    }

    [TestCase("ReferenceValueId")]
    [TestCase("UnresolvedReferenceValueId")]
    public async Task ForeignKeyCheckOnDeletingTheReferencedObject_SeeksTheReferenceIndexAsync(string column)
    {
        // Deleting a Metaverse Object (ReferenceValueId) or a Connected System Object (UnresolvedReferenceValueId) runs
        // PostgreSQL's referential integrity check against this table: the statement below, as ri_triggers.c builds it,
        // planned generically because it is prepared once and run for every row deleted. $1 = col implies col IS NOT
        // NULL, which is what lets the partial index serve it; without an index the check scans the table per row.
        await SeedEstateAsync();
        await using var connection = await OpenAsync();
        await using (var prepare = new NpgsqlCommand($"""
            SET plan_cache_mode = force_generic_plan;
            PREPARE ri_check(uuid) AS
                SELECT 1 FROM ONLY "public"."MetaverseObjectAttributeValues" x
                WHERE $1 OPERATOR(pg_catalog.=) "{column}" FOR KEY SHARE OF x;
            """, connection))
        {
            await prepare.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand($"EXPLAIN EXECUTE ri_check('{SeededMvoId(7)}')", connection);
        AssertSeeksThrough(await PlanAsync(explain), column);
    }

    [Test]
    public async Task MetaverseObjectDeletion_ReferenceCleanUp_SeeksTheReferenceIndexAsync()
    {
        await SeedEstateAsync();
        var recorder = new NonQueryRecordingInterceptor();
        await using (var ctx = NewContext(recorder))
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            await MetaverseReferenceRowCleanup.CleanUpReferencesToDeletedMvosAsync(ctx, [SeededMvoId(7)]);
            await transaction.RollbackAsync();
        }

        Assert.That(recorder.Commands, Has.Count.EqualTo(2), "The clean-up runs a DELETE of valueless rows and an UPDATE of the rest");
        using (Assert.EnterMultipleScope())
        {
            foreach (var command in recorder.Commands)
                AssertSeeksThrough(await ExplainRecordedAsync(command), "ReferenceValueId");
        }
    }

    [Test]
    public async Task ConnectedSystemObjectDeletion_ClearingUnresolvedReferences_SeeksTheUnresolvedReferenceIndexAsync()
    {
        await SeedEstateAsync();
        var recorder = new NonQueryRecordingInterceptor();
        await using (var ctx = NewContext(recorder))
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            await ConnectedSystemObjectDeletionSql.DeleteAsync(ctx, [Guid.NewGuid()]);
            await transaction.RollbackAsync();
        }

        var clear = recorder.Commands.Single(c => c.Text.Contains("\"MetaverseObjectAttributeValues\" SET \"UnresolvedReferenceValueId\" = NULL"));
        AssertSeeksThrough(await ExplainRecordedAsync(clear), "UnresolvedReferenceValueId");
    }

    private static string IndexName(string column) => $"IX_MetaverseObjectAttributeValues_{column}";

    // The ids and Guid values SeedEstateAsync writes, which it derives in SQL as md5(prefix || n)::uuid.
    private static Guid SeededMvoId(int n) => Md5Guid($"mvo{n}");

    private static Guid SeededGuid(int n) => Md5Guid($"guid{n}");

    private static Guid Md5Guid(string text) => Guid.Parse(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))));

    private static void AssertSeeksThrough(IReadOnlyList<string> plan, string column, string? index = null)
    {
        // The index must be in the plan, and the value must drive its condition rather than filter what it returns.
        var planText = string.Join("\n", plan);
        index ??= IndexName(column);
        var nodeAt = plan.ToList().FindIndex(line => line.Contains($" on \"{index}\"") || line.Contains($" using \"{index}\"") || line.Contains($" on {index}") || line.Contains($" using {index}"));
        Assert.That(nodeAt, Is.GreaterThanOrEqualTo(0), $"{index} is not used.\n{planText}");
        if (nodeAt < 0)
            return;

        var condition = plan.Skip(nodeAt + 1).FirstOrDefault(line => line.Contains("Index Cond:"));
        Assert.That(condition, Does.Contain($"\"{column}\""), $"{index} is not sought by {column}.\n{planText}");
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<IReadOnlyList<string>> PlanAsync(NpgsqlCommand explain)
    {
        var plan = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            plan.Add(reader.GetString(0));
        return plan;
    }

    private async Task<IReadOnlyList<string>> ExplainRecordedAsync((string Text, List<NpgsqlParameter> Parameters) command,
        bool sequentialScansPricedOut = false)
    {
        await using var connection = await OpenAsync();
        if (sequentialScansPricedOut)
        {
            await using var setting = new NpgsqlCommand("SET enable_seqscan = off", connection);
            await setting.ExecuteNonQueryAsync();
        }


        await using var explain = new NpgsqlCommand("EXPLAIN " + command.Text, connection);
        foreach (var parameter in command.Parameters)
            explain.Parameters.Add(parameter.Clone());
        return await PlanAsync(explain);
    }

    private sealed record Estate(
        MetaverseObjectType Person,
        MetaverseAttribute Text,
        MetaverseAttribute Number,
        MetaverseAttribute LongNumber,
        MetaverseAttribute Decimal,
        MetaverseAttribute Guid,
        MetaverseAttribute DateTime,
        MetaverseAttribute Reference)
    {
        public MetaverseAttribute AttributeFor(AttributeDataType type) => type switch
        {
            AttributeDataType.Number => Number,
            AttributeDataType.LongNumber => LongNumber,
            AttributeDataType.Decimal => Decimal,
            AttributeDataType.Guid => Guid,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    /// <summary>
    /// <see cref="ObjectCount"/> people, each holding one value of each type in a row of its own, as synchronisation
    /// writes them: every row has exactly one value column set. No row stages an unresolved reference, which is also
    /// how a settled Metaverse looks. Analysed, so the plans are read against statistics for a populated table.
    /// </summary>
    private async Task<Estate> SeedEstateAsync()
    {
        Estate estate;
        await using (var seed = NewContext())
        {
            estate = new Estate(
                new MetaverseObjectType { Name = "Person", PluralName = "People", BuiltIn = false },
                new MetaverseAttribute { Name = "Employee ID", Type = AttributeDataType.Text },
                new MetaverseAttribute { Name = "Staff Number", Type = AttributeDataType.Number },
                new MetaverseAttribute { Name = "Badge Number", Type = AttributeDataType.LongNumber },
                new MetaverseAttribute { Name = "Rate", Type = AttributeDataType.Decimal },
                new MetaverseAttribute { Name = "Object Key", Type = AttributeDataType.Guid },
                new MetaverseAttribute { Name = "Start Date", Type = AttributeDataType.DateTime },
                new MetaverseAttribute { Name = "Manager", Type = AttributeDataType.Reference });
            seed.AddRange(estate.Person, estate.Text, estate.Number, estate.LongNumber, estate.Decimal, estate.Guid, estate.DateTime, estate.Reference);
            await seed.SaveChangesAsync();
        }

        await using var connection = await OpenAsync();
        await using var populate = new NpgsqlCommand("""
            INSERT INTO "MetaverseObjects" ("Id", "Created", "TypeId", "Status", "DeletionInitiatedByType", "Origin")
            SELECT md5('mvo' || n)::uuid, now(), @typeId, 0, 0, 0 FROM generate_series(1, @count) AS n;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "AttributeId", "MetaverseObjectId", "StringValue")
            SELECT md5('s' || n)::uuid, @text, md5('mvo' || n)::uuid, 'E' || n FROM generate_series(1, @count) AS n;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "AttributeId", "MetaverseObjectId", "IntValue")
            SELECT md5('i' || n)::uuid, @number, md5('mvo' || n)::uuid, n FROM generate_series(1, @count) AS n;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "AttributeId", "MetaverseObjectId", "LongValue")
            SELECT md5('l' || n)::uuid, @long, md5('mvo' || n)::uuid, 9000000000 + n FROM generate_series(1, @count) AS n;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "AttributeId", "MetaverseObjectId", "DecimalValue")
            SELECT md5('d' || n)::uuid, @decimal, md5('mvo' || n)::uuid, n + 0.5 FROM generate_series(1, @count) AS n;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "AttributeId", "MetaverseObjectId", "GuidValue")
            SELECT md5('g' || n)::uuid, @guid, md5('mvo' || n)::uuid, md5('guid' || n)::uuid FROM generate_series(1, @count) AS n;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "AttributeId", "MetaverseObjectId", "DateTimeValue")
            SELECT md5('t' || n)::uuid, @dateTime, md5('mvo' || n)::uuid, now() - n * interval '1 day' FROM generate_series(1, @count) AS n;
            INSERT INTO "MetaverseObjectAttributeValues" ("Id", "AttributeId", "MetaverseObjectId", "ReferenceValueId")
            SELECT md5('r' || n)::uuid, @reference, md5('mvo' || n)::uuid, md5('mvo' || (n % @count + 1))::uuid FROM generate_series(1, @count) AS n;
            ANALYZE "MetaverseObjects";
            ANALYZE "MetaverseObjectAttributeValues";
            """, connection);
        populate.Parameters.AddWithValue("count", ObjectCount);
        populate.Parameters.AddWithValue("typeId", estate.Person.Id);
        populate.Parameters.AddWithValue("text", estate.Text.Id);
        populate.Parameters.AddWithValue("number", estate.Number.Id);
        populate.Parameters.AddWithValue("long", estate.LongNumber.Id);
        populate.Parameters.AddWithValue("decimal", estate.Decimal.Id);
        populate.Parameters.AddWithValue("guid", estate.Guid.Id);
        populate.Parameters.AddWithValue("dateTime", estate.DateTime.Id);
        populate.Parameters.AddWithValue("reference", estate.Reference.Id);
        await populate.ExecuteNonQueryAsync();
        return estate;
    }
}
