// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Preview;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The REST surface for previewing a Connected System's schema selection (#1475).
///
/// The behaviour worth pinning is what an omitted field means, and it matters more here than on any other preview
/// surface because every type default is the destructive answer: an omitted <c>selected</c> read as <c>false</c>
/// proposes taking a whole Object Type out of management, and an omitted attribute list read as empty proposes
/// deselecting every attribute on it. A caller changing one flag would then be shown, and could consent to, a
/// change they never asked for.
/// </summary>
[TestFixture]
public class SynchronisationControllerSchemaPreviewTests
{
    private const int ConnectedSystemId = 2;
    private const int UserTypeId = 9;
    private const int GroupTypeId = 11;
    private const int AnchorAttributeId = 100;
    private const int DisplayNameAttributeId = 101;
    private const int DepartmentAttributeId = 102;

    private Mock<IRepository> _repository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IApiKeyRepository> _apiKeyRepo = null!;
    private Mock<IConfigurationChangePreviewRepository> _previewRepo = null!;
    private Mock<ITaskingRepository> _taskingRepo = null!;
    private Mock<IServiceSettingsRepository> _serviceSettingsRepo = null!;
    private JimApplication _application = null!;
    private SynchronisationController _controller = null!;
    private List<WorkerTask> _queuedWorkerTasks = null!;
    private Activity? _previewActivity;
    private ConnectedSystem _connectedSystem = null!;
    private ConnectedSystemObjectType _userType = null!;
    private ConnectedSystemObjectType _groupType = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _connectedSystemRepo = new Mock<IConnectedSystemRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _apiKeyRepo = new Mock<IApiKeyRepository>();
        _previewRepo = new Mock<IConfigurationChangePreviewRepository>();
        _taskingRepo = new Mock<ITaskingRepository>();
        _serviceSettingsRepo = new Mock<IServiceSettingsRepository>();

        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepo.Object);
        _repository.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repository.Setup(r => r.ApiKeys).Returns(_apiKeyRepo.Object);
        _repository.Setup(r => r.ConfigurationChangePreviews).Returns(_previewRepo.Object);
        _repository.Setup(r => r.Tasking).Returns(_taskingRepo.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(_serviceSettingsRepo.Object);

        _queuedWorkerTasks = [];
        _previewActivity = null;

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _previewActivity = a;
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetActivityAsync(It.IsAny<Guid>())).ReturnsAsync(() => _previewActivity);
        _previewRepo.Setup(r => r.CreatePreviewAsync(It.IsAny<ConfigurationChangePreview>())).Returns(Task.CompletedTask);
        _previewRepo.Setup(r => r.UpdatePreviewAsync(It.IsAny<ConfigurationChangePreview>())).Returns(Task.CompletedTask);
        _previewRepo.Setup(r => r.GetPreviewAsync(It.IsAny<Guid>())).ReturnsAsync((ConfigurationChangePreview?)null);
        _taskingRepo.Setup(r => r.CreateWorkerTaskAsync(It.IsAny<WorkerTask>()))
            .Callback<WorkerTask>(t => _queuedWorkerTasks.Add(t))
            .Returns(Task.CompletedTask);

        // A stored schema deliberately away from the type defaults, so an omitted field merged to a default rather
        // than to the stored value is caught: the User type is selected with recall ON, the Group type is not.
        _userType = new ConnectedSystemObjectType
        {
            Id = UserTypeId,
            Name = "User",
            ConnectedSystemId = ConnectedSystemId,
            Selected = true,
            RemoveContributedAttributesOnObsoletion = true,
            Attributes =
            [
                new ConnectedSystemObjectTypeAttribute { Id = AnchorAttributeId, Name = "objectGuid", Type = AttributeDataType.Guid, IsExternalId = true },
                new ConnectedSystemObjectTypeAttribute { Id = DisplayNameAttributeId, Name = "displayName", Type = AttributeDataType.Text, Selected = true },
                new ConnectedSystemObjectTypeAttribute { Id = DepartmentAttributeId, Name = "department", Type = AttributeDataType.Text, Selected = true }
            ]
        };

        _groupType = new ConnectedSystemObjectType
        {
            Id = GroupTypeId,
            Name = "Group",
            ConnectedSystemId = ConnectedSystemId,
            Selected = false,
            RemoveContributedAttributesOnObsoletion = false,
            Attributes = [new ConnectedSystemObjectTypeAttribute { Id = 110, Name = "objectGuid", Type = AttributeDataType.Guid, IsExternalId = true }]
        };

        _connectedSystem = new ConnectedSystem { Id = ConnectedSystemId, Name = "Directory" };

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(() => _connectedSystem);
        _connectedSystemRepo.Setup(r => r.GetObjectTypesAsync(ConnectedSystemId)).ReturnsAsync(() => [_userType, _groupType]);
        _connectedSystemRepo.Setup(r => r.GetSyncRulesAsync(ConnectedSystemId, It.IsAny<bool>(), It.IsAny<bool>())).ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountOfTypeAsync(ConnectedSystemId, It.IsAny<int>())).ReturnsAsync(0);
        _connectedSystemRepo.Setup(r => r.GetLiveConnectedSystemObjectIdsOfTypeAsync(ConnectedSystemId, It.IsAny<int>())).ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetObsoleteJoinedConnectedSystemObjectIdsOfTypeAsync(ConnectedSystemId, It.IsAny<int>())).ReturnsAsync([]);
        _connectedSystemRepo.Setup(r => r.GetLiveConnectedSystemObjectIdsHoldingAttributeAsync(ConnectedSystemId, It.IsAny<int>(), It.IsAny<int>())).ReturnsAsync([]);

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
    public void TearDown() => _application?.Dispose();

    [Test]
    public async Task StartSchemaPreview_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(99)).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.StartConnectedSystemSchemaPreviewAsync(99,
            new StartConnectedSystemSchemaPreviewRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task StartSchemaPreview_NoObjectTypesSupplied_PreviewsTheStoredSchemaAsync()
    {
        // Asking what the configuration already in force does is a legitimate question, and the answer must be the
        // stored schema rather than an empty one, which would propose deselecting everything.
        var result = await _controller.StartConnectedSystemSchemaPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemSchemaPreviewRequest());

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.ObjectTypes.Select(t => t.ObjectTypeId), Is.EquivalentTo(new[] { UserTypeId, GroupTypeId }));
            Assert.That(proposal.For(UserTypeId)!.Selected, Is.True);
            Assert.That(proposal.For(GroupTypeId)!.Selected, Is.False);
        }
    }

    [Test]
    public async Task StartSchemaPreview_OneFieldSupplied_MergesTheRestFromTheStoredTypeAsync()
    {
        // The case the whole merge exists for: a caller turning recall off has not asked to deselect the Type or
        // to drop its attributes, and every one of those omissions has a destructive type default.
        var result = await _controller.StartConnectedSystemSchemaPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemSchemaPreviewRequest
            {
                ObjectTypes =
                [
                    new ConnectedSystemObjectTypeSelectionRequest
                    {
                        ObjectTypeId = UserTypeId,
                        RemoveContributedAttributesOnObsoletion = false
                    }
                ]
            });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var userType = QueuedProposal().For(UserTypeId)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(userType.RemoveContributedAttributesOnObsoletion, Is.False, "the field the caller set");
            Assert.That(userType.Selected, Is.True,
                "an omitted Selected must not be read as false, which would propose deselecting the whole Object Type");
            Assert.That(userType.SelectedAttributeIds,
                Is.EquivalentTo(new[] { AnchorAttributeId, DisplayNameAttributeId, DepartmentAttributeId }),
                "an omitted attribute list must not be read as empty, which would propose deselecting every attribute");
        }
    }

    [Test]
    public async Task StartSchemaPreview_AnObjectTypeNotNamed_IsCarriedThroughUnchangedAsync()
    {
        var result = await _controller.StartConnectedSystemSchemaPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemSchemaPreviewRequest
            {
                ObjectTypes = [new ConnectedSystemObjectTypeSelectionRequest { ObjectTypeId = UserTypeId, Selected = false }]
            });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.For(UserTypeId)!.Selected, Is.False);
            Assert.That(proposal.For(GroupTypeId), Is.Not.Null,
                "a Type the request does not name must still reach the adapter, so the comparison can see it unchanged");
            Assert.That(proposal.For(GroupTypeId)!.RemoveContributedAttributesOnObsoletion, Is.False,
                "and it must carry its stored settings, not the type defaults");
        }
    }

    [Test]
    public async Task StartSchemaPreview_AnExplicitlyNarrowedAttributeList_IsHonouredRatherThanTreatedAsOmittedAsync()
    {
        // A shorter list is a proposal to deselect what it leaves out, and must not be confused with saying
        // nothing. The anchor is kept, so the proposal is workable and gets evaluated.
        var result = await _controller.StartConnectedSystemSchemaPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemSchemaPreviewRequest
            {
                ObjectTypes =
                [
                    new ConnectedSystemObjectTypeSelectionRequest
                    {
                        ObjectTypeId = UserTypeId,
                        SelectedAttributeIds = [AnchorAttributeId, DisplayNameAttributeId]
                    }
                ]
            });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        Assert.That(QueuedProposal().For(UserTypeId)!.SelectedAttributeIds,
            Is.EquivalentTo(new[] { AnchorAttributeId, DisplayNameAttributeId }));
    }

    [Test]
    public async Task StartSchemaPreview_AnEmptyAttributeList_DropsTheAnchorAndIsBlockedAsync()
    {
        // An empty list is honoured as the statement it is, and deselecting everything necessarily deselects the
        // External ID, which the storage layer refuses. The preview says so rather than evaluating a proposal that
        // could never be saved.
        var result = await _controller.StartConnectedSystemSchemaPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemSchemaPreviewRequest
            {
                ObjectTypes =
                [
                    new ConnectedSystemObjectTypeSelectionRequest { ObjectTypeId = UserTypeId, SelectedAttributeIds = [] }
                ]
            });

        var response = (ConfigurationChangePreviewStartResponse)((AcceptedAtRouteResult)result).Value!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.IsBlocked, Is.True);
            Assert.That(_queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>(), Is.Empty,
                "a blocked proposal is answered, not evaluated");
        }
    }

    [Test]
    public async Task StartSchemaPreview_AnObjectTypeOnAnotherConnectedSystem_IsRefusedAsync()
    {
        var result = await _controller.StartConnectedSystemSchemaPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemSchemaPreviewRequest
            {
                ObjectTypes = [new ConnectedSystemObjectTypeSelectionRequest { ObjectTypeId = 4242, Selected = false }]
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_queuedWorkerTasks, Is.Empty,
                "there is no coherent proposal to evaluate, so nothing should have been queued");
        }
    }

    [Test]
    public async Task StartSchemaPreview_AnAttributeOnAnotherObjectType_IsRefusedAsync()
    {
        var result = await _controller.StartConnectedSystemSchemaPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemSchemaPreviewRequest
            {
                ObjectTypes =
                [
                    new ConnectedSystemObjectTypeSelectionRequest
                    {
                        ObjectTypeId = UserTypeId,
                        SelectedAttributeIds = [AnchorAttributeId, 9999]
                    }
                ]
            });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_queuedWorkerTasks, Is.Empty);
        }
    }

    private ConnectedSystemSchemaProposal QueuedProposal()
    {
        var task = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().SingleOrDefault();
        Assert.That(task, Is.Not.Null, "the preview should have been queued for evaluation");
        return JsonSerializer.Deserialize<ConnectedSystemSchemaProposal>(task!.ProposedConfigurationPayload)!;
    }
}
