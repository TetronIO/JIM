// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the two reads the Connected System deletion preview's hosts need (#134): finding
/// the latest preview of a kind for a system, so the Danger Zone reattaches to it after the administrator navigates
/// away, and judging whether anything has happened since a preview started that could change its answer.
/// </summary>
/// <remarks>
/// <c>NoTracking</c>, matching JIM.Web, where both are read from.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationChangePreviewStalenessDatabaseTests
{
    private static readonly DateTime PreviewStarted = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL preview staleness tests.");

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

    // -----------------------------------------------------------------------------------------------------------------
    // The latest preview of a kind for a system
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task GetLatestConnectedSystemPreviewAsync_SeveralPreviews_ReturnsTheNewestOfThatKindForThatSystemAsync()
    {
        var (systemA, systemB) = await SeedTwoSystemsAsync();
        await SeedPreviewAsync(systemA, ConfigurationChangePreviewSurface.ConnectedSystemDeletion, PreviewStarted.AddHours(-2));
        var expected = await SeedPreviewAsync(systemA, ConfigurationChangePreviewSurface.ConnectedSystemDeletion, PreviewStarted);
        await SeedPreviewAsync(systemB, ConfigurationChangePreviewSurface.ConnectedSystemDeletion, PreviewStarted.AddHours(1));
        await SeedPreviewAsync(systemA, ConfigurationChangePreviewSurface.ConnectedSystemSchema, PreviewStarted.AddHours(2));

        await using var context = NewContext();
        var latest = await new PostgresDataRepository(context).ConfigurationChangePreviews
            .GetLatestConnectedSystemPreviewAsync(ConfigurationChangePreviewSurface.ConnectedSystemDeletion, systemA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(latest?.ActivityId, Is.EqualTo(expected));
            Assert.That(latest?.Activity, Is.Not.Null, "the host reads when it started and how it ended from its Activity");
        }
    }

    [Test]
    public async Task GetLatestConnectedSystemPreviewAsync_NoneForThatSystem_ReturnsNothingAsync()
    {
        var (systemA, systemB) = await SeedTwoSystemsAsync();
        await SeedPreviewAsync(systemB, ConfigurationChangePreviewSurface.ConnectedSystemDeletion, PreviewStarted);

        await using var context = NewContext();
        var latest = await new PostgresDataRepository(context).ConfigurationChangePreviews
            .GetLatestConnectedSystemPreviewAsync(ConfigurationChangePreviewSurface.ConnectedSystemDeletion, systemA);

        Assert.That(latest, Is.Null);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Staleness
    // -----------------------------------------------------------------------------------------------------------------

    [Test]
    public async Task GetPreviewStalenessSinceAsync_NothingHappenedSince_IsCurrentAsync()
    {
        // What happened before the preview started is what it evaluated.
        await SeedActivityAsync(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute, PreviewStarted.AddMinutes(-5));
        await SeedActivityAsync(ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.Update, PreviewStarted.AddMinutes(-5),
            ConfigurationChangeClass.SyncAffecting);

        var staleness = await GetStalenessAsync();

        Assert.That(staleness.IsStale, Is.False);
    }

    [TestCase(ActivityTargetType.ConnectedSystemRunProfile, ActivityTargetOperationType.Execute, TestName = "GetPreviewStalenessSinceAsync_ARunSince_DataChangedAsync")]
    [TestCase(ActivityTargetType.MetaverseObject, ActivityTargetOperationType.Update, TestName = "GetPreviewStalenessSinceAsync_AMetaverseObjectEditedSince_DataChangedAsync")]
    [TestCase(ActivityTargetType.MetaverseObjectHousekeeping, ActivityTargetOperationType.Delete, TestName = "GetPreviewStalenessSinceAsync_HousekeepingSince_DataChangedAsync")]
    public async Task GetPreviewStalenessSinceAsync_DataMovedSince_DataChangedAsync(ActivityTargetType targetType, ActivityTargetOperationType operation)
    {
        var when = PreviewStarted.AddMinutes(10);
        await SeedActivityAsync(targetType, operation, when);

        var staleness = await GetStalenessAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(staleness.DataChangedAt, Is.EqualTo(when).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(staleness.ConfigurationChangedAt, Is.Null);
        }
    }

    [TestCase(ConfigurationChangeClass.SyncAffecting, TestName = "GetPreviewStalenessSinceAsync_SyncAffectingChangeSince_ConfigurationChangedAsync")]
    [TestCase(ConfigurationChangeClass.Destructive, TestName = "GetPreviewStalenessSinceAsync_DestructiveChangeSince_ConfigurationChangedAsync")]
    public async Task GetPreviewStalenessSinceAsync_ClassifiedChangeSince_ConfigurationChangedAsync(ConfigurationChangeClass changeClass)
    {
        var when = PreviewStarted.AddMinutes(10);
        await SeedActivityAsync(ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.Update, when, changeClass);

        var staleness = await GetStalenessAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(staleness.ConfigurationChangedAt, Is.EqualTo(when).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(staleness.DataChangedAt, Is.Null);
        }
    }

    [Test]
    public async Task GetPreviewStalenessSinceAsync_ANewSynchronisationRuleSince_ConfigurationChangedAsync()
    {
        // Creates carry no class (there is no prior state to diff), but a new rule can contribute exactly the value
        // the preview reported as cleared, which is the fix an administrator reading a deletion preview reaches for.
        await SeedActivityAsync(ActivityTargetType.SynchronisationRule, ActivityTargetOperationType.Create, PreviewStarted.AddMinutes(10));

        var staleness = await GetStalenessAsync();

        Assert.That(staleness.ConfigurationChangedAt, Is.Not.Null);
    }

    [Test]
    public async Task GetPreviewStalenessSinceAsync_OnlyCosmeticChangesPreviewsAndReadsSince_IsCurrentAsync()
    {
        await SeedActivityAsync(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Update, PreviewStarted.AddMinutes(10),
            ConfigurationChangeClass.Cosmetic);
        await SeedActivityAsync(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Preview, PreviewStarted.AddMinutes(10));
        await SeedActivityAsync(ActivityTargetType.MetaverseObject, ActivityTargetOperationType.Read, PreviewStarted.AddMinutes(10));
        await SeedActivityAsync(ActivityTargetType.ConnectedSystem, ActivityTargetOperationType.Create, PreviewStarted.AddMinutes(10));

        var staleness = await GetStalenessAsync();

        Assert.That(staleness.IsStale, Is.False);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------------------------------

    private async Task<ConfigurationChangePreviewStaleness> GetStalenessAsync()
    {
        await using var context = NewContext();
        return await new PostgresDataRepository(context).Activity.GetPreviewStalenessSinceAsync(PreviewStarted);
    }

    private async Task SeedActivityAsync(ActivityTargetType targetType, ActivityTargetOperationType operation, DateTime created,
        ConfigurationChangeClass changeClass = ConfigurationChangeClass.NotClassified)
    {
        await using var context = NewContext();
        context.Activities.Add(new Activity
        {
            TargetType = targetType,
            TargetOperationType = operation,
            TargetName = "Seeded",
            Created = created,
            Executed = created,
            ConfigurationChangeClass = changeClass
        });
        await context.SaveChangesAsync();
    }

    private async Task<(int SystemA, int SystemB)> SeedTwoSystemsAsync()
    {
        await using var context = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Staleness Test Connector", BuiltIn = false };
        var systemA = new ConnectedSystem { Name = "Old HR System", ConnectorDefinition = connectorDefinition };
        var systemB = new ConnectedSystem { Name = "New HR System", ConnectorDefinition = connectorDefinition };
        context.ConnectorDefinitions.Add(connectorDefinition);
        context.ConnectedSystems.AddRange(systemA, systemB);
        await context.SaveChangesAsync();
        return (systemA.Id, systemB.Id);
    }

    private async Task<Guid> SeedPreviewAsync(int connectedSystemId, ConfigurationChangePreviewSurface surface, DateTime started)
    {
        await using var context = NewContext();
        var activity = new Activity
        {
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = ActivityTargetOperationType.Preview,
            TargetName = "Seeded",
            ConnectedSystemId = connectedSystemId,
            Created = started,
            Executed = started
        };
        context.Activities.Add(activity);
        await context.SaveChangesAsync();
        context.ConfigurationChangePreviews.Add(new ConfigurationChangePreview { ActivityId = activity.Id, Surface = surface });
        await context.SaveChangesAsync();
        return activity.Id;
    }
}
