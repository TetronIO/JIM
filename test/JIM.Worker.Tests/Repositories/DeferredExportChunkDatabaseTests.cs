// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage for the repository reads and the write behind the chunked deferred export pass: the
/// deferred export ids the first pass collects, the chunk load, the reference-anchor projection the chunk resolves
/// against, and the narrow write of what resolution changed. Each is raw SQL or an Include chain whose faults the
/// in-memory provider cannot see: a projection that picks the wrong anchor, a write that touches columns it should
/// not or leaves a tracked copy behind, a load missing a navigation the connector reads.
/// Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent. <c>SetUp</c> TRUNCATEs every table.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class DeferredExportChunkDatabaseTests
{
    private static readonly DateTime T0 = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL deferred export chunk tests.");

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

    // ─── GetReferenceAnchorsByMetaverseObjectIdsAsync ───

    [Test]
    public async Task GetReferenceAnchorsByMetaverseObjectIdsAsync_SecondaryExternalIdHeld_IsPreferredOverThePrimaryAsync()
    {
        var s = await SeedSchemaAsync();
        var mvoId = await SeedMetaverseObjectAsync(s);
        var csoId = await SeedConnectedSystemObjectAsync(s, s.SystemId, mvoId,
            (s.ObjectGuidAttributeId, av => av.GuidValue = Guid.NewGuid()),
            (s.DnAttributeId, av => av.StringValue = "CN=Ada Ashcroft,OU=Users,DC=glitterband,DC=local"));

        await using var ctx = NewContext();
        var anchors = await new PostgresDataRepository(ctx).Sync.GetReferenceAnchorsByMetaverseObjectIdsAsync([mvoId], s.SystemId);

        Assert.That(anchors[mvoId], Is.EqualTo(new ReferenceAnchorSummary(csoId, "CN=Ada Ashcroft,OU=Users,DC=glitterband,DC=local", IsSecondaryExternalId: true)));
    }

    [Test]
    public async Task GetReferenceAnchorsByMetaverseObjectIdsAsync_OnlyAPrimaryHeld_RendersTheTypedPrimaryAsync()
    {
        var s = await SeedSchemaAsync();
        var guidMvo = await SeedMetaverseObjectAsync(s);
        var longMvo = await SeedMetaverseObjectAsync(s);
        var anchor = Guid.NewGuid();
        await SeedConnectedSystemObjectAsync(s, s.SystemId, guidMvo, (s.ObjectGuidAttributeId, av => av.GuidValue = anchor));
        await SeedConnectedSystemObjectAsync(s, s.SystemId, longMvo, (s.EmployeeNumberAttributeId, av => av.LongValue = 5000000123L));

        await using var ctx = NewContext();
        var anchors = await new PostgresDataRepository(ctx).Sync.GetReferenceAnchorsByMetaverseObjectIdsAsync([guidMvo, longMvo], s.SystemId);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchors[guidMvo].AnchorValue, Is.EqualTo(anchor.ToString()));
            Assert.That(anchors[guidMvo].IsSecondaryExternalId, Is.False);
            Assert.That(anchors[longMvo].AnchorValue, Is.EqualTo("5000000123"),
                "an anchor held in LongValue renders like any other (#1398)");
        }
    }

    [Test]
    public async Task GetReferenceAnchorsByMetaverseObjectIdsAsync_ObjectHoldsNoAnchorYet_ReturnsItWithoutOneAsync()
    {
        var s = await SeedSchemaAsync();
        var mvoId = await SeedMetaverseObjectAsync(s);
        var csoId = await SeedConnectedSystemObjectAsync(s, s.SystemId, mvoId, (s.DisplayNameAttributeId, av => av.StringValue = "Ada Ashcroft"));

        await using var ctx = NewContext();
        var anchors = await new PostgresDataRepository(ctx).Sync.GetReferenceAnchorsByMetaverseObjectIdsAsync([mvoId], s.SystemId);

        Assert.That(anchors[mvoId], Is.EqualTo(new ReferenceAnchorSummary(csoId, null, IsSecondaryExternalId: false)),
            "an object that exists without an anchor is waiting on it, which is not the same as having no object at all");
    }

    [Test]
    public async Task GetReferenceAnchorsByMetaverseObjectIdsAsync_ObjectsInAnotherSystemOrNotAsked_AreNotReturnedAsync()
    {
        var s = await SeedSchemaAsync();
        var otherSystemId = await SeedOtherSystemAsync();
        var askedInOtherSystem = await SeedMetaverseObjectAsync(s);
        var notAsked = await SeedMetaverseObjectAsync(s);
        await SeedConnectedSystemObjectAsync(s, otherSystemId, askedInOtherSystem, (s.ObjectGuidAttributeId, av => av.GuidValue = Guid.NewGuid()));
        await SeedConnectedSystemObjectAsync(s, s.SystemId, notAsked, (s.ObjectGuidAttributeId, av => av.GuidValue = Guid.NewGuid()));

        await using var ctx = NewContext();
        var anchors = await new PostgresDataRepository(ctx).Sync.GetReferenceAnchorsByMetaverseObjectIdsAsync([askedInOtherSystem, Guid.NewGuid()], s.SystemId);

        Assert.That(anchors, Is.Empty);
    }

    // ─── PersistResolvedReferencesAsync ───

    [Test]
    public async Task PersistResolvedReferencesAsync_OnAContextTrackingTheRows_WritesOnlyWhatResolutionChangedAsync()
    {
        var s = await SeedSchemaAsync();
        var referencedCsoId = await SeedConnectedSystemObjectAsync(s, s.SystemId, await SeedMetaverseObjectAsync(s));
        var exportId = await SeedDeferredExportAsync(s, T0, [Guid.NewGuid(), Guid.NewGuid()]);

        await using (var ctx = NewContext())
        {
            // Loaded tracked, as a long-lived worker context can hold them; the write must leave no tracked copy that a
            // later save could write back.
            var export = await ctx.PendingExports.AsTracking().Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == exportId);
            var resolved = export.AttributeValueChanges.OrderBy(c => c.UnresolvedReferenceValue).First();
            resolved.StringValue = "CN=Ada Ashcroft,OU=Users,DC=glitterband,DC=local";
            resolved.UnresolvedReferenceValue = null;
            resolved.ResolvedReferenceCsoId = referencedCsoId;
            resolved.Status = PendingExportAttributeChangeStatus.ExportedPendingConfirmation; // not resolution's to write
            export.HasUnresolvedReferences = false;
            export.ErrorCount = 7; // nor this

            await new PostgresDataRepository(ctx).Sync.PersistResolvedReferencesAsync([export], [resolved]);

            Assert.That(ctx.ChangeTracker.Entries().Count(e => e.Entity is PendingExport or PendingExportAttributeValueChange), Is.Zero,
                "the rows written are detached");
            await ctx.SaveChangesAsync();
        }

        await using var read = NewContext();
        var stored = await read.PendingExports.AsNoTracking().Include(pe => pe.AttributeValueChanges).SingleAsync(pe => pe.Id == exportId);
        var storedResolved = stored.AttributeValueChanges.Single(c => c.ResolvedReferenceCsoId.HasValue);
        var storedOther = stored.AttributeValueChanges.Single(c => !c.ResolvedReferenceCsoId.HasValue);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.HasUnresolvedReferences, Is.False);
            Assert.That(stored.ErrorCount, Is.Zero, "only the reference state is written");
            Assert.That(storedResolved.StringValue, Is.EqualTo("CN=Ada Ashcroft,OU=Users,DC=glitterband,DC=local"));
            Assert.That(storedResolved.UnresolvedReferenceValue, Is.Null);
            Assert.That(storedResolved.ResolvedReferenceCsoId, Is.EqualTo(referencedCsoId));
            Assert.That(storedResolved.Status, Is.EqualTo(PendingExportAttributeChangeStatus.Pending), "only the reference columns are written");
            Assert.That(storedOther.UnresolvedReferenceValue, Is.Not.Null, "a change not handed over is left as it was");
            Assert.That(storedOther.StringValue, Is.Null);
        }
    }

    // ─── GetRemainingDeferredExportIdsAsync ───

    [Test]
    public async Task GetRemainingDeferredExportIdsAsync_AfterACursor_ReturnsTheExecutableDeferredExportsBeyondItInQueueOrderAsync()
    {
        var s = await SeedSchemaAsync();
        await SeedDeferredExportAsync(s, T0, [Guid.NewGuid()]);
        var cursor = await SeedDeferredExportAsync(s, T0.AddSeconds(1), [Guid.NewGuid()]);
        var second = await SeedDeferredExportAsync(s, T0.AddSeconds(3), [Guid.NewGuid()]);
        var first = await SeedDeferredExportAsync(s, T0.AddSeconds(2), [Guid.NewGuid()]);
        await SeedDeferredExportAsync(s, T0.AddSeconds(4), [Guid.NewGuid()], hasUnresolvedReferences: false);
        await SeedDeferredExportAsync(s, T0.AddSeconds(5), [Guid.NewGuid()], status: PendingExportStatus.Exported);

        await using var ctx = NewContext();
        var ids = await new PostgresDataRepository(ctx).Sync.GetRemainingDeferredExportIdsAsync(s.SystemId, T0.AddSeconds(1), cursor);

        Assert.That(ids, Is.EqualTo(new[] { first, second }),
            "deferred and still executable, strictly after the cursor, in (CreatedAt, Id) order");
    }

    // ─── GetDeferredExportsByIdsAsync ───

    [Test]
    public async Task GetDeferredExportsByIdsAsync_SomeIds_LoadsTheGraphTheConnectorReadsInQueueOrderUntrackedAsync()
    {
        var s = await SeedSchemaAsync();
        var later = await SeedDeferredExportAsync(s, T0.AddSeconds(2), [Guid.NewGuid()]);
        var earlier = await SeedDeferredExportAsync(s, T0.AddSeconds(1), [Guid.NewGuid()]);
        await SeedDeferredExportAsync(s, T0.AddSeconds(3), [Guid.NewGuid()]);

        await using var ctx = NewContext();
        var exports = await new PostgresDataRepository(ctx).Sync.GetDeferredExportsByIdsAsync([later, earlier]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exports.Select(pe => pe.Id), Is.EqualTo(new[] { earlier, later }), "the chunk's exports, in queue order");
            Assert.That(exports.SelectMany(pe => pe.AttributeValueChanges).All(c => c.Attribute?.Name == "member"), Is.True,
                "each change carries its attribute, which resolution logs and the connector writes");
            Assert.That(exports.All(pe => pe.ConnectedSystemObject?.Type?.Name == "group"), Is.True,
                "each export carries its object and the object's type, which the LDAP Connector reads");
            Assert.That(exports.All(pe => pe.ConnectedSystemObject!.AttributeValues.Single().Attribute?.Name == "distinguishedName"), Is.True,
                "and the object's attribute values with their attributes");
            Assert.That(ctx.ChangeTracker.Entries().Any(), Is.False, "the chunk is loaded untracked; its writes are raw SQL");
        }
    }

    // ─── Helpers ───

    private sealed record Schema(
        int SystemId, int GroupTypeId, int MvTypeId,
        int ObjectGuidAttributeId, int DnAttributeId, int EmployeeNumberAttributeId, int DisplayNameAttributeId, int MemberAttributeId);

    private async Task<Schema> SeedSchemaAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband EMEA", ConnectorDefinition = connectorDefinition };
        var groupType = new ConnectedSystemObjectType { Name = "group", ConnectedSystem = system, Selected = true };

        ConnectedSystemObjectTypeAttribute Attribute(string name, AttributeDataType type, bool externalId = false, bool secondary = false,
            AttributePlurality plurality = AttributePlurality.SingleValued)
        {
            var attribute = new ConnectedSystemObjectTypeAttribute
            {
                Name = name, ConnectedSystemObjectType = groupType, Type = type, AttributePlurality = plurality, Selected = true,
                IsExternalId = externalId, IsSecondaryExternalId = secondary
            };
            groupType.Attributes.Add(attribute);
            return attribute;
        }

        var objectGuid = Attribute("objectGUID", AttributeDataType.Guid, externalId: true);
        var dn = Attribute("distinguishedName", AttributeDataType.Text, secondary: true);
        var employeeNumber = Attribute("employeeNumber", AttributeDataType.LongNumber, externalId: true);
        var displayName = Attribute("displayName", AttributeDataType.Text);
        var member = Attribute("member", AttributeDataType.Reference, plurality: AttributePlurality.MultiValued);
        var mvType = new MetaverseObjectType { Name = "Group", PluralName = "Groups" };

        seed.AddRange(connectorDefinition, system, groupType, mvType);
        await seed.SaveChangesAsync();
        return new Schema(system.Id, groupType.Id, mvType.Id, objectGuid.Id, dn.Id, employeeNumber.Id, displayName.Id, member.Id);
    }

    private async Task<int> SeedOtherSystemAsync()
    {
        await using var seed = NewContext();
        var connectorDefinition = new ConnectorDefinition { Name = "Other Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Glitterband APAC", ConnectorDefinition = connectorDefinition };
        seed.AddRange(connectorDefinition, system);
        await seed.SaveChangesAsync();
        return system.Id;
    }

    private async Task<Guid> SeedMetaverseObjectAsync(Schema s)
    {
        await using var seed = NewContext();
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(), Created = DateTime.UtcNow,
            Type = await seed.MetaverseObjectTypes.AsTracking().SingleAsync(t => t.Id == s.MvTypeId)
        };
        seed.Add(mvo);
        await seed.SaveChangesAsync();
        return mvo.Id;
    }

    private async Task<Guid> SeedConnectedSystemObjectAsync(Schema s, int systemId, Guid? mvoId,
        params (int AttributeId, Action<ConnectedSystemObjectAttributeValue> Set)[] values)
    {
        await using var seed = NewContext();
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(), ConnectedSystemId = systemId, TypeId = s.GroupTypeId, MetaverseObjectId = mvoId,
            Status = ConnectedSystemObjectStatus.Normal, JoinType = mvoId.HasValue ? ConnectedSystemObjectJoinType.Joined : ConnectedSystemObjectJoinType.NotJoined
        };
        foreach (var (attributeId, set) in values)
        {
            var value = new ConnectedSystemObjectAttributeValue { Id = Guid.NewGuid(), AttributeId = attributeId };
            set(value);
            cso.AttributeValues.Add(value);
        }
        seed.Add(cso);
        await seed.SaveChangesAsync();
        return cso.Id;
    }

    /// <summary>
    /// Seeds a Pending Export for a new group, its member references unresolved (each naming a Metaverse Object), and
    /// returns its id.
    /// </summary>
    private async Task<Guid> SeedDeferredExportAsync(Schema s, DateTime createdAt, Guid[] referencedMvoIds,
        bool hasUnresolvedReferences = true, PendingExportStatus status = PendingExportStatus.Pending)
    {
        var groupCsoId = await SeedConnectedSystemObjectAsync(s, s.SystemId, null,
            (s.DnAttributeId, av => av.StringValue = $"CN=Group {Guid.NewGuid():N},OU=Groups,DC=glitterband,DC=local"));

        await using var seed = NewContext();
        var export = new PendingExport
        {
            Id = Guid.NewGuid(), ConnectedSystemId = s.SystemId, ConnectedSystemObjectId = groupCsoId,
            ChangeType = PendingExportChangeType.Create, Status = status, HasUnresolvedReferences = hasUnresolvedReferences, CreatedAt = createdAt
        };
        foreach (var mvoId in referencedMvoIds)
        {
            export.AttributeValueChanges.Add(new PendingExportAttributeValueChange
            {
                Id = Guid.NewGuid(), AttributeId = s.MemberAttributeId, ChangeType = PendingExportAttributeChangeType.Add,
                Status = PendingExportAttributeChangeStatus.Pending, UnresolvedReferenceValue = mvoId.ToString()
            });
        }
        seed.Add(export);
        await seed.SaveChangesAsync();
        return export.Id;
    }
}
