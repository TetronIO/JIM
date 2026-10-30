// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL coverage of how a synchronisation page loads the Metaverse Objects its Connected System Objects are
/// joined to (#2598, Phase 2). The load is raw SQL into untracked objects: a tracked EF load materialised and tracked
/// every attribute value, which cost 154 s for one page of groups at Scale750k40kGroups. Everything the engine reads,
/// and everything the bulk update writes back, must arrive exactly as stored, so each column is compared with EF's
/// own reading of the same row, column by column from the model: a column added later is compared without anyone
/// remembering to add it here. Opt-in via <c>JIM_TEST_RESET_*</c>; ignored when absent.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class PageMetaverseObjectLoadDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext(QueryTrackingBehavior tracking = QueryTrackingBehavior.NoTracking)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL page Metaverse Object load tests.");

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

    [Test]
    public async Task GetConnectedSystemObjectsAsync_WorkerContext_LoadsJoinedMetaverseObjectsExactlyAndUntrackedAsync()
    {
        var s = await SeedAsync();

        // As a synchronisation page runs: change detection off from the load to the tracker clear.
        await using var pageContext = NewContext(QueryTrackingBehavior.TrackAll);
        pageContext.ChangeTracker.AutoDetectChangesEnabled = false;
        var page = await new PostgresDataRepository(pageContext).Sync.GetConnectedSystemObjectsAsync(
            s.SystemId, 1, 100, afterId: Guid.Empty);
        var joined = page.Results.Single(c => c.Id == s.JoinedCsoId);
        var mvo = joined.MetaverseObject!;

        await AssertMatchesStoredRowAsync(mvo, s);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.Type.Id, Is.EqualTo(s.MvoTypeId));
            Assert.That(mvo.AttributeValues.Select(v => v.Attribute.Id), Is.EquivalentTo(mvo.AttributeValues.Select(v => v.AttributeId)),
                "Each value carries its Attribute, which the engine reads by name and type.");
            Assert.That(mvo.AttributeValues.All(v => ReferenceEquals(v.MetaverseObject, mvo)), Is.True,
                "Each value points back at its own object.");
            Assert.That(mvo.ConnectedSystemObjects, Is.EquivalentTo(new[] { joined }),
                "The object lists the page's Connected System Objects joined to it.");
            Assert.That(pageContext.ChangeTracker.Entries<MetaverseObject>(), Is.Empty,
                "Page Metaverse Objects are loaded untracked.");
            Assert.That(pageContext.ChangeTracker.Entries<MetaverseObjectAttributeValue>(), Is.Empty,
                "Their attribute values are loaded untracked.");
        }
    }

    /// <summary>
    /// Why a synchronisation page keeps change detection off from its load to its tracker clear: the untracked objects
    /// hang off tracked Connected System Objects, so a detection pass discovers them and attaches every one, values
    /// included, as Modified, ready for a later save to issue xmin-guarded updates that match no rows.
    /// </summary>
    [Test]
    public async Task GetConnectedSystemObjectsAsync_WorkerContextWithChangeDetectionOn_DetectionAttachesTheObjectsAsync()
    {
        var s = await SeedAsync();

        await using var pageContext = NewContext(QueryTrackingBehavior.TrackAll);
        await new PostgresDataRepository(pageContext).Sync.GetConnectedSystemObjectsAsync(s.SystemId, 1, 100, afterId: Guid.Empty);
        pageContext.ChangeTracker.DetectChanges();

        Assert.That(pageContext.ChangeTracker.Entries<MetaverseObject>().Select(e => e.State), Is.EquivalentTo(new[] { EntityState.Modified }));
    }

    [Test]
    public async Task GetConnectedSystemObjectsAsync_PortalContext_AssignsTheSameNavigationsAsync()
    {
        // Previews load pages on the portal's no-tracking context, where EF's fix-up never filled any navigation in.
        var s = await SeedAsync();

        await using var portalContext = NewContext();
        var page = await new PostgresDataRepository(portalContext).Sync.GetConnectedSystemObjectsAsync(
            s.SystemId, 1, 100, afterId: Guid.Empty);
        var joined = page.Results.Single(c => c.Id == s.JoinedCsoId);
        var mvo = joined.MetaverseObject!;

        await AssertMatchesStoredRowAsync(mvo, s);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvo.Type.Id, Is.EqualTo(s.MvoTypeId));
            Assert.That(mvo.AttributeValues.All(v => v.Attribute != null && ReferenceEquals(v.MetaverseObject, mvo)), Is.True);
            Assert.That(mvo.ConnectedSystemObjects, Is.EquivalentTo(new[] { joined }));
        }
    }

    [Test]
    public async Task UpdateMetaverseObjectsAsync_PageLoadedObjectUnchanged_WritesEveryColumnBackAsItWasAsync()
    {
        // The bulk update writes back the columns it is given, so a column the load read wrongly is written back wrongly.
        var s = await SeedAsync();

        await using (var pageContext = NewContext(QueryTrackingBehavior.TrackAll))
        {
            var repository = new PostgresDataRepository(pageContext);
            var page = await repository.Sync.GetConnectedSystemObjectsAsync(s.SystemId, 1, 100, afterId: Guid.Empty);
            pageContext.ChangeTracker.AutoDetectChangesEnabled = false;
            await repository.Sync.UpdateMetaverseObjectsAsync([page.Results.Single(c => c.Id == s.JoinedCsoId).MetaverseObject!]);
        }

        await using var verify = NewContext();
        var reread = await verify.MetaverseObjects.Include(m => m.AttributeValues).Include(m => m.Type).SingleAsync(m => m.Id == s.MvoId);
        AssertColumnsEqual(verify.Model.FindEntityType(typeof(MetaverseObject))!, s.Expected, reread);
        AssertAttributeValuesEqual(verify.Model.FindEntityType(typeof(MetaverseObjectAttributeValue))!, s.Expected, reread);
    }

    /// <summary>
    /// Compares the loaded object and its values with EF's own reading of the same rows, every mapped column.
    /// </summary>
    private async Task AssertMatchesStoredRowAsync(MetaverseObject loaded, Seeded s)
    {
        await using var reference = NewContext();
        var stored = await reference.MetaverseObjects.Include(m => m.AttributeValues).Include(m => m.Type).SingleAsync(m => m.Id == s.MvoId);
        Assert.That(loaded.AttributeValues, Has.Count.EqualTo(stored.AttributeValues.Count), "Every attribute value is loaded.");
        AssertColumnsEqual(reference.Model.FindEntityType(typeof(MetaverseObject))!, stored, loaded);
        AssertAttributeValuesEqual(reference.Model.FindEntityType(typeof(MetaverseObjectAttributeValue))!, stored, loaded);
    }

    private static void AssertAttributeValuesEqual(IEntityType valueType, MetaverseObject expected, MetaverseObject actual)
    {
        var actualById = actual.AttributeValues.ToDictionary(v => v.Id);
        foreach (var expectedValue in expected.AttributeValues)
        {
            Assert.That(actualById.ContainsKey(expectedValue.Id), Is.True, $"Value {expectedValue.Id} is loaded.");
            AssertColumnsEqual(valueType, expectedValue, actualById[expectedValue.Id], expected.Id, actual.Id);
        }
    }

    /// <summary>
    /// Every column the model maps for <paramref name="entityType"/>, other than the store-generated xmin, holds the
    /// same value on both objects. Shadow foreign keys are read from what they stand for: a Metaverse Object's Type,
    /// and an attribute value's owner, whose id the caller passes because a no-tracking load leaves the back-pointer unset.
    /// </summary>
    private static void AssertColumnsEqual(IEntityType entityType, object expected, object actual, Guid? expectedOwnerId = null, Guid? actualOwnerId = null)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (var property in entityType.GetProperties().Where(p => p.GetColumnName() != "xmin"))
            {
                Assert.That(ValueOf(property, actual, actualOwnerId), Is.EqualTo(ValueOf(property, expected, expectedOwnerId)),
                    $"{entityType.ClrType.Name}.{property.GetColumnName()}");
            }
        }
    }

    private static object? ValueOf(IProperty property, object entity, Guid? ownerId)
    {
        if (property.PropertyInfo != null)
            return property.PropertyInfo.GetValue(entity);

        return (entity, property.Name) switch
        {
            (MetaverseObject mvo, "TypeId") => mvo.Type.Id,
            (MetaverseObjectAttributeValue, "MetaverseObjectId") => ownerId,
            _ => throw new InvalidOperationException(
                $"Shadow property {property.DeclaringType.ClrType.Name}.{property.Name} has no reading in this test; add one.")
        };
    }

    private sealed record Seeded(int SystemId, int MvoTypeId, Guid MvoId, Guid JoinedCsoId, MetaverseObject Expected);

    /// <summary>
    /// A Metaverse Object with every column populated and a value of every kind, provenance included, joined to one
    /// Connected System Object; plus an unjoined object in the same system.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        int systemId, syncRuleId, mvoTypeId, textId, numberId, longId, decimalId, binaryId, guidId, boolId, dateId, referenceId;
        Guid unresolvedCsoId;
        MetaverseObjectType mvoType;
        await using (var seed = NewContext())
        {
            mvoType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
            var text = new MetaverseAttribute { Name = "displayName", Type = AttributeDataType.Text };
            var number = new MetaverseAttribute { Name = "employeeNumber", Type = AttributeDataType.Number };
            var longNumber = new MetaverseAttribute { Name = "accountExpires", Type = AttributeDataType.LongNumber };
            var decimalAttr = new MetaverseAttribute { Name = "fte", Type = AttributeDataType.Decimal };
            var binary = new MetaverseAttribute { Name = "thumbnailPhoto", Type = AttributeDataType.Binary };
            var guid = new MetaverseAttribute { Name = "correlationId", Type = AttributeDataType.Guid };
            var boolean = new MetaverseAttribute { Name = "enabled", Type = AttributeDataType.Boolean };
            var date = new MetaverseAttribute { Name = "whenChanged", Type = AttributeDataType.DateTime };
            var reference = new MetaverseAttribute { Name = "manager", Type = AttributeDataType.Reference };
            var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
            var system = new ConnectedSystem { Name = "Yellowstone HR", ConnectorDefinition = connectorDefinition };
            var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
            var syncRule = new SyncRule
            {
                Name = "HR Import", ConnectedSystem = system, ConnectedSystemObjectType = csType,
                MetaverseObjectType = mvoType, Direction = SyncRuleDirection.Import
            };
            var unresolved = new ConnectedSystemObject { Type = csType, ConnectedSystem = system, Status = ConnectedSystemObjectStatus.Normal };
            seed.AddRange(mvoType, text, number, longNumber, decimalAttr, binary, guid, boolean, date, reference,
                connectorDefinition, system, csType, syncRule, unresolved);
            await seed.SaveChangesAsync();
            (systemId, syncRuleId, mvoTypeId, unresolvedCsoId) = (system.Id, syncRule.Id, mvoType.Id, unresolved.Id);
            (textId, numberId, longId, decimalId, binaryId) = (text.Id, number.Id, longNumber.Id, decimalAttr.Id, binary.Id);
            (guidId, boolId, dateId, referenceId) = (guid.Id, boolean.Id, date.Id, reference.Id);
        }

        var referenced = new MetaverseObject { Id = Guid.NewGuid(), Type = mvoType, Created = Micro(DateTime.UtcNow) };
        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvoType,
            Created = Micro(DateTime.UtcNow.AddDays(-30)),
            LastUpdated = Micro(DateTime.UtcNow.AddMinutes(-2)),
            Status = MetaverseObjectStatus.Normal,
            Origin = MetaverseObjectOrigin.Projected,
            LastConnectorDisconnectedDate = Micro(DateTime.UtcNow.AddDays(-3)),
            DeletionInitiatedByType = JIM.Models.Activities.ActivityInitiatorType.User,
            DeletionInitiatedById = Guid.NewGuid(),
            DeletionInitiatedByName = "Jo Admin",
            DeletionTriggeredBySystemId = systemId,
            DeletionTriggeredBySystemName = "Yellowstone HR",
            DeletionPolicySnapshotJson = """{"deletionRule":"WhenAuthoritativeSourceDisconnected"}""",
            DeletionScheduledByRunProfileExecutionItemId = Guid.NewGuid(),
            CachedDisplayName = "Jo Bloggs",
            ScopeReviewPending = true,
            LastScopeEvaluatedAt = Micro(DateTime.UtcNow.AddMinutes(-20))
        };
        void Add(MetaverseObjectAttributeValue value)
        {
            value.Id = Guid.NewGuid();
            value.MetaverseObject = mvo;
            mvo.AttributeValues.Add(value);
        }
        Add(new MetaverseObjectAttributeValue { AttributeId = textId, StringValue = "Jo Bloggs", ContributedBySystemId = systemId, ContributedBySyncRuleId = syncRuleId, ContributedByKind = ValueContributorKind.SynchronisationRule });
        Add(new MetaverseObjectAttributeValue { AttributeId = textId, StringValue = "Jo", ContributedByKind = ValueContributorKind.SignInClaim, ContributedByClaimType = "given_name" });
        Add(new MetaverseObjectAttributeValue { AttributeId = numberId, IntValue = 42 });
        Add(new MetaverseObjectAttributeValue { AttributeId = longId, LongValue = 9999999999L });
        Add(new MetaverseObjectAttributeValue { AttributeId = decimalId, DecimalValue = 3.14159m });
        Add(new MetaverseObjectAttributeValue { AttributeId = binaryId, ByteValue = [9, 8, 7, 6] });
        Add(new MetaverseObjectAttributeValue { AttributeId = guidId, GuidValue = Guid.NewGuid() });
        Add(new MetaverseObjectAttributeValue { AttributeId = boolId, BoolValue = true });
        Add(new MetaverseObjectAttributeValue { AttributeId = dateId, DateTimeValue = Micro(DateTime.UtcNow.AddDays(-1)) });
        Add(new MetaverseObjectAttributeValue { AttributeId = referenceId, ReferenceValueId = referenced.Id });
        Add(new MetaverseObjectAttributeValue { AttributeId = referenceId, UnresolvedReferenceValueId = unresolvedCsoId });
        Add(new MetaverseObjectAttributeValue { AttributeId = textId, NullValue = true });

        await using (var write = NewContext())
            await new PostgresDataRepository(write).Sync.CreateMetaverseObjectsAsync([referenced, mvo]);

        var joinedCsoId = Guid.NewGuid();
        await using (var join = NewContext())
        {
            await join.Database.ExecuteSqlRawAsync(
                @"INSERT INTO ""ConnectedSystemObjects"" (""Id"", ""ConnectedSystemId"", ""TypeId"", ""ExternalIdAttributeId"", ""Status"", ""JoinType"", ""MetaverseObjectId"", ""Created"", ""DateJoined"")
                  SELECT {0}, ""ConnectedSystemId"", ""TypeId"", ""ExternalIdAttributeId"", {1}, {2}, {3}, now(), now() FROM ""ConnectedSystemObjects"" WHERE ""Id"" = {4}",
                joinedCsoId, (int)ConnectedSystemObjectStatus.Normal, (int)ConnectedSystemObjectJoinType.Joined, mvo.Id, unresolvedCsoId);
        }

        await using var reference2 = NewContext();
        var expected = await reference2.MetaverseObjects.Include(m => m.AttributeValues).Include(m => m.Type).SingleAsync(m => m.Id == mvo.Id);
        return new Seeded(systemId, mvoTypeId, mvo.Id, joinedCsoId, expected);
    }

    private static DateTime Micro(DateTime value) => new(value.Ticks / 10 * 10, value.Kind);
}
