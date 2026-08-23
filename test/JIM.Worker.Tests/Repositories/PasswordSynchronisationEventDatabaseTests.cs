// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the identity's password history read behind the Metaverse Object panel
/// (#1119, requirement 25).
/// <para>
/// Against a real provider because the read is two queries whose correctness is in how they join: the second
/// matches children by a nullable parent id against a list, and the grouping that reassembles them is what keeps
/// an outcome attached to the change it belongs to. The in-memory provider resolves parent and child from its own
/// tracked graph, so a projection that never translated would still look right there.
/// </para>
/// <para>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures. Do NOT run this fixture outside the sanctioned scratch-database workflow: <c>SetUp</c> TRUNCATEs
/// every table.
/// </para>
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PasswordSynchronisationEventDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL password history tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await using var ctx = NewContext();
        await ctx.Database.ExecuteSqlRawAsync(@"
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN (SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') LOOP
                    EXECUTE 'TRUNCATE TABLE ""' || r.tablename || '"" RESTART IDENTITY CASCADE';
                END LOOP;
            END $$;");
    }

    /// <summary>
    /// Writes one password change Activity for an identity, with an outcome per named system.
    /// </summary>
    private async Task<Guid> SeedChangeAsync(
        Guid metaverseObjectId,
        DateTime created,
        params (string SystemName, ActivityStatus Status, string? ErrorMessage)[] outcomes)
    {
        await using var ctx = NewContext();

        var change = new Activity
        {
            Id = Guid.NewGuid(),
            Created = created,
            Executed = created,
            TargetType = ActivityTargetType.PasswordSynchronisation,
            TargetOperationType = ActivityTargetOperationType.SetPassword,
            TargetName = "Ada Lovelace",
            MetaverseObjectId = metaverseObjectId,
            InitiatedByType = ActivityInitiatorType.ApiKey,
            InitiatedByName = "Self-service portal",
            InitiatedById = Guid.NewGuid(),
            Status = ActivityStatus.Complete,
            Message = "Password change queued."
        };
        ctx.Activities.Add(change);

        var offset = 0;
        foreach (var (systemName, status, errorMessage) in outcomes)
        {
            ctx.Activities.Add(new Activity
            {
                Id = Guid.NewGuid(),
                Created = created.AddSeconds(++offset),
                Executed = created.AddSeconds(offset),
                ParentActivityId = change.Id,
                TargetType = ActivityTargetType.PasswordSynchronisation,
                TargetOperationType = ActivityTargetOperationType.SetPassword,
                TargetName = systemName,
                TargetContext = systemName,
                MetaverseObjectId = metaverseObjectId,
                InitiatedByType = ActivityInitiatorType.System,
                InitiatedByName = "JIM",
                Status = status,
                ErrorMessage = errorMessage,
                Message = errorMessage ?? $"Password set on {systemName}."
            });
        }

        await ctx.SaveChangesAsync();
        return change.Id;
    }

    [Test]
    public async Task GetPasswordSynchronisationEventsAsync_ReturnsEachChangeWithItsOwnOutcomesAsync()
    {
        var ada = Guid.NewGuid();
        var at = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);

        var older = await SeedChangeAsync(ada, at,
            ("Corporate AD", ActivityStatus.Complete, null));
        var newer = await SeedChangeAsync(ada, at.AddHours(1),
            ("Corporate AD", ActivityStatus.Complete, null),
            ("HR SQL", ActivityStatus.FailedWithError, "The password does not meet the requirements of the domain."));

        await using var ctx = NewContext();
        var events = await new PostgresDataRepository(ctx).Activity.GetPasswordSynchronisationEventsAsync(ada, 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(events.Select(e => e.ActivityId), Is.EqualTo(new[] { newer, older }).AsCollection,
                "Newest first: the change an administrator is asking about is nearly always the last one.");
            Assert.That(events[0].Outcomes, Has.Count.EqualTo(2));
            Assert.That(events[1].Outcomes, Has.Count.EqualTo(1),
                "An outcome must stay attached to the change it belongs to.");
            Assert.That(events[0].Outcomes.Single(o => o.ConnectedSystemName == "HR SQL").Succeeded, Is.False);
            Assert.That(events[0].Outcomes.Single(o => o.ConnectedSystemName == "HR SQL").ErrorMessage,
                Does.Contain("requirements of the domain"),
                "The target's own words are what say where the remedy lives.");
            Assert.That(events[0].Outcomes.Single(o => o.ConnectedSystemName == "Corporate AD").Succeeded, Is.True);
        }
    }

    [Test]
    public async Task GetPasswordSynchronisationEventsAsync_AnswersOnlyForTheIdentityAskedAboutAsync()
    {
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();
        var at = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);

        await SeedChangeAsync(ada, at, ("Corporate AD", ActivityStatus.Complete, null));
        await SeedChangeAsync(grace, at.AddMinutes(1), ("Corporate AD", ActivityStatus.Complete, null));

        await using var ctx = NewContext();
        var events = await new PostgresDataRepository(ctx).Activity.GetPasswordSynchronisationEventsAsync(ada, 10);

        Assert.That(events, Has.Exactly(1).Items);
    }

    /// <summary>
    /// The limit counts changes, not rows. Applied to a flat parent-and-child result it would cut a change off
    /// from half its outcomes, showing an identity a success while hiding the refusal that followed it.
    /// </summary>
    [Test]
    public async Task GetPasswordSynchronisationEventsAsync_LimitsChangesNotOutcomesAsync()
    {
        var ada = Guid.NewGuid();
        var at = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);

        await SeedChangeAsync(ada, at, ("Corporate AD", ActivityStatus.Complete, null));
        await SeedChangeAsync(ada, at.AddHours(1),
            ("Corporate AD", ActivityStatus.Complete, null),
            ("HR SQL", ActivityStatus.FailedWithError, "Refused."),
            ("Contractor LDAP", ActivityStatus.Complete, null));

        await using var ctx = NewContext();
        var events = await new PostgresDataRepository(ctx).Activity.GetPasswordSynchronisationEventsAsync(ada, 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(events, Has.Exactly(1).Items);
            Assert.That(events[0].Outcomes, Has.Count.EqualTo(3));
        }
    }

    [Test]
    public async Task GetPasswordSynchronisationEventsAsync_ChangeThatReachedNothing_ReturnsItWithNoOutcomesAsync()
    {
        // Requirement 14: a change that reached no system is still recorded, and the panel must show it rather
        // than leave the administrator believing nothing happened.
        var ada = Guid.NewGuid();
        await SeedChangeAsync(ada, new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc));

        await using var ctx = NewContext();
        var events = await new PostgresDataRepository(ctx).Activity.GetPasswordSynchronisationEventsAsync(ada, 10);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(events, Has.Exactly(1).Items);
            Assert.That(events[0].Outcomes, Is.Empty);
        }
    }

    [Test]
    public async Task GetPasswordSynchronisationEventsAsync_IdentityWithNoPasswordHistory_ReturnsNothingAsync()
    {
        await using var ctx = NewContext();
        var events = await new PostgresDataRepository(ctx).Activity.GetPasswordSynchronisationEventsAsync(Guid.NewGuid(), 10);

        Assert.That(events, Is.Empty);
    }
}
