// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the light single-item read behind the REST execution item detail endpoint
/// (<see cref="JIM.Application.Servers.ActivityServer.GetActivityRunProfileExecutionItemCoreAsync"/>, #2032): every
/// field the endpoint returns round-trips from the row, on a context that does not track, as JIM.Web's does.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other database-backed tests; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityExecutionItemCoreDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL execution item detail tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemCoreAsync_ExistingItem_ReturnsEveryFieldTheEndpointExposesAsync()
    {
        var activity = NewActivity();
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activity.Id,
            ObjectChangeType = ObjectChangeType.Projected,
            NoChangeReason = null,
            MetaverseObjectId = Guid.NewGuid(),
            PendingExportId = Guid.NewGuid(),
            ExternalIdSnapshot = "EMP900083",
            DisplayNameSnapshot = "Osric Tamworth",
            ObjectTypeSnapshot = "person",
            ErrorType = ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted,
            ErrorMessage = "No free value was found for Staff Number after 1000 attempts.",
            ErrorStackTrace = "   at JIM.Application.UniqueValues.UniqueValueGenerationServer.ResolveAsync()",
            AttributeFlowCount = 3,
            OutcomeSummary = "Projected:1,AttributeFlow:3"
        };
        await using (var ctx = NewContext())
        {
            ctx.Activities.Add(activity);
            ctx.ActivityRunProfileExecutionItems.Add(item);
            await ctx.SaveChangesAsync();
        }

        var loaded = await NewJim().Activities.GetActivityRunProfileExecutionItemCoreAsync(item.Id);

        Assert.That(loaded, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded!.Id, Is.EqualTo(item.Id));
            Assert.That(loaded!.ActivityId, Is.EqualTo(activity.Id));
            Assert.That(loaded!.ObjectChangeType, Is.EqualTo(ObjectChangeType.Projected));
            Assert.That(loaded!.MetaverseObjectId, Is.EqualTo(item.MetaverseObjectId));
            Assert.That(loaded!.PendingExportId, Is.EqualTo(item.PendingExportId));
            Assert.That(loaded!.ExternalIdSnapshot, Is.EqualTo("EMP900083"));
            Assert.That(loaded!.DisplayNameSnapshot, Is.EqualTo("Osric Tamworth"));
            Assert.That(loaded!.ObjectTypeSnapshot, Is.EqualTo("person"));
            Assert.That(loaded!.ErrorType, Is.EqualTo(ActivityRunProfileExecutionItemErrorType.GeneratedValueExhausted));
            Assert.That(loaded!.ErrorMessage, Is.EqualTo(item.ErrorMessage));
            Assert.That(loaded!.ErrorStackTrace, Is.EqualTo(item.ErrorStackTrace));
            Assert.That(loaded!.AttributeFlowCount, Is.EqualTo(3));
            Assert.That(loaded!.OutcomeSummary, Is.EqualTo("Projected:1,AttributeFlow:3"));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemCoreAsync_UnknownId_ReturnsNullAsync()
    {
        var loaded = await NewJim().Activities.GetActivityRunProfileExecutionItemCoreAsync(Guid.NewGuid());

        Assert.That(loaded, Is.Null);
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    private static Activity NewActivity() => new()
    {
        Id = Guid.NewGuid(),
        TargetType = ActivityTargetType.ConnectedSystemRunProfile,
        TargetOperationType = ActivityTargetOperationType.Execute,
        TargetName = "Delta Synchronisation",
        InitiatedByType = ActivityInitiatorType.User,
        InitiatedByName = "Test User",
        Created = DateTime.UtcNow
    };
}
