// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Connectors;
using JIM.Connectors.File;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for previewing a schema refresh (#421): retrieve the Connected System's schema and report what
/// a refresh would change, without persisting anything. A client previews, inspects the result, then commits with
/// the existing import-schema endpoint. What matters is that the preview really is a read (no persistence, no
/// Activity) and that the result carries the drift signals a caller decides on.
/// </summary>
[TestFixture]
public class SynchronisationControllerSchemaRefreshPreviewTests
{
    private const int ConnectedSystemId = 3;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private string _csvPath = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();

        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepo.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);
        _connectedSystemRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        _connectedSystemRepo.Setup(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>())).Returns(Task.CompletedTask);
        // Dependent detection loads the system's rules whenever the diff is destructive; default to none.
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(It.IsAny<int>(), It.IsAny<bool>())).ReturnsAsync([]);

        _csvPath = Path.Join(Path.GetTempPath(), $"jim-schema-rest-preview-{Guid.NewGuid():N}.csv");
        File.WriteAllText(_csvPath, "id,displayName\n1,Test User\n");

        _application = new JimApplication(_repository.Object);
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object);

        var apiKeyId = Guid.NewGuid();
        _apiKeyRepo.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey
        {
            Id = apiKeyId,
            Name = "TestApiKey",
            KeyHash = "test-hash",
            KeyPrefix = "test",
            IsEnabled = true,
            Created = DateTime.UtcNow
        });

        var identity = new ClaimsIdentity(
        [
            new Claim("auth_method", "api_key"),
            new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
            new Claim(ClaimTypes.Name, "TestApiKey")
        ], "ApiKey");

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
        if (File.Exists(_csvPath))
            File.Delete(_csvPath);
    }

    [Test]
    public async Task PreviewSchemaImport_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(99)).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.PreviewConnectedSystemSchemaImportAsync(99);

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task PreviewSchemaImport_ReportsWhatWouldChangeAndPersistsNothingAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Number },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text },
                    new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "department", Type = AttributeDataType.Text }
                ]
            }
        ];
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);

        var result = await _controller.PreviewConnectedSystemSchemaImportAsync(ConnectedSystemId);

        var ok = result as OkObjectResult;
        Assert.That(ok, Is.Not.Null);
        var dto = ok!.Value as SchemaRefreshResultDto;
        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.Success, Is.True);
            Assert.That(dto!.RemovedAttributes["user"], Does.Contain("department"));
            Assert.That(dto!.HasRemovalsOrDefinitionChanges, Is.True);
            _connectedSystemRepo.Verify(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>()), Times.Never,
                "A preview must not persist the merged schema.");
            _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never,
                "A preview changes nothing in JIM, so it must not record an Activity.");
        }
    }

    [Test]
    public async Task PreviewSchemaImport_WithDestructiveChanges_NamesTheDependentsAsync()
    {
        // The decision needs the dependents on the wire (#1485): a REST caller reviewing the diff must see
        // what the removals invalidate, exactly as the portal review names them.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        var departmentAttr = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "department", Type = AttributeDataType.Text };
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Number },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text },
                    departmentAttr
                ]
            }
        ];
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);

        var rule = new SyncRule { Id = 20, Name = "HR Users Inbound", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemObjectTypeId = 7 };
        var mapping = new SyncRuleMapping { Id = 200, TargetMetaverseAttribute = new MetaverseAttribute { Id = 900, Name = "Department", Type = AttributeDataType.Text } };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = departmentAttr, ConnectedSystemAttributeId = departmentAttr.Id });
        rule.AttributeFlowRules.Add(mapping);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, true)).ReturnsAsync([rule]);

        var result = await _controller.PreviewConnectedSystemSchemaImportAsync(ConnectedSystemId);

        var dto = (result as OkObjectResult)!.Value as SchemaRefreshResultDto;
        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.Dependents, Is.Not.Null, "A destructive diff carries its dependents.");
            Assert.That(dto!.Dependents!.InvalidatedMappings, Has.Count.EqualTo(1));
            Assert.That(dto!.Dependents!.InvalidatedMappings[0].SyncRuleName, Is.EqualTo("HR Users Inbound"));
            Assert.That(dto!.Dependents!.InvalidatedMappings[0].Reason, Does.Contain("department"));
        }
    }

    [Test]
    public async Task ImportSchema_WithDisableDependents_AppliesAndDisablesAsync()
    {
        // The commit flavour of the decision: POST import-schema with disableDependents applies the schema and
        // disables what the removals invalidate, so automation gets the same protective option as the portal.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        var departmentAttr = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "department", Type = AttributeDataType.Text };
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Number },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text },
                    departmentAttr
                ]
            }
        ];
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, true)).ReturnsAsync(connectedSystem);

        var rule = new SyncRule { Id = 20, Name = "HR Users Inbound", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemObjectTypeId = 7 };
        var mapping = new SyncRuleMapping { Id = 200, TargetMetaverseAttribute = new MetaverseAttribute { Id = 900, Name = "Department", Type = AttributeDataType.Text } };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = departmentAttr, ConnectedSystemAttributeId = departmentAttr.Id });
        rule.AttributeFlowRules.Add(mapping);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, true)).ReturnsAsync([rule]);

        IReadOnlyCollection<SyncRuleMapping>? disabledMappings = null;
        _connectedSystemRepo.Setup(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()))
            .Callback<IReadOnlyCollection<SyncRuleMapping>>(m => disabledMappings = m)
            .Returns(Task.CompletedTask);

        var result = await _controller.ImportConnectedSystemSchemaAsync(ConnectedSystemId,
            new ImportConnectedSystemSchemaRequest { DisableDependents = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _connectedSystemRepo.Verify(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>()), Times.Once);
            Assert.That(disabledMappings, Is.Not.Null, "The invalidated mapping must be disabled.");
            Assert.That(disabledMappings!.Single().Enabled, Is.False);
            Assert.That(disabledMappings!.Single().DisabledReason, Does.Contain("department"));
        }
    }

    [Test]
    public async Task PreviewSchemaImport_WithDestructiveChanges_CountsTheRemovalImpactAsync()
    {
        // The Apply and Remove decision needs its numbers on the wire (#1485): how many objects and stored
        // values committing with removeDependents would take, per removed Object Type and attribute.
        var connectedSystem = CreateFileConnectorConnectedSystem();
        SeedDestructiveSchema(connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetLiveConnectedSystemObjectCountOfTypeAsync(ConnectedSystemId, 8)).ReturnsAsync(1204);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAttributeValueCountAsync(ConnectedSystemId, 3)).ReturnsAsync(87);

        var result = await _controller.PreviewConnectedSystemSchemaImportAsync(ConnectedSystemId);

        var dto = (result as OkObjectResult)!.Value as SchemaRefreshResultDto;
        Assert.That(dto, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto!.RemovalImpact, Is.Not.Null, "A destructive diff carries what a removal would take.");
            Assert.That(dto!.RemovalImpact!.RemovedObjectTypes.Single().ConnectedSystemObjectCount, Is.EqualTo(1204));
            Assert.That(dto!.RemovalImpact!.RemovedObjectTypes.Single().ObjectTypeName, Is.EqualTo("computer"));
            Assert.That(dto!.RemovalImpact!.RemovedAttributes.Single().StoredValueCount, Is.EqualTo(87));
            Assert.That(dto!.RemovalImpact!.RemovedAttributes.Single().AttributeName, Is.EqualTo("department"));
        }
    }

    [Test]
    public async Task ImportSchema_WithBothDisableAndRemove_ReturnsBadRequestAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, true)).ReturnsAsync(connectedSystem);

        var result = await _controller.ImportConnectedSystemSchemaAsync(ConnectedSystemId,
            new ImportConnectedSystemSchemaRequest { DisableDependents = true, RemoveDependents = true });

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>(),
            "Disabling and removing are mutually exclusive postures for one refresh.");
    }

    [Test]
    public async Task ImportSchema_WithRemoveDependents_AppliesDeletesAndQueuesTheDataRemovalAsync()
    {
        // The commit flavour of the full decision: POST import-schema with removeDependents applies the schema,
        // deletes what the removals invalidate and queues the data removal task, matching the portal.
        var taskingRepo = new Mock<ITaskingRepository>();
        _repository.Setup(r => r.Tasking).Returns(taskingRepo.Object);
        var queuedTasks = new List<JIM.Models.Tasking.WorkerTask>();
        taskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<JIM.Models.Tasking.WorkerTask>()))
            .Callback<JIM.Models.Tasking.WorkerTask>(t => queuedTasks.Add(t))
            .Returns(Task.CompletedTask);

        var connectedSystem = CreateFileConnectorConnectedSystem();
        var departmentAttr = SeedDestructiveSchema(connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, true)).ReturnsAsync(connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);

        var rule = new SyncRule { Id = 20, Name = "HR Users Inbound", Direction = SyncRuleDirection.Import, Enabled = true, ConnectedSystemId = ConnectedSystemId, ConnectedSystemObjectTypeId = 7 };
        var mapping = new SyncRuleMapping { Id = 200, SyncRuleId = 20, TargetMetaverseAttribute = new MetaverseAttribute { Id = 900, Name = "Department", Type = AttributeDataType.Text } };
        mapping.Sources.Add(new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = departmentAttr, ConnectedSystemAttributeId = departmentAttr.Id });
        rule.AttributeFlowRules.Add(mapping);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, true)).ReturnsAsync([rule]);
        _connectedSystemRepo.Setup(r => r.DeleteSyncRuleMappingAsync(It.IsAny<SyncRuleMapping>())).Returns(Task.CompletedTask);

        var result = await _controller.ImportConnectedSystemSchemaAsync(ConnectedSystemId,
            new ImportConnectedSystemSchemaRequest { RemoveDependents = true });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _connectedSystemRepo.Verify(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>()), Times.Once);
            _connectedSystemRepo.Verify(r => r.DeleteSyncRuleMappingAsync(It.Is<SyncRuleMapping>(m => m.Id == 200)), Times.Once,
                "The invalidated mapping must be deleted, not disabled.");
            var removalTask = queuedTasks.OfType<JIM.Models.Tasking.SchemaRefreshRemovalWorkerTask>().Single();
            Assert.That(removalTask.RemovedObjectTypeIds, Is.EquivalentTo(new[] { 8 }));
            Assert.That(removalTask.RemovedAttributeIds, Is.EquivalentTo(new[] { 3 }));
        }
    }

    /// <summary>
    /// Seeds a stored schema holding more than the CSV reports: the 'computer' Object Type (id 8) and the
    /// 'department' attribute (id 3) on 'user' both vanish on refresh. Returns the removed attribute.
    /// </summary>
    private static ConnectedSystemObjectTypeAttribute SeedDestructiveSchema(ConnectedSystem connectedSystem)
    {
        var departmentAttr = new ConnectedSystemObjectTypeAttribute { Id = 3, Name = "department", Type = AttributeDataType.Text };
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType
            {
                Id = 7,
                Name = "user",
                Selected = true,
                Attributes =
                [
                    new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Number },
                    new ConnectedSystemObjectTypeAttribute { Id = 2, Name = "displayName", Type = AttributeDataType.Text },
                    departmentAttr
                ]
            },
            new ConnectedSystemObjectType
            {
                Id = 8,
                Name = "computer",
                Selected = true,
                Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 4, Name = "id", Type = AttributeDataType.Number }]
            }
        ];
        return departmentAttr;
    }

    [Test]
    public async Task PreviewSchemaImport_WhenTheConnectorCannotRead_ReturnsBadRequestAsync()
    {
        var connectedSystem = CreateFileConnectorConnectedSystem();
        connectedSystem.SettingValues.Single(sv => sv.Setting.Name == "File Path").StringValue =
            Path.Join(Path.GetTempPath(), $"jim-missing-{Guid.NewGuid():N}.csv");
        connectedSystem.ObjectTypes =
        [
            new ConnectedSystemObjectType { Id = 7, Name = "user", Selected = true, Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 1, Name = "id", Type = AttributeDataType.Number }] }
        ];
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);

        var result = await _controller.PreviewConnectedSystemSchemaImportAsync(ConnectedSystemId);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    private ConnectedSystem CreateFileConnectorConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = ConnectorConstants.FileConnectorName };
        _application.ConnectedSystems.CopyConnectorSettingsToConnectorDefinition(new FileConnector(), connectorDefinition);

        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
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
