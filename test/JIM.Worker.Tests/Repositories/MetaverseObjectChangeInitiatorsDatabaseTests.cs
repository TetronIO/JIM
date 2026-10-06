// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of a Metaverse Object's Created By and Last Updated By (#348): the initiators of its
/// earliest and latest changes, read once for the REST API and the portal alike. Opt-in via <c>JIM_TEST_RESET_*</c>.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectChangeInitiatorsDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.TrackAll)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL change initiator tests.");

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
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    private async Task<Guid> SeedPersonAsync(params (DateTime ChangeTime, ActivityInitiatorType Type, string Name)[] changes)
    {
        await using var seed = NewContext();
        var type = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = type, Created = DateTime.UtcNow };
        seed.AddRange(type, mvo);
        foreach (var (changeTime, initiatorType, name) in changes)
        {
            seed.Add(new MetaverseObjectChange
            {
                Id = Guid.NewGuid(), MetaverseObject = mvo, ChangeTime = changeTime, ChangeType = ObjectChangeType.Updated,
                InitiatedByType = initiatorType, InitiatedById = Guid.NewGuid(), InitiatedByName = name
            });
        }
        await seed.SaveChangesAsync();
        return mvo.Id;
    }

    [Test]
    public async Task GetMetaverseObjectChangeInitiatorsAsync_SeveralChanges_ReturnsTheEarliestAndLatestInitiatorsAsync()
    {
        var start = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc);
        var mvoId = await SeedPersonAsync(
            (start.AddDays(2), ActivityInitiatorType.System, "Synchronisation"),
            (start, ActivityInitiatorType.User, "Alice Admin"),
            (start.AddDays(5), ActivityInitiatorType.ApiKey, "Provisioning key"));

        await using var read = NewContext(QueryTrackingBehavior.NoTracking);
        var (earliest, latest) = await new PostgresDataRepository(read).Metaverse.GetMetaverseObjectChangeInitiatorsAsync(mvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(earliest!.InitiatedByName, Is.EqualTo("Alice Admin"));
            Assert.That(earliest.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            Assert.That(latest!.InitiatedByName, Is.EqualTo("Provisioning key"));
            Assert.That(latest.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        }
    }

    [Test]
    public async Task GetMetaverseObjectChangeInitiatorsAsync_NoChangeHistory_ReturnsNeitherAsync()
    {
        var mvoId = await SeedPersonAsync();

        await using var read = NewContext(QueryTrackingBehavior.NoTracking);
        var (earliest, latest) = await new PostgresDataRepository(read).Metaverse.GetMetaverseObjectChangeInitiatorsAsync(mvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(earliest, Is.Null);
            Assert.That(latest, Is.Null);
        }
    }
}
