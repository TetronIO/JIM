// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;
using System.Reflection;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// #2817: where a Delta Import's time goes for a group of 200,000 members that lost a few of them. Explicit: it seeds
/// 200,000 objects and reports timings rather than asserting on them, so it runs only when asked for.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
[Explicit("Measures; seeds 200,000 objects")]
public class LargeGroupDeltaImportTimingDatabaseTests
{
    private const int Members = 200_000;

    private static readonly MethodInfo UpdateMethod = typeof(JIM.Worker.Processors.SyncImportTaskProcessor).GetMethod(
        "UpdateConnectedSystemObjectFromImportObject", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("UpdateConnectedSystemObjectFromImportObject not found via reflection.");

    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL timing.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass};Command Timeout=600";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// #2836: the confirming Delta Import after an export whose optimistic apply already wrote the change: a group of
    /// 200,000 members held exactly as it is imported. Asking finds nothing undecided, so every millisecond it costs is
    /// spent learning that nothing changed.
    /// </summary>
    [Test]
    public async Task DeltaImport_GroupOf200kMembersAlreadyCurrent_ReportsWhereTheTimeGoesAsync()
    {
        var seedWatch = Stopwatch.StartNew();
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, Members, Members);
        TestContext.Out.WriteLine($"Seeded {Members:N0} users and a group of {Members:N0} members in {seedWatch.Elapsed.TotalSeconds:F1} s");

        // Warm the cache, as a run's earlier pages would.
        await using (var warm = NewContext())
            _ = await new PostgresDataRepository(warm).Sync.GetReferenceExternalIdsForCsosAsync([seeded.GroupCsoId]);

        for (var run = 1; run <= 3; run++)
        {
            await using var ctx = NewContext();
            var repository = new PostgresDataRepository(ctx).Sync;
            var groupType = (await repository.GetConnectedSystemObjectsByIdsAsync(seeded.SystemId, [seeded.GroupCsoId], [seeded.MemberAttributeId], [])).Single().Type;
            var importObject = new ConnectedSystemImportObject
            {
                ObjectType = "group",
                ChangeType = JIM.Models.Enums.ObjectChangeType.Updated,
                Attributes =
                [
                    new ConnectedSystemImportObjectAttribute { Name = "objectGUID", GuidValues = [LargeGroupImportSeed.GroupGuid] },
                    new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", StringValues = [LargeGroupImportSeed.GroupDn] },
                    new ConnectedSystemImportObjectAttribute
                    {
                        Name = "member",
                        ReferenceValues = Enumerable.Range(0, Members).Select(LargeGroupImportSeed.UserDn).ToList()
                    }
                ]
            };

            var watch = Stopwatch.StartNew();
            var questions = JIM.Worker.Processors.SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(importObject, seeded.GroupCsoId, groupType);
            var question = watch.Elapsed;

            // As the import asked before #2836: every member compared in the database.
            watch.Restart();
            var asked = (await repository.GetHeldReferenceValueAnswersAsync(questions)).Single();
            var ask = watch.Elapsed;

            // As it asks now: is the group held exactly as imported?
            watch.Restart();
            var heldAsImported = await repository.GetReferenceAttributesHeldAsImportedAsync(questions);
            var find = watch.Elapsed;

            watch.Restart();
            var answer = heldAsImported.Count == 1 ? new HeldReferenceValueAnswer(questions.Single()) : asked;
            var answersByCsoId = new Dictionary<Guid, Dictionary<int, HeldReferenceValueAnswer>>
                { [seeded.GroupCsoId] = new() { [seeded.MemberAttributeId] = answer } };
            var partial = (await JIM.Worker.Processors.SyncImportTaskProcessor.LoadConnectedSystemObjectsForComparisonAsync(repository, seeded.SystemId,
                [seeded.GroupCsoId], answersByCsoId)).Single();
            var hydrate = watch.Elapsed;

            watch.Restart();
            var externalIds = await JIM.Worker.Processors.SyncImportTaskProcessor.GetReferenceExternalIdsForComparisonAsync(repository, [partial]);
            var lookup = watch.Elapsed;

            watch.Restart();
            UpdateMethod.Invoke(null, [importObject, partial, partial.Type, new ActivityRunProfileExecutionItem(), externalIds[seeded.GroupCsoId]]);
            var diff = watch.Elapsed;

            TestContext.Out.WriteLine($"Run {run}: questions {question.TotalMilliseconds:N0} ms; " +
                $"asking {ask.TotalMilliseconds:N0} ms (undecided held {asked.UnmatchedValueIds.Count}, related {asked.RelatedValues.Count}, " +
                $"undecided imported {asked.UnmatchedIncomingValues.Count}); held as imported {find.TotalMilliseconds:N0} ms ({heldAsImported.Count == 1}); " +
                $"hydrate {hydrate.TotalMilliseconds:N0} ms ({partial.AttributeValues.Count} values); " +
                $"reference External IDs {lookup.TotalMilliseconds:N0} ms; diff {diff.TotalMilliseconds:N0} ms " +
                $"(removals {partial.PendingAttributeValueRemovals.Count}, additions {partial.PendingAttributeValueAdditions.Count})");

            // What finding it costs where it is not held as imported: five members lost (the count differs), and five
            // replaced (the count is the same, and the members are read and compared).
            foreach (var (name, members) in new[]
                     {
                         ("five lost", Enumerable.Range(5, Members - 5).Select(LargeGroupImportSeed.UserDn).ToList()),
                         ("five replaced", Enumerable.Range(5, Members - 5).Select(LargeGroupImportSeed.UserDn)
                             .Concat(Enumerable.Range(0, 5).Select(n => $"uid=other{n},ou=People,dc=glitterband,dc=local")).ToList())
                     })
            {
                var changed = new HeldReferenceValueQuestion(seeded.GroupCsoId, seeded.MemberAttributeId, members);
                watch.Restart();
                var found = await repository.GetReferenceAttributesHeldAsImportedAsync([changed]);
                TestContext.Out.WriteLine($"Run {run}, {name}: held as imported {watch.Elapsed.TotalMilliseconds:N0} ms ({found.Count == 1})");
            }

            await using var explainConnection = new Npgsql.NpgsqlConnection(_connectionString);
            await explainConnection.OpenAsync();
            await using (var explain = new Npgsql.NpgsqlCommand { Connection = explainConnection })
            {
                JIM.PostgresData.Repositories.SyncRepository.PrepareReferenceAttributesHeldAsImportedCommand(explain, questions);
                explain.CommandText = "EXPLAIN (ANALYZE, SUMMARY) " + explain.CommandText;
                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var line = reader.GetString(0);
                    if (line.Contains("Execution Time"))
                        TestContext.Out.WriteLine($"Run {run}, held as imported in the database: {line.Trim()}");
                }
            }

            await using (var explain = new Npgsql.NpgsqlCommand { Connection = explainConnection })
            {
                JIM.PostgresData.Repositories.SyncRepository.PrepareHeldReferenceValueAnswersCommand(explain, questions);
                explain.CommandText = "EXPLAIN (ANALYZE, SUMMARY) " + explain.CommandText;
                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var line = reader.GetString(0);
                    if (line.Contains("Execution Time"))
                        TestContext.Out.WriteLine($"Run {run}, asking in the database: {line.Trim()}");
                }
            }

            await using (var explain = new Npgsql.NpgsqlCommand { Connection = explainConnection })
            {
                explain.CommandText = "EXPLAIN (ANALYZE, SUMMARY) " + JIM.PostgresData.Repositories.ConnectedSystemRepository.ReferenceExternalIdsForCsosSql;
                explain.Parameters.AddRange(JIM.PostgresData.Repositories.ConnectedSystemRepository.ReferenceExternalIdsForCsosParameters(
                    [seeded.GroupCsoId], [seeded.MemberAttributeId]));
                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var line = reader.GetString(0);
                    if (line.Contains("Execution Time") || line.Contains("Rows Removed"))
                        TestContext.Out.WriteLine($"Run {run}, reference External IDs in the database: {line.Trim()}");
                }
            }
        }
    }

    [Test]
    public async Task DeltaImport_GroupOf200kMembersLosesFive_ReportsWhereTheTimeGoesAsync()
    {
        var seedWatch = Stopwatch.StartNew();
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, Members, Members);
        TestContext.Out.WriteLine($"Seeded {Members:N0} users and a group of {Members:N0} members in {seedWatch.Elapsed.TotalSeconds:F1} s");

        // Warm the cache, as a run's earlier pages would.
        await using (var warm = NewContext())
            _ = await new PostgresDataRepository(warm).Sync.GetReferenceExternalIdsForCsosAsync([seeded.GroupCsoId]);

        for (var run = 1; run <= 2; run++)
        {
            await using var ctx = NewContext();
            var repository = new PostgresDataRepository(ctx).Sync;

            var watch = Stopwatch.StartNew();
            var cso = (await repository.GetConnectedSystemObjectsByIdsAsync(seeded.SystemId, [seeded.GroupCsoId])).Single();
            var hydrate = watch.Elapsed;

            watch.Restart();
            var referenceExternalIds = await repository.GetReferenceExternalIdsForCsosAsync([seeded.GroupCsoId]);
            var lookup = watch.Elapsed;

            var importObject = new ConnectedSystemImportObject
            {
                ObjectType = "group",
                ChangeType = JIM.Models.Enums.ObjectChangeType.Updated,
                Attributes =
                [
                    new ConnectedSystemImportObjectAttribute { Name = "objectGUID", GuidValues = [LargeGroupImportSeed.GroupGuid] },
                    new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", StringValues = [LargeGroupImportSeed.GroupDn] },
                    new ConnectedSystemImportObjectAttribute
                    {
                        Name = "member",
                        ReferenceValues = Enumerable.Range(5, Members - 5).Select(LargeGroupImportSeed.UserDn).ToList()
                    }
                ]
            };

            watch.Restart();
            UpdateMethod.Invoke(null, [importObject, cso, cso.Type, new ActivityRunProfileExecutionItem(), referenceExternalIds[seeded.GroupCsoId]]);
            var diff = watch.Elapsed;

            TestContext.Out.WriteLine($"Run {run}, whole group: hydrate {hydrate.TotalMilliseconds:N0} ms ({cso.AttributeValues.Count:N0} values), " +
                $"reference External IDs {lookup.TotalMilliseconds:N0} ms ({referenceExternalIds[seeded.GroupCsoId].Count:N0}), " +
                $"diff {diff.TotalMilliseconds:N0} ms (removals {cso.PendingAttributeValueRemovals.Count}, additions {cso.PendingAttributeValueAdditions.Count})");

            // As a Delta Import now compares it (#2817): ask, load what is undecided, compare with the answer.
            await using var partialContext = NewContext();
            var partialRepository = new PostgresDataRepository(partialContext).Sync;
            var partialImport = new ConnectedSystemImportObject
            {
                ObjectType = importObject.ObjectType,
                ChangeType = importObject.ChangeType,
                Attributes = importObject.Attributes.Select(a => new ConnectedSystemImportObjectAttribute
                    { Name = a.Name, GuidValues = a.GuidValues.ToList(), StringValues = a.StringValues.ToList(), ReferenceValues = a.ReferenceValues.ToList() }).ToList()
            };

            watch.Restart();
            var questions = JIM.Worker.Processors.SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(partialImport, seeded.GroupCsoId, cso.Type);
            var answers = await partialRepository.GetHeldReferenceValueAnswersAsync(questions);
            var ask = watch.Elapsed;

            watch.Restart();
            var answersByCsoId = answers.ToDictionary(a => a.Question.ConnectedSystemObjectId, a => new Dictionary<int, HeldReferenceValueAnswer> { [a.Question.AttributeId] = a });
            var partial = (await JIM.Worker.Processors.SyncImportTaskProcessor.LoadConnectedSystemObjectsForComparisonAsync(partialRepository, seeded.SystemId,
                [seeded.GroupCsoId], answersByCsoId)).Single();
            var partialHydrate = watch.Elapsed;

            watch.Restart();
            var partialExternalIds = await JIM.Worker.Processors.SyncImportTaskProcessor.GetReferenceExternalIdsForComparisonAsync(partialRepository, [partial]);
            var partialLookup = watch.Elapsed;

            watch.Restart();
            UpdateMethod.Invoke(null, [partialImport, partial, partial.Type, new ActivityRunProfileExecutionItem(), partialExternalIds[seeded.GroupCsoId]]);
            var partialDiff = watch.Elapsed;

            // The database's own share of asking.
            await using (var explainConnection = new Npgsql.NpgsqlConnection(_connectionString))
            {
                await explainConnection.OpenAsync();
                await using var explain = new Npgsql.NpgsqlCommand { Connection = explainConnection };
                JIM.PostgresData.Repositories.SyncRepository.PrepareHeldReferenceValueAnswersCommand(explain, questions);
                explain.CommandText = "EXPLAIN (ANALYZE, SUMMARY) " + explain.CommandText;
                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var line = reader.GetString(0);
                    if (line.Contains("Execution Time") || line.Contains("Planning Time"))
                        TestContext.Out.WriteLine($"Run {run}, asking in the database: {line.Trim()}");
                }
            }

            TestContext.Out.WriteLine($"Run {run}, with the answer: ask {ask.TotalMilliseconds:N0} ms, hydrate {partialHydrate.TotalMilliseconds:N0} ms " +
                $"({partial.AttributeValues.Count:N0} values), reference External IDs {partialLookup.TotalMilliseconds:N0} ms, " +
                $"diff {partialDiff.TotalMilliseconds:N0} ms (removals {partial.PendingAttributeValueRemovals.Count}, additions {partial.PendingAttributeValueAdditions.Count})");
        }
    }
}
