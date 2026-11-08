// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// #2818: Drift Detection asks the database which Metaverse Objects a group's members resolve to that it does not expect,
/// and which expected ones none resolves to, instead of loading every member. The database's answer must be exactly what
/// the values themselves give (<see cref="DriftHeldReferenceAnswer.Compute"/>), which the differential tests hold to the
/// whole-load comparison, and the statement must stay set-based however large the group.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DriftHeldReferenceAnswerDatabaseTests
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
        var connectionString = PostgresTestDatabase.ConnectionStringFromEnvironment();
        if (connectionString == null)
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL drift comparison tests.");
        _connectionString = connectionString!;

        TestUtilities.SetEnvironmentVariables();
        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync() => await PostgresTestDatabase.ResetAsync(_connectionString);

    /// <summary>
    /// Generated groups: members joined and not, references held twice, references not yet resolved, values held under
    /// another data type beside references and on their own, no members at all; asked about expected sets that hold some,
    /// all, none and more of them, the empty id included. Every answer, in one round trip, is what the values give.
    /// </summary>
    [Test]
    public async Task GetDriftHeldReferenceAnswersAsync_GeneratedGroups_AnswerAsTheValuesThemselvesDoAsync()
    {
        var random = new Random(2818);
        var seed = await DriftComparisonSeed.CreateAsync(NewContext, _connectionString);
        var users = Enumerable.Range(0, 60).Select(_ => (CsoId: Guid.NewGuid(), MvoId: random.Next(5) == 0 ? (Guid?)null : Guid.NewGuid())).ToList();
        await seed.AddUsersAsync(users);

        var groups = Enumerable.Range(0, 40).Select(_ => (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid(), Status: ConnectedSystemObjectStatus.Normal)).ToList();
        await seed.AddGroupsAsync(groups);
        for (var g = 0; g < groups.Count; g++)
        {
            var onlyAnotherType = g == 1;
            var rows = g == 0 ? [] : Enumerable.Range(0, random.Next(1, 40)).Select(_ =>
            {
                var user = users[random.Next(users.Count)];
                return (onlyAnotherType ? 1 : random.Next(10)) switch
                {
                    0 => new DriftComparisonSeed.MemberRow(Guid.NewGuid(), UnresolvedReferenceValue: $"uid={random.Next(9)}"),
                    1 => random.Next(2) == 0
                        ? new DriftComparisonSeed.MemberRow(Guid.NewGuid(), StringValue: "held as text")
                        : new DriftComparisonSeed.MemberRow(Guid.NewGuid(), IntValue: 7),
                    _ => new DriftComparisonSeed.MemberRow(Guid.NewGuid(), ReferenceValueId: user.CsoId, UnresolvedReferenceValue: $"uid={user.CsoId:N}")
                };
            }).ToList();
            await seed.AddMemberRowsAsync(groups[g].CsoId, rows);
        }

        var questions = groups.Select(group => new DriftHeldReferenceQuestion(group.CsoId, seed.MemberAttributeId, 99,
            users.Where(u => u.MvoId.HasValue && random.Next(3) != 0).Select(u => u.MvoId!.Value)
                .Concat(Enumerable.Range(0, random.Next(3)).Select(_ => Guid.NewGuid()))
                .Concat(random.Next(10) == 0 ? [Guid.Empty] : [])
                .OrderBy(_ => random.Next())
                .ToList())).ToList();

        List<DriftHeldReferenceAnswer> answers;
        await using (var ctx = NewContext())
            answers = await new PostgresDataRepository(ctx).Sync.GetDriftHeldReferenceAnswersAsync(questions);

        var expectedAnswers = await ComputeFromTheValuesAsync(questions);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answers, Has.Count.EqualTo(questions.Count));
            for (var i = 0; i < questions.Count; i++)
            {
                var (actual, expected) = (answers[i], expectedAnswers[i]);
                Assert.That(actual.Question, Is.SameAs(questions[i]), $"answer {i} is to question {i}");
                Assert.That(actual.HeldNotExpected, Is.EqualTo(expected.HeldNotExpected), $"group {i}: held but not expected, in value order");
                Assert.That(actual.ExpectedNotHeld, Is.EquivalentTo(expected.ExpectedNotHeld), $"group {i}: expected but not held");
                Assert.That(actual.HeldValueCount, Is.EqualTo(expected.HeldValueCount), $"group {i}: values held");
                Assert.That(actual.HoldsReference, Is.EqualTo(expected.HoldsReference), $"group {i}: holds a reference");
                Assert.That(actual.FirstOtherHeldDataType, Is.EqualTo(expected.FirstOtherHeldDataType), $"group {i}: first other type held");
            }

            Assert.That(answers.Count(a => a.HeldNotExpected.Count > 0), Is.GreaterThan(5), "answers holding unexpected members are covered");
            Assert.That(answers.Count(a => a.ExpectedNotHeld.Count > 0), Is.GreaterThan(5), "answers missing expected members are covered");
            Assert.That(answers[0].HeldValueCount, Is.Zero, "a group with no members is covered");
            Assert.That(answers[1].HeldOnlyUnderAnotherDataType, Is.Not.Null, "a group held only under another data type is covered");
        }
    }

    /// <summary>
    /// The point of the question: for a group of 20,000 members that lost ten and gained five, the answer is those fifteen,
    /// and the plan compares every member against the expected values as a hash of a constant array and an EXCEPT, never
    /// one by one.
    /// </summary>
    [Test]
    public async Task GetDriftHeldReferenceAnswersAsync_LargeGroup_AnswersOnlyTheDisagreementSetBasedAsync()
    {
        const int members = 20_000;
        var seed = await DriftComparisonSeed.CreateAsync(NewContext, _connectionString);
        var users = Enumerable.Range(0, members + 5).Select(_ => (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid())).ToList();
        await seed.AddUsersAsync(users);
        var group = (CsoId: Guid.NewGuid(), MvoId: (Guid?)Guid.NewGuid(), Status: ConnectedSystemObjectStatus.Normal);
        await seed.AddGroupsAsync([group]);
        await seed.AddMemberRowsAsync(group.CsoId, users.Take(members)
            .Select(u => new DriftComparisonSeed.MemberRow(Guid.NewGuid(), ReferenceValueId: u.CsoId, UnresolvedReferenceValue: $"uid={u.CsoId:N}")).ToList());
        await seed.AnalyseAsync();

        // The Metaverse Object no longer holds the first ten members and holds five users the group does not.
        var expected = users.Skip(10).Select(u => u.MvoId!.Value).ToList();
        var question = new DriftHeldReferenceQuestion(group.CsoId, seed.MemberAttributeId, 99, expected);

        List<DriftHeldReferenceAnswer> answers;
        await using (var ctx = NewContext())
            answers = await new PostgresDataRepository(ctx).Sync.GetDriftHeldReferenceAnswersAsync([question]);

        var plan = await ExplainAsync(question);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(answers.Single().HeldNotExpected, Is.EquivalentTo(users.Take(10).Select(u => u.MvoId!.Value)));
            Assert.That(answers.Single().ExpectedNotHeld, Is.EquivalentTo(users.Skip(members).Select(u => u.MvoId!.Value)));
            Assert.That(answers.Single().HeldValueCount, Is.EqualTo(members));
            Assert.That(plan, Does.Not.Contain("ANY ($"),
                "The expected values reach the plan as a constant array, which PostgreSQL hashes, not as a parameter it would scan per member");
            Assert.That(plan, Does.Not.Contain("Nested Loop Anti Join"), "No member is compared with the expected values one by one");
            Assert.That(plan, Does.Contain("SetOp"), "The expected values no member resolves to are a set operation");
        }
    }

    private async Task<string> ExplainAsync(DriftHeldReferenceQuestion question)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var explain = new NpgsqlCommand("EXPLAIN " + JIM.PostgresData.Repositories.SyncRepository.DriftHeldReferenceAnswerSql, connection);
        explain.Parameters.AddRange(JIM.PostgresData.Repositories.SyncRepository.DriftHeldReferenceAnswerParameters(question));
        var lines = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(0));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Each answer from the values themselves, read back in value id order, each resolved to the Metaverse Object its
    /// referenced object is joined to.
    /// </summary>
    private async Task<List<DriftHeldReferenceAnswer>> ComputeFromTheValuesAsync(List<DriftHeldReferenceQuestion> questions)
    {
        await using var ctx = NewContext();
        var joins = await ctx.ConnectedSystemObjects.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.MetaverseObjectId);
        var answers = new List<DriftHeldReferenceAnswer>();
        foreach (var question in questions)
        {
            var values = await ctx.ConnectedSystemObjectAttributeValues.AsNoTracking()
                .Where(v => EF.Property<Guid>(v, "ConnectedSystemObjectId") == question.ConnectedSystemObjectId && v.AttributeId == question.AttributeId)
                .OrderBy(v => v.Id)
                .ToListAsync();
            answers.Add(DriftHeldReferenceAnswer.Compute(question, values,
                v => v.ReferenceValueId is { } id ? joins.GetValueOrDefault(id) : null));
        }

        return answers;
    }
}
