// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for a large group's values loading into their collections on the Worker's tracking context
/// (#2598, Phase 4). The value collections are a <see cref="JIM.Models.Utility.ReferenceIndexedList{T}"/> so that EF
/// Core's check for whether a collection already holds an entity is answered by the collection rather than by a scan,
/// and that answer is what keeps a value from being added twice when a second load, or fix-up, meets a collection
/// already filled. These tests load each collection twice on one context and require every value once, tracked once,
/// with nothing to save. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class LargeValueCollectionLoadDatabaseTests
{
    // Far past the size at which the collection starts answering from its index.
    private const int Members = 2_000;

    private string _connectionString = null!;

    private JimDbContext NewTrackingContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        var context = new JimDbContext(options);
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;
        return context;
    }

    /// <summary>
    /// A tracking context with change detection off, as the Worker holds it while a page's objects are loaded.
    /// </summary>
    private JimDbContext NewPageContext()
    {
        var context = NewTrackingContext();
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        return context;
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL large value collection tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewTrackingContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        await PostgresTestDatabase.ResetAsync(_connectionString);
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_LargeGroupLoadedTwice_HoldsEachValueOnceUnchangedAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewPageContext();
        var repository = new PostgresDataRepository(ctx);
        var first = await repository.Sync.GetConnectedSystemObjectsAsync(seeded.SystemId, 1, 500);
        var second = await repository.Sync.GetConnectedSystemObjectsAsync(seeded.SystemId, 1, 500);

        var group = second.Results.Single(c => c.Id == seeded.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Results.Single(c => c.Id == seeded.CsoId), Is.SameAs(group));
            AssertEachValueOnce(group.AttributeValues, v => v.Id, seeded.CsoValueIds);
            AssertTrackedOnceAndUnchanged(ctx.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>(), [.. seeded.CsoValueIds, seeded.OtherCsoValueId]);
            AssertNoValueChangedOnDetection(ctx.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>());
        }
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_ValuesAlreadyTracked_FixUpHoldsEachValueOnceAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewPageContext();
        // The values first, without their owner: the page load then meets them already tracked and attaches them to
        // the owner through fix-up as well as through its include.
        var values = await ctx.ConnectedSystemObjectAttributeValues
            .Where(av => EF.Property<Guid>(av, "ConnectedSystemObjectId") == seeded.CsoId)
            .ToListAsync();
        var page = await new PostgresDataRepository(ctx).Sync.GetConnectedSystemObjectsAsync(seeded.SystemId, 1, 500);

        var group = page.Results.Single(c => c.Id == seeded.CsoId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Has.Count.EqualTo(seeded.CsoValueIds.Count));
            AssertEachValueOnce(group.AttributeValues, v => v.Id, seeded.CsoValueIds);
            Assert.That(values.All(group.AttributeValues.Contains), Is.True, "the owner holds the instances already tracked");
            AssertNoValueChangedOnDetection(ctx.ChangeTracker.Entries<ConnectedSystemObjectAttributeValue>());
        }
    }

    [Test]
    public async Task GetPendingExportLightweightByConnectedSystemObjectIdAsync_LargeChangeSetLoadedTwice_HoldsEachChangeOnceAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewTrackingContext();
        var repository = new PostgresDataRepository(ctx);
        var first = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);
        var second = await repository.Sync.GetPendingExportLightweightByConnectedSystemObjectIdAsync(seeded.CsoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.SameAs(first));
            AssertEachValueOnce(second!.AttributeValueChanges, c => c.Id, seeded.PendingExportChangeIds);
            AssertTrackedOnceAndUnchanged(ctx.ChangeTracker.Entries<PendingExportAttributeValueChange>(), seeded.PendingExportChangeIds);
        }
        await AssertNothingToSaveAsync(ctx);
    }

    [Test]
    public async Task GetMetaverseObjectAsync_LargeGroupLoadedTwice_HoldsEachValueOnceWithNothingToSaveAsync()
    {
        var seeded = await SeedAsync();

        await using var ctx = NewTrackingContext();
        var repository = new PostgresDataRepository(ctx);
        var first = await repository.Metaverse.GetMetaverseObjectAsync(seeded.MvoId);
        var second = await repository.Metaverse.GetMetaverseObjectAsync(seeded.MvoId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.SameAs(first));
            AssertEachValueOnce(second!.AttributeValues, v => v.Id, seeded.MvoValueIds);
            AssertTrackedOnceAndUnchanged(ctx.ChangeTracker.Entries<MetaverseObjectAttributeValue>(), seeded.MvoValueIds);
        }
        await AssertNothingToSaveAsync(ctx);
    }

    private static void AssertEachValueOnce<T>(IReadOnlyCollection<T> collection, Func<T, Guid> id, IReadOnlyCollection<Guid> expectedIds) where T : class
    {
        Assert.That(collection.Select(id), Is.EquivalentTo(expectedIds), $"each {typeof(T).Name} once, and no other");
        Assert.That(collection.Distinct(ReferenceEqualityComparer.Instance).Count(), Is.EqualTo(collection.Count), $"no {typeof(T).Name} instance held twice");
    }

    private static void AssertTrackedOnceAndUnchanged<T>(IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<T>> entries, IReadOnlyCollection<Guid> expectedIds) where T : class
    {
        var entryList = entries.ToList();
        Assert.That(entryList.Select(e => (Guid)e.Property("Id").CurrentValue!), Is.EquivalentTo(expectedIds), $"each {typeof(T).Name} tracked once");
        Assert.That(entryList.Select(e => e.State).Distinct(), Is.EqualTo(new[] { EntityState.Unchanged }));
    }

    /// <summary>
    /// Detection over the values alone: the page's Metaverse Objects are read raw and untracked, so a whole-context pass
    /// would attach them through the objects' navigations, which is why the page keeps detection off (#2598, Phase 2).
    /// </summary>
    private static void AssertNoValueChangedOnDetection<T>(IEnumerable<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<T>> entries) where T : class
    {
        var entryList = entries.ToList();
        foreach (var entry in entryList)
            entry.DetectChanges();
        Assert.That(entryList.Select(e => e.State).Distinct(), Is.EqualTo(new[] { EntityState.Unchanged }), $"no {typeof(T).Name} changed on detection");
    }

    private static async Task AssertNothingToSaveAsync(JimDbContext ctx)
    {
        ctx.ChangeTracker.DetectChanges();
        Assert.That(ctx.ChangeTracker.HasChanges(), Is.False, "loading twice left nothing for a save to write");
        Assert.That(await ctx.SaveChangesAsync(), Is.Zero);
    }

    private sealed record Seeded(
        int SystemId, Guid CsoId, Guid MvoId, Guid OtherCsoValueId,
        IReadOnlyCollection<Guid> CsoValueIds, IReadOnlyCollection<Guid> MvoValueIds, IReadOnlyCollection<Guid> PendingExportChangeIds);

    /// <summary>
    /// A group of <see cref="Members"/> members in the Metaverse and in a Connected System, joined, with a Pending Export
    /// adding every member, plus a second, small object so the page is not only the group.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewTrackingContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var type = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };
        var dn = new ConnectedSystemObjectTypeAttribute
        {
            Name = "distinguishedName", ConnectedSystemObjectType = type, Type = AttributeDataType.Text, Selected = true
        };
        var member = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member", ConnectedSystemObjectType = type, Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        type.Attributes.Add(dn);
        type.Attributes.Add(member);
        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };
        var memberNames = new MetaverseAttribute { Name = "Member Names", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.MultiValued };
        seed.AddRange(connectorDefinition, system, type, mvType, memberNames);
        await seed.SaveChangesAsync();

        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Created = DateTime.UtcNow, Type = mvType };
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = system.Id, TypeId = type.Id, MetaverseObject = mvo,
            ExternalIdAttributeId = dn.Id, Status = ConnectedSystemObjectStatus.Normal, JoinType = ConnectedSystemObjectJoinType.Joined
        };
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = dn.Id, StringValue = "CN=All Staff,OU=Groups,DC=glitterband,DC=local" });
        var other = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = system.Id, TypeId = type.Id,
            ExternalIdAttributeId = dn.Id, Status = ConnectedSystemObjectStatus.Normal
        };
        other.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = dn.Id, StringValue = "CN=Auditors,OU=Groups,DC=glitterband,DC=local" });
        var pendingExport = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = system.Id, ConnectedSystemObjectId = cso.Id, SourceMetaverseObjectId = mvo.Id,
            ChangeType = PendingExportChangeType.Update, Status = PendingExportStatus.Pending, CreatedAt = DateTime.UtcNow
        };
        for (var i = 0; i < Members; i++)
        {
            var memberDn = $"CN=Person {i:D5},OU=Users,DC=glitterband,DC=local";
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = member.Id, UnresolvedReferenceValue = memberDn });
            mvo.AttributeValues.Add(new MetaverseObjectAttributeValue { Id = Guid.NewGuid(), Attribute = memberNames, MetaverseObject = mvo, StringValue = $"Person {i:D5}" });
            pendingExport.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = member.Id, UnresolvedReferenceValue = memberDn, ChangeType = PendingExportAttributeChangeType.Add
            });
        }
        seed.AddRange(mvo, cso, other);
        await seed.SaveChangesAsync();
        seed.Add(pendingExport);
        await seed.SaveChangesAsync();

        return new Seeded(system.Id, cso.Id, mvo.Id, other.AttributeValues.Single().Id,
            cso.AttributeValues.Select(v => v.Id).ToList(),
            mvo.AttributeValues.Select(v => v.Id).ToList(),
            pendingExport.AttributeValueChanges.Select(c => c.Id).ToList());
    }
}
