// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL round trip of the manual removal of Accumulate values (#3059, Phase 3):
/// <c>MetaverseRepository.RemoveMetaverseObjectAttributeValuesAsync</c> deletes the named value rows, writes the object's
/// own columns under its row version, and inserts the change record it carries, all in one transaction. The portal loads
/// the object on one context and saves on another, with no tracking, so every context here is <c>NoTracking</c>, as in
/// JIM.Web. The in-memory provider runs neither <c>ExecuteDelete</c> nor a row-version guard.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class AccumulatedValueRemovalDatabaseTests
{
    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Accumulate value removal tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    private JimDbContext NewContext() => new(new DbContextOptionsBuilder<JimDbContext>()
        .UseNpgsql(_connectionString)
        .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
        .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
        .Options);

    private sealed record Seeded(Guid MetaverseObjectId, Guid KeptValueId, Guid RemovedValueId);

    private static async Task<Seeded> SeedAsync(JimDbContext ctx)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var type = new MetaverseObjectType { Name = $"rm-person-{suffix}", PluralName = $"rm-people-{suffix}" };
        var auditLog = new MetaverseAttribute
        {
            Name = $"rm-log-{suffix}", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued,
            ContributionMode = AttributeContributionMode.Accumulate
        };
        ctx.MetaverseObjectTypes.Add(type);
        ctx.MetaverseAttributes.Add(auditLog);
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = type, Created = DateTime.UtcNow };
        ctx.MetaverseObjects.Add(mvo);
        await ctx.SaveChangesAsync();

        var kept = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, AttributeId = auditLog.Id, StringValue = "John Smith" };
        var removed = new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), MetaverseObject = mvo, AttributeId = auditLog.Id, StringValue = "J Smith" };
        ctx.MetaverseObjectAttributeValues.AddRange(kept, removed);
        await ctx.SaveChangesAsync();
        return new Seeded(mvo.Id, kept.Id, removed.Id);
    }

    private async Task<(MetaverseObject Mvo, MetaverseObjectAttributeValue Removed)> LoadAndStageRemovalAsync(Seeded seeded)
    {
        await using var read = NewContext();
        var mvo = (await new PostgresDataRepository(read).Metaverse.GetMetaverseObjectAsync(seeded.MetaverseObjectId))!;
        var removed = mvo.AttributeValues.Single(av => av.Id == seeded.RemovedValueId);
        mvo.AttributeValues.Remove(removed);
        mvo.ScopeReviewPending = true;
        mvo.UnsynchronisedChangesJson = """{"removals":[{"stringValue":"J Smith"}]}""";
        var change = new MetaverseObjectChange
        {
            MetaverseObjectId = mvo.Id,
            ChangeType = ObjectChangeType.Updated,
            ChangeTime = DateTime.UtcNow,
            InitiatedByType = ActivityInitiatorType.User,
            InitiatedByName = "Administrator",
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.User
        };
        change.AddAttributeValueChange(removed, ValueChangeType.Remove);
        mvo.Changes.Add(change);
        return (mvo, removed);
    }

    [Test]
    public async Task RemoveMetaverseObjectAttributeValuesAsync_DeletesTheValueAndSavesTheObjectAndItsChangeRecordAsync()
    {
        Seeded seeded;
        await using (var seed = NewContext())
            seeded = await SeedAsync(seed);
        var (mvo, _) = await LoadAndStageRemovalAsync(seeded);

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Metaverse.RemoveMetaverseObjectAttributeValuesAsync(mvo, [seeded.RemovedValueId]);

        await using var check = NewContext();
        var values = await check.MetaverseObjectAttributeValues
            .Where(av => EF.Property<Guid>(av, "MetaverseObjectId") == seeded.MetaverseObjectId)
            .Select(av => av.Id).ToListAsync();
        var stored = await check.MetaverseObjects.SingleAsync(m => m.Id == seeded.MetaverseObjectId);
        var change = await check.MetaverseObjectChanges
            .Include(c => c.AttributeChanges).ThenInclude(a => a.ValueChanges)
            .SingleAsync(c => c.MetaverseObjectId == seeded.MetaverseObjectId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Is.EqualTo(new[] { seeded.KeptValueId }));
            Assert.That(stored.ScopeReviewPending, Is.True);
            Assert.That(stored.UnsynchronisedChangesJson, Does.Contain("J Smith"));
            Assert.That(change.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            var valueChange = change.AttributeChanges.Single().ValueChanges.Single();
            Assert.That(valueChange.ValueChangeType, Is.EqualTo(ValueChangeType.Remove));
            Assert.That(valueChange.StringValue, Is.EqualTo("J Smith"));
        }
    }

    [Test]
    public async Task RemoveMetaverseObjectAttributeValuesAsync_ObjectChangedSinceRead_RemovesNothingAndThrowsAsync()
    {
        Seeded seeded;
        await using (var seed = NewContext())
            seeded = await SeedAsync(seed);
        var (mvo, _) = await LoadAndStageRemovalAsync(seeded);

        // A synchronisation writes the object after the portal read it.
        await using (var sync = NewContext())
            await sync.Database.ExecuteSqlRawAsync(@"UPDATE ""MetaverseObjects"" SET ""LastUpdated"" = now() WHERE ""Id"" = {0}", seeded.MetaverseObjectId);

        await using (var write = NewContext())
        {
            Assert.That(() => new PostgresDataRepository(write).Metaverse.RemoveMetaverseObjectAttributeValuesAsync(mvo, [seeded.RemovedValueId]),
                Throws.InstanceOf<DbUpdateConcurrencyException>());
        }

        await using var check = NewContext();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(await check.MetaverseObjectAttributeValues.CountAsync(av => av.Id == seeded.RemovedValueId), Is.EqualTo(1), "rolled back");
            Assert.That(await check.MetaverseObjectChanges.CountAsync(c => c.MetaverseObjectId == seeded.MetaverseObjectId), Is.Zero);
            Assert.That((await check.MetaverseObjects.SingleAsync(m => m.Id == seeded.MetaverseObjectId)).ScopeReviewPending, Is.False);
        }
    }
    /// <summary>
    /// The inspector's history links a removal to its own Activity: a change made outside synchronisation has one and no
    /// Run Profile Execution Item, which the history read once looked for alone.
    /// </summary>
    [Test]
    public async Task GetAttributeHistoryRawEntriesAsync_RemovalByHand_CarriesItsActivityAsync()
    {
        Seeded seeded;
        await using (var seed = NewContext())
            seeded = await SeedAsync(seed);
        var activity = new Activity { TargetType = ActivityTargetType.MetaverseObject, TargetOperationType = ActivityTargetOperationType.Update, TargetName = "rm" };
        await using (var seed = NewContext())
        {
            seed.Activities.Add(activity);
            await seed.SaveChangesAsync();
        }
        var (mvo, removed) = await LoadAndStageRemovalAsync(seeded);
        mvo.Changes.Single().ActivityId = activity.Id;
        await using (var write = NewContext())
            await new PostgresDataRepository(write).Metaverse.RemoveMetaverseObjectAttributeValuesAsync(mvo, [seeded.RemovedValueId]);

        await using var read = NewContext();
        var entries = await new PostgresDataRepository(read).Metaverse.GetAttributeHistoryRawEntriesAsync(seeded.MetaverseObjectId, removed.AttributeId, 10);

        Assert.That(entries.Single().Change.ActivityId, Is.EqualTo(activity.Id));
    }
}

