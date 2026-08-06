// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
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
/// The REST surface for previewing a change to a Connected System's partition and container selection (#1251).
///
/// The behaviour worth pinning is what an omitted list means. Deselecting everything and saying nothing about the
/// selection are opposite proposals, and the destructive one is the one a caller sends deliberately: reading an
/// omitted list as "select nothing" would report a mass obsoletion nobody proposed, and reading an empty list as
/// "leave it alone" would report no impact from the single most destructive change this surface allows.
/// </summary>
[TestFixture]
public class SynchronisationControllerScopeSelectionPreviewTests
{
    private const int ConnectedSystemId = 2;
    private const int PartitionId = 11;
    private const int UsersContainerId = 21;
    private const int ContractorsContainerId = 22;

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

        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId))
            .ReturnsAsync(BuildConnectedSystem);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(ConnectedSystemId, It.IsAny<int?>()))
            .ReturnsAsync(0);

        // No background runner is registered in a unit fixture, so every preview is queued for JIM.Worker, which
        // makes the queued task's payload the honest record of what was handed to the framework.
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
    public async Task StartScopeSelectionPreview_UnknownConnectedSystem_ReturnsNotFoundAsync()
    {
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(99)).ReturnsAsync((ConnectedSystem?)null);

        var result = await _controller.StartConnectedSystemScopeSelectionPreviewAsync(99,
            new StartConnectedSystemScopeSelectionPreviewRequest());

        Assert.That(result, Is.InstanceOf<NotFoundObjectResult>());
    }

    [Test]
    public async Task StartScopeSelectionPreview_OmittedLists_PreviewTheStoredSelectionAsync()
    {
        // Asking what the selection already in force would do is a legitimate question, and must not be read as
        // "deselect everything".
        var result = await _controller.StartConnectedSystemScopeSelectionPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemScopeSelectionPreviewRequest());

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.SelectedPartitionIds, Is.EquivalentTo(new[] { PartitionId }));
            Assert.That(proposal.SelectedContainerIds, Is.EquivalentTo(new[] { UsersContainerId, ContractorsContainerId }));
        }
    }

    [Test]
    public async Task StartScopeSelectionPreview_EmptyContainerList_PreviewsDeselectingEveryContainerAsync()
    {
        // The opposite of the case above, and the one that matters most: an explicitly empty list is the single
        // most destructive proposal this surface allows, and quietly substituting the stored selection for it
        // would report that it does nothing.
        var result = await _controller.StartConnectedSystemScopeSelectionPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemScopeSelectionPreviewRequest { SelectedContainerIds = [] });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        var proposal = QueuedProposal();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proposal.SelectedContainerIds, Is.Empty);
            Assert.That(proposal.SelectedPartitionIds, Is.EquivalentTo(new[] { PartitionId }),
                "an omitted partition list should still preview the stored partitions");
        }
    }

    [Test]
    public async Task StartScopeSelectionPreview_SuppliedLists_PreviewTheProposedSelectionAsync()
    {
        var result = await _controller.StartConnectedSystemScopeSelectionPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemScopeSelectionPreviewRequest
            {
                SelectedPartitionIds = [PartitionId],
                SelectedContainerIds = [UsersContainerId]
            });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
        Assert.That(QueuedProposal().SelectedContainerIds, Is.EquivalentTo(new[] { UsersContainerId }));
    }

    [Test]
    public async Task StartScopeSelectionPreview_ContainerFromAnotherConnectedSystem_ReturnsBadRequestAsync()
    {
        // An id naming nothing in this hierarchy has no coherent proposal behind it. Ignoring it silently would
        // produce a confident answer to a question the caller did not ask.
        var result = await _controller.StartConnectedSystemScopeSelectionPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemScopeSelectionPreviewRequest { SelectedContainerIds = [9999] });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(_queuedWorkerTasks, Is.Empty, "nothing should be evaluated for an incoherent proposal");
        }
    }

    [Test]
    public async Task StartScopeSelectionPreview_NestedContainer_IsAcceptedAsync()
    {
        // Containers form a tree and only the roots hang off the partition; a validity check that looked no deeper
        // would reject a perfectly ordinary selection of a nested container.
        var connectedSystem = BuildConnectedSystem();
        var nested = new ConnectedSystemContainer { Id = 31, Name = "Finance", ExternalId = "OU=Finance,OU=Users,DC=example,DC=com" };
        connectedSystem.Partitions![0].Containers!.Single(c => c.Id == UsersContainerId).ChildContainers.Add(nested);
        _connectedSystemRepo.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId)).ReturnsAsync(connectedSystem);

        var result = await _controller.StartConnectedSystemScopeSelectionPreviewAsync(ConnectedSystemId,
            new StartConnectedSystemScopeSelectionPreviewRequest { SelectedContainerIds = [31] });

        Assert.That(result, Is.InstanceOf<AcceptedAtRouteResult>());
    }

    private ConnectedSystemScopeSelectionProposal QueuedProposal()
    {
        var task = _queuedWorkerTasks.OfType<ConfigurationChangePreviewWorkerTask>().SingleOrDefault();
        Assert.That(task, Is.Not.Null, "the preview should have been queued for evaluation");
        return JsonSerializer.Deserialize<ConnectedSystemScopeSelectionProposal>(task!.ProposedConfigurationPayload)!;
    }

    private static ConnectedSystem BuildConnectedSystem()
    {
        var partition = new ConnectedSystemPartition
        {
            Id = PartitionId,
            Name = "example.com",
            ExternalId = "DC=example,DC=com",
            Selected = true,
            Containers = []
        };

        partition.Containers.Add(new ConnectedSystemContainer
        {
            Id = UsersContainerId,
            Name = "Users",
            ExternalId = "OU=Users,DC=example,DC=com",
            Selected = true
        });

        partition.Containers.Add(new ConnectedSystemContainer
        {
            Id = ContractorsContainerId,
            Name = "Contractors",
            ExternalId = "OU=Contractors,DC=example,DC=com",
            Selected = true
        });

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Example Directory",
            ConnectorDefinition = new ConnectorDefinition
            {
                Name = "JIM LDAP Connector",
                SupportsPartitions = true,
                SupportsPartitionContainers = true
            },
            Partitions = [partition]
        };
    }
}
