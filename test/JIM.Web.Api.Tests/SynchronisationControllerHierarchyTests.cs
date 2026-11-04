// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Expressions;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Web.Controllers.Api;
using JIM.Web.Models.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// The hierarchy import endpoint's choice for Run Profiles that target a partition the refresh removes (#2740). A script
/// that names no choice must never have a Run Profile widened, so no body disables them; Cancel applies nothing and
/// answers 409 naming them.
/// </summary>
[TestFixture]
public class SynchronisationControllerHierarchyTests
{
    private const int ConnectedSystemId = 1;

    private Mock<IConnectedSystemRepository> _connectedSystems = null!;
    private SynchronisationController _controller = null!;
    private JimApplication _application = null!;
    private ConnectedSystemRunProfile _glitterbandImport = null!;

    [SetUp]
    public void SetUp()
    {
        var repository = new Mock<IRepository>();
        _connectedSystems = new Mock<IConnectedSystemRepository>();
        var apiKeys = new Mock<IApiKeyRepository>();
        var apiKeyId = Guid.NewGuid();
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystems.Object);
        repository.Setup(r => r.Activity).Returns(new Mock<IActivityRepository>().Object);
        repository.Setup(r => r.ApiKeys).Returns(apiKeys.Object);
        apiKeys.Setup(r => r.GetByIdAsync(apiKeyId)).ReturnsAsync(new JIM.Models.Security.ApiKey { Id = apiKeyId, Name = "Automation" });

        var hostSetting = new ConnectorDefinitionSetting { Id = 1, Name = "Host", Type = ConnectedSystemSettingType.String };
        var connectorDefinition = new ConnectorDefinition { Name = "Stub Partitions Connector", SupportsPartitions = true, Settings = [hostSetting] };
        var yellowstone = new ConnectedSystemPartition { Id = 1, Name = "dc=yellowstone,dc=local", ExternalId = "dc=yellowstone,dc=local", Selected = true, Containers = [] };
        var glitterband = new ConnectedSystemPartition { Id = 2, Name = "dc=glitterband,dc=local", ExternalId = "dc=glitterband,dc=local", Selected = true, Containers = [] };
        _glitterbandImport = new ConnectedSystemRunProfile
        {
            Id = 7, Name = "Full Import (Glitterband)", ConnectedSystemId = ConnectedSystemId, RunType = ConnectedSystemRunType.FullImport, Partition = glitterband
        };
        var connectedSystem = new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Panoply Directory",
            ConnectorDefinition = connectorDefinition,
            Partitions = [yellowstone, glitterband],
            RunProfiles = [_glitterbandImport],
            SettingValues = [new ConnectedSystemSettingValue { Id = 1, Setting = hostSetting, StringValue = "directory.panoply.local" }]
        };
        yellowstone.ConnectedSystem = connectedSystem;
        glitterband.ConnectedSystem = connectedSystem;
        _connectedSystems.Setup(r => r.GetConnectedSystemAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);
        _connectedSystems.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, It.IsAny<bool>())).ReturnsAsync(connectedSystem);

        // The directory no longer has Glitterband.
        var connector = new StubPartitionsConnector([new ConnectorPartition { Id = "dc=yellowstone,dc=local", Name = "dc=yellowstone,dc=local", Containers = [] }]);
        _application = new JimApplication(repository.Object, connectorFactory: new StubConnectorFactory(connector));
        _controller = new SynchronisationController(new Mock<ILogger<SynchronisationController>>().Object, _application,
            new DynamicExpressoEvaluator(), new Mock<ICredentialProtectionService>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("auth_method", "api_key"),
                        new Claim(ClaimTypes.NameIdentifier, apiKeyId.ToString()),
                        new Claim(ClaimTypes.Name, "Automation")
                    ], "ApiKey"))
                }
            }
        };
    }

    [TearDown]
    public void TearDown()
    {
        _application.Dispose();
    }

    [Test]
    public async Task ImportConnectedSystemHierarchyAsync_NoBody_DisablesTheRunProfilesThatTargetARemovedPartitionAsync()
    {
        var result = await _controller.ImportConnectedSystemHierarchyAsync(ConnectedSystemId);

        var dto = (result as OkObjectResult)?.Value as HierarchyRefreshResultDto;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto, Is.Not.Null, "no body applies the refresh");
            Assert.That(dto!.Applied, Is.True);
            Assert.That(dto!.AffectedRunProfiles.Select(rp => (rp.RunProfileId, rp.RunProfileName, rp.PartitionName, rp.Disabled)),
                Is.EqualTo(new[] { (7, "Full Import (Glitterband)", "dc=glitterband,dc=local", true) }),
                "the response names each Run Profile it disabled");
            Assert.That(_glitterbandImport.Enabled, Is.False, "and the Run Profile is disabled rather than widened");
        }
    }

    [Test]
    public async Task ImportConnectedSystemHierarchyAsync_Cancel_Returns409NamingTheRunProfilesAndChangesNothingAsync()
    {
        var result = await _controller.ImportConnectedSystemHierarchyAsync(ConnectedSystemId,
            new ImportConnectedSystemHierarchyRequest { RunProfileAction = RemovedPartitionRunProfileAction.Cancel });

        var body = (result as ConflictObjectResult)?.Value as HierarchyRefreshRefusedResponse;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(body, Is.Not.Null, "a cancelled refresh is a 409");
            Assert.That(body!.Message, Does.StartWith("Nothing from this refresh was saved."));
            Assert.That(body!.AffectedRunProfiles.Select(rp => (rp.RunProfileId, rp.PartitionName, rp.Disabled)),
                Is.EqualTo(new[] { (7, "dc=glitterband,dc=local", false) }));
            Assert.That(_glitterbandImport.Enabled, Is.True);
        }

        _connectedSystems.Verify(r => r.UpdateConnectedSystemHierarchyAsync(It.IsAny<ConnectedSystem>(),
            It.IsAny<IReadOnlyCollection<ConnectedSystemPartition>>(), It.IsAny<IReadOnlyCollection<ConnectedSystemContainer>>()), Times.Never,
            "nothing is saved");
    }

    private sealed class StubPartitionsConnector(List<ConnectorPartition> partitions) : IConnector, IConnectorPartitions
    {
        public string Name => "Stub Partitions Connector";
        public string? Description => null;
        public string? Url => null;

        public Task<List<ConnectorPartition>> GetPartitionsAsync(List<ConnectedSystemSettingValue> settings, Serilog.ILogger logger) => Task.FromResult(partitions);
    }

    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }
}
