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
/// #2817: a Delta Import asks the database which of a group's held member values none of the imported values match
/// exactly, and which imported values no held value matches exactly, instead of loading every member. The answers must
/// leave out only what an exact match settles, and must name every held value the import's comparison could match an
/// unmatched imported value with ignoring case; the partial load and the External ID lookup must leave out what the
/// answer stands in for, and nothing else.
/// </summary>
/// <remarks>
/// Runs against real PostgreSQL: the statements under test are the database's own. Opt-in via the same
/// <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class HeldReferenceValueAnswerDatabaseTests
{
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL held reference value tests.");

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

    [Test]
    public async Task GetHeldReferenceValueAnswersAsync_MembersRemovedAndAdded_AnswersOnlyWhatNoExactMatchSettlesAsync()
    {
        // Twenty users; the group holds the first ten. The import drops users 0 and 1, adds user 15 (held by JIM, not yet
        // a member) and a DN JIM does not hold at all.
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 20, memberCount: 10);
        const string ghost = "uid=ghost,ou=People,dc=glitterband,dc=local";
        var incoming = Enumerable.Range(2, 8).Select(LargeGroupImportSeed.UserDn).Append(LargeGroupImportSeed.UserDn(15)).Append(ghost).ToList();

        var answer = await AskAsync(seeded, incoming);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.Question.Values, Is.EqualTo(incoming));
            Assert.That(answer.UnmatchedValueIds, Is.EquivalentTo(new[] { LargeGroupImportSeed.MemberValueId(0), LargeGroupImportSeed.MemberValueId(1) }));
            Assert.That(answer.UnmatchedIncomingValues, Is.EquivalentTo(new[] { LargeGroupImportSeed.UserDn(15), ghost }));
            Assert.That(answer.RelatedValues, Is.Empty, "No member names user 15 or the unknown DN");
            Assert.That(answer.ReferencedExternalIds, Is.EquivalentTo(new Dictionary<Guid, string>
            {
                [LargeGroupImportSeed.UserCsoId(0)] = LargeGroupImportSeed.UserDn(0),
                [LargeGroupImportSeed.UserCsoId(1)] = LargeGroupImportSeed.UserDn(1)
            }));
        }
    }

    [Test]
    public async Task GetHeldReferenceValueAnswersAsync_NothingChanged_AnswersNothingAsync()
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 10, memberCount: 10);

        var answer = await AskAsync(seeded, Enumerable.Range(0, 10).Select(LargeGroupImportSeed.UserDn).ToList());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.UnmatchedValueIds, Is.Empty);
            Assert.That(answer.UnmatchedIncomingValues, Is.Empty);
            Assert.That(answer.RelatedValues, Is.Empty);
        }
    }

    /// <summary>
    /// A member renamed in the directory keeps the DN it was imported as, while the object it names now holds the new
    /// one. The comparison keeps such a value by its object's External ID, so an exact match on that settles it too.
    /// </summary>
    [Test]
    public async Task GetHeldReferenceValueAnswersAsync_MemberKeepingTheDnItWasImportedAs_IsSettledByItsObjectsExternalIdAsync()
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 5, memberCount: 5);
        await ExecuteAsync("""UPDATE "ConnectedSystemObjectAttributeValues" SET "UnresolvedReferenceValue" = 'uid=old3,ou=People,dc=glitterband,dc=local' WHERE "Id" = @id""",
            ("id", LargeGroupImportSeed.MemberValueId(3)));

        var answer = await AskAsync(seeded, Enumerable.Range(0, 5).Select(LargeGroupImportSeed.UserDn).ToList());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.UnmatchedValueIds, Is.Empty);
            Assert.That(answer.UnmatchedIncomingValues, Is.Empty);
        }
    }

    /// <summary>
    /// The comparison matches a member to an imported DN ignoring case through the member's object, so a DN whose case
    /// changed matches no held value exactly: the held value is undecided, and the answer carries it as related to the
    /// imported value, with its object's External ID, for the comparison to match.
    /// </summary>
    [Test]
    public async Task GetHeldReferenceValueAnswersAsync_CaseOnlyDnChange_CarriesTheMemberItMatchesIgnoringCaseAsync()
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 5, memberCount: 5);
        var upper = LargeGroupImportSeed.UserDn(4).ToUpperInvariant();
        var incoming = Enumerable.Range(0, 4).Select(LargeGroupImportSeed.UserDn).Append(upper).ToList();

        var answer = await AskAsync(seeded, incoming);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.UnmatchedValueIds, Is.EquivalentTo(new[] { LargeGroupImportSeed.MemberValueId(4) }));
            Assert.That(answer.UnmatchedIncomingValues, Is.EquivalentTo(new[] { upper }));
            Assert.That(answer.RelatedValues, Is.EquivalentTo(new[]
            {
                new HeldReferenceValue(LargeGroupImportSeed.MemberValueId(4), LargeGroupImportSeed.UserDn(4), LargeGroupImportSeed.UserCsoId(4))
            }));
            Assert.That(answer.ReferencedExternalIds[LargeGroupImportSeed.UserCsoId(4)], Is.EqualTo(LargeGroupImportSeed.UserDn(4)));
            Assert.That(answer.IsCompleteIgnoringCase(databaseFoldsCaseLikeIdentifierKey: true), Is.True);
            Assert.That(answer.IsCompleteIgnoringCase(databaseFoldsCaseLikeIdentifierKey: false), Is.False);
        }
    }

    /// <summary>
    /// A value still unresolved (its object not imported yet) is matched by the string alone; a value held under another
    /// data type, or holding nothing, matches nothing and is always undecided, so the comparison sees every value it
    /// could remove.
    /// </summary>
    [Test]
    public async Task GetHeldReferenceValueAnswersAsync_UnresolvedAndNonReferenceValues_AreMatchedByStringOrLeftUndecidedAsync()
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 3, memberCount: 3);
        const string pending = "uid=pending,ou=People,dc=glitterband,dc=local";
        const string gone = "uid=gone,ou=People,dc=glitterband,dc=local";
        var pendingId = Guid.NewGuid();
        var goneId = Guid.NewGuid();
        var textId = Guid.NewGuid();
        var emptyId = Guid.NewGuid();
        await ExecuteAsync("""
            INSERT INTO "ConnectedSystemObjectAttributeValues" ("Id", "AttributeId", "ConnectedSystemObjectId", "UnresolvedReferenceValue", "StringValue")
            VALUES (@pendingId, @memberId, @groupId, @pending, NULL), (@goneId, @memberId, @groupId, @gone, NULL),
                   (@textId, @memberId, @groupId, NULL, 'held as text'), (@emptyId, @memberId, @groupId, NULL, NULL)
            """,
            ("pendingId", pendingId), ("goneId", goneId), ("textId", textId), ("emptyId", emptyId), ("memberId", seeded.MemberAttributeId),
            ("groupId", seeded.GroupCsoId), ("pending", pending), ("gone", gone));

        var answer = await AskAsync(seeded, Enumerable.Range(0, 3).Select(LargeGroupImportSeed.UserDn).Append(pending).ToList());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.UnmatchedValueIds, Is.EquivalentTo(new[] { goneId, textId, emptyId }));
            Assert.That(answer.UnmatchedIncomingValues, Is.Empty);
        }
    }

    /// <summary>
    /// The questions are answered in one statement, and one group's answer must never take another's rows, even where the
    /// two groups share members.
    /// </summary>
    [Test]
    public async Task GetHeldReferenceValueAnswersAsync_TwoQuestions_AnswersEachForItsOwnObjectAndAttributeAsync()
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
        var answers = await new PostgresDataRepository(ctx).Sync.GetHeldReferenceValueAnswersAsync(
        [
            new HeldReferenceValueQuestion(seeded.GroupCsoId, seeded.MemberAttributeId, Enumerable.Range(1, 3).Select(LargeGroupImportSeed.UserDn).ToList()),
            new HeldReferenceValueQuestion(secondGroupId, seeded.MemberAttributeId, Enumerable.Range(0, 3).Select(LargeGroupImportSeed.UserDn).ToList())
        ]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answers.Select(a => a.Question.ConnectedSystemObjectId), Is.EqualTo(new[] { seeded.GroupCsoId, secondGroupId }));
            Assert.That(answers[0].UnmatchedValueIds, Is.EquivalentTo(new[] { LargeGroupImportSeed.MemberValueId(0) }));
            Assert.That(answers[1].UnmatchedValueIds, Is.EquivalentTo(new[] { LargeGroupImportSeed.DeterministicGuidOf(LargeGroupImportSeed.MemberValueId(3)) }));
            Assert.That(answers.SelectMany(a => a.UnmatchedIncomingValues), Is.Empty);
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectsByIdsAsync_ExcludedAttribute_LoadsOnlyTheNamedValuesOfItAsync()
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 5, memberCount: 5);

        await using var ctx = NewContext();
        var cso = (await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsByIdsAsync(seeded.SystemId, [seeded.GroupCsoId],
            [seeded.MemberAttributeId], [LargeGroupImportSeed.MemberValueId(2)])).Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cso.AttributeValues.Where(av => av.AttributeId == seeded.MemberAttributeId).Select(av => av.Id),
                Is.EqualTo(new[] { LargeGroupImportSeed.MemberValueId(2) }));
            Assert.That(cso.AttributeValues.Select(av => av.AttributeId), Is.SupersetOf(new[] { seeded.GroupDnAttributeId, seeded.GroupGuidAttributeId }),
                "Other attributes are loaded whole");
            Assert.That(cso.AttributeValues.All(av => av.Attribute != null && cso.Type.Attributes.Contains(av.Attribute)), Is.True,
                "Values are wired to the Object Type's attributes as a whole load wires them");
        }
    }

    [Test]
    public async Task GetReferenceExternalIdsForCsosAsync_ExcludedAttribute_LeavesItsReferencesOutAsync()
    {
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: 3, memberCount: 3);

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx).Sync;
        var whole = await repository.GetReferenceExternalIdsForCsosAsync([seeded.GroupCsoId]);
        var excluded = await repository.GetReferenceExternalIdsForCsosAsync([seeded.GroupCsoId], [seeded.MemberAttributeId]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(whole[seeded.GroupCsoId], Has.Count.EqualTo(3));
            Assert.That(excluded, Contains.Key(seeded.GroupCsoId), "Every requested object still has an entry");
            Assert.That(excluded[seeded.GroupCsoId], Is.Empty);
        }
    }

    /// <summary>
    /// The point of the question (#2817): for a group of 20,000 members that lost two and gained one, the answer is those
    /// three values, however many members the group holds, and the related values are found through the index on
    /// <c>lower("StringValue")</c> rather than by reading every object's values.
    /// </summary>
    [Test]
    public async Task GetHeldReferenceValueAnswersAsync_LargeGroup_ReturnsOnlyTheChangedValuesAndLooksUpRelatedValuesByIndexAsync()
    {
        const int members = 20_000;
        var seeded = await LargeGroupImportSeed.SeedAsync(NewContext, _connectionString, userCount: members + 1, memberCount: members);
        var incoming = Enumerable.Range(2, members - 2).Select(LargeGroupImportSeed.UserDn).Append(LargeGroupImportSeed.UserDn(members).ToUpperInvariant()).ToList();
        var question = new HeldReferenceValueQuestion(seeded.GroupCsoId, seeded.MemberAttributeId, incoming);

        var answer = await AskAsync(seeded, incoming);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var explain = new NpgsqlCommand();
        explain.Connection = connection;
        JIM.PostgresData.Repositories.SyncRepository.PrepareHeldReferenceValueAnswersCommand(explain, [question]);
        explain.CommandText = "EXPLAIN " + explain.CommandText;
        var plan = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                plan.Add(reader.GetString(0));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.UnmatchedValueIds, Is.EquivalentTo(new[] { LargeGroupImportSeed.MemberValueId(0), LargeGroupImportSeed.MemberValueId(1) }));
            Assert.That(answer.UnmatchedIncomingValues, Is.EquivalentTo(new[] { LargeGroupImportSeed.UserDn(members).ToUpperInvariant() }));
            Assert.That(string.Join("\n", plan), Does.Contain("IX_CsoAttrVal_Lower_StringValue"), "Related values found through the lower(StringValue) index");
            Assert.That(string.Join("\n", plan), Does.Not.Contain("ANY ($"),
                "The imported values reach the plan as a constant array, which PostgreSQL hashes, not as a parameter it would scan per member");
            Assert.That(string.Join("\n", plan), Does.Not.Contain("Nested Loop Anti Join"), "No member is compared with the imported values one by one");
        }
    }

    private async Task<HeldReferenceValueAnswer> AskAsync(LargeGroupImportSeed seeded, List<string> incoming)
    {
        await using var ctx = NewContext();
        var answers = await new PostgresDataRepository(ctx).Sync.GetHeldReferenceValueAnswersAsync(
            [new HeldReferenceValueQuestion(seeded.GroupCsoId, seeded.MemberAttributeId, incoming)]);
        return answers.Single();
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
