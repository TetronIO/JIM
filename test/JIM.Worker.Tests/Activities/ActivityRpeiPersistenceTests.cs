// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;

namespace JIM.Worker.Tests.Activities;

/// <summary>
/// Proves the layer-respecting persistence path for Run Profile Execution Items recorded outside sync task
/// processing (issue #1020): <c>ActivityServer.AddRunProfileExecutionItemsAsync</c> must attach the items to the
/// Activity, persist them together with their sync outcome trees and any Connected System Object change snapshots,
/// and must not re-insert pre-existing entities (attribute definitions) referenced by those snapshots.
/// </summary>
[TestFixture]
public class ActivityRpeiPersistenceTests
{
    private JimDbContext DbContext = null!;
    private PostgresDataRepository Repository = null!;
    private JimApplication Jim = null!;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        DbContext = new JimDbContext(options);
        Repository = new PostgresDataRepository(DbContext);
        Jim = new JimApplication(Repository, syncRepository: new JIM.InMemoryData.SyncRepository());
    }

    [TearDown]
    public void TearDown()
    {
        Jim?.Dispose();
        (Repository as IDisposable)?.Dispose();
        DbContext?.Dispose();
    }

    private async Task<Activity> CreatePersistedActivityAsync()
    {
        var activity = new Activity
        {
            TargetName = "Metaverse Object Housekeeping",
            TargetType = ActivityTargetType.MetaverseObjectHousekeeping,
            TargetOperationType = ActivityTargetOperationType.Execute
        };
        await Jim.Activities.CreateActivityWithTriadAsync(activity, ActivityInitiatorType.System, null, null);
        return activity;
    }

    /// <summary>
    /// Items and their sync outcome trees must be attached to the Activity and persisted with the Activity's id.
    /// </summary>
    [Test]
    public async Task AddRunProfileExecutionItems_ItemsWithOutcomes_PersistsItemsAndOutcomesAsync()
    {
        // Arrange
        var activity = await CreatePersistedActivityAsync();
        var deletedRpei = new ActivityRunProfileExecutionItem
        {
            ObjectChangeType = ObjectChangeType.Deleted,
            DisplayNameSnapshot = "Lena Leaver",
            ObjectTypeSnapshot = "Person"
        };
        deletedRpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
            TargetEntityDescription = "Lena Leaver"
        });

        // Act
        await Jim.Activities.AddRunProfileExecutionItemsAsync(activity, [deletedRpei]);

        // Assert: attached to the Activity.
        Assert.That(activity.RunProfileExecutionItems, Has.Count.EqualTo(1));
        Assert.That(deletedRpei.ActivityId, Is.EqualTo(activity.Id));

        // Assert: persisted with the outcome tree.
        var persistedRpei = await DbContext.ActivityRunProfileExecutionItems
            .SingleAsync(r => r.ActivityId == activity.Id);
        Assert.That(persistedRpei.ObjectChangeType, Is.EqualTo(ObjectChangeType.Deleted));
        Assert.That(persistedRpei.DisplayNameSnapshot, Is.EqualTo("Lena Leaver"));

        var persistedOutcome = await DbContext.ActivityRunProfileExecutionItemSyncOutcomes
            .SingleAsync(o => o.ActivityRunProfileExecutionItemId == persistedRpei.Id);
        Assert.That(persistedOutcome.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted));
    }

    /// <summary>
    /// A Connected System Object change snapshot carried on an outcome references a pre-existing attribute
    /// definition. Persisting the items must store the snapshot hierarchy without re-inserting (duplicating)
    /// the attribute definition, and must sever the navigation to the pre-existing Connected System Object,
    /// keeping only its scalar foreign key.
    /// </summary>
    [Test]
    public async Task AddRunProfileExecutionItems_OutcomeWithChangeSnapshot_DoesNotReinsertExistingEntitiesAsync()
    {
        // Arrange: a pre-existing attribute definition, persisted separately (as schema import would).
        var csoType = new ConnectedSystemObjectType { Name = "Group", ConnectedSystemId = 5 };
        var memberAttribute = new ConnectedSystemObjectTypeAttribute
        {
            Name = "member",
            Type = AttributeDataType.Reference,
            ConnectedSystemObjectType = csoType,
            Selected = true
        };
        DbContext.ConnectedSystemAttributes.Add(memberAttribute);
        await DbContext.SaveChangesAsync();
        var attributeCountBefore = await DbContext.ConnectedSystemAttributes.CountAsync();

        var activity = await CreatePersistedActivityAsync();

        var csoId = Guid.NewGuid();
        var change = new ConnectedSystemObjectChange
        {
            ConnectedSystemId = 5,
            ConnectedSystemObject = new ConnectedSystemObject { Id = csoId, ConnectedSystemId = 5, TypeId = csoType.Id },
            ChangeType = ObjectChangeType.PendingExport,
            ChangeTime = DateTime.UtcNow,
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "System"
        };
        var attributeChange = new ConnectedSystemObjectChangeAttribute
        {
            Attribute = memberAttribute,
            AttributeName = memberAttribute.Name,
            AttributeType = memberAttribute.Type,
            ConnectedSystemChange = change
        };
        attributeChange.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue(
            attributeChange, ValueChangeType.Remove, "uid=lena.leaver,ou=People,dc=glitterband,dc=local"));
        change.AttributeChanges.Add(attributeChange);

        var recallRpei = new ActivityRunProfileExecutionItem
        {
            ObjectChangeType = ObjectChangeType.PendingExport,
            ConnectedSystemObjectId = csoId,
            PendingExportId = Guid.NewGuid()
        };
        recallRpei.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            ConnectedSystemObjectChange = change
        });

        // Act
        await Jim.Activities.AddRunProfileExecutionItemsAsync(activity, [recallRpei]);

        // Assert: the snapshot hierarchy persisted.
        var persistedChange = await DbContext.ConnectedSystemObjectChanges.SingleAsync();
        Assert.That(persistedChange.ConnectedSystemObjectId, Is.EqualTo(csoId),
            "The snapshot must keep the Connected System Object's scalar foreign key");
        Assert.That(persistedChange.ConnectedSystemObject, Is.Null,
            "The navigation to the pre-existing Connected System Object must be severed, not re-inserted");
        var persistedAttributeChange = await DbContext.ConnectedSystemObjectChangeAttributes.SingleAsync();
        Assert.That(persistedAttributeChange.AttributeName, Is.EqualTo("member"));
        Assert.That(DbContext.Entry(persistedAttributeChange).Property("AttributeId").CurrentValue,
            Is.EqualTo(memberAttribute.Id), "The attribute definition's foreign key must be preserved");
        Assert.That(await DbContext.ConnectedSystemObjectChangeAttributeValues.CountAsync(), Is.EqualTo(1));

        // Assert: no pre-existing entities were duplicated or re-inserted.
        Assert.That(await DbContext.ConnectedSystemAttributes.CountAsync(), Is.EqualTo(attributeCountBefore),
            "Persisting the snapshot must not re-insert the pre-existing attribute definition");
        Assert.That(await DbContext.ConnectedSystemObjects.CountAsync(), Is.Zero,
            "Persisting the snapshot must not insert the referenced Connected System Object");
    }

    /// <summary>
    /// An empty item collection must be a no-op.
    /// </summary>
    [Test]
    public async Task AddRunProfileExecutionItems_NoItems_IsNoOpAsync()
    {
        var activity = await CreatePersistedActivityAsync();

        await Jim.Activities.AddRunProfileExecutionItemsAsync(activity, []);

        Assert.That(activity.RunProfileExecutionItems, Is.Empty);
        Assert.That(await DbContext.ActivityRunProfileExecutionItems.CountAsync(), Is.Zero);
    }
}
