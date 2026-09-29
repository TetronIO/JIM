// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification of the Metaverse-Derived Attribute Flow mark (#1750, "Position 2"):
/// <c>ConnectedSystemObjects.DerivedInputChangePending</c> is set in bulk on the hosting systems' joined objects,
/// selected by delta synchronisation and by full synchronisation's unchanged-object partition, and cleared in bulk
/// once processed. Both writes are raw SQL on the worker's long-lived tracking context, so each must also bring any
/// tracked instance into line with the row it wrote, or a later save of that instance writes the stale value back
/// (<c>src/CLAUDE.md</c>, "Raw SQL Writes Must Fix Up or Detach Tracked Instances"); the in-memory provider cannot
/// see that. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other RequiresPostgres fixtures.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DerivedInputChangePendingDatabaseTests
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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL derived input mark tests.");

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

    private sealed record Seeded(int HrSystemId, int AdSystemId, Guid FirstMvoId, Guid SecondMvoId,
        Guid HrFirstCsoId, Guid AdFirstCsoId, Guid AdSecondCsoId, Guid AdUnjoinedCsoId);

    /// <summary>
    /// Two Connected Systems (HR, AD) and two Metaverse Objects: HR and AD objects joined to the first, an AD object
    /// joined to the second, and an unjoined AD object. Everything is created, and last updated, two hours ago, so a
    /// watermark of one hour ago sees none of it as changed.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext();
        var twoHoursAgo = DateTime.UtcNow.AddHours(-2);

        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var hr = new ConnectedSystem { Name = "HR", ConnectorDefinition = connectorDefinition };
        var ad = new ConnectedSystem { Name = "AD", ConnectorDefinition = connectorDefinition };
        var hrType = new ConnectedSystemObjectType { Name = "person", ConnectedSystem = hr, Selected = true };
        var adType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = ad, Selected = true };
        var hrId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "employeeId", ConnectedSystemObjectType = hrType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var adId = new ConnectedSystemObjectTypeAttribute
        {
            Name = "objectGUID", ConnectedSystemObjectType = adType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        hrType.Attributes.Add(hrId);
        adType.Attributes.Add(adId);

        var mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var firstMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = twoHoursAgo };
        var secondMvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = twoHoursAgo };

        seed.AddRange(connectorDefinition, hr, ad, hrType, adType, mvoType, firstMvo, secondMvo);
        await seed.SaveChangesAsync();

        ConnectedSystemObject Cso(ConnectedSystem system, ConnectedSystemObjectType type, ConnectedSystemObjectTypeAttribute idAttribute, MetaverseObject? mvo, string value)
        {
            var cso = new ConnectedSystemObject
            {
                Id = Guid.NewGuid(), ConnectedSystem = system, Type = type, ExternalIdAttributeId = idAttribute.Id,
                Status = ConnectedSystemObjectStatus.Normal, Created = twoHoursAgo, LastUpdated = twoHoursAgo,
                MetaverseObject = mvo, JoinType = mvo == null ? ConnectedSystemObjectJoinType.NotJoined : ConnectedSystemObjectJoinType.Joined
            };
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Attribute = idAttribute, AttributeId = idAttribute.Id, StringValue = value });
            seed.Add(cso);
            return cso;
        }

        var hrFirst = Cso(hr, hrType, hrId, firstMvo, "E1");
        var adFirst = Cso(ad, adType, adId, firstMvo, "G1");
        var adSecond = Cso(ad, adType, adId, secondMvo, "G2");
        var adUnjoined = Cso(ad, adType, adId, null, "G3");
        await seed.SaveChangesAsync();

        return new Seeded(hr.Id, ad.Id, firstMvo.Id, secondMvo.Id, hrFirst.Id, adFirst.Id, adSecond.Id, adUnjoined.Id);
    }

    private async Task<Dictionary<Guid, bool>> ReadFlagsAsync()
    {
        await using var read = NewContext();
        return await read.ConnectedSystemObjects.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.DerivedInputChangePending);
    }

    private async Task<uint> ReadRowVersionAsync(Guid csoId)
    {
        await using var read = NewContext();
        return await read.ConnectedSystemObjects.AsNoTracking().Where(c => c.Id == csoId).Select(c => c.xmin).SingleAsync();
    }

    [Test]
    public async Task MarkConnectedSystemObjectsDerivedInputChangePendingAsync_MarksOnlyTheNamedSystemsJoinedObjectAsync()
    {
        var s = await SeedAsync();

        int marked;
        await using (var ctx = NewContext())
        {
            var repository = new PostgresDataRepository(ctx);
            marked = await repository.Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId)]);
        }

        var flags = await ReadFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(marked, Is.EqualTo(1), "exactly one object is joined to that Metaverse Object in that system");
            Assert.That(flags[s.AdFirstCsoId], Is.True, "the hosting system's object joined to the changed Metaverse Object is marked");
            Assert.That(flags[s.HrFirstCsoId], Is.False, "the same Metaverse Object's object in another system is not marked");
            Assert.That(flags[s.AdSecondCsoId], Is.False, "another Metaverse Object's object in the hosting system is not marked");
            Assert.That(flags[s.AdUnjoinedCsoId], Is.False, "an unjoined object is never marked");
        }
    }

    [Test]
    public async Task MarkConnectedSystemObjectsDerivedInputChangePendingAsync_AlreadyMarked_CountsNothingNewAsync()
    {
        var s = await SeedAsync();
        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);

        var first = await repository.Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
            [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId), new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId)]);
        var versionAfterFirst = await ReadRowVersionAsync(s.AdFirstCsoId);
        var second = await repository.Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
            [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId)]);
        var versionAfterSecond = await ReadRowVersionAsync(s.AdFirstCsoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first, Is.EqualTo(1), "a duplicate mark in one batch still marks one row");
            Assert.That(second, Is.Zero, "a re-mark is not a new mark, so the summary does not count it");
            Assert.That(versionAfterSecond, Is.Not.EqualTo(versionAfterFirst),
                "a re-mark must still write the row, moving its xmin, or a run that loaded it earlier could clear it");
        }
    }

    /// <summary>
    /// The worker's context lives for the whole run: an object of the hosting system can already be tracked (the
    /// export evaluation cache loads target objects). A raw mark that left the tracked instance at false would be
    /// undone by any later whole-entity save of it.
    /// </summary>
    [Test]
    public async Task MarkConnectedSystemObjectsDerivedInputChangePendingAsync_TrackedInstance_IsFixedUpSoALaterSaveKeepsTheMarkAsync()
    {
        var s = await SeedAsync();

        await using (var ctx = NewContext())
        {
            var tracked = await ctx.ConnectedSystemObjects.AsTracking().SingleAsync(c => c.Id == s.AdFirstCsoId);
            var repository = new PostgresDataRepository(ctx);

            await repository.Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId)]);

            Assert.That(tracked.DerivedInputChangePending, Is.True, "the tracked instance must reflect the row the raw UPDATE wrote");
            Assert.That(ctx.Entry(tracked).Property(c => c.DerivedInputChangePending).IsModified, Is.False,
                "the fix-up records database state, not a pending change");

            ctx.Entry(tracked).State = EntityState.Modified;
            await ctx.SaveChangesAsync();
        }

        var flags = await ReadFlagsAsync();
        Assert.That(flags[s.AdFirstCsoId], Is.True, "a whole-entity save after the mark must not write the stale false back");
    }

    [Test]
    public async Task ClearConnectedSystemObjectDerivedInputChangePendingAsync_ClearsOnlyTheNamedObjectsAndFixesUpTrackedInstancesAsync()
    {
        var s = await SeedAsync();
        await using (var mark = NewContext())
        {
            await new PostgresDataRepository(mark).Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId), new DerivedInputChangeMark(s.SecondMvoId, s.AdSystemId)]);
        }

        int cleared;
        await using (var ctx = NewContext())
        {
            var tracked = await ctx.ConnectedSystemObjects.AsTracking().SingleAsync(c => c.Id == s.AdFirstCsoId);
            Assert.That(tracked.DerivedInputChangePending, Is.True, "precondition: loaded marked");

            cleared = await new PostgresDataRepository(ctx).Sync.ClearConnectedSystemObjectDerivedInputChangePendingAsync(
                [new DerivedInputChangeClear(s.AdFirstCsoId, tracked.xmin)]);

            Assert.That(tracked.DerivedInputChangePending, Is.False, "the tracked instance must reflect the cleared row");
            ctx.Entry(tracked).State = EntityState.Modified;
            await ctx.SaveChangesAsync();
        }

        var flags = await ReadFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(flags[s.AdFirstCsoId], Is.False, "the processed object is cleared, and a later save does not re-mark it");
            Assert.That(flags[s.AdSecondCsoId], Is.True, "an object not processed keeps its mark");
        }
    }

    /// <summary>
    /// Delta synchronisation selects what changed since the watermark; an object marked because another system
    /// changed its derived flow's input has not changed at all, so it is selected by the mark, and counted, and loaded
    /// with its attribute values like any changed object.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsModifiedSinceAsync_UnchangedButMarkedObject_IsSelectedCountedAndLoadedAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await using (var mark = NewContext())
        {
            await new PostgresDataRepository(mark).Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId)]);
        }

        await using var ctx = NewContext();
        var repository = new PostgresDataRepository(ctx);
        var count = await repository.Sync.GetConnectedSystemObjectModifiedSinceCountAsync(s.AdSystemId, watermark);
        var page = await repository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(s.AdSystemId, watermark, 1, 100, count, Guid.Empty);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(count, Is.EqualTo(1), "only the marked object is counted; nothing changed since the watermark");
            Assert.That(page.Results.Select(c => c.Id), Is.EquivalentTo(new[] { s.AdFirstCsoId }));
            Assert.That(page.Results.Single().AttributeValues, Has.Count.EqualTo(1), "a marked object is loaded with its attribute values");
            Assert.That(page.Results.Single().MetaverseObject, Is.Not.Null, "and with its Metaverse Object, like any changed object");
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_WithWatermarkUnchangedButMarkedObject_IsPartitionedAsChangedAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await using (var mark = NewContext())
        {
            await new PostgresDataRepository(mark).Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId)]);
        }

        await using var ctx = NewContext();
        var page = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsAsync(
            s.AdSystemId, 1, 100, knownTotalCount: 3, lastSyncTimestamp: watermark, afterId: Guid.Empty);

        var marked = page.Results.Single(c => c.Id == s.AdFirstCsoId);
        var unmarked = page.Results.Single(c => c.Id == s.AdSecondCsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(marked.IsUnchangedSinceLastSync, Is.False, "the full synchronisation's unchanged skip must not skip a marked object");
            Assert.That(marked.AttributeValues, Has.Count.EqualTo(1), "a marked object's attribute values are loaded");
            Assert.That(unmarked.IsUnchangedSinceLastSync, Is.True, "control: an unchanged, unmarked object is still skipped");
        }
    }

    /// <summary>
    /// The race the row-version guard exists for: the hosting system's delta loads a marked object (reading its
    /// xmin), another system's run then re-marks it because a derived input changed again, and only then does the
    /// hosting run clear the objects it processed. Its evaluation may not have seen that second change, so the mark
    /// must survive the clear and the next run re-evaluate the object.
    /// </summary>
    [Test]
    public async Task ClearConnectedSystemObjectDerivedInputChangePendingAsync_RowReMarkedAfterThePageLoad_StaysMarkedAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await using (var mark = NewContext())
        {
            await new PostgresDataRepository(mark).Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId)]);
        }

        await using var hostingRun = NewContext();
        var hostingRepository = new PostgresDataRepository(hostingRun);
        var page = await hostingRepository.Sync.GetConnectedSystemObjectsModifiedSinceAsync(s.AdSystemId, watermark, 1, 100, null, Guid.Empty);
        var loaded = page.Results.Single(c => c.Id == s.AdFirstCsoId);
        var seenRowVersion = loaded.xmin;
        Assert.That(seenRowVersion, Is.EqualTo(await ReadRowVersionAsync(s.AdFirstCsoId)),
            "precondition: the delta page load reads the row's current xmin in the Connected System Object statement");

        // Another system's synchronisation re-marks the object on its own connection.
        await using (var otherRun = NewContext())
        {
            await new PostgresDataRepository(otherRun).Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId)]);
        }

        var cleared = await hostingRepository.Sync.ClearConnectedSystemObjectDerivedInputChangePendingAsync(
            [new DerivedInputChangeClear(s.AdFirstCsoId, seenRowVersion)]);

        var flags = await ReadFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cleared, Is.Zero, "the row was re-marked after this run read it, so it is not cleared");
            Assert.That(flags[s.AdFirstCsoId], Is.True, "the mark survives, so the next run re-evaluates the object");
            Assert.That(loaded.DerivedInputChangePending, Is.True, "a row the clear skipped is not fixed up to false in memory");
        }
    }

    [Test]
    public async Task ClearConnectedSystemObjectDerivedInputChangePendingAsync_RowUnchangedSinceTheFullSyncPageLoad_IsClearedAsync()
    {
        var s = await SeedAsync();
        var watermark = DateTime.UtcNow.AddHours(-1);
        await using (var mark = NewContext())
        {
            await new PostgresDataRepository(mark).Sync.MarkConnectedSystemObjectsDerivedInputChangePendingAsync(
                [new DerivedInputChangeMark(s.FirstMvoId, s.AdSystemId)]);
        }

        await using var hostingRun = NewContext();
        var hostingRepository = new PostgresDataRepository(hostingRun);
        var page = await hostingRepository.Sync.GetConnectedSystemObjectsAsync(
            s.AdSystemId, 1, 100, knownTotalCount: 3, lastSyncTimestamp: watermark, afterId: Guid.Empty);
        var loaded = page.Results.Single(c => c.Id == s.AdFirstCsoId);

        var cleared = await hostingRepository.Sync.ClearConnectedSystemObjectDerivedInputChangePendingAsync(
            [new DerivedInputChangeClear(s.AdFirstCsoId, loaded.xmin)]);

        var flags = await ReadFlagsAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(loaded.xmin, Is.Not.Zero, "the full synchronisation's scalar page query reads the xmin too");
            Assert.That(cleared, Is.EqualTo(1));
            Assert.That(flags[s.AdFirstCsoId], Is.False, "nobody wrote the row after the load, so the clear applies");
            Assert.That(loaded.DerivedInputChangePending, Is.False, "and the tracked instance is fixed up");
        }
    }
}
