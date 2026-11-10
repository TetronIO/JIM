// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the two reference indexes on <c>ConnectedSystemObjectAttributeValues</c> (#2868).
/// <c>IX_ConnectedSystemObjectAttributeValues_ReferenceValueId</c> is partial (<c>ReferenceValueId IS NOT NULL</c>):
/// only a reference value has an id, so the whole index held a NULL entry for every other value row. Each test reads the
/// plan of a reader of that column against a production-shaped table (users with a text and a Guid value, a group whose
/// member values are resolved and keep their DN) and asserts it still seeks through the index; a partial index serves
/// only a predicate the planner can prove implies its own. <c>IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValue</c>
/// stays as it is, over resolved rows too, because reconciliation's probe finds a resolved member by its DN through it.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConnectedSystemObjectAttributeValueReferenceIndexDatabaseTests
{
    private const int UserCount = 5000;
    private const string ReferenceIndex = "IX_ConnectedSystemObjectAttributeValues_ReferenceValueId";

    // The model's name, IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValue, is 64 characters, so PostgreSQL
    // stored it cut to its 63-character identifier limit.
    private const string UnresolvedReferenceIndex = "IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValu";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL reference index tests.");

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
    public async Task ReferenceIndexes_IdIndexHoldsOnlyResolvedRowsAndDnIndexKeepsThemAsync()
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT indexname, indexdef FROM pg_indexes
            WHERE tablename = 'ConnectedSystemObjectAttributeValues' AND indexname = ANY(@names)
            """, connection);
        command.Parameters.AddWithValue("names", new[] { ReferenceIndex, UnresolvedReferenceIndex });

        var definitions = new Dictionary<string, string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                definitions[reader.GetString(0)] = reader.GetString(1);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(definitions.GetValueOrDefault(ReferenceIndex), Does.EndWith("(\"ReferenceValueId\") WHERE (\"ReferenceValueId\" IS NOT NULL)"),
                $"{ReferenceIndex} must hold only the rows with a referenced object");
            Assert.That(definitions.GetValueOrDefault(UnresolvedReferenceIndex), Does.EndWith("(\"UnresolvedReferenceValue\") WHERE (\"UnresolvedReferenceValue\" IS NOT NULL)"),
                $"{UnresolvedReferenceIndex} must keep the resolved rows' DNs, which reconciliation's probe looks up by exact text");
        }
    }

    [Test]
    public async Task ForeignKeyCheckOnDeletingTheReferencedObject_SeeksTheReferenceIndexAsync()
    {
        // Deleting a Connected System Object (a leaver's deprovisioning, a Connector Space cleared, a Connected System
        // deleted) runs PostgreSQL's referential integrity check against this table for every object deleted: the
        // statement below, as ri_triggers.c builds it, planned generically because it is prepared once and run per row.
        // $1 = col implies col IS NOT NULL, which is what lets the partial index serve it; without an index the check
        // scans the table once per object deleted.
        await LargeGroupImportSeed.SeedAsync(() => NewContext(), _connectionString, UserCount, UserCount);
        await using var connection = await OpenAsync();
        await using (var prepare = new NpgsqlCommand("""
            SET plan_cache_mode = force_generic_plan;
            PREPARE ri_check(uuid) AS
                SELECT 1 FROM ONLY "public"."ConnectedSystemObjectAttributeValues" x
                WHERE $1 OPERATOR(pg_catalog.=) "ReferenceValueId" FOR KEY SHARE OF x;
            """, connection))
        {
            await prepare.ExecuteNonQueryAsync();
        }

        await using var explain = new NpgsqlCommand($"EXPLAIN EXECUTE ri_check('{LargeGroupImportSeed.UserCsoId(7)}')", connection);
        AssertSeeksThrough(await PlanAsync(explain), "ReferenceValueId", ReferenceIndex);
    }

    [Test]
    public async Task ConnectedSystemObjectDeletion_ClearingReferencesToTheDeletedObjects_SeeksTheReferenceIndexAsync()
    {
        await LargeGroupImportSeed.SeedAsync(() => NewContext(), _connectionString, UserCount, UserCount);
        var recorder = new NonQueryRecordingInterceptor();
        await using (var ctx = NewContext(recorder))
        {
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            await ConnectedSystemObjectDeletionSql.DeleteAsync(ctx, [LargeGroupImportSeed.UserCsoId(7)]);
            await transaction.RollbackAsync();
        }

        var clear = recorder.Commands.Single(c => c.Text.Contains("\"ConnectedSystemObjectAttributeValues\" SET \"ReferenceValueId\" = NULL"));
        AssertSeeksThrough(await ExplainRecordedAsync(clear), "ReferenceValueId", ReferenceIndex);
    }

    [Test]
    public async Task ReferenceRecallMatch_DeletedObjectsIds_SeekTheReferenceIndexAsync()
    {
        // Reference recall asks which members of the referencing groups name the deleted objects, by id.
        var seeded = await LargeGroupImportSeed.SeedAsync(() => NewContext(), _connectionString, UserCount, UserCount);
        await using var connection = await OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + SyncRepository.CsoReferenceValueMatchSql, connection);
        SyncRepository.AddCsoReferenceValueMatchParameters(explain, [seeded.GroupCsoId], [seeded.MemberAttributeId],
            [LargeGroupImportSeed.UserCsoId(7)], []);

        AssertSeeksThrough(await PlanAsync(explain), "ReferenceValueId", ReferenceIndex);
    }

    [Test]
    public async Task ReconciliationProbe_ReferencedId_SeeksTheReferenceIndexAsync()
    {
        // Reconciliation confirms a staged member by the id of the object it names, without loading the group.
        var seeded = await LargeGroupImportSeed.SeedAsync(() => NewContext(), _connectionString, UserCount, UserCount);
        await using var connection = await OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + SyncRepository.ReconciliationProbeValuesSql, connection);
        SyncRepository.AddReconciliationProbeValuesParameters(explain, [seeded.GroupCsoId], [seeded.MemberAttributeId],
            [LargeGroupImportSeed.UserCsoId(7)], [], []);

        AssertSeeksThrough(await PlanAsync(explain), "ReferenceValueId", ReferenceIndex);
    }

    [Test]
    public async Task SynchronisationCandidatePageReference_SeeksTheReferenceIndexAsync()
    {
        // Synchronisation asks which candidates hold a value naming an object on the page, without loading a group whole.
        var seeded = await LargeGroupImportSeed.SeedAsync(() => NewContext(), _connectionString, UserCount, UserCount);
        await using var connection = await OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + ConnectedSystemRepository.CandidatePageReferenceSql, connection);
        explain.Parameters.Add(new NpgsqlParameter("pageObjectIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = new[] { LargeGroupImportSeed.UserCsoId(7), LargeGroupImportSeed.UserCsoId(8) } });
        explain.Parameters.Add(new NpgsqlParameter("objectIds", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid)
            { Value = new[] { seeded.GroupCsoId } });

        AssertSeeksThrough(await PlanAsync(explain), "ReferenceValueId", ReferenceIndex);
    }

    [Test]
    public async Task ReconciliationProbe_ExactDnOfAResolvedMember_FindsItThroughTheUnresolvedReferenceValueIndexAsync()
    {
        // Why IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValue still covers resolved rows (#2868). On a
        // case-exact reference attribute the probe asks for a staged member's DN exactly, and a change naming no object
        // matches a resolved row by the DN it keeps (SyncEngine.ReferenceRowMatches). Narrowed to unresolved rows, the
        // index could not serve that arm, which would read every member of the group to find one.
        var seeded = await LargeGroupImportSeed.SeedAsync(() => NewContext(), _connectionString, UserCount, UserCount);
        var question = new ReconciliationProbeQuestion
        {
            ConnectedSystemObjectId = seeded.GroupCsoId,
            AttributeId = seeded.MemberAttributeId,
            ChangeIds = new HashSet<Guid>(),
            ReferenceIds = [],
            ExactValues = [LargeGroupImportSeed.UserDn(7)],
            CaseInsensitiveValues = [],
            NeedsCount = false
        };

        await using (var ctx = NewContext())
        {
            var answers = await new PostgresDataRepository(ctx).Sync.GetReconciliationProbeValuesAsync([question]);
            Assert.That(answers.Single().CandidateValues.Select(v => v.Id), Is.EqualTo(new[] { LargeGroupImportSeed.MemberValueId(7) }),
                "The resolved member is found by the DN it keeps");
        }

        await using var connection = await OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + SyncRepository.ReconciliationProbeValuesSql, connection);
        SyncRepository.AddReconciliationProbeValuesParameters(explain, [seeded.GroupCsoId], [seeded.MemberAttributeId],
            [], [LargeGroupImportSeed.UserDn(7)], []);

        AssertSeeksThrough(await PlanAsync(explain), "UnresolvedReferenceValue", UnresolvedReferenceIndex);
    }

    private static void AssertSeeksThrough(IReadOnlyList<string> plan, string column, string index)
    {
        // The index must be in the plan, and the value must drive its condition rather than filter what it returns.
        var planText = string.Join("\n", plan);
        var nodeAt = plan.ToList().FindIndex(line => line.Contains($" on \"{index}\"") || line.Contains($" using \"{index}\""));
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

    private async Task<IReadOnlyList<string>> ExplainRecordedAsync((string Text, List<NpgsqlParameter> Parameters) command)
    {
        await using var connection = await OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + command.Text, connection);
        foreach (var parameter in command.Parameters)
            explain.Parameters.Add(parameter.Clone());
        return await PlanAsync(explain);
    }
}
