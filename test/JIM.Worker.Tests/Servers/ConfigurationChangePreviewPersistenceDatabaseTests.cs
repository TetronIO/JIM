// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Preview;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using System.Text.Json.Nodes;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of the three configuration change preview tables: that a fully populated preview
/// round-trips every field, and that the whole result set disappears when its Activity does.
///
/// The cascade is the load-bearing part. Preview deltas hold attribute values, which are personal data, and the
/// entire retention story for them is "they hang off the preview's Activity, so the existing history-retention
/// housekeeping removes them with it". If that cascade is not actually configured, nothing fails and nothing is
/// logged; the rows simply stay for ever, past the retention period a customer was told applies to them. The
/// in-memory provider cannot answer the question either way, because it enforces no referential actions at all.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures; ignored
/// when <c>JIM_TEST_RESET_DB</c> is absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ConfigurationChangePreviewPersistenceDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL configuration change preview tests.");

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
    public async Task Preview_FullyPopulated_RoundTripsEveryFieldAsync()
    {
        var activityId = await SeedPreviewAsync();

        await using var verify = NewContext();
        var preview = await verify.ConfigurationChangePreviews.SingleAsync(p => p.ActivityId == activityId);

        Assert.Multiple(() =>
        {
            Assert.That(preview.Surface, Is.EqualTo(ConfigurationChangePreviewSurface.MetaverseObjectType));
            // Compared as a document, not as a string: the column is jsonb, and PostgreSQL normalises what it
            // stores (whitespace, key order, duplicate keys). Nothing reads these snapshots byte-for-byte, but a
            // test that asserted on the exact text would fail for a reason that says nothing about correctness.
            Assert.That(JsonNode.DeepEquals(
                    JsonNode.Parse(preview.ProposedConfigurationSnapshot!),
                    JsonNode.Parse("{\"deletionRule\":\"WhenLastConnectorDisconnected\"}")),
                Is.True);
            Assert.That(preview.ValidationFindings, Does.Contain("No trigger systems are selected."));
            Assert.That(preview.ImpactCounts, Does.Contain("4812"));
            Assert.That(preview.ValidationStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete));
            Assert.That(preview.ImpactCountsStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete));
            Assert.That(preview.SummaryStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.Complete));
            Assert.That(preview.DeltasStatus, Is.EqualTo(ConfigurationChangePreviewStageStatus.NotApplicable));
            Assert.That(preview.ValidationStarted, Is.Not.Null);
            Assert.That(preview.DeltasCompleted, Is.Null);
            Assert.That(preview.EstimatedAffectedObjects, Is.EqualTo(4_812));
            Assert.That(preview.EstimatedDeltaRows, Is.EqualTo(9_624L));
            Assert.That(preview.DeltaPersistence, Is.EqualTo(ConfigurationChangePreviewDeltaPersistence.Capped));
            Assert.That(preview.DispatchedToWorker, Is.True);
            Assert.That(preview.StalenessBaseline, Is.Not.Null);
        });
    }

    [Test]
    public async Task PreviewGroupAndDelta_FullyPopulated_RoundTripEveryFieldAsync()
    {
        var activityId = await SeedPreviewAsync();

        await using var verify = NewContext();
        var group = await verify.ConfigurationChangePreviewGroups.SingleAsync(g => g.ActivityId == activityId);
        var delta = await verify.ConfigurationChangePreviewDeltas.SingleAsync(d => d.ActivityId == activityId);

        Assert.Multiple(() =>
        {
            Assert.That(group.TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible));
            Assert.That(group.MetaverseObjectTypeId, Is.EqualTo(11));
            Assert.That(group.MetaverseObjectTypeName, Is.EqualTo("User"));
            Assert.That(group.ConnectedSystemId, Is.EqualTo(3));
            Assert.That(group.ConnectedSystemName, Is.EqualTo("Corporate Directory"));
            Assert.That(group.AttributeName, Is.EqualTo("Email"));
            Assert.That(group.OldValue, Is.EqualTo("@old.example"));
            Assert.That(group.NewValue, Is.EqualTo("@new.example"));
            Assert.That(group.PatternKey, Is.EqualTo("email-domain-changed"));
            Assert.That(group.ObjectCount, Is.EqualTo(4_812));
            Assert.That(group.DeltasSampled, Is.True);

            Assert.That(delta.GroupId, Is.EqualTo(group.Id));
            Assert.That(delta.TransitionType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible));
            Assert.That(delta.MetaverseObjectId, Is.Not.Null);
            Assert.That(delta.ConnectedSystemObjectId, Is.Not.Null);
            Assert.That(delta.ConnectedSystemId, Is.EqualTo(3));
            Assert.That(delta.ObjectDisplayName, Is.EqualTo("Ada Lovelace"));
            Assert.That(delta.ObjectTypeName, Is.EqualTo("User"));
            Assert.That(delta.AttributeName, Is.EqualTo("Email"));
            Assert.That(delta.OldValue, Is.EqualTo("ada@old.example"));
            Assert.That(delta.NewValue, Is.EqualTo("ada@new.example"));
            Assert.That(delta.PatternKey, Is.EqualTo("email-domain-changed"));
        });
    }

    [Test]
    public async Task DeletingTheActivity_RemovesThePreviewAndAllItsResultsAsync()
    {
        // This is the whole retention story for preview data: nothing removes these rows except the Activity going.
        var activityId = await SeedPreviewAsync();

        await using (var deleteContext = NewContext())
        {
            var activity = await deleteContext.Activities.AsTracking().SingleAsync(a => a.Id == activityId);
            deleteContext.Activities.Remove(activity);
            await deleteContext.SaveChangesAsync();
        }

        await using var verify = NewContext();
        Assert.Multiple(async () =>
        {
            Assert.That(await verify.ConfigurationChangePreviews.CountAsync(), Is.Zero);
            Assert.That(await verify.ConfigurationChangePreviewGroups.CountAsync(), Is.Zero,
                "summary groups must not outlive the Activity that owns them");
            Assert.That(await verify.ConfigurationChangePreviewDeltas.CountAsync(), Is.Zero,
                "delta rows hold attribute values; surviving their Activity means surviving their retention period");
        });
    }

    [Test]
    public async Task ApplyActivity_RecordsThePreviewItWasInformedByAsync()
    {
        var previewActivityId = await SeedPreviewAsync();

        Guid applyActivityId;
        await using (var context = NewContext())
        {
            var apply = new Activity
            {
                TargetType = ActivityTargetType.MetaverseObjectType,
                TargetName = "User",
                PreviewActivityId = previewActivityId
            };
            context.Activities.Add(apply);
            await context.SaveChangesAsync();
            applyActivityId = apply.Id;
        }

        await using var verify = NewContext();
        var reloaded = await verify.Activities.SingleAsync(a => a.Id == applyActivityId);

        Assert.That(reloaded.PreviewActivityId, Is.EqualTo(previewActivityId));
    }

    /// <summary>
    /// A preview Activity carrying one fully populated preview, one group, and one delta. Every nullable column is
    /// given a value: a round-trip test whose fixture leaves columns null proves nothing about them.
    /// </summary>
    private async Task<Guid> SeedPreviewAsync()
    {
        await using var context = NewContext();

        var activity = new Activity
        {
            TargetType = ActivityTargetType.MetaverseObjectType,
            TargetName = "User",
            MetaverseObjectTypeId = 11
        };
        context.Activities.Add(activity);
        await context.SaveChangesAsync();

        var now = DateTime.UtcNow;
        var preview = new ConfigurationChangePreview
        {
            ActivityId = activity.Id,
            Surface = ConfigurationChangePreviewSurface.MetaverseObjectType,
            ProposedConfigurationSnapshot = "{\"deletionRule\":\"WhenLastConnectorDisconnected\"}",
            ValidationFindings = "[{\"Severity\":1,\"Message\":\"No trigger systems are selected.\",\"PropertyName\":\"DeletionTriggers\"}]",
            ImpactCounts = "[{\"TransitionType\":22,\"ObjectCount\":4812,\"ConnectedSystemId\":3,\"MetaverseObjectTypeId\":11}]",
            ValidationStatus = ConfigurationChangePreviewStageStatus.Complete,
            ValidationStarted = now,
            ValidationCompleted = now.AddSeconds(1),
            ImpactCountsStatus = ConfigurationChangePreviewStageStatus.Complete,
            ImpactCountsStarted = now.AddSeconds(1),
            ImpactCountsCompleted = now.AddSeconds(4),
            SummaryStatus = ConfigurationChangePreviewStageStatus.Complete,
            SummaryStarted = now.AddSeconds(4),
            SummaryCompleted = now.AddSeconds(30),
            DeltasStatus = ConfigurationChangePreviewStageStatus.NotApplicable,
            EstimatedAffectedObjects = 4_812,
            EstimatedDeltaRows = 9_624L,
            DeltaPersistence = ConfigurationChangePreviewDeltaPersistence.Capped,
            DispatchedToWorker = true,
            StalenessBaseline = now.AddHours(-2)
        };
        context.ConfigurationChangePreviews.Add(preview);
        await context.SaveChangesAsync();

        var group = new ConfigurationChangePreviewGroup
        {
            ActivityId = activity.Id,
            TransitionType = ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
            MetaverseObjectTypeId = 11,
            MetaverseObjectTypeName = "User",
            ConnectedSystemId = 3,
            ConnectedSystemName = "Corporate Directory",
            AttributeName = "Email",
            OldValue = "@old.example",
            NewValue = "@new.example",
            PatternKey = "email-domain-changed",
            ObjectCount = 4_812,
            DeltasSampled = true
        };
        context.ConfigurationChangePreviewGroups.Add(group);
        await context.SaveChangesAsync();

        context.ConfigurationChangePreviewDeltas.Add(new ConfigurationChangePreviewDelta
        {
            ActivityId = activity.Id,
            GroupId = group.Id,
            TransitionType = ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible,
            MetaverseObjectId = Guid.CreateVersion7(),
            ConnectedSystemObjectId = Guid.CreateVersion7(),
            ConnectedSystemId = 3,
            ObjectDisplayName = "Ada Lovelace",
            ObjectTypeName = "User",
            AttributeName = "Email",
            OldValue = "ada@old.example",
            NewValue = "ada@new.example",
            PatternKey = "email-domain-changed"
        });
        await context.SaveChangesAsync();

        return activity.Id;
    }
}
