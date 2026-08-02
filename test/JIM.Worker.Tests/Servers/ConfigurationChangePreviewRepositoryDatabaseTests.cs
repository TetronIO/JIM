// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of <see cref="ConfigurationChangePreviewRepository"/>. Everything here is a
/// question the in-memory provider cannot answer:
///
/// The preview row's key is its Activity's key, so an insert that walks the entity graph would reach the already
/// persisted Activity and try to insert it again, failing on a duplicate key against a table the caller never
/// touched. In memory there are no key constraints, so that bug passes silently.
///
/// Summary groups and their delta rows are inserted together specifically so EF fills in the group foreign key
/// that could not be known before the group had an id; and the whole result set is written in one unit of work, so
/// a preview cannot end up with counts that disagree with the rows beneath them.
///
/// The context here is configured <c>NoTracking</c>, matching JIM.Web, because that is where previews are started
/// from: a repository method that only works against a tracking context works perfectly in the worker and does
/// nothing at all in the portal, reporting success either way.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationChangePreviewRepositoryDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL configuration change preview repository tests.");

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

    [Test]
    public async Task CreatePreviewAsync_ForAnExistingActivity_InsertsOnlyThePreviewAsync()
    {
        var activityId = await SeedActivityAsync();

        await using (var context = NewContext())
        {
            var repository = new ConfigurationChangePreviewRepository(context);
            await repository.CreatePreviewAsync(new ConfigurationChangePreview
            {
                ActivityId = activityId,
                Surface = ConfigurationChangePreviewSurface.MetaverseObjectType,
                ProposedConfigurationSnapshot = """{"deletionRule":"AllTriggersLost"}"""
            });
        }

        await using var verify = NewContext();
        Assert.Multiple(async () =>
        {
            Assert.That(await verify.ConfigurationChangePreviews.CountAsync(), Is.EqualTo(1));
            Assert.That(await verify.Activities.CountAsync(), Is.EqualTo(1),
                "inserting a preview must not re-insert the Activity it belongs to");
        });
    }

    [Test]
    public async Task UpdatePreviewAsync_FromANoTrackingContext_PersistsStageProgressAsync()
    {
        var activityId = await SeedActivityAsync();
        await using (var create = NewContext())
        {
            await new ConfigurationChangePreviewRepository(create).CreatePreviewAsync(new ConfigurationChangePreview
            {
                ActivityId = activityId,
                Surface = ConfigurationChangePreviewSurface.MetaverseObjectType
            });
        }

        // Load in one context and save from another, exactly as the portal does across two units of work.
        await using (var update = NewContext())
        {
            var repository = new ConfigurationChangePreviewRepository(update);
            var preview = await repository.GetPreviewAsync(activityId);
            preview!.SummaryStatus = ConfigurationChangePreviewStageStatus.Complete;
            preview.DeltaPersistence = ConfigurationChangePreviewDeltaPersistence.Capped;
            preview.EstimatedDeltaRows = 96_240L;
            await repository.UpdatePreviewAsync(preview);
        }

        await using var verify = NewContext();
        var reloaded = await verify.ConfigurationChangePreviews.SingleAsync(p => p.ActivityId == activityId);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.SummaryStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete));
            Assert.That(reloaded.DeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Capped));
            Assert.That(reloaded.EstimatedDeltaRows, Is.EqualTo(96_240L));
        });
    }

    [Test]
    public async Task CreatePreviewResultsAsync_GroupsWithDeltas_LinksEachDeltaToItsGroupAsync()
    {
        var activityId = await SeedPreviewAsync();

        await using (var context = NewContext())
        {
            await new ConfigurationChangePreviewRepository(context).CreatePreviewResultsAsync(
            [
                NewGroup(activityId, ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, 3, "Ada", "Grace", "Alan"),
                NewGroup(activityId, ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible, 1, "Katherine")
            ]);
        }

        await using var verify = NewContext();
        var groups = await verify.ConfigurationChangePreviewGroups.Where(g => g.ActivityId == activityId).ToListAsync();
        var deltas = await verify.ConfigurationChangePreviewDeltas.Where(d => d.ActivityId == activityId).ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(groups, Has.Count.EqualTo(2));
            Assert.That(deltas, Has.Count.EqualTo(4));
            Assert.That(deltas.Select(d => d.GroupId).Distinct().Count(), Is.EqualTo(2),
                "each delta must land under the group whose count it contributed to");
            Assert.That(deltas.All(d => groups.Any(g => g.Id == d.GroupId)), Is.True);
        });
    }

    [Test]
    public async Task GetPreviewDeltasAsync_PagedByGroup_ReturnsStableNonOverlappingPagesAsync()
    {
        var activityId = await SeedPreviewAsync();
        var names = Enumerable.Range(0, 25).Select(i => $"User {i:D2}").ToArray();

        Guid groupId;
        await using (var context = NewContext())
        {
            var group = NewGroup(activityId, ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope, 25, names);
            await new ConfigurationChangePreviewRepository(context).CreatePreviewResultsAsync([group]);
            groupId = group.Id;
        }

        await using var read = NewContext();
        var repository = new ConfigurationChangePreviewRepository(read);
        var firstPage = await repository.GetPreviewDeltasAsync(activityId, groupId, 1, 10);
        var secondPage = await repository.GetPreviewDeltasAsync(activityId, groupId, 2, 10);
        var firstPageAgain = await repository.GetPreviewDeltasAsync(activityId, groupId, 1, 10);

        Assert.Multiple(() =>
        {
            Assert.That(firstPage.TotalResults, Is.EqualTo(25));
            Assert.That(firstPage.Results, Has.Count.EqualTo(10));
            Assert.That(firstPage.Results.Select(d => d.Id).Intersect(secondPage.Results.Select(d => d.Id)), Is.Empty,
                "a drill-down that repeats rows between pages is also omitting others");
            Assert.That(firstPageAgain.Results.Select(d => d.Id), Is.EqualTo(firstPage.Results.Select(d => d.Id)),
                "the same page must return the same rows every time it is asked for");
        });
    }

    private static ConfigurationChangePreviewGroup NewGroup(Guid activityId,
        ActivityRunProfileExecutionItemSyncOutcomeType transition, int objectCount, params string[] deltaNames) => new()
        {
            ActivityId = activityId,
            TransitionType = transition,
            MetaverseObjectTypeId = 11,
            MetaverseObjectTypeName = "User",
            ObjectCount = objectCount,
            DeltasSampled = objectCount > deltaNames.Length,
            Deltas = [.. deltaNames.Select(n => new ConfigurationChangePreviewDelta
            {
                ActivityId = activityId,
                TransitionType = transition,
                MetaverseObjectId = Guid.CreateVersion7(),
                ObjectDisplayName = n,
                ObjectTypeName = "User"
            })]
        };

    private async Task<Guid> SeedActivityAsync()
    {
        await using var context = NewContext();
        var activity = new Activity
        {
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetOperationType = ActivityTargetOperationType.Preview,
            TargetName = "User",
            MetaverseObjectTypeId = 11
        };
        context.Activities.Add(activity);
        await context.SaveChangesAsync();
        return activity.Id;
    }

    private async Task<Guid> SeedPreviewAsync()
    {
        var activityId = await SeedActivityAsync();
        await using var context = NewContext();
        await new ConfigurationChangePreviewRepository(context).CreatePreviewAsync(new ConfigurationChangePreview
        {
            ActivityId = activityId,
            Surface = ConfigurationChangePreviewSurface.MetaverseObjectType
        });
        return activityId;
    }
}
