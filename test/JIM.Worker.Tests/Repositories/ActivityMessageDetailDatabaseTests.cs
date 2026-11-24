// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Localisation;
using JIM.Application.Resources;
using JIM.Models.Activities;
using JIM.Models.Staging;
using JIM.Models.Localisation;
using JIM.PostgresData;
using JIM.PostgresData.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL round trip of the localisable message an Activity stores beside its English (#3039): the entity save
/// writes all three detail columns, and each raw statement that writes the English of a progress message clears the detail
/// beside it, so the two can never disagree. Opt-in via JIM_TEST_RESET_*; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ActivityMessageDetailDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Activity message detail tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private static JimMessage Sample(string key, params MessageArgument[] arguments) =>
        JimMessage.Create(ApplicationMessageCatalogues.Activities, key, arguments);

    private static async Task<Guid> SeedActivityAsync(JimDbContext context, Action<Activity>? configure = null)
    {
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            Status = ActivityStatus.InProgress,
            InitiatedByType = ActivityInitiatorType.System
        };
        configure?.Invoke(activity);
        context.Activities.Add(activity);
        await context.SaveChangesAsync();
        return activity.Id;
    }

    [Test]
    public async Task UpdateActivityAsync_AllThreeMessageDetails_AreWrittenBesideTheirEnglishAsync()
    {
        // Arrange
        await using var seed = NewContext();
        var activityId = await SeedActivityAsync(seed);

        // Act: the way ActivityServer completes an Activity, through the repository's entity update.
        await using (var context = NewContext())
        {
            var activity = await context.Activities.SingleAsync(a => a.Id == activityId);
            activity.ErrorMessageDetail = Sample(nameof(ActivityMessages.FailureWithCause), "outer", "inner");
            activity.WarningMessageDetail = Sample(nameof(ActivityMessages.FailureWithCause), "warned", "because");
            activity.MessageDetail = Sample(nameof(ActivityMessages.FailureWithCause), "said", "so");
            await new PostgresDataRepository(context).Activity.UpdateActivityAsync(activity);
        }

        // Assert
        await using var read = NewContext();
        var stored = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.ErrorMessage, Is.EqualTo("outer --> inner"));
            Assert.That(stored.ErrorMessageDetail, Is.EqualTo(Sample(nameof(ActivityMessages.FailureWithCause), "outer", "inner")).Using(MessageComparer));
            Assert.That(stored.WarningMessage, Is.EqualTo("warned --> because"));
            Assert.That(stored.WarningMessageDetail?.ToEnglish(), Is.EqualTo("warned --> because"));
            Assert.That(stored.Message, Is.EqualTo("said --> so"));
            Assert.That(stored.MessageDetail?.ToEnglish(), Is.EqualTo("said --> so"));
        }
    }

    [Test]
    public async Task UpdateActivityMessageAsync_RawStatement_WritesTheEnglishAndClearsTheDetailAsync()
    {
        // Arrange: an Activity whose message carries a detail
        await using var seed = NewContext();
        var activityId = await SeedActivityAsync(seed, a => a.MessageDetail = Sample(nameof(ActivityMessages.FailureWithCause), "a", "b"));

        // Act
        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            await sync.UpdateActivityMessageAsync(new Activity { Id = activityId, Message = "Saving changes" }, "Saving changes");
        }

        // Assert
        await using var read = NewContext();
        var stored = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Message, Is.EqualTo("Saving changes"));
            Assert.That(stored.MessageDetailJson, Is.Null, "A detail left beside different English would show the old text");
        }
    }

    [Test]
    public async Task UpdateActivityMessageAsync_Message_WritesTheEnglishAndTheMessageAsync()
    {
        // Arrange
        await using var seed = NewContext();
        var activityId = await SeedActivityAsync(seed);

        // Act
        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            await sync.UpdateActivityMessageAsync(new Activity { Id = activityId }, Sample(nameof(ActivityMessages.FailureWithCause), "Export", "cancelled"));
        }

        // Assert
        await using var read = NewContext();
        var stored = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Message, Is.EqualTo("Export --> cancelled"));
            Assert.That(stored.MessageDetail?.ToEnglish(), Is.EqualTo("Export --> cancelled"));
        }
    }

    [Test]
    public async Task ProgressWriter_WritesTheEnglishAndClearsTheDetailAsync()
    {
        // Arrange
        await using var seed = NewContext();
        var activityId = await SeedActivityAsync(seed, a => a.MessageDetail = Sample(nameof(ActivityMessages.FailureWithCause), "a", "b"));

        // Act
        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            sync.ConnectionStringForParallelWrites = _connectionString;
            await sync.CreateActivityProgressWriter()!.WriteAsync(new ActivityProgressWrite(activityId, "Reading page 2", 10, 100, []));
        }

        // Assert
        await using var read = NewContext();
        var stored = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Message, Is.EqualTo("Reading page 2"));
            Assert.That(stored.MessageDetailJson, Is.Null);
        }
    }

    [Test]
    public async Task ProgressWriter_WithAMessage_WritesTheEnglishAndTheMessageTogetherAsync()
    {
        // Arrange: an Activity whose message is stale
        await using var seed = NewContext();
        var activityId = await SeedActivityAsync(seed, a => a.Message = "Old");
        var narration = ActivityMessage.Create(nameof(ActivityMessages.ProgressImportingFromSystemPage), "3");

        // Act
        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            sync.ConnectionStringForParallelWrites = _connectionString;
            await sync.CreateActivityProgressWriter()!.WriteAsync(
                new ActivityProgressWrite(activityId, narration.ToEnglish(), 10, 100, [], MessageJson.Serialise(narration)));
        }

        // Assert
        await using var read = NewContext();
        var stored = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Message, Is.EqualTo("Importing objects from Connected System (page 3)"));
            Assert.That(stored.MessageDetail?.ToEnglish(), Is.EqualTo(stored.Message));
            Assert.That(stored.ObjectsProcessed, Is.EqualTo(10));
            Assert.That(stored.ObjectsToProcess, Is.EqualTo(100));
        }
    }

    [Test]
    public async Task UpdateActivityProgressOutOfBandAsync_WritesTheEnglishAndTheMessageTogetherAsync()
    {
        // Arrange
        await using var seed = NewContext();
        var activityId = await SeedActivityAsync(seed);
        var narration = ActivityMessage.Create(nameof(ActivityMessages.ProgressCreatingCsos));

        // Act: a message, then an English-only one, as the run narrates one phase after another
        await using (var context = NewContext())
        {
            var sync = (SyncRepository)new PostgresDataRepository(context).Sync;
            var activity = new Activity { Id = activityId, ObjectsProcessed = 5, ObjectsToProcess = 9, MessageDetail = narration };
            await sync.UpdateActivityProgressOutOfBandAsync(activity);

            await using var read = NewContext();
            var first = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.Message, Is.EqualTo("Creating Connected System Objects"));
                Assert.That(first.MessageDetail?.ToEnglish(), Is.EqualTo(first.Message));
                Assert.That(first.ObjectsProcessed, Is.EqualTo(5));
            }

            activity.Message = "From a Connector";
            activity.MessageDetailJson = null;
            await sync.UpdateActivityProgressOutOfBandAsync(activity);
        }

        // Assert
        await using var readAgain = NewContext();
        var stored = await readAgain.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Message, Is.EqualTo("From a Connector"));
            Assert.That(stored.MessageDetailJson, Is.Null);
        }
    }

    [Test]
    public async Task AddToScimRequestSummaryAsync_RawUpdate_WritesTheWarningAndItsMessageTogetherAsync()
    {
        // Arrange: a summary Activity for a window, as the first tally of the window creates it
        await using var seed = NewContext();
        var system = new ConnectedSystem { Name = $"Scim-{Guid.NewGuid():N}", ConnectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true } };
        seed.ConnectedSystems.Add(system);
        await seed.SaveChangesAsync();
        var keyId = Guid.NewGuid();
        var window = new DateTime(2026, 11, 24, 10, 0, 0, DateTimeKind.Utc);
        var activityId = await SeedActivityAsync(seed, a =>
        {
            a.ConnectedSystemId = system.Id;
            a.InitiatedById = keyId;
            a.AggregationWindowStart = window;
            a.Status = ActivityStatus.Complete;
        });
        await seed.Database.ExecuteSqlRawAsync("UPDATE \"Activities\" SET \"TargetType\" = {0} WHERE \"Id\" = {1}", (int)ActivityTargetType.ScimRequests, activityId);
        var tally = new JIM.Models.Scim.ScimRequestTally
        {
            ConnectedSystemId = system.Id, ApiKeyId = keyId, ApiKeyName = "Key", WindowStart = window, FirstSeen = window, LastSeen = window, Rejected = 1, LatestRejection = "Unknown schema"
        };

        // Act
        bool updated;
        await using (var context = NewContext())
            updated = await new PostgresDataRepository(context).Activity.AddToScimRequestSummaryAsync(
                tally, ActivityMessage.Create(nameof(ActivityMessages.ScimLatestRejection), "Unknown schema"));

        // Assert
        await using var read = NewContext();
        var stored = await read.Activities.AsNoTracking().SingleAsync(a => a.Id == activityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated, Is.True);
            Assert.That(stored.WarningMessage, Is.EqualTo("Latest rejection: Unknown schema"));
            Assert.That(stored.WarningMessageDetail?.ToEnglish(), Is.EqualTo(stored.WarningMessage));
            Assert.That(stored.Status, Is.EqualTo(ActivityStatus.CompleteWithWarning));
            Assert.That(stored.TotalErrors, Is.EqualTo(1));
        }
    }

    /// <summary>Two messages are equal when they say the same thing; the record's list arguments compare by reference.</summary>
    private static readonly IEqualityComparer<JimMessage?> MessageComparer = new EnglishComparer();

    private sealed class EnglishComparer : IEqualityComparer<JimMessage?>
    {
        public bool Equals(JimMessage? x, JimMessage? y) => x?.ToEnglish() == y?.ToEnglish();

        public int GetHashCode(JimMessage? obj) => obj?.ToEnglish().GetHashCode() ?? 0;
    }
}
