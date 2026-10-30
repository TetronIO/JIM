// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Real-PostgreSQL verification of how an Activity item's attribute changes are read back for the REST API (#2033):
/// the Metaverse Object change a synchronisation recorded, the Connected System Object change an import or export
/// recorded (including one whose object has since been deleted), and the snapshot of what each staged Pending Export
/// would write. Read on a context that does not track, as JIM.Web's does, and in the same shapes object change
/// history returns.
/// </summary>
[TestFixture]
[Category("RequiresPostgres")]
public class ExecutionItemChangesReadDatabaseTests
{
    private string _connectionString = null!;

    private JimDbContext NewContext(bool tracking = false)
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .UseQueryTrackingBehavior(tracking ? QueryTrackingBehavior.TrackAll : QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL execution item change read tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        using var ctx = NewContext();
        ctx.Database.Migrate();
    }

    [SetUp]
    public async Task SetUp() => await PostgresTestDatabase.ResetAsync(_connectionString);

    [Test]
    public async Task GetExecutionItemChangesAsync_SynchronisationItem_ReadsTheMetaverseObjectChangeAndEachStagedExportAsync()
    {
        var seeded = await SeedAsync();
        var item = NewItem(seeded.ActivityId, ObjectChangeType.Projected);
        item.MetaverseObjectId = seeded.MvoId;
        item.MetaverseObjectChange = MvoChange(seeded, "Finance");
        var staged = CsoChange(seeded, ObjectChangeType.Added, "jsmith@example.com");
        item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            StagedChangeType = PendingExportChangeType.Create,
            ConnectedSystemObjectChange = staged
        });
        await SaveAsync(seeded, item);

        var changes = await NewJim().Activities.GetExecutionItemChangesAsync(item);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.ConnectedSystemObjectChange, Is.Null, "this synchronisation provisioned nothing, so it recorded no Connected System Object change");
            var mvoChange = changes.MetaverseObjectChange;
            Assert.That(mvoChange, Is.Not.Null);
            Assert.That(mvoChange!.ActivityRunProfileExecutionItemId, Is.EqualTo(item.Id));
            Assert.That(mvoChange!.AttributeChanges.Single().AttributeName, Is.EqualTo("department"));
            Assert.That(mvoChange!.AttributeChanges.Single().ValueChanges.Single().StringValue, Is.EqualTo("Finance"));
            var stagedChange = changes.StagedExportChanges.Single();
            Assert.That(stagedChange.Id, Is.EqualTo(staged.Id), "keyed by the id the outcome carries");
            Assert.That(stagedChange.AttributeChanges.Single().ValueChanges.Single().StringValue, Is.EqualTo("jsmith@example.com"));
        }
    }

    [Test]
    public async Task GetExecutionItemChangesAsync_ImportOfAnObjectSinceDeleted_StillReadsItsChangeAsync()
    {
        var seeded = await SeedAsync();
        var item = NewItem(seeded.ActivityId, ObjectChangeType.Deleted);
        var change = CsoChange(seeded, ObjectChangeType.Deleted, "jsmith@example.com");
        change.ConnectedSystemObject = null;
        change.DeletedConnectedSystemObjectId = Guid.NewGuid();
        item.ConnectedSystemObjectChange = change;
        await SaveAsync(seeded, item);

        var changes = await NewJim().Activities.GetExecutionItemChangesAsync(item);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.ConnectedSystemObjectChange?.Id, Is.EqualTo(change.Id),
                "object change history cannot reach this change any more; the item still can");
            Assert.That(changes.ConnectedSystemObjectChange?.ChangeType, Is.EqualTo(ObjectChangeType.Deleted));
            Assert.That(changes.MetaverseObjectChange, Is.Null);
            Assert.That(changes.StagedExportChanges, Is.Empty);
        }
    }

    [Test]
    public async Task GetExecutionItemChangesAsync_ItemThatRecordedNoChanges_ReadsNoneAsync()
    {
        var seeded = await SeedAsync();
        var item = NewItem(seeded.ActivityId, ObjectChangeType.NoChange);
        await SaveAsync(seeded, item);

        var changes = await NewJim().Activities.GetExecutionItemChangesAsync(item);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(changes.MetaverseObjectChange, Is.Null);
            Assert.That(changes.ConnectedSystemObjectChange, Is.Null);
            Assert.That(changes.StagedExportChanges, Is.Empty);
        }
    }

    [Test]
    public async Task GetMvoChangeHistoryAsync_AfterTheProjectionWasShared_StillReadsTheObjectsHistoryAsync()
    {
        // The item read and object history share one projection; object history must read exactly as it did.
        var seeded = await SeedAsync();
        var item = NewItem(seeded.ActivityId, ObjectChangeType.Joined);
        item.MetaverseObjectId = seeded.MvoId;
        item.MetaverseObjectChange = MvoChange(seeded, "Sales");
        await SaveAsync(seeded, item);

        var (items, total) = await NewJim().Metaverse.GetMvoChangeHistoryAsync(seeded.MvoId, 1, 10);

        Assert.That(total, Is.EqualTo(1));
        Assert.That(items.Single().AttributeChanges.Single().ValueChanges.Single().StringValue, Is.EqualTo("Sales"));
    }

    /// <summary>
    /// A change to a large group carries one value change per member, so the shared projection reads each attribute's
    /// first values and its total rather than all of them (#2498); the rest are paged on demand.
    /// </summary>
    [Test]
    public async Task GetExecutionItemChangesAsync_AttributeWithMoreValueChangesThanTheCap_ReadsTheCapAndTheTotalAsync()
    {
        var seeded = await SeedAsync();
        var item = NewItem(seeded.ActivityId, ObjectChangeType.Joined);
        item.MetaverseObjectId = seeded.MvoId;
        item.MetaverseObjectChange = MvoChange(seeded, Enumerable.Range(0, 25).Select(i => $"Department {i:D2}").ToArray());
        item.ConnectedSystemObjectChange = CsoChange(seeded, ObjectChangeType.Updated, Enumerable.Range(0, 25).Select(i => $"user{i:D2}@example.com").ToArray());
        await SaveAsync(seeded, item);

        var changes = await NewJim().Activities.GetExecutionItemChangesAsync(item);

        var csoAttribute = changes.ConnectedSystemObjectChange!.AttributeChanges.Single();
        var mvoAttribute = changes.MetaverseObjectChange!.AttributeChanges.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(csoAttribute.ValueChanges, Has.Count.EqualTo(10), "the Connected System Object side reads the capped sample");
            Assert.That(csoAttribute.ValueChangeTotal, Is.EqualTo(25), "and says how many there are in all");
            Assert.That(mvoAttribute.ValueChanges, Has.Count.EqualTo(10), "the Metaverse Object side reads the capped sample");
            Assert.That(mvoAttribute.ValueChangeTotal, Is.EqualTo(25), "and says how many there are in all");
        }
    }

    [Test]
    public async Task GetExecutionItemChangesAsync_AttributeWithinTheCap_ReadsEveryValueChangeAndAMatchingTotalAsync()
    {
        var seeded = await SeedAsync();
        var item = NewItem(seeded.ActivityId, ObjectChangeType.Updated);
        item.ConnectedSystemObjectChange = CsoChange(seeded, ObjectChangeType.Updated, "a@example.com", "b@example.com", "c@example.com");
        await SaveAsync(seeded, item);

        var changes = await NewJim().Activities.GetExecutionItemChangesAsync(item);

        var attribute = changes.ConnectedSystemObjectChange!.AttributeChanges.Single();
        Assert.That(attribute.ValueChanges.Select(v => v.StringValue), Is.EquivalentTo(new[] { "a@example.com", "b@example.com", "c@example.com" }));
        Assert.That(attribute.ValueChangeTotal, Is.EqualTo(3));
    }

    /// <summary>
    /// The portal's item page reads the item bounded (#2498): what names its object, its change records without their
    /// values, and a summary of each change in their place, however large the group the item changed.
    /// </summary>
    [Test]
    public async Task GetActivityRunProfileExecutionItemDetailAsync_ReadsWhatNamesTheObjectAndSummarisesEachChangeAsync()
    {
        var seeded = await SeedAsync();
        var item = NewItem(seeded.ActivityId, ObjectChangeType.Updated);
        item.ConnectedSystemObject = seeded.Cso;
        item.MetaverseObjectId = seeded.MvoId;
        item.MetaverseObjectChange = MvoChange(seeded, Enumerable.Range(0, 25).Select(i => $"Department {i:D2}").ToArray());
        item.ConnectedSystemObjectChange = CsoChange(seeded, ObjectChangeType.Updated, Enumerable.Range(0, 25).Select(i => $"user{i:D2}@example.com").ToArray());
        var staged = CsoChange(seeded, ObjectChangeType.Added, "a@example.com", "b@example.com", "c@example.com");
        item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            StagedChangeType = PendingExportChangeType.Create,
            ConnectedSystemObjectChange = staged
        });
        await SaveAsync(seeded, item);

        var detail = await NewJim().Activities.GetActivityRunProfileExecutionItemDetailAsync(item.Id);

        Assert.That(detail, Is.Not.Null);
        var read = detail!.Item;
        var outcome = read.SyncOutcomes.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(read.ConnectedSystemObject!.NameOrId, Is.EqualTo("John Smith"));
            Assert.That(read.ConnectedSystemObject!.ExternalIdAttributeValue?.StringValue, Is.EqualTo("jsmith@example.com"));
            Assert.That(read.ConnectedSystemObject!.AttributeValues, Has.Count.EqualTo(2), "what names and identifies it, never its group memberships");
            Assert.That(read.ConnectedSystemObjectChange!.AttributeChanges, Is.Empty, "a change's values are read a window at a time");
            Assert.That(read.MetaverseObjectChange!.AttributeChanges, Is.Empty);
            Assert.That(outcome.ConnectedSystemObjectChange?.Id, Is.EqualTo(staged.Id), "the outcome's staged change is there to name its record");
            Assert.That(outcome.ConnectedSystemObjectChange!.AttributeChanges, Is.Empty);
            Assert.That(detail!.ChangeValueSummaries[read.ConnectedSystemObjectChange!.Id].RowCount, Is.EqualTo(25));
            Assert.That(detail!.ChangeValueSummaries[read.MetaverseObjectChange!.Id].RowCount, Is.EqualTo(25));
            Assert.That(detail!.ChangeValueSummaries[staged.Id].RowCount, Is.EqualTo(3));
        }
    }

    [Test]
    public async Task GetActivityRunProfileExecutionItemDetailAsync_NoSuchItem_ReturnsNullAsync()
    {
        await SeedAsync();

        var detail = await NewJim().Activities.GetActivityRunProfileExecutionItemDetailAsync(Guid.NewGuid());

        Assert.That(detail, Is.Null);
    }

    [Test]
    public async Task GetConnectedSystemObjectNamesAsync_NamesEachObjectByTheNamingPolicyAndSkipsMissingOnesAsync()
    {
        var seeded = await SeedAsync();
        var missing = Guid.NewGuid();

        var names = await NewJim().ConnectedSystems.GetConnectedSystemObjectNamesAsync([seeded.Cso.Id, missing]);

        Assert.That(names, Is.EqualTo(new Dictionary<Guid, string> { [seeded.Cso.Id] = "John Smith" }));
    }

    private JimApplication NewJim() => new(new PostgresDataRepository(NewContext()));

    private static ActivityRunProfileExecutionItem NewItem(Guid activityId, ObjectChangeType type) => new()
    {
        Id = Guid.NewGuid(),
        ActivityId = activityId,
        ObjectChangeType = type
    };

    private static MetaverseObjectChange MvoChange(Seeded seeded, params string[] departments)
    {
        var change = new MetaverseObjectChange
        {
            MetaverseObject = seeded.Mvo,
            ChangeTime = DateTime.UtcNow,
            ChangeType = ObjectChangeType.Updated,
            ChangeInitiatorType = MetaverseObjectChangeInitiatorType.SynchronisationRule
        };
        var attributeChange = new MetaverseObjectChangeAttribute
        {
            Attribute = seeded.MvAttribute,
            AttributeName = "department",
            AttributeType = AttributeDataType.Text,
            MetaverseObjectChange = change
        };
        foreach (var department in departments)
            attributeChange.ValueChanges.Add(new MetaverseObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, department));
        change.AttributeChanges.Add(attributeChange);
        return change;
    }

    private static ConnectedSystemObjectChange CsoChange(Seeded seeded, ObjectChangeType type, params string[] mails)
    {
        var change = new ConnectedSystemObjectChange
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = seeded.SystemId,
            ConnectedSystemObject = seeded.Cso,
            ChangeTime = DateTime.UtcNow,
            ChangeType = type,
            InitiatedByType = ActivityInitiatorType.System
        };
        var attributeChange = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = seeded.CsAttribute,
            AttributeName = "mail",
            AttributeType = AttributeDataType.Text,
            ConnectedSystemChange = change
        };
        foreach (var mail in mails)
            attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(attributeChange, ValueChangeType.Add, mail));
        change.AttributeChanges.Add(attributeChange);
        return change;
    }

    private async Task SaveAsync(Seeded seeded, ActivityRunProfileExecutionItem item)
    {
        await using var ctx = NewContext(tracking: true);
        // The seeded rows exist already; attach them so adding the item's graph inserts only what is new.
        ctx.AttachRange(seeded.Mvo, seeded.MvAttribute, seeded.Cso, seeded.CsAttribute);
        ctx.ActivityRunProfileExecutionItems.Add(item);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// The seeded rows' ids, with a key-only stub of each row a change refers to (the change entities carry those
    /// references as shadow foreign keys, so a test reaches them through these navigations).
    /// </summary>
    private sealed record Seeded(Guid ActivityId, int SystemId, Guid MvoId)
    {
        public required MetaverseObject Mvo { get; init; }
        public required MetaverseAttribute MvAttribute { get; init; }
        public required ConnectedSystemObject Cso { get; init; }
        public required ConnectedSystemObjectTypeAttribute CsAttribute { get; init; }
    }

    /// <summary>A Connected System Object and a Metaverse Object, each with an attribute to change, and an Activity.</summary>
    private async Task<Seeded> SeedAsync()
    {
        await using var seed = NewContext(tracking: true);
        var connectorDefinition = new ConnectorDefinition { Name = "Test Connector", BuiltIn = true };
        var system = new ConnectedSystem { Name = "Corporate Directory", ConnectorDefinition = connectorDefinition };
        var csType = new ConnectedSystemObjectType { Name = "user", ConnectedSystem = system, Selected = true };
        var mail = new ConnectedSystemObjectTypeAttribute
        {
            Name = "mail", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true, IsExternalId = true
        };
        var displayName = new ConnectedSystemObjectTypeAttribute
        {
            Name = "displayName", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued, Selected = true
        };
        var memberOf = new ConnectedSystemObjectTypeAttribute
        {
            Name = "memberOf", ConnectedSystemObjectType = csType, Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued, Selected = true
        };
        csType.Attributes.AddRange([mail, displayName, memberOf]);
        var mvType = new MetaverseObjectType { Name = "Person", PluralName = "People" };
        var department = new MetaverseAttribute { Name = "department", Type = AttributeDataType.Text, AttributePlurality = AttributePlurality.SingleValued };
        mvType.Attributes.Add(department);
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType, Created = DateTime.UtcNow };
        var activity = new Activity
        {
            Id = Guid.NewGuid(),
            TargetName = "Full Synchronisation",
            TargetOperationType = ActivityTargetOperationType.Execute,
            Status = ActivityStatus.Complete,
            InitiatedByType = ActivityInitiatorType.System
        };
        seed.AddRange(connectorDefinition, system, csType, mvType, mvo, activity);
        await seed.SaveChangesAsync();

        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = system.Id,
            TypeId = csType.Id,
            ExternalIdAttributeId = mail.Id,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow,
            JoinType = ConnectedSystemObjectJoinType.NotJoined
        };
        // Named and identified, and a member of many groups: a detail read takes the first two and none of the third.
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, Attribute = mail, StringValue = "jsmith@example.com" });
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, Attribute = displayName, StringValue = "John Smith" });
        for (var i = 0; i < 30; i++)
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue { ConnectedSystemObject = cso, Attribute = memberOf, StringValue = $"CN=Group {i:D2}" });
        seed.Add(cso);
        await seed.SaveChangesAsync();

        return new Seeded(activity.Id, system.Id, mvo.Id)
        {
            Mvo = new MetaverseObject { Id = mvo.Id },
            MvAttribute = new MetaverseAttribute { Id = department.Id, Name = department.Name },
            Cso = new ConnectedSystemObject { Id = cso.Id },
            CsAttribute = new ConnectedSystemObjectTypeAttribute { Id = mail.Id, Name = mail.Name }
        };
    }
}
