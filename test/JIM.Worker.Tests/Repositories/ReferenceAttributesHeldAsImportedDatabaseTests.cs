// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// #2836: before a Delta Import asks which of a group's members are undecided, it asks which groups it holds exactly as
/// imported, which costs no imported value sent to the database. A group named there is never asked about, so it must be
/// one whose answer would have left nothing undecided: every case checks that the full answer is empty wherever the group
/// is named, and that a group holding anything the import would change is never named.
/// </summary>
/// <remarks>
/// Runs against real PostgreSQL: the statement under test is the database's own. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ReferenceAttributesHeldAsImportedDatabaseTests
{
    private const string Pending = "uid=pending,ou=People,dc=glitterband,dc=local";

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL held-as-imported tests.");

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

    /// <summary>The groups held: users 0 to 7 are members; users 8 to 11 are held by JIM but are not members.</summary>
    public enum HeldState
    {
        AsSeeded,
        MemberHeldTwice,
        ValueHoldingNoReference,
        MemberRenamedSinceImported,
        MemberNotYetResolved
    }

    private static IEnumerable<TestCaseData> Cases()
    {
        static string Dn(int n) => LargeGroupImportSeed.UserDn(n);
        static List<string> Members(int from, int to) => Enumerable.Range(from, to - from + 1).Select(Dn).ToList();

        yield return new TestCaseData(HeldState.AsSeeded, Members(0, 7), true).SetName("HeldExactlyAsImported");
        yield return new TestCaseData(HeldState.AsSeeded, Members(0, 7).AsEnumerable().Reverse().ToList(), true).SetName("ImportedInAnotherOrder");
        yield return new TestCaseData(HeldState.MemberNotYetResolved, Members(0, 7).Append(Pending).ToList(), true)
            .SetName("MemberNotYetResolvedHeldAsImported");
        yield return new TestCaseData(HeldState.AsSeeded, Members(1, 7), false).SetName("MemberRemoved");
        yield return new TestCaseData(HeldState.AsSeeded, Members(0, 8), false).SetName("MemberAdded");
        yield return new TestCaseData(HeldState.AsSeeded, Members(1, 8), false).SetName("MemberReplacedKeepingTheCount");
        yield return new TestCaseData(HeldState.AsSeeded, Members(0, 6).Append(Dn(7).ToUpperInvariant()).ToList(), false).SetName("CaseOnlyDnChange");
        yield return new TestCaseData(HeldState.MemberHeldTwice, Members(0, 8), false).SetName("MemberHeldTwiceWhereAnotherIsImported");
        yield return new TestCaseData(HeldState.ValueHoldingNoReference, Members(0, 8), false).SetName("ValueHoldingNoReferenceWhereAnotherIsImported");
        yield return new TestCaseData(HeldState.MemberRenamedSinceImported, Members(0, 7), false).SetName("MemberKeptByItsObjectsExternalIdIsLeftToTheAnswer");
        yield return new TestCaseData(HeldState.AsSeeded, Members(0, 7).Append(Dn(0)).ToList(), false).SetName("ImportedValueRepeated");
        yield return new TestCaseData(HeldState.AsSeeded, new List<string>(), false).SetName("NoValueImported");
    }

    [TestCaseSource(nameof(Cases))]
    public async Task GetReferenceAttributesHeldAsImportedAsync_NamesOnlyAGroupWhoseAnswerWouldLeaveNothingUndecidedAsync(HeldState held,
        List<string> imported, bool expectedHeldAsImported)
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 12, memberCount: 8);
        await ArrangeAsync(seeded, held);
        var question = new HeldReferenceValueQuestion(seeded.GroupCsoId, seeded.MemberAttributeId, imported);

        HashSet<(Guid ConnectedSystemObjectId, int AttributeId)> heldAsImported;
        HeldReferenceValueAnswer answer;
        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx).Sync;
            heldAsImported = await repository.GetReferenceAttributesHeldAsImportedAsync([question]);
            answer = (await repository.GetHeldReferenceValueAnswersAsync([question])).Single();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(heldAsImported.Contains((seeded.GroupCsoId, seeded.MemberAttributeId)), Is.EqualTo(expectedHeldAsImported));
            if (heldAsImported.Count > 0)
            {
                Assert.That(answer.UnmatchedValueIds, Is.Empty, "Named as held as imported, so asking would leave no held value undecided");
                Assert.That(answer.UnmatchedIncomingValues, Is.Empty, "... and no imported value undecided");
                Assert.That(answer.RelatedValues, Is.Empty);
                Assert.That(answer.ReferencedExternalIds, Is.Empty);
            }
        }
    }

    /// <summary>
    /// The questions are answered in one statement, and one group's values must never count towards another's, even where
    /// the two hold the same members.
    /// </summary>
    [Test]
    public async Task GetReferenceAttributesHeldAsImportedAsync_TwoGroupsSharingMembers_NamesOnlyTheOneHeldAsImportedAsync()
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 4, memberCount: 4);
        var secondGroupId = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO "ConnectedSystemObjects"
            SELECT (jsonb_populate_record(cso, jsonb_build_object('Id', @secondGroupId))).* FROM "ConnectedSystemObjects" cso WHERE cso."Id" = @groupId;
            INSERT INTO "ConnectedSystemObjectAttributeValues"
            SELECT (jsonb_populate_record(av, jsonb_build_object('Id', md5(av."Id"::text)::uuid, 'ConnectedSystemObjectId', @secondGroupId))).*
            FROM "ConnectedSystemObjectAttributeValues" av WHERE av."ConnectedSystemObjectId" = @groupId AND av."AttributeId" = @memberId;
            """,
            ("secondGroupId", secondGroupId), ("groupId", seeded.GroupCsoId), ("memberId", seeded.MemberAttributeId));

        await using var ctx = NewContext();
        var heldAsImported = await new PostgresDataRepository(ctx).Sync.GetReferenceAttributesHeldAsImportedAsync(
        [
            new HeldReferenceValueQuestion(seeded.GroupCsoId, seeded.MemberAttributeId, Enumerable.Range(0, 3).Select(LargeGroupImportSeed.UserDn).Append("uid=other").ToList()),
            new HeldReferenceValueQuestion(secondGroupId, seeded.MemberAttributeId, Enumerable.Range(0, 4).Select(LargeGroupImportSeed.UserDn).ToList()),
            new HeldReferenceValueQuestion(Guid.NewGuid(), seeded.MemberAttributeId, [LargeGroupImportSeed.UserDn(0)])
        ]);

        Assert.That(heldAsImported, Is.EquivalentTo(new[] { (secondGroupId, seeded.MemberAttributeId) }));
    }

    [Test]
    public async Task GetReferenceAttributesHeldAsImportedAsync_NoQuestions_NamesNothingAsync()
    {
        await using var ctx = NewContext();
        Assert.That(await new PostgresDataRepository(ctx).Sync.GetReferenceAttributesHeldAsImportedAsync([]), Is.Empty);
    }

    private async Task ArrangeAsync(LargeGroupImportSeed seeded, HeldState held)
    {
        switch (held)
        {
            case HeldState.MemberHeldTwice:
                await ExecuteAsync("""
                    INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "AttributeId", "ConnectedSystemObjectId", "UnresolvedReferenceValue", "ReferenceValueId")
                    VALUES (gen_random_uuid(), @memberId, @groupId, @dn, @userId)
                    """,
                    ("memberId", seeded.MemberAttributeId), ("groupId", seeded.GroupCsoId), ("dn", LargeGroupImportSeed.UserDn(0)),
                    ("userId", LargeGroupImportSeed.UserCsoId(0)));
                break;
            case HeldState.ValueHoldingNoReference:
                await ExecuteAsync("""
                    INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "AttributeId", "ConnectedSystemObjectId", "UnresolvedReferenceValue", "StringValue")
                    VALUES (gen_random_uuid(), @memberId, @groupId, NULL, 'held as text')
                    """,
                    ("memberId", seeded.MemberAttributeId), ("groupId", seeded.GroupCsoId));
                break;
            case HeldState.MemberRenamedSinceImported:
                await ExecuteAsync("""UPDATE "ConnectedSystemObjectAttributeValues" SET "UnresolvedReferenceValue" = 'uid=old3,ou=People,dc=glitterband,dc=local' WHERE "Id" = @id""",
                    ("id", LargeGroupImportSeed.MemberValueId(3)));
                break;
            case HeldState.MemberNotYetResolved:
                await ExecuteAsync("""
                    INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "AttributeId", "ConnectedSystemObjectId", "UnresolvedReferenceValue")
                    VALUES (gen_random_uuid(), @memberId, @groupId, @pending)
                    """,
                    ("memberId", seeded.MemberAttributeId), ("groupId", seeded.GroupCsoId), ("pending", Pending));
                break;
            case HeldState.AsSeeded:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(held), held, null);
        }
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }
}
