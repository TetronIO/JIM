// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The schema refresh decision's "Apply and Disable Dependents" option (#1485): applying a destructive refresh
/// with a disable plan records the new schema and then disables exactly what the plan names, each rule and
/// mapping carrying the reason, under child Activities of the refresh so the history reads as one decision.
/// </summary>
[TestFixture]
public class SchemaRefreshDisableDependentsTests
{
    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private JimApplication _jim = null!;
    private string _csvPath = null!;
    private SyncRule _computerRule = null!;
    private SyncRule _userRule = null!;
    private SyncRuleMapping _faxMapping = null!;
    private List<Activity> _createdActivities = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

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
        _connectedSystemRepository.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>())).Returns(Task.CompletedTask);

        // The rules the plan acts on: one bound to a removed Object Type, one surviving rule with the mapping
        // the plan disables.
        _computerRule = new SyncRule { Id = 10, Name = "Directory Computers Inbound", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemObjectTypeId = 2 };
        _userRule = new SyncRule { Id = 11, Name = "HR Users Inbound", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemObjectTypeId = 1 };
        _faxMapping = new SyncRuleMapping { Id = 102, SyncRuleId = 11 };
        _userRule.AttributeFlowRules.Add(_faxMapping);
        _connectedSystemRepository.Setup(r => r.GetSyncRulesAsync(1, true)).ReturnsAsync([_computerRule, _userRule]);

        _jim = new JimApplication(_repository.Object);

        _csvPath = Path.Join(Path.GetTempPath(), $"jim-schema-disable-{Guid.NewGuid():N}.csv");
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
    public async Task ApplyConnectedSystemSchemaRefreshAsync_WithADisablePlan_DisablesTheInvalidatedRuleWithItsReasonAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        var previewResult = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);
        var plan = new SchemaRefreshDependents();
        plan.InvalidatedSyncRules.Add(new SchemaRefreshDependentRule
        {
            SyncRuleId = 10,
            SyncRuleName = "Directory Computers Inbound",
            ObjectTypeName = "computer",
            MappingCount = 0,
            Reason = "Object Type 'computer' is no longer reported by the Connected System (schema refresh of 21 Aug 2026)."
        });

        await _jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshAsync(connectedSystem, previewResult, plan, NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            _connectedSystemRepository.Verify(r => r.UpdateConnectedSystemSchemaAsync(connectedSystem), Times.Once,
                "The schema must still be recorded; disabling is in addition to applying, not instead of it.");
            _connectedSystemRepository.Verify(r => r.UpdateSyncRuleAsync(It.Is<SyncRule>(rule =>
                rule.Id == 10 && !rule.Enabled && rule.DisabledReason!.Contains("computer"))), Times.Once);
            Assert.That(_computerRule.Enabled, Is.False);
            Assert.That(_computerRule.DisabledReason, Does.Contain("no longer reported"));
            Assert.That(_userRule.Enabled, Is.True, "A rule the plan does not name must be left alone.");
        }
    }

    [Test]
    public async Task ApplyConnectedSystemSchemaRefreshAsync_WithADisablePlan_DisablesTheInvalidatedMappingWithItsReasonAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        var previewResult = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);
        var plan = new SchemaRefreshDependents();
        plan.InvalidatedMappings.Add(new SchemaRefreshDependentMapping
        {
            MappingId = 102,
            SyncRuleId = 11,
            SyncRuleName = "HR Users Inbound",
            Description = "faxNumber → Fax Number",
            Reason = "Attribute 'faxNumber' is no longer reported by the Connected System (schema refresh of 21 Aug 2026)."
        });

        IReadOnlyCollection<SyncRuleMapping>? persistedMappings = null;
        _connectedSystemRepository.Setup(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()))
            .Callback<IReadOnlyCollection<SyncRuleMapping>>(mappings => persistedMappings = mappings)
            .Returns(Task.CompletedTask);

        await _jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshAsync(connectedSystem, previewResult, plan, NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedMappings, Is.Not.Null, "The disabled mapping must be persisted through the bulk update path.");
            Assert.That(persistedMappings!.Single().Id, Is.EqualTo(102));
            Assert.That(_faxMapping.Enabled, Is.False);
            Assert.That(_faxMapping.DisabledReason, Does.Contain("faxNumber"));
            Assert.That(_userRule.Enabled, Is.True, "Disabling one mapping must not disable the rule that carries it.");
        }
    }

    [Test]
    public async Task ApplyConnectedSystemSchemaRefreshAsync_WithADisablePlan_RecordsChildActivitiesUnderTheRefreshAsync()
    {
        // The disables are part of one decision, so their Activities are children of the refresh's ImportSchema
        // Activity: the history reads "this refresh disabled these", not a scatter of unexplained updates.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        var previewResult = await _jim.ConnectedSystems.PreviewConnectedSystemSchemaRefreshAsync(connectedSystem);
        var plan = new SchemaRefreshDependents();
        plan.InvalidatedSyncRules.Add(new SchemaRefreshDependentRule
        {
            SyncRuleId = 10,
            SyncRuleName = "Directory Computers Inbound",
            ObjectTypeName = "computer",
            MappingCount = 0,
            Reason = "Object Type 'computer' is no longer reported by the Connected System (schema refresh of 21 Aug 2026)."
        });

        await _jim.ConnectedSystems.ApplyConnectedSystemSchemaRefreshAsync(connectedSystem, previewResult, plan, NewInitiator());

        var refreshActivity = _createdActivities.Single(a => a.TargetOperationType == ActivityTargetOperationType.ImportSchema);
        var ruleActivity = _createdActivities.Single(a => a.TargetType == ActivityTargetType.SynchronisationRule);
        Assert.That(ruleActivity.ParentActivityId, Is.EqualTo(refreshActivity.Id));
    }

    private static MetaverseObject NewInitiator() => new()
    {
        Id = Guid.NewGuid(),
        CachedDisplayName = "Test Administrator"
    };

    private ConnectedSystem CreateFileConnectorConnectedSystem()
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
        return connectedSystem;
    }
}
