// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Core;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of updating a Metaverse Object Type that was loaded on a different DbContext, which is
/// how the portal does it: the page loads the type (with its attributes) inside a <c>using</c> block, and the save
/// creates a fresh context minutes later.
/// </summary>
/// <remarks>
/// This guards a defect that made the Deletion Rules panel unusable on any object type with attributes bound, which is
/// every real one. The repository called <c>DbSet.Update()</c> on the detached graph; <c>Update</c> walks the whole
/// graph, and the attribute bindings are a many-to-many skip navigation whose join rows are not entities the tracker
/// can recognise as existing, so EF inserted a join row per bound attribute and PostgreSQL rejected the batch with
/// <c>duplicate key value violates unique constraint "PK_MetaverseAttributeMetaverseObjectType"</c>. Nothing saved,
/// and the change history recorded an Activity with no snapshot.
///
/// The in-memory provider cannot reproduce it: it enforces no unique constraint on the join table, so the duplicate
/// inserts succeed silently and every unit test passes. Only a real database fails.
///
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other real-database fixtures; ignored when
/// <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class MetaverseObjectTypeUpdateDatabaseTests
{
    private string _connectionString = null!;

    // NoTracking, because that is what JIM.Web configures (JIM.Web/Program.cs, and JimDbContext's own default) and
    // this fixture exists to reproduce what the portal does. A tracking context hides the whole class of fault this
    // guards: a repository read that assumes it got a tracked entity back gets a detached one instead, and every
    // change it then makes is silently discarded at SaveChanges.
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL Metaverse Object Type update tests.");

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
    public async Task UpdateMetaverseObjectTypeAsync_TypeLoadedOnAnotherContext_SavesWithoutDuplicatingAttributeBindingsAsync()
    {
        var (objectTypeId, initiatorId) = await SeedObjectTypeWithBoundAttributesAsync();

        // Load it the way the page does: a context of its own, attributes included, then gone.
        MetaverseObjectType detached;
        await using (var loadContext = NewContext())
        {
            detached = await loadContext.MetaverseObjectTypes
                .Include(t => t.Attributes)
                .SingleAsync(t => t.Id == objectTypeId);
        }
        Assert.That(detached.Attributes, Has.Count.EqualTo(2), "the fixture must carry bound attributes; without them the defect cannot occur");

        detached.DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected;
        detached.DeletionGracePeriod = TimeSpan.FromDays(3);
        detached.DeletionTriggerConnectedSystemIds = [7];

        await using (var saveContext = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(saveContext));
            var initiator = await saveContext.MetaverseObjects.SingleAsync(o => o.Id == initiatorId);
            await jim.Metaverse.UpdateMetaverseObjectTypeAsync(detached, initiator);
        }

        await using var verify = NewContext();
        var reloaded = await verify.MetaverseObjectTypes.SingleAsync(t => t.Id == objectTypeId);
        var bindingCount = await verify.Database
            .SqlQuery<int>($@"SELECT COUNT(*)::int AS ""Value"" FROM ""MetaverseAttributeMetaverseObjectType""")
            .SingleAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded.DeletionRule, Is.EqualTo(MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected));
            Assert.That(reloaded.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromDays(3)));
            Assert.That(reloaded.DeletionTriggerConnectedSystemIds, Is.EqualTo(new List<int> { 7 }));
            Assert.That(bindingCount, Is.EqualTo(2), "the update must leave the attribute bindings alone, not re-insert them");
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_TypeLoadedOnAnotherContext_LeavesAttributeBindingsIntactAsync()
    {
        // The sibling risk of the fix: writing only the type's own columns must not become writing nothing at all, nor
        // silently dropping the bindings the caller never intended to touch.
        var (objectTypeId, initiatorId) = await SeedObjectTypeWithBoundAttributesAsync();

        MetaverseObjectType detached;
        await using (var loadContext = NewContext())
        {
            detached = await loadContext.MetaverseObjectTypes
                .Include(t => t.Attributes)
                .SingleAsync(t => t.Id == objectTypeId);
        }

        detached.Icon = "Devices";

        await using (var saveContext = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(saveContext));
            var initiator = await saveContext.MetaverseObjects.SingleAsync(o => o.Id == initiatorId);
            await jim.Metaverse.UpdateMetaverseObjectTypeAsync(detached, initiator);
        }

        await using var verify = NewContext();
        var reloaded = await verify.MetaverseObjectTypes
            .Include(t => t.Attributes)
            .SingleAsync(t => t.Id == objectTypeId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reloaded.Icon, Is.EqualTo("Devices"));
            Assert.That(reloaded.Attributes.Select(a => a.Name), Is.EquivalentTo(new[] { "serialNumber", "assetTag" }));
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_InitiatorIsOfTheTypeBeingEdited_StillPersistsTheChangeAsync()
    {
        // The portal's shape, which the two tests above do not reproduce: resolving the signed-in administrator loads
        // their Metaverse Object with its Type included, and that Type is usually the very object type being edited
        // (an administrator is a User, and User is the type whose deletion rules are being changed). The object type
        // is therefore already in the change tracker, carrying the database's values, before the update runs.
        var (objectTypeId, initiatorId) = await SeedObjectTypeWithBoundAttributesAsync(initiatorIsOfTheSameType: true);

        MetaverseObjectType detached;
        await using (var loadContext = NewContext())
        {
            detached = await loadContext.MetaverseObjectTypes
                .Include(t => t.Attributes)
                .SingleAsync(t => t.Id == objectTypeId);
        }

        detached.DeletionGracePeriod = TimeSpan.FromDays(5);

        await using (var saveContext = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(saveContext));

            // Exactly what Helpers.GetUserAsync does, and the reason the object type is already tracked.
            var initiator = await saveContext.MetaverseObjects
                .Include(o => o.Type)
                .SingleAsync(o => o.Id == initiatorId);

            await jim.Metaverse.UpdateMetaverseObjectTypeAsync(detached, initiator);
        }

        await using var verify = NewContext();
        var reloaded = await verify.MetaverseObjectTypes.SingleAsync(t => t.Id == objectTypeId);
        Assert.That(reloaded.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromDays(5)),
            "the edit must win over the copy of the object type the initiator lookup happened to put in the tracker");
    }

    [Test]
    public async Task UpdateMetaverseObjectTypeAsync_ContextIsNoTracking_StillPersistsTheChangeAsync()
    {
        // Every context in this fixture is NoTracking, matching JIM.Web. Without an explicit AsTracking on the
        // repository's read, this update writes nothing and reports success: the exact shape that shipped the defect,
        // and the reason TrackedEntityGuard now asserts the contract rather than trusting it.
        var (objectTypeId, initiatorId) = await SeedObjectTypeWithBoundAttributesAsync();

        MetaverseObjectType detached;
        await using (var loadContext = NewContext())
        {
            detached = await loadContext.MetaverseObjectTypes
                .Include(t => t.Attributes)
                .SingleAsync(t => t.Id == objectTypeId);
        }

        detached.DeletionGracePeriod = TimeSpan.FromHours(6);

        await using (var saveContext = NewContext())
        {
            var jim = new JimApplication(new PostgresDataRepository(saveContext));
            var initiator = await saveContext.MetaverseObjects.SingleAsync(o => o.Id == initiatorId);
            await jim.Metaverse.UpdateMetaverseObjectTypeAsync(detached, initiator);
        }

        await using var verify = NewContext();
        var reloaded = await verify.MetaverseObjectTypes.SingleAsync(t => t.Id == objectTypeId);
        Assert.That(reloaded.DeletionGracePeriod, Is.EqualTo(TimeSpan.FromHours(6)),
            "a NoTracking context must not turn the save into a silent no-op");
    }

    private Task<(int ObjectTypeId, Guid InitiatorId)> SeedObjectTypeWithBoundAttributesAsync() =>
        SeedObjectTypeWithBoundAttributesAsync(initiatorIsOfTheSameType: false);

    private async Task<(int ObjectTypeId, Guid InitiatorId)> SeedObjectTypeWithBoundAttributesAsync(bool initiatorIsOfTheSameType)
    {
        await using var seed = NewContext();
        var objectType = new MetaverseObjectType { Name = "Device", PluralName = "Devices", BuiltIn = false };
        objectType.Attributes.Add(new MetaverseAttribute
        {
            Name = "serialNumber", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued
        });
        objectType.Attributes.Add(new MetaverseAttribute
        {
            Name = "assetTag", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued
        });

        // an initiator is required so the update's Activity can be attributed to a security principal
        var userType = new MetaverseObjectType { Name = "User", PluralName = "Users", BuiltIn = true };
        var initiator = new MetaverseObject
        {
            Type = initiatorIsOfTheSameType ? objectType : userType,
            CachedDisplayName = "Test Administrator"
        };

        seed.MetaverseObjectTypes.Add(objectType);
        seed.MetaverseObjectTypes.Add(userType);
        seed.MetaverseObjects.Add(initiator);
        await seed.SaveChangesAsync();
        return (objectType.Id, initiator.Id);
    }
}
