// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Tasking;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The schema refresh decision's "Apply and Remove" option (#1485): applying a destructive refresh with a
/// removal plan records the new schema, deletes exactly the configuration the plan names under child Activities
/// of the refresh, and queues the data-removal worker task carrying the pre-refresh ids of what the Connected
/// System no longer reports.
/// </summary>
[TestFixture]
public class SchemaRefreshApplyAndRemoveTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private Mock<ITaskingRepository> _taskingRepository = null!;
    private JimApplication _jim = null!;
    private string _csvPath = null!;
    private SyncRule _computerRule = null!;
    private SyncRule _userRule = null!;
    private SyncRuleMapping _faxMapping = null!;
    private List<Activity> _createdActivities = null!;
    private List<WorkerTask> _createdWorkerTasks = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _taskingRepository = new Mock<ITaskingRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        _repository.Setup(r => r.Tasking).Returns(_taskingRepository.Object);

        _createdActivities = [];
        _activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _createdActivities.Add(a);
            })
            .Returns(Task.CompletedTask);
        _activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.DeleteSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.DeleteSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);

        _createdWorkerTasks = [];
        _taskingRepository.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => _createdWorkerTasks.Add(t))
            .Returns(Task.CompletedTask);

        // The rules the plan acts on: one bound to the removed Object Type, one surviving rule with the mapping
        // the plan removes.
        _computerRule = new SyncRule { Id = 10, Name = "Directory Computers Inbound", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemId = 1, ConnectedSystemObjectTypeId = 2 };
        _userRule = new SyncRule { Id = 11, Name = "HR Users Inbound", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemId = 1, ConnectedSystemObjectTypeId = 1 };
        _faxMapping = new SyncRuleMapping { Id = 102, SyncRuleId = 11 };
        _userRule.AttributeFlowRules.Add(_faxMapping);
        _connectedSystemRepository.Setup(r => r.GetSyncRulesAsync(1, true)).ReturnsAsync([_computerRule, _userRule]);

        _jim = new JimApplication(_repository.Object);

        _csvPath = Path.Join(Path.GetTempPath(), $"jim-schema-remove-{Guid.NewGuid():N}.csv");
        File.WriteAllText(_csvPath, "id,displayName\n1,Test User\n");
    }

    [TearDown]
    public void TearDown()
    {
        _jim?.Dispose();
        if (File.Exists(_csvPath))
            File.Delete(_csvPath);
    }

    [Test]
    public async Task ApplyWithRemoval_WithInvalidatedConfiguration_DeletesItUnderChildActivitiesOfTheRefreshAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem(seedPreRefreshSchema: true);
        var previewResult = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);
        var plan = BuildRemovalPlan();

        await _jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshWithRemovalAsync(connectedSystem, previewResult, plan, NewInitiator());

        var refreshActivity = _createdActivities.Single(a => a.TargetOperationType == ActivityTargetOperationType.ImportSchema);
        var deleteActivities = _createdActivities.Where(a =>
            a.TargetType == ActivityTargetType.SynchronisationRule &&
            a.TargetOperationType == ActivityTargetOperationType.Delete).ToList();

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(connectedSystem), Times.Once,
                "The schema must still be recorded; removing is in addition to applying, not instead of it.");
            _connectedSystemRepository.Verify(r => r.DeleteSyncRuleAsync(It.Is<SyncRule>(rule => rule.Id == 10)), Times.Once);
            _connectedSystemRepository.Verify(r => r.DeleteSyncRuleMappingAsync(It.Is<SyncRuleMapping>(m => m.Id == 102)), Times.Once);
            _connectedSystemRepository.Verify(r => r.DeleteSyncRuleAsync(It.Is<SyncRule>(rule => rule.Id == 11)), Times.Never,
                "A rule the plan does not name must be left alone.");
            Assert.That(deleteActivities, Has.Count.EqualTo(2), "One delete Activity per removed rule and per removed mapping.");
            Assert.That(deleteActivities.Select(a => a.ParentActivityId), Is.All.EqualTo(refreshActivity.Id),
                "The deletions are part of one decision, so their Activities are children of the refresh.");
        }
    }

    [Test]
    public async Task ApplyWithRemoval_WithRemovedTypesAndAttributes_QueuesTheRemovalTaskWithResolvedPreRefreshIdsAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem(seedPreRefreshSchema: true);
        var previewResult = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);
        var plan = BuildRemovalPlan();

        var creationResult = await _jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshWithRemovalAsync(connectedSystem, previewResult, plan, NewInitiator());

        var removalTask = _createdWorkerTasks.OfType<SchemaRefreshRemovalWorkerTask>().Single();
        var removalActivity = _createdActivities.Single(a => a.TargetOperationType == ActivityTargetOperationType.SchemaRefreshRemoval);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(creationResult, Is.Not.Null);
            Assert.That(creationResult!.Success, Is.True);
            Assert.That(removalTask.ConnectedSystemId, Is.EqualTo(1));
            Assert.That(removalTask.RemovedObjectTypeIds, Is.EquivalentTo(new[] { 2 }),
                "The pre-refresh id of the removed 'computer' Object Type.");
            Assert.That(removalTask.RemovedAttributeIds, Is.EquivalentTo(new[] { 13 }),
                "The pre-refresh id of the removed 'faxNumber' attribute on the surviving 'user' type.");
            Assert.That(removalActivity.TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystem));
            Assert.That(removalActivity.ConnectedSystemId, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task ApplyWithRemoval_WithNothingRemovedFromTheSchema_DeletesConfigurationButQueuesNoDataTaskAsync()
    {
        // The seeded schema matches what the Connector reports, so the refresh removes nothing; a plan can still
        // name configuration (a mapping invalidated by a definition change), and only that work happens.
        var connectedSystem = CreateFileConnectorConnectedSystem(seedPreRefreshSchema: false);
        var previewResult = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);
        var plan = new SchemaRefreshDependents();
        plan.InvalidatedMappings.Add(new SchemaRefreshDependentMapping
        {
            MappingId = 102,
            SyncRuleId = 11,
            SyncRuleName = "HR Users Inbound",
            Description = "faxNumber → Fax Number",
            Reason = "Attribute 'faxNumber' was redefined by the Connected System (schema refresh of 21 Aug 2026)."
        });

        var creationResult = await _jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshWithRemovalAsync(connectedSystem, previewResult, plan, NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(creationResult, Is.Null, "No removed Object Types or attributes means no data to remove and no task to queue.");
            Assert.That(_createdWorkerTasks, Is.Empty);
            _connectedSystemRepository.Verify(r => r.DeleteSyncRuleMappingAsync(It.Is<SyncRuleMapping>(m => m.Id == 102)), Times.Once);
        }
    }

    [Test]
    public async Task ExecuteSchemaRefreshRemoval_WithRemovedTypesAndAttributes_ObsoletesObjectsAndDeletesValuesWithPerObjectResultsAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem(seedPreRefreshSchema: true);
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemAsync(1, It.IsAny<bool>())).ReturnsAsync(connectedSystem);

        var csoIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var csos = csoIds.Select(id => new ConnectedSystemObject { Id = id, ConnectedSystemId = 1, TypeId = 2 }).ToList();
        _connectedSystemRepository.Setup(r => r.GetLiveConnectedSystemObjectIdsOfTypeAsync(1, 2)).ReturnsAsync(csoIds);
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemObjectsByIdsNoTrackingAsync(1, It.IsAny<IEnumerable<Guid>>())).ReturnsAsync(csos);
        _connectedSystemRepository.Setup(r => r.ObsoleteConnectedSystemObjectsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync(2);
        _connectedSystemRepository.Setup(r => r.DeletePendingExportsForConnectedSystemObjectsAsync(It.IsAny<IReadOnlyCollection<Guid>>())).ReturnsAsync(1);
        _connectedSystemRepository.Setup(r => r.DeleteConnectedSystemAttributeValuesByAttributeIdsAsync(1, It.IsAny<IReadOnlyCollection<int>>())).ReturnsAsync(5);

        List<ActivityRunProfileExecutionItem>? persistedItems = null;
        _activityRepository.Setup(r => r.CreateActivityRunProfileExecutionItemsAsync(It.IsAny<IReadOnlyCollection<ActivityRunProfileExecutionItem>>()))
            .Callback<IReadOnlyCollection<ActivityRunProfileExecutionItem>>(items => persistedItems = items.ToList())
            .Returns(Task.CompletedTask);

        var task = SchemaRefreshRemovalWorkerTask.ForUser(1, [2], [13], Guid.NewGuid(), "Test Administrator");
        task.Activity = new Activity { Id = Guid.NewGuid(), TargetType = ActivityTargetType.ConnectedSystem, TargetOperationType = ActivityTargetOperationType.SchemaRefreshRemoval, ConnectedSystemId = 1 };

        var result = await _jim.ConnectedSystems.ExecuteSchemaRefreshRemovalAsync(task);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ConnectedSystemObjectsObsoleted, Is.EqualTo(2));
            Assert.That(result.PendingExportsRemoved, Is.EqualTo(1));
            Assert.That(result.AttributeValuesRemoved, Is.EqualTo(5));
            _connectedSystemRepository.Verify(r => r.ObsoleteConnectedSystemObjectsByIdsAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2)), Times.Once);
            _connectedSystemRepository.Verify(r => r.DeletePendingExportsForConnectedSystemObjectsAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2)), Times.Once);
            _connectedSystemRepository.Verify(r => r.DeleteConnectedSystemAttributeValuesByAttributeIdsAsync(1, It.Is<IReadOnlyCollection<int>>(ids => ids.Single() == 13)), Times.Once);
            Assert.That(persistedItems, Is.Not.Null, "Per-object results must be recorded on the Activity.");
            Assert.That(persistedItems!, Has.Count.EqualTo(2));
            Assert.That(persistedItems!.Select(i => i.ObjectChangeType), Is.All.EqualTo(ObjectChangeType.Deleted));
            Assert.That(persistedItems!.Select(i => i.ObjectTypeSnapshot), Is.All.EqualTo("computer"),
                "The removed Object Type's name must survive on the item; the object itself will be deleted by the sync pipeline.");
            Assert.That(task.Activity.TotalDeleted, Is.EqualTo(2));
        }
    }

    private static SchemaRefreshDependents BuildRemovalPlan()
    {
        var plan = new SchemaRefreshDependents();
        plan.InvalidatedSyncRules.Add(new SchemaRefreshDependentRule
        {
            SyncRuleId = 10,
            SyncRuleName = "Directory Computers Inbound",
            ObjectTypeName = "computer",
            MappingCount = 0,
            Reason = "Object Type 'computer' is no longer reported by the Connected System (schema refresh of 21 Aug 2026)."
        });
        plan.InvalidatedMappings.Add(new SchemaRefreshDependentMapping
        {
            MappingId = 102,
            SyncRuleId = 11,
            SyncRuleName = "HR Users Inbound",
            Description = "faxNumber → Fax Number",
            Reason = "Attribute 'faxNumber' is no longer reported by the Connected System (schema refresh of 21 Aug 2026)."
        });
        return plan;
    }

    private static MetaverseObject NewInitiator() => new()
    {
        Id = Guid.NewGuid(),
        CachedDisplayName = "Test Administrator"
    };

    /// <summary>
    /// A File-connector Connected System whose seeded schema (optionally) holds more than the CSV reports:
    /// the 'computer' Object Type and the 'faxNumber' attribute on 'user' both vanish on refresh, which is
    /// what gives the preview real removals to resolve pre-refresh ids from.
    /// </summary>
    private ConnectedSystem CreateFileConnectorConnectedSystem(bool seedPreRefreshSchema)
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName };
        _jim.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        var connectedSystem = new ConnectedSystem
        {
            Id = 1,
            Name = "Test File System",
            ConnectorDefinition = connectorDefinition,
            SettingValues = connectorDefinition.Settings.Select(s => new ConnectedSystemSettingValue
            {
                Setting = s,
                StringValue = s.DefaultStringValue
            }).ToList()
        };

        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue = _csvPath;
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "Object Type").StringValue = "user";

        if (seedPreRefreshSchema)
        {
            var userType = new ConnectedSystemObjectType { Id = 1, Name = "user", Selected = true };
            userType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 11, Name = "id", Type = AttributeDataType.Number, ConnectedSystemObjectType = userType });
            userType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 12, Name = "displayName", Type = AttributeDataType.Text, ConnectedSystemObjectType = userType });
            userType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 13, Name = "faxNumber", Type = AttributeDataType.Text, ConnectedSystemObjectType = userType });
            var computerType = new ConnectedSystemObjectType { Id = 2, Name = "computer", Selected = true };
            computerType.Attributes.Add(new ConnectedSystemObjectTypeAttribute { Id = 21, Name = "id", Type = AttributeDataType.Number, ConnectedSystemObjectType = computerType });
            connectedSystem.ObjectTypes = [userType, computerType];
        }

        return connectedSystem;
    }
}
