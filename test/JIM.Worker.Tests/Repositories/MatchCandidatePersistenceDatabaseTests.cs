// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the raw-SQL path that persists an ambiguous match's candidates with its Activity
/// item (#2052). The in-memory provider stores the graph verbatim, so a column the writer omits or writes in the
/// wrong position would round-trip there perfectly; and it enforces no cascade, so it cannot show the candidates
/// leaving with their item.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MatchCandidatePersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL match candidate persistence tests.");

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
    public async Task BulkInsertRpeisAsync_ItemWithAFullyPopulatedCandidate_PersistsEveryFieldAndEmptiesTheBufferAsync()
    {
        var activityId = await SeedActivityAsync();
        var candidate = new ActivityRunProfileExecutionItemMatchCandidate
        {
            CandidateKind = MatchCandidateKind.ConnectedSystemObject,
            CandidateId = Guid.NewGuid(),
            DisplayNameSnapshot = "p.shah-admin",
            TypeNameSnapshot = "user",
            ConnectedSystemId = 7,
            ConnectedSystemNameSnapshot = "Corporate Directory",
            MatchedValue = "48213",
            ObjectMatchingRuleId = 12,
            ObjectMatchingRuleDescriptionSnapshot = "Employee ID equals employeeID",
            Ordinal = 1,
            CandidateTotal = 2
        };
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = activityId,
            ObjectChangeType = ObjectChangeType.NotSet,
            ErrorType = ActivityRunProfileExecutionItemErrorType.AmbiguousMatch,
            MatchCandidates = [candidate]
        };

        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.BulkInsertRpeisAsync([rpei]);

        await using var verify = NewContext();
        var persisted = await verify.ActivityRunProfileExecutionItemMatchCandidates.AsNoTracking().SingleAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpei.MatchCandidates, Is.Empty, "the buffer is emptied once written, so a later flush cannot duplicate it");
            Assert.That(persisted.Id, Is.EqualTo(candidate.Id));
            Assert.That(persisted.ActivityRunProfileExecutionItemId, Is.EqualTo(rpei.Id));
            Assert.That(persisted.CandidateKind, Is.EqualTo(MatchCandidateKind.ConnectedSystemObject));
            Assert.That(persisted.CandidateId, Is.EqualTo(candidate.CandidateId));
            Assert.That(persisted.DisplayNameSnapshot, Is.EqualTo("p.shah-admin"));
            Assert.That(persisted.TypeNameSnapshot, Is.EqualTo("user"));
            Assert.That(persisted.ConnectedSystemId, Is.EqualTo(7));
            Assert.That(persisted.ConnectedSystemNameSnapshot, Is.EqualTo("Corporate Directory"));
            Assert.That(persisted.MatchedValue, Is.EqualTo("48213"));
            Assert.That(persisted.ObjectMatchingRuleId, Is.EqualTo(12));
            Assert.That(persisted.ObjectMatchingRuleDescriptionSnapshot, Is.EqualTo("Employee ID equals employeeID"));
            Assert.That(persisted.Ordinal, Is.EqualTo(1));
            Assert.That(persisted.CandidateTotal, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task DeletingTheItem_CascadesToItsCandidatesAsync()
    {
        var activityId = await SeedActivityAsync();
        var rpei = new ActivityRunProfileExecutionItem
        {
            ActivityId = activityId,
            ErrorType = ActivityRunProfileExecutionItemErrorType.AmbiguousMatch,
            MatchCandidates = [new ActivityRunProfileExecutionItemMatchCandidate { CandidateId = Guid.NewGuid(), CandidateTotal = 1 }]
        };
        await using (var ctx = NewContext())
            await new PostgresDataRepository(ctx).Sync.BulkInsertRpeisAsync([rpei]);

        await using (var ctx = NewContext())
            await ctx.ActivityRunProfileExecutionItems.Where(r => r.Id == rpei.Id).ExecuteDeleteAsync();

        await using var verify = NewContext();
        Assert.That(await verify.ActivityRunProfileExecutionItemMatchCandidates.CountAsync(), Is.Zero);
    }

    private async Task<Guid> SeedActivityAsync()
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Full Sync",
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity.Id;
    }
}
