// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.History;
using JIM.Models.Staging;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using static JIM.Worker.Tests.Repositories.AuditReferenceTestSeed;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// Real-PostgreSQL verification that every reader deciding whether an object a history record names is live, or
/// finding the records of an object by its id, does so by the object table and the id (#2669), never by a null id or
/// a navigation. Each test seeds a history record naming an id whose object has gone without the record being
/// rewritten: what every deletion will leave once history keeps its ids, and what a reader that tests the id for null
/// mistakes for a live object.
/// </summary>
/// <remarks>
/// Run on a <c>NoTracking</c> context, as JIM.Web runs. Opt-in via the same <c>JIM_TEST_RESET_*</c> environment
/// variables as the other database-backed tests; ignored when <c>JIM_TEST_RESET_DB</c> is absent.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class AuditReferenceReadersDatabaseTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    private string _connectionString = null!;
    private AuditReferenceTestSeed _seed = null!;

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
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL audit reference reader tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";
        _seed = new AuditReferenceTestSeed(_connectionString, NewContext);

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    // ─── Deleted Objects lists ───

    [Test]
    public async Task GetDeletedCsoChangesRangeAsync_DeletionRecordNamingItsObjectById_IsListedAsync()
    {
        var schema = await _seed.SeedCsoSchemaAsync();
        var gone = Guid.NewGuid();
        await _seed.CreateCsoDeletionRecordAsync(schema, gone, DeletionKey.SubjectId, T0, "Mia Young", "E1003");

        var result = await NewJim().ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 0, count: 10);

        Assert.That(result.TotalResults, Is.EqualTo(1));
        Assert.That(result.Results.Select(c => c.DeletedObjectExternalId), Is.EqualTo(new[] { "E1003" }));
    }

    [Test]
    public async Task GetDeletedCsoChangesRangeAsync_DeletedChangeWhoseObjectIsLive_IsNotListedAsync()
    {
        // An export that deprovisions an object records a Deleted change before its confirming import removes it.
        var schema = await _seed.SeedCsoSchemaAsync();
        var live = await _seed.CreateCsoAsync(schema, "Raj Patel", "E1004");
        await _seed.CreateCsoChangeAsync(schema, live, ObjectChangeType.Deleted, T0);
        await _seed.CreateCsoDeletionRecordAsync(schema, Guid.NewGuid(), DeletionKey.DeletedObjectIdColumn, T0, "Mia Young", "E1003");

        var result = await NewJim().ConnectedSystems.GetDeletedCsoChangesRangeAsync(offset: 0, count: 10);

        Assert.That(result.TotalResults, Is.EqualTo(1));
        Assert.That(result.Results.Select(c => c.DeletedObjectExternalId), Is.EqualTo(new[] { "E1003" }));
    }

    [TestCase(DeletionKey.DeletedObjectIdColumn)]
    [TestCase(DeletionKey.SubjectId)]
    public async Task GetDeletedMvoChangesRangeAsync_DeletionRecordEitherKeying_IsListedAsync(DeletionKey key)
    {
        var schema = await _seed.SeedMvoSchemaAsync();
        await _seed.CreateMvoDeletionRecordAsync(schema, Guid.NewGuid(), key, T0, "Bob Jones");

        var result = await NewJim().Metaverse.GetDeletedMvoChangesRangeAsync(offset: 0, count: 10);

        Assert.That(result.TotalResults, Is.EqualTo(1));
        Assert.That(result.Results.Select(c => c.DeletedObjectDisplayName), Is.EqualTo(new[] { "Bob Jones" }));
    }

    // ─── A live object's history, and the references in it ───

    [Test]
    public async Task GetCsoChangeHistoryAsync_ReferenceToAnObjectNoLongerHeld_ReadsTheValueWithoutAReferenceAsync()
    {
        var schema = await _seed.SeedCsoSchemaAsync();
        var group = await _seed.CreateCsoAsync(schema, "Engineers", "G1");
        var member = await _seed.CreateCsoAsync(schema, "Tina Adams", "E1001");
        await SeedCsoMemberChangeAsync(schema, group, member, "CN=Tina Adams,OU=People");
        await _seed.DeleteCsoLeavingHistoryAsync(member);

        var (items, total) = await NewJim().ConnectedSystems.GetCsoChangeHistoryAsync(group, 1, 10);

        Assert.That(total, Is.EqualTo(1));
        var value = items.Single().AttributeChanges.Single().ValueChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(value.ReferenceValue, Is.Null, "the reference describes live objects only until deleted references are surfaced");
            Assert.That(value.StringValue, Is.EqualTo("CN=Tina Adams,OU=People"));
        }
    }

    [Test]
    public async Task GetMvoChangeHistoryAsync_ReferenceToAnObjectNoLongerHeld_ReadsTheValueWithoutAReferenceAsync()
    {
        var schema = await _seed.SeedMvoSchemaAsync();
        var manager = await _seed.CreateMvoAsync(schema, "Alice Smith");
        var report = await _seed.CreateMvoAsync(schema, "Bob Jones");
        await SeedMvoReportChangeAsync(schema, manager, report);
        await _seed.DeleteMvoLeavingHistoryAsync(report);

        var (items, total) = await NewJim().Metaverse.GetMvoChangeHistoryAsync(manager, 1, 10);

        Assert.That(total, Is.EqualTo(1));
        Assert.That(items.Single().AttributeChanges.Single().ValueChanges.Single().ReferenceValue, Is.Null);
    }

    [Test]
    public async Task GetMvoChangeHistoryAsync_LiveObject_ReadsOnlyTheChangesNamingItAsync()
    {
        var schema = await _seed.SeedMvoSchemaAsync();
        var alice = await _seed.CreateMvoAsync(schema, "Alice Smith");
        var bob = await _seed.CreateMvoAsync(schema, "Bob Jones");
        await _seed.CreateMvoChangeAsync(alice, T0);
        await _seed.CreateMvoChangeAsync(alice, T0.AddMinutes(1));
        await _seed.CreateMvoChangeAsync(bob, T0.AddMinutes(2));

        var (items, total) = await NewJim().Metaverse.GetMvoChangeHistoryAsync(alice, 1, 10);

        Assert.That(total, Is.EqualTo(2));
        Assert.That(items, Has.Count.EqualTo(2));
    }

    // ─── A deleted object's history ───

    [Test]
    public async Task GetDeletedCsoChangeHistoryAsync_TwoObjectsThatHeldOneExternalId_AreReadApartAsync()
    {
        // Two objects held the same external ID at different times. Read by external ID, their histories merged; read by
        // id, each deletion record leads only to its own object's changes.
        var schema = await _seed.SeedCsoSchemaAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var firstUpdate = await _seed.CreateCsoChangeAsync(schema, first, ObjectChangeType.Updated, T0, deletedObjectExternalId: "E1001");
        var firstDeletion = await _seed.CreateCsoDeletionRecordAsync(schema, first, DeletionKey.SubjectId, T0.AddMinutes(1), "Tina Adams", "E1001");
        var secondUpdate = await _seed.CreateCsoChangeAsync(schema, second, ObjectChangeType.Updated, T0.AddMinutes(2), deletedObjectExternalId: "E1001");
        var secondDeletion = await _seed.CreateCsoDeletionRecordAsync(schema, second, DeletionKey.SubjectId, T0.AddMinutes(3), "Tina Adams", "E1001");
        var jim = NewJim();

        var firstHistory = await jim.ConnectedSystems.GetDeletedCsoChangeHistoryAsync(firstDeletion);
        var secondHistory = await jim.ConnectedSystems.GetDeletedCsoChangeHistoryAsync(secondDeletion);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstHistory.Select(c => c.Id), Is.EquivalentTo(new[] { firstDeletion, firstUpdate }));
            Assert.That(secondHistory.Select(c => c.Id), Is.EquivalentTo(new[] { secondDeletion, secondUpdate }));
        }
    }

    [Test]
    public async Task GetDeletedCsoChangeHistoryAsync_EarlierChangesWhoseIdWasCleared_AreStillFoundByExternalIdAsync()
    {
        // Changes recorded before ids were kept lost theirs when the object went; the external ID is all that links them,
        // so it still does, for rows with no id only.
        var schema = await _seed.SeedCsoSchemaAsync();
        var gone = Guid.NewGuid();
        Guid legacyUpdate;
        await using (var ctx = NewContext())
        {
            var change = new ConnectedSystemObjectChange
            {
                Id = Guid.NewGuid(), ConnectedSystemId = schema.ConnectedSystemId, ChangeType = ObjectChangeType.Updated, ChangeTime = T0,
                DeletedObjectExternalId = "E1001"
            };
            ctx.Add(change);
            await ctx.SaveChangesAsync();
            legacyUpdate = change.Id;
        }
        var otherObjectsUpdate = await _seed.CreateCsoChangeAsync(schema, Guid.NewGuid(), ObjectChangeType.Updated, T0, deletedObjectExternalId: "E1001");
        var deletion = await _seed.CreateCsoDeletionRecordAsync(schema, gone, DeletionKey.DeletedObjectIdColumn, T0.AddMinutes(1), "Tina Adams", "E1001");

        var history = await NewJim().ConnectedSystems.GetDeletedCsoChangeHistoryAsync(deletion);

        Assert.That(history.Select(c => c.Id), Is.EquivalentTo(new[] { deletion, legacyUpdate }),
            $"a change naming another object by id ({otherObjectsUpdate}) is that object's, whatever external ID it kept");
    }

    [Test]
    public async Task GetDeletedMvoChangeHistoryAsync_RecordsNamingTheObjectById_AreAllReadAsync()
    {
        var schema = await _seed.SeedMvoSchemaAsync();
        var gone = Guid.NewGuid();
        var update = await _seed.CreateMvoChangeAsync(gone, T0);
        var deletion = await _seed.CreateMvoDeletionRecordAsync(schema, gone, DeletionKey.SubjectId, T0.AddMinutes(1), "Bob Jones");
        var unrelated = await _seed.CreateMvoChangeAsync(Guid.NewGuid(), T0);

        var history = await NewJim().Metaverse.GetDeletedMvoChangeHistoryAsync(deletion);

        Assert.That(history.Select(c => c.Id), Is.EquivalentTo(new[] { deletion, update }), $"not {unrelated}");
    }

    // ─── The Object Changes search ───

    [Test]
    public async Task SearchMetaverseObjectChangesRangeAsync_ChangeToADeletedObjectNamedById_IsNamedAndFoundFromItsDeletionRecordAsync()
    {
        var schema = await _seed.SeedMvoSchemaAsync();
        var gone = Guid.NewGuid();
        var update = await _seed.CreateMvoChangeAsync(gone, T0);
        await _seed.CreateMvoDeletionRecordAsync(schema, gone, DeletionKey.SubjectId, T0.AddMinutes(1), "Bob Jones");
        var live = await _seed.CreateMvoAsync(schema, "Alice Smith");
        await _seed.CreateMvoChangeAsync(live, T0);
        var jim = NewJim();

        var byName = await jim.Metaverse.SearchMetaverseObjectChangesRangeAsync(
            new MetaverseObjectChangeSearchCriteria { DisplayNameSearch = "bob" }, 0, 10);
        var byType = await jim.Metaverse.SearchMetaverseObjectChangesRangeAsync(
            new MetaverseObjectChangeSearchCriteria { ObjectTypeId = schema.TypeId }, 0, 10);

        var result = byName.Results.Single(r => r.Change.Id == update);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.MetaverseObjectId, Is.EqualTo(gone));
            Assert.That(result.MetaverseObjectDeleted, Is.True);
            Assert.That(result.MetaverseObjectDeletedAt, Is.EqualTo(T0.AddMinutes(1)));
            Assert.That(result.MetaverseObjectDisplayName, Is.EqualTo("Bob Jones"));
            Assert.That(result.MetaverseObjectTypeName, Is.EqualTo("Person"));
            Assert.That(byType.Results.Select(r => r.Change.Id), Does.Contain(update));
            Assert.That(byName.Results.Select(r => r.MetaverseObjectDisplayName), Has.None.EqualTo("Alice Smith"));
        }
    }

    [Test]
    public async Task SearchMetaverseObjectChangesRangeAsync_ChangeToALiveObject_IsNamedFromTheObjectAsync()
    {
        var schema = await _seed.SeedMvoSchemaAsync();
        var live = await _seed.CreateMvoAsync(schema, "Alice Smith");
        var update = await _seed.CreateMvoChangeAsync(live, T0);

        var found = await NewJim().Metaverse.SearchMetaverseObjectChangesRangeAsync(
            new MetaverseObjectChangeSearchCriteria { DisplayNameSearch = "alice", ObjectTypeId = schema.TypeId }, 0, 10);

        var result = found.Results.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Change.Id, Is.EqualTo(update));
            Assert.That(result.MetaverseObjectId, Is.EqualTo(live));
            Assert.That(result.MetaverseObjectDeleted, Is.False);
            Assert.That(result.MetaverseObjectTypePluralName, Is.EqualTo("People"));
        }
    }

    // ─── The Causality Explorer ───

    [Test]
    public async Task GetCausalObjectHeadersAsync_ObjectsDeletedWithRecordsNamingThemById_AreDescribedAsDeletedAsync()
    {
        var csoSchema = await _seed.SeedCsoSchemaAsync();
        var mvoSchema = await _seed.SeedMvoSchemaAsync();
        var goneCso = Guid.NewGuid();
        var goneMvo = Guid.NewGuid();
        await _seed.CreateCsoDeletionRecordAsync(csoSchema, goneCso, DeletionKey.SubjectId, T0, "Mia Young", "E1003");
        await _seed.CreateMvoDeletionRecordAsync(mvoSchema, goneMvo, DeletionKey.SubjectId, T0.AddMinutes(1), "Bob Jones");
        var csoSubject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, goneCso);
        var mvoSubject = new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, goneMvo);

        var headers = await NewJim().Activities.GetCausalObjectHeadersAsync([csoSubject, mvoSubject]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers[csoSubject].IsDeleted, Is.True);
            Assert.That(headers[csoSubject].DeletedAt, Is.EqualTo(T0));
            Assert.That(headers[csoSubject].DisplayName, Is.EqualTo("Mia Young"));
            Assert.That(headers[csoSubject].ExternalId, Is.EqualTo("E1003"));
            Assert.That(headers[csoSubject].ObjectTypeName, Is.EqualTo("user"));
            Assert.That(headers[csoSubject].ConnectedSystemName, Is.EqualTo("Glitterband EMEA"));
            Assert.That(headers[mvoSubject].IsDeleted, Is.True);
            Assert.That(headers[mvoSubject].DisplayName, Is.EqualTo("Bob Jones"));
            Assert.That(headers[mvoSubject].ObjectTypePluralName, Is.EqualTo("People"));
        }
    }

    [Test]
    public async Task GetCausalObjectHeadersAsync_LiveObjects_AreDescribedFromTheObjectsAsync()
    {
        var csoSchema = await _seed.SeedCsoSchemaAsync();
        var mvoSchema = await _seed.SeedMvoSchemaAsync();
        var cso = await _seed.CreateCsoAsync(csoSchema, "Tina Adams", "E1001");
        var mvo = await _seed.CreateMvoAsync(mvoSchema, "Alice Smith");
        var csoSubject = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, cso);
        var mvoSubject = new CausalTimelineSubject(CausalTimelineSubjectKind.MetaverseObject, mvo);
        var notHeld = new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, Guid.NewGuid());

        var headers = await NewJim().Activities.GetCausalObjectHeadersAsync([csoSubject, mvoSubject, notHeld]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(headers[csoSubject].IsDeleted, Is.False);
            Assert.That(headers[csoSubject].DisplayName, Is.EqualTo("Tina Adams"));
            Assert.That(headers[csoSubject].ExternalId, Is.EqualTo("E1001"));
            Assert.That(headers[csoSubject].ObjectTypeName, Is.EqualTo("user"));
            Assert.That(headers[csoSubject].ConnectedSystemId, Is.EqualTo(csoSchema.ConnectedSystemId));
            Assert.That(headers[csoSubject].ConnectedSystemName, Is.EqualTo("Glitterband EMEA"));
            Assert.That(headers[mvoSubject].IsDeleted, Is.False);
            Assert.That(headers[mvoSubject].DisplayName, Is.EqualTo("Alice Smith"));
            Assert.That(headers[mvoSubject].ObjectTypeName, Is.EqualTo("Person"));
            Assert.That(headers[mvoSubject].ObjectTypePluralName, Is.EqualTo("People"));
            Assert.That(headers.ContainsKey(notHeld), Is.False, "an object neither live nor recorded as deleted has no header");
        }
    }

    [Test]
    public async Task GetCausalTimelineAsync_ConnectedSystemObjectWhoseDeletionRecordNamesItById_IncludesTheDeletingItemAsync()
    {
        var schema = await _seed.SeedCsoSchemaAsync();
        var gone = Guid.NewGuid();
        var activity = await SeedActivityAsync(schema.ConnectedSystemId, T0);
        var deletingItem = await SeedItemAsync(activity, ObjectChangeType.Deleted, "Mia Young");
        var deletion = await _seed.CreateCsoDeletionRecordAsync(schema, gone, DeletionKey.SubjectId, T0, "Mia Young", "E1003");
        await using (var ctx = NewContext())
        {
            await ctx.ConnectedSystemObjectChanges.Where(c => c.Id == deletion)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ActivityRunProfileExecutionItemId, deletingItem));
        }

        var timeline = await NewJim().Activities.GetCausalTimelineAsync(new CausalTimelineSubject(CausalTimelineSubjectKind.ConnectedSystemObject, gone));

        Assert.That(timeline.Rows.Select(r => r.Entry.RunProfileExecutionItemId), Is.EqualTo(new[] { deletingItem }));
    }

    [Test]
    public async Task GetNearestCausesAsync_SynchronisationOfARecordSinceDeleted_FindsItsImportByIdRatherThanReportingTheTimelineSeveredAsync()
    {
        // A timeline is walked on the id an item holds. Once deletions stop clearing it, an item naming a deleted record
        // still reaches the import that fed it; only an item that lost its id is cut off from its timeline.
        var schema = await _seed.SeedCsoSchemaAsync();
        var record = await _seed.CreateCsoAsync(schema, "Mia Young", "E1003");
        var import = await SeedActivityAsync(schema.ConnectedSystemId, T0);
        var importItem = await SeedItemAsync(import, ObjectChangeType.Updated, "Mia Young", csoId: record, externalId: "E1003");
        var synchronisation = await SeedActivityAsync(schema.ConnectedSystemId, T0.AddMinutes(5));
        var synchronisationItem = await SeedItemAsync(synchronisation, ObjectChangeType.AttributeFlow, "Mia Young", csoId: record);
        await _seed.DeleteCsoLeavingHistoryAsync(record);

        var causes = (await NewJim().Activities.GetNearestCausesAsync([synchronisationItem]))[synchronisationItem];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(causes.Resolution, Is.EqualTo(CausalChainResolution.Resolved));
            Assert.That(causes.Cohorts.SelectMany(c => c.Members).Select(m => m.RunProfileExecutionItemId), Does.Contain(importItem));
        }
    }

    // ─── An item's page ───

    [Test]
    public async Task GetActivityRunProfileExecutionItemDetailAsync_ItemNamingALiveRecord_ResolvesItAsLiveAsync()
    {
        var schema = await _seed.SeedCsoSchemaAsync();
        var record = await _seed.CreateCsoAsync(schema, "Tina Adams", "E1001");
        var activity = await SeedActivityAsync(schema.ConnectedSystemId, T0);
        var item = await SeedItemAsync(activity, ObjectChangeType.Updated, "Tina Adams", csoId: record, externalId: "E1001");

        var detail = await NewJim().Activities.GetActivityRunProfileExecutionItemDetailAsync(item)
            ?? throw new AssertionException("the item exists");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail.Record?.State, Is.EqualTo(ObjectReferenceState.Live));
            Assert.That(detail.Record?.DisplayName, Is.EqualTo("Tina Adams"));
            Assert.That(detail.Record?.ConnectedSystemId, Is.EqualTo(schema.ConnectedSystemId));
            Assert.That(detail.RecordJoinedMetaverseObjectTypeId, Is.Null, "the record is not joined");
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemDetailAsync_ItemNamingARecordSinceDeleted_ResolvesItFromItsDeletionRecordAsync()
    {
        var schema = await _seed.SeedCsoSchemaAsync();
        var record = await _seed.CreateCsoAsync(schema, "Mia Young", "E1003");
        var activity = await SeedActivityAsync(schema.ConnectedSystemId, T0);
        var item = await SeedItemAsync(activity, ObjectChangeType.Updated, "Mia Young", csoId: record, externalId: "E1003");
        await _seed.DeleteCsoLeavingHistoryAsync(record);
        await _seed.CreateCsoDeletionRecordAsync(schema, record, DeletionKey.SubjectId, T0.AddMinutes(1), "Mia Young", "E1003");

        var detail = await NewJim().Activities.GetActivityRunProfileExecutionItemDetailAsync(item)
            ?? throw new AssertionException("the item exists");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(detail.Item.ConnectedSystemObjectId, Is.EqualTo(record));
            Assert.That(detail.Record?.State, Is.EqualTo(ObjectReferenceState.Deleted));
            Assert.That(detail.Record?.DeletedAt, Is.EqualTo(T0.AddMinutes(1)));
            Assert.That(detail.RecordJoinedMetaverseObjectTypeId, Is.Null);
        }
    }

    // ─── An Activity's items ───

    [Test]
    public async Task GetActivityRunProfileExecutionItemHeadersRangeAsync_ItemWhoseObjectIsNoLongerHeld_ReadsAndSortsByItsSnapshotsAsync()
    {
        var schema = await _seed.SeedCsoSchemaAsync();
        var live = await _seed.CreateCsoAsync(schema, "Tina Adams", "E1001");
        var gone = await _seed.CreateCsoAsync(schema, "Mia Young", "E1003");
        var activity = await SeedActivityAsync(schema.ConnectedSystemId, T0);
        await SeedItemAsync(activity, ObjectChangeType.Updated, "Tina (snapshot)", csoId: live, externalId: "E1001", objectType: "user");
        var goneItem = await SeedItemAsync(activity, ObjectChangeType.Updated, "Mia Young", csoId: gone, externalId: "E1003", objectType: "user");
        await _seed.DeleteCsoLeavingHistoryAsync(gone);
        var jim = NewJim();

        var byName = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(activity, 0, 10, sortBy: "displayname");
        var search = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(activity, 0, 10, searchQuery: "mia");
        var byType = await jim.Activities.GetActivityRunProfileExecutionItemHeadersRangeAsync(activity, 0, 10, objectTypeFilter: ["user"]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(byName.Results.Select(h => h.DisplayName), Is.EqualTo(new[] { "Mia Young", "Tina Adams" }));
            Assert.That(byName.Results.Select(h => h.ExternalIdValue), Is.EqualTo(new[] { "E1003", "E1001" }));
            Assert.That(search.Results.Select(h => h.Id), Is.EqualTo(new[] { goneItem }));
            Assert.That(byType.Results, Has.Count.EqualTo(2));
        }
    }

    // ─── Helpers ───

    private async Task SeedCsoMemberChangeAsync(CsoSchema schema, Guid group, Guid member, string distinguishedName)
    {
        await using var ctx = NewContext();
        var change = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(), ConnectedSystemId = schema.ConnectedSystemId, ChangeTime = T0, ChangeType = ObjectChangeType.Updated
        };
        ctx.Add(change);
        ctx.Entry(change).Property("ConnectedSystemObjectId").CurrentValue = group;
        var members = new ConnectedSystemObjectChangeAttribute
        {
            AttributeName = "member", AttributeType = AttributeDataType.Reference, ConnectedSystemChange = change
        };
        ctx.Entry(members).Property("AttributeId").CurrentValue = schema.MemberAttributeId;
        members.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue
        {
            ConnectedSystemObjectChangeAttribute = members, ValueChangeType = ValueChangeType.Add, StringValue = distinguishedName, ReferenceValueId = member
        });
        change.AttributeChanges.Add(members);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedMvoReportChangeAsync(MvoSchema schema, Guid manager, Guid report)
    {
        await using var ctx = NewContext();
        var change = new MetaverseObjectChange
        {
            Id = Guid.NewGuid(), MetaverseObjectId = manager, ChangeTime = T0, ChangeType = ObjectChangeType.Updated,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule
        };
        var reports = new MetaverseObjectChangeAttribute
        {
            AttributeName = "directReports", AttributeType = AttributeDataType.Reference, MetaverseObjectChange = change
        };
        ctx.Entry(reports).Property("AttributeId").CurrentValue = schema.MemberAttributeId;
        reports.ValueChanges.Add(new MetaverseObjectChangeAttributeValue
        {
            MetaverseObjectChangeAttribute = reports, ValueChangeType = ValueChangeType.Add, ReferenceValueId = report
        });
        change.AttributeChanges.Add(reports);
        ctx.Add(change);
        await ctx.SaveChangesAsync();
    }

    private async Task<Guid> SeedActivityAsync(int connectedSystemId, DateTime executed)
    {
        await using var ctx = NewContext();
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetName = "Delta Import",
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemRunType = ConnectedSystemRunType.DeltaImport,
            Executed = executed
        };
        ctx.Activities.Add(activity);
        await ctx.SaveChangesAsync();
        return activity.Id;
    }

    private async Task<Guid> SeedItemAsync(Guid activityId, ObjectChangeType changeType, string displayName, Guid? csoId = null,
        string? externalId = null, string? objectType = null)
    {
        await using var ctx = NewContext();
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ActivityId = activityId,
            ObjectChangeType = changeType,
            DisplayNameSnapshot = displayName,
            ExternalIdSnapshot = externalId,
            ObjectTypeSnapshot = objectType,
            ConnectedSystemObjectId = csoId
        };
        ctx.ActivityRunProfileExecutionItems.Add(item);
        await ctx.SaveChangesAsync();
        return item.Id;
    }
}
