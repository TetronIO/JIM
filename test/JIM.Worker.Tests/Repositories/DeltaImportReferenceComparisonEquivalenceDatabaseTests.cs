// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// #2817: a Delta Import compares a group with what the Connector imported without loading the members an exact match
/// settles. It must stage exactly what comparing the whole group stages: the same removals (by value), the same additions
/// (in the same order), and no removal and re-addition of a member it simply did not load. Each case compares the two
/// against the same rows in real PostgreSQL.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DeltaImportReferenceComparisonEquivalenceDatabaseTests
{
    private const string Ghost = "uid=ghost,ou=People,dc=glitterband,dc=local";
    private const string Pending = "uid=pending,ou=People,dc=glitterband,dc=local";

    private static readonly MethodInfo UpdateMethod = typeof(SyncImportTaskProcessor).GetMethod(
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL comparison equivalence tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// Each case: the member DNs the import carries (users 0 to 7 are held as members; users 8 to 11 are held by JIM but
    /// are not members). Null carries no member attribute at all.
    /// </summary>
    private static IEnumerable<TestCaseData> Cases()
    {
        static string Dn(int n) => LargeGroupImportSeed.UserDn(n);
        static List<string> Members(int from, int to) => Enumerable.Range(from, to - from + 1).Select(Dn).ToList();

        yield return new TestCaseData(Members(1, 7), false).SetName("MemberRemoved");
        yield return new TestCaseData(Members(0, 7).Append(Dn(9)).ToList(), false).SetName("MemberAdded");
        yield return new TestCaseData(Members(2, 7).Prepend(Dn(10)).Append(Dn(9)).ToList(), false).SetName("MembersRemovedAndAdded");
        yield return new TestCaseData(null, false).SetName("GroupEmptied");
        yield return new TestCaseData(Members(0, 7).Append(Pending).Append(Ghost).ToList(), true).SetName("ReferenceToObjectNotYetImported");
        yield return new TestCaseData(Members(0, 6).Append(Dn(7).ToUpperInvariant()).ToList(), false).SetName("CaseOnlyDnChange");
        yield return new TestCaseData(Members(0, 6).Append(Dn(7).ToUpperInvariant()).Append(Dn(8).ToUpperInvariant()).ToList(), true)
            .SetName("CaseOnlyDnChangeWithUnresolvedAndNonReferenceValues");
        yield return new TestCaseData(Members(0, 7).Select(dn => dn.Replace("user3,", "renamed3,")).ToList(), false).SetName("MemberRenamedInTheDirectory");
        yield return new TestCaseData(new List<string>(), false).SetName("NoMembersCarried");
    }

    [TestCaseSource(nameof(Cases))]
    public async Task DeltaImportComparison_PartialLoad_StagesExactlyWhatTheWholeGroupStagesAsync(List<string>? importedMembers, bool withOddValues)
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 12, memberCount: 8);
        if (withOddValues)
            await AddOddValuesAsync(seeded);
        if (importedMembers?.Any(dn => dn.Contains("renamed3,")) == true)
            await RenameUserAsync(3, "uid=renamed3,ou=People,dc=glitterband,dc=local");

        // As comparing the whole group: every value and every reference's External ID.
        ConnectedSystemObject whole;
        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx).Sync;
            whole = (await repository.GetConnectedSystemObjectsByIdsAsync(seeded.SystemId, [seeded.GroupCsoId])).Single();
            var externalIds = await repository.GetReferenceExternalIdsForCsosAsync([seeded.GroupCsoId]);
            Diff(ImportObject(importedMembers), whole, externalIds[seeded.GroupCsoId]);
        }

        // As a Delta Import now does: ask, load only what is undecided, and compare with the answer.
        ConnectedSystemObject partial;
        int membersLoaded;
        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx).Sync;
            var importObject = ImportObject(importedMembers);
            var groupType = (await repository.GetConnectedSystemObjectsByIdsAsync(seeded.SystemId, [seeded.GroupCsoId])).Single().Type;
            var questions = SyncImportTaskProcessor.HeldReferenceValueQuestionsFor(importObject, seeded.GroupCsoId, groupType);
            var answers = questions.Count == 0 ? [] : await repository.GetHeldReferenceValueAnswersAsync(questions);
            var databaseFolds = await repository.DoesDatabaseFoldCaseLikeIdentifierKeyAsync();
            Assert.That(answers.All(a => a.IsCompleteIgnoringCase(databaseFolds)), Is.True,
                "precondition: every case here is answered without loading the attribute in full");

            var answersByCsoId = answers.GroupBy(a => a.Question.ConnectedSystemObjectId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.Question.AttributeId));
            partial = (await SyncImportTaskProcessor.LoadConnectedSystemObjectsForComparisonAsync(repository, seeded.SystemId, [seeded.GroupCsoId], answersByCsoId)).Single();
            membersLoaded = partial.AttributeValues.Count(av => av.AttributeId == seeded.MemberAttributeId);
            var externalIds = await SyncImportTaskProcessor.GetReferenceExternalIdsForComparisonAsync(repository, [partial]);
            Diff(importObject, partial, externalIds[seeded.GroupCsoId]);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(partial.PendingAttributeValueRemovals.Select(v => v.Id), Is.EquivalentTo(whole.PendingAttributeValueRemovals.Select(v => v.Id)),
                "The same values removed");
            Assert.That(partial.PendingAttributeValueAdditions.Select(Describe), Is.EqualTo(whole.PendingAttributeValueAdditions.Select(Describe)),
                "The same values added, in the same order");
            if (importedMembers is { Count: > 0 })
                Assert.That(membersLoaded, Is.LessThanOrEqualTo(whole.PendingAttributeValueRemovals.Count + 3),
                    "Only the undecided member values were loaded");
        }
    }

    private static string Describe(ConnectedSystemObjectAttributeValue value) =>
        $"{value.Attribute?.Name}:{value.UnresolvedReferenceValue ?? value.StringValue ?? value.GuidValue?.ToString()}";

    private static ConnectedSystemImportObject ImportObject(List<string>? members)
    {
        var importObject = new ConnectedSystemImportObject
        {
            ObjectType = "group",
            ChangeType = ObjectChangeType.Updated,
            Attributes =
            [
                new ConnectedSystemImportObjectAttribute { Name = "objectGUID", GuidValues = [LargeGroupImportSeed.GroupGuid] },
                new ConnectedSystemImportObjectAttribute { Name = "distinguishedName", StringValues = [LargeGroupImportSeed.GroupDn] }
            ]
        };
        if (members != null)
            importObject.Attributes.Add(new ConnectedSystemImportObjectAttribute { Name = "member", ReferenceValues = members.ToList() });
        return importObject;
    }

    private static void Diff(ConnectedSystemImportObject importObject, ConnectedSystemObject cso, Dictionary<Guid, string> externalIds) =>
        UpdateMethod.Invoke(null, [importObject, cso, cso.Type, new ActivityRunProfileExecutionItem(), externalIds]);

    /// <summary>
    /// A member still unresolved (its object not imported yet), one whose object is gone, a value held under another data
    /// type and a value holding nothing: every kind of held value the comparison treats differently.
    /// </summary>
    private async Task AddOddValuesAsync(LargeGroupImportSeed seeded)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "AttributeId", "ConnectedSystemObjectId", "UnresolvedReferenceValue", "StringValue")
            VALUES (gen_random_uuid(), @memberId, @groupId, @pending, NULL),
                   (gen_random_uuid(), @memberId, @groupId, 'uid=gone,ou=People,dc=glitterband,dc=local', NULL),
                   (gen_random_uuid(), @memberId, @groupId, NULL, 'held as text'),
                   (gen_random_uuid(), @memberId, @groupId, NULL, NULL)
            """, connection);
        command.Parameters.AddWithValue("memberId", seeded.MemberAttributeId);
        command.Parameters.AddWithValue("groupId", seeded.GroupCsoId);
        command.Parameters.AddWithValue("pending", Pending);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The directory renamed user <paramref name="n"/>: its own DN changed, and the group's member value keeps the DN it
    /// was imported as.
    /// </summary>
    private async Task RenameUserAsync(int n, string newDn)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE "ConnectedSystemObjectAttributeValues" SET "StringValue" = @newDn
            WHERE "ConnectedSystemObjectId" = @userId AND "StringValue" IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("newDn", newDn);
        command.Parameters.AddWithValue("userId", LargeGroupImportSeed.UserCsoId(n));
        await command.ExecuteNonQueryAsync();
    }
}
